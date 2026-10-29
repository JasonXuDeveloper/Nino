using System;
using System.Buffers;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;
using Nino.Core;
using Nino.Core.CompilerServices;
using Perfolizer.Horology;

namespace Nino.Benchmark;

// Probes of NinoUnsafe's block copies against Memmove, on their own jobs: the sizes, alignments and CPUs at which
// Nino.Core chooses a vector loop, a copy into a new array or Arm64's paired loads (see NinoUnsafe.Copy, CopyToNew and
// CopyPairs). Run them again on a CPU those choices were not measured on. Only a filter that names a probe runs it
// (Program.cs), never the suite's '*'.

/// <summary>
/// The jobs of the x64 probes: AVX-512 on, off (DOTNET_EnableAVX512=0: .NET 10 ignores DOTNET_EnableAVX512F) and
/// with 64-byte vectors not preferred (Avx512F still supported, Vector512 not accelerated).
/// </summary>
public class ProbeConfig : ManualConfig
{
    public ProbeConfig()
    {
        var job = Job.Default.WithRuntime(CoreRuntime.Core10_0).WithLaunchCount(1)
            .WithIterationTime(TimeInterval.FromMilliseconds(250)).WithWarmupCount(3)
            .WithMinIterationCount(6).WithMaxIterationCount(10).WithMaxRelativeError(0.02);
        AddJob(job.WithId("Avx512"));
        AddJob(job.WithEnvironmentVariables(new EnvironmentVariable("DOTNET_EnableAVX512", "0")).WithId("Avx2"));
        AddJob(job.WithEnvironmentVariables(new EnvironmentVariable("DOTNET_PreferredVectorBitWidth", "256")).WithId("Prefer256"));
    }
}

/// <summary>Experiment: NinoUnsafe.Copy against Memmove (Span.CopyTo), into a buffer at offset 4.</summary>
[Config(typeof(ProbeConfig))]
public class CopyProbe
{
    [Params(4000, 8000, 16000, 32000, 64000, 160000)]
    public int Bytes = 4000;

    private readonly byte[] _source = GC.AllocateArray<byte>(1 << 18, pinned: true);
    private readonly byte[] _destination = GC.AllocateArray<byte>(1 << 18, pinned: true);

    [Benchmark(Baseline = true)]
    public int Memmove() { _source.AsSpan(0, Bytes).CopyTo(_destination.AsSpan(4)); return Bytes; }

    [Benchmark]
    public int NinoCopy() { NinoUnsafe.Copy(ref _destination[4], ref MemoryMarshal.GetArrayDataReference(_source), (uint)Bytes); return Bytes; }

    /// <summary>The 32-byte vector loop of NinoUnsafe.Copy at every size (it calls Memmove above 4 KB).</summary>
    [Benchmark]
    public int Vector256Loop()
    {
        ref byte destination = ref _destination[4];
        ref byte source = ref MemoryMarshal.GetArrayDataReference(_source);
        nuint size = (nuint)Bytes;
        Vector256.LoadUnsafe(ref source).StoreUnsafe(ref destination);
        nuint i = 32 - ((nuint)(nint)Marshal.UnsafeAddrOfPinnedArrayElement(_destination, 4) & 31);
        nuint end = size - 32;
        for (; i + 96 <= end; i += 128)
        {
            var a = Vector256.LoadUnsafe(ref source, i);
            var b = Vector256.LoadUnsafe(ref source, i + 32);
            var c = Vector256.LoadUnsafe(ref source, i + 64);
            var d = Vector256.LoadUnsafe(ref source, i + 96);
            a.StoreUnsafe(ref destination, i);
            b.StoreUnsafe(ref destination, i + 32);
            c.StoreUnsafe(ref destination, i + 64);
            d.StoreUnsafe(ref destination, i + 96);
        }
        for (; i <= end; i += 32)
            Vector256.LoadUnsafe(ref source, i).StoreUnsafe(ref destination, i);
        Vector256.LoadUnsafe(ref source, end).StoreUnsafe(ref destination, end);
        return Bytes;
    }
}

/// <summary>
/// Experiment: a read into a new array (deserializing a large raw array): a new uninitialized array, then the bytes
/// copied in from offset 4 of a buffer, with Memmove (NinoUnsafe.CopyToNew, as MemoryPack copies) or with
/// NinoUnsafe.Copy's vector loop.
/// </summary>
[Config(typeof(ProbeConfig))]
public class NewArrayCopyProbe
{
    [Params(8000, 16000, 32000, 64000, 160000)]
    public int Bytes = 8000;

    private readonly byte[] _source = GC.AllocateArray<byte>(1 << 18, pinned: true);

    [Benchmark(Baseline = true)]
    public byte[] Memmove()
    {
        var array = GC.AllocateUninitializedArray<byte>(Bytes);
        Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetArrayDataReference(array), ref _source[4], (uint)Bytes);
        return array;
    }

    [Benchmark]
    public byte[] NinoCopy()
    {
        var array = GC.AllocateUninitializedArray<byte>(Bytes);
        NinoUnsafe.Copy(ref MemoryMarshal.GetArrayDataReference(array), ref _source[4], (uint)Bytes);
        return array;
    }
}

/// <summary>
/// Experiment: 8,000 bytes copied with the destination <see cref="Gap"/> bytes past the source modulo 4 KB (the
/// source 64-byte aligned or <see cref="SourceShift"/> past it). A load whose address matches a pending store's in its
/// low 12 bits waits for it (4K aliasing): copying forward, that happens when the destination is a little past the
/// source modulo 4 KB, and the gap between a serializer's buffer and the array it writes changes from process to
/// process. Inventory (1,000 8-byte structs, copied whole) took 105 to 170 ns by the process on an EPYC 9V74
/// (MemoryPack, whose Memmove copies backward at such gaps: 105). NinoUnsafe.Copy (forward, stores aligned), the same
/// loop run backward, and Memmove.
/// </summary>
[Config(typeof(ProbeConfig))]
public unsafe class CopyGapProbe
{
    [Params(4, 36, 132, 260, 516, 1028, 2052, 4036)]
    public int Gap;

    [Params(0, 40)]
    public int SourceShift;

    private const int Bytes = 8000;
    private static readonly byte* Base = (byte*)NativeMemory.AlignedAlloc(1 << 16, 4096);

    private byte* Source => Base + SourceShift;
    private byte* Destination => Base + 16384 + SourceShift + Gap;

    [Benchmark(Baseline = true)]
    public int NinoCopy()
    {
        NinoUnsafe.Copy(ref *Destination, ref *Source, Bytes);
        return Bytes;
    }

    [Benchmark]
    public int Backward()
    {
        if (Vector512.IsHardwareAccelerated)
            CopyBackward<Vector512<byte>>(Destination, Source, Bytes);
        else
            CopyBackward<Vector256<byte>>(Destination, Source, Bytes);
        return Bytes;
    }

    [Benchmark]
    public int Memmove()
    {
        Buffer.MemoryCopy(Source, Destination, Bytes, Bytes);
        return Bytes;
    }

    /// <summary>NinoUnsafe.CopyVectors from the end: the last vector unaligned, aligned stores down, the first one.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void CopyBackward<TVector>(byte* d, byte* s, nuint size) where TVector : struct
    {
        nuint width = (nuint)Unsafe.SizeOf<TVector>();
        Unsafe.WriteUnaligned(d + size - width, Unsafe.ReadUnaligned<TVector>(s + size - width));
        // The aligned stores end at i (exclusive), the last one at the aligned offset at or before the last vector.
        nuint i = size - ((nuint)(d + size - width) & (width - 1));
        for (; i >= 4 * width; i -= 4 * width)
        {
            var a = Unsafe.ReadUnaligned<TVector>(s + i - width);
            var b = Unsafe.ReadUnaligned<TVector>(s + i - 2 * width);
            var c = Unsafe.ReadUnaligned<TVector>(s + i - 3 * width);
            var e = Unsafe.ReadUnaligned<TVector>(s + i - 4 * width);
            Unsafe.WriteUnaligned(d + i - width, a);
            Unsafe.WriteUnaligned(d + i - 2 * width, b);
            Unsafe.WriteUnaligned(d + i - 3 * width, c);
            Unsafe.WriteUnaligned(d + i - 4 * width, e);
        }

        for (; i >= width; i -= width)
            Unsafe.WriteUnaligned(d + i - width, Unsafe.ReadUnaligned<TVector>(s + i - width));
        Unsafe.WriteUnaligned(d, Unsafe.ReadUnaligned<TVector>(s));
    }
}

/// <summary>One job, for kernels that environment switches do not change (Arm64).</summary>
public class SingleProbeConfig : ManualConfig
{
    public SingleProbeConfig() =>
        AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0).WithLaunchCount(1)
            .WithIterationTime(TimeInterval.FromMilliseconds(250)).WithWarmupCount(3)
            .WithMinIterationCount(6).WithMaxIterationCount(10).WithMaxRelativeError(0.02).WithId("Probe"));
}

/// <summary>
/// Experiment: block copies of 16-byte vectors (Arm64 ldp/stp of q registers, which the JIT pairs from adjacent loads
/// and stores through cursors) against Memmove (the C library's memcpy), into a buffer 4 bytes past a 64-byte boundary
/// (a collection after its length), from a source <see cref="SourceShift"/> bytes past one (arrays' elements start 16
/// bytes into the object, itself 8-byte aligned): 64 bytes a step as they come, or after one unaligned step that
/// brings the loads to a 16-byte boundary. On a Neoverse N2 with an aligned source, the plain loop took 0.85 to 0.92 of
/// Memmove's time from 1 to 64 KB, and the loop with its stores aligned (its loads then not) 0.96 to 1.31.
/// </summary>
[Config(typeof(SingleProbeConfig))]
public unsafe class Copy128Probe
{
    [Params(128, 256, 512, 1024, 8000, 64000, 256000, 1048576)]
    public int Bytes = 8000;

    [Params(0, 4, 8)]
    public int SourceShift;

    private static readonly byte* SourceBase = (byte*)NativeMemory.AlignedAlloc(1 << 21, 64);
    private static readonly byte* Destination = (byte*)NativeMemory.AlignedAlloc(1 << 21, 64) + 4;

    private byte* Source => SourceBase + SourceShift;

    [Benchmark(Baseline = true)]
    public int Memmove()
    {
        Buffer.MemoryCopy(Source, Destination, Bytes, Bytes);
        return Bytes;
    }

    [Benchmark]
    public int Loop64()
    {
        Copy64(Source, Destination, Bytes);
        Tail(Source, Destination, Bytes);
        return Bytes;
    }

    [Benchmark]
    public int Loop64LoadsAligned()
    {
        byte* s = Source, d = Destination;
        Vector128.Load(s).Store(d);
        nint skip = 16 - (nint)((nuint)s & 15);
        Copy64(s + skip, d + skip, Bytes - (int)skip);
        Tail(Source, Destination, Bytes);
        return Bytes;
    }

    // 64 bytes a step through cursors (indexed loads are not paired into ldp).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Copy64(byte* s, byte* d, int bytes)
    {
        for (nuint n = (nuint)bytes / 64; n != 0; n--)
        {
            var a = Vector128.Load(s);
            var b = Vector128.Load(s + 16);
            var c = Vector128.Load(s + 32);
            var e = Vector128.Load(s + 48);
            a.Store(d);
            b.Store(d + 16);
            c.Store(d + 32);
            e.Store(d + 48);
            s += 64;
            d += 64;
        }
    }

    // The last 64 bytes, overlapping what the loop wrote.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Tail(byte* s, byte* d, int bytes)
    {
        nuint end = (nuint)bytes - 64;
        var a = Vector128.Load(s + end);
        var b = Vector128.Load(s + end + 16);
        var c = Vector128.Load(s + end + 32);
        var e = Vector128.Load(s + end + 48);
        a.Store(d + end);
        b.Store(d + end + 16);
        c.Store(d + end + 32);
        e.Store(d + end + 48);
    }
}

/// <summary>
/// Experiment: an array of 1,000 padded raw structs (InventoryItem: 8 bytes, a padding byte) written into a buffer,
/// Nino (NinoMask's masked vector copy) against MemoryPack (a plain copy).
/// </summary>
[Config(typeof(ProbeConfig))]
public class MaskCopyProbe
{
    private static readonly InventoryItem[] Items = Enumerable.Range(0, 1000)
        .Select(i => new InventoryItem { ItemId = i, Count = (short)(i % 100), Bound = i % 3 == 0 }).ToArray();

    private readonly NinoArrayBufferWriter _nino = new(1 << 16);
    private readonly ArrayBufferWriter<byte> _memoryPack = new(1 << 16);

    [Benchmark(Baseline = true)]
    public int Nino()
    {
        _nino.ResetWrittenCount();
        NinoSerializer.Serialize(Items, _nino);
        return _nino.WrittenCount;
    }

    [Benchmark]
    public int MemoryPackCopy()
    {
        _memoryPack.ResetWrittenCount();
        MemoryPack.MemoryPackSerializer.Serialize(_memoryPack, Items);
        return _memoryPack.WrittenCount;
    }
}

/// <summary>
/// Experiment: the kernels NinoMask could copy padded values with, on 8,000 bytes of 8-byte values with a padding byte
/// each, the destination 4 bytes past a 64-byte boundary (after a length): Memmove, 64-byte vectors masked by vpand
/// with aligned or unaligned stores, masked by a zero-masking load, 32-byte vectors, and Memmove then a masking pass.
/// </summary>
[Config(typeof(ProbeConfig))]
public unsafe class MaskKernelProbe
{
    private const int Bytes = 8000;
    private static readonly byte* Source = (byte*)NativeMemory.AlignedAlloc(1 << 14, 64);
    private static readonly byte* Destination = (byte*)NativeMemory.AlignedAlloc(1 << 14, 64) + 4;
    private static readonly Vector512<byte> Mask512 = Vector512.Create(0x00FF_FFFF_FFFF_FFFFUL).AsByte();
    private static readonly Vector256<byte> Mask256 = Vector256.Create(0x00FF_FFFF_FFFF_FFFFUL).AsByte();

    [Benchmark(Baseline = true)]
    public int Memmove()
    {
        Buffer.MemoryCopy(Source, Destination, Bytes, Bytes);
        return Bytes;
    }

    [Benchmark]
    public int Aligned512()
    {
        if (!Vector512.IsHardwareAccelerated) return 0;
        // As NinoMask.CopyVectors: an unaligned first vector, the body with aligned stores, an overlapping last one.
        var v = Mask512;
        (Vector512.Load(Source) & v).Store(Destination);
        nuint i = 64 - ((nuint)Destination & 63);
        nuint end = Bytes - 64;
        for (; i + 192 <= end; i += 256)
        {
            var a = Vector512.Load(Source + i) & v;
            var b = Vector512.Load(Source + i + 64) & v;
            var c = Vector512.Load(Source + i + 128) & v;
            var d = Vector512.Load(Source + i + 192) & v;
            a.Store(Destination + i);
            b.Store(Destination + i + 64);
            c.Store(Destination + i + 128);
            d.Store(Destination + i + 192);
        }

        for (; i <= end; i += 64)
            (Vector512.Load(Source + i) & v).Store(Destination + i);
        (Vector512.Load(Source + end) & v).Store(Destination + end);
        return Bytes;
    }

    [Benchmark]
    public int ZeroMaskedLoad512()
    {
        // Aligned512 with the mask applied by the load (vmovdqu8 with zero-masking) instead of a vpand.
        if (!Avx512BW.IsSupported) return 0;
        var v = Mask512;
        var zero = Vector512<byte>.Zero;
        Avx512BW.MaskLoad(Source, v, zero).Store(Destination);
        nuint i = 64 - ((nuint)Destination & 63);
        nuint end = Bytes - 64;
        for (; i + 192 <= end; i += 256)
        {
            var a = Avx512BW.MaskLoad(Source + i, v, zero);
            var b = Avx512BW.MaskLoad(Source + i + 64, v, zero);
            var c = Avx512BW.MaskLoad(Source + i + 128, v, zero);
            var d = Avx512BW.MaskLoad(Source + i + 192, v, zero);
            a.Store(Destination + i);
            b.Store(Destination + i + 64);
            c.Store(Destination + i + 128);
            d.Store(Destination + i + 192);
        }

        for (; i <= end; i += 64)
            Avx512BW.MaskLoad(Source + i, v, zero).Store(Destination + i);
        Avx512BW.MaskLoad(Source + end, v, zero).Store(Destination + end);
        return Bytes;
    }

    [Benchmark]
    public int Unaligned512()
    {
        if (!Vector512.IsHardwareAccelerated) return 0;
        var v = Mask512;
        nuint i = 0, end = Bytes - 64;
        for (; i + 192 <= end; i += 256)
        {
            (Vector512.Load(Source + i) & v).Store(Destination + i);
            (Vector512.Load(Source + i + 64) & v).Store(Destination + i + 64);
            (Vector512.Load(Source + i + 128) & v).Store(Destination + i + 128);
            (Vector512.Load(Source + i + 192) & v).Store(Destination + i + 192);
        }

        for (; i <= end; i += 64)
            (Vector512.Load(Source + i) & v).Store(Destination + i);
        (Vector512.Load(Source + end) & v).Store(Destination + end);
        return Bytes;
    }

    [Benchmark]
    public int Aligned256()
    {
        var v = Mask256;
        (Vector256.Load(Source) & v).Store(Destination);
        nuint i = 32 - ((nuint)Destination & 31);
        nuint end = Bytes - 32;
        for (; i + 96 <= end; i += 128)
        {
            var a = Vector256.Load(Source + i) & v;
            var b = Vector256.Load(Source + i + 32) & v;
            var c = Vector256.Load(Source + i + 64) & v;
            var d = Vector256.Load(Source + i + 96) & v;
            a.Store(Destination + i);
            b.Store(Destination + i + 32);
            c.Store(Destination + i + 64);
            d.Store(Destination + i + 96);
        }

        for (; i <= end; i += 32)
            (Vector256.Load(Source + i) & v).Store(Destination + i);
        (Vector256.Load(Source + end) & v).Store(Destination + end);
        return Bytes;
    }

    [Benchmark]
    public int MemmoveThenMask()
    {
        Buffer.MemoryCopy(Source, Destination, Bytes, Bytes);
        var v = Mask256;
        for (nuint i = 0; i + 32 <= Bytes; i += 32)
            (Vector256.Load(Destination + i) & v).Store(Destination + i);
        return Bytes;
    }
}
