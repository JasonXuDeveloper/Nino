using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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
/// Experiment (round 103): what 1,000 (int, long) cost on one CPU, taken apart. The values sit
/// <see cref="SourceShift"/> bytes past a 64-byte line (arrays of them are 8-byte aligned, so every shift that is a
/// multiple of 8 occurs); Nino's blocks and MemoryPack's copy write into reused buffers. Beside them, kernels of the
/// same size that isolate one cost each: 250 64-byte loads of the values (split across lines unless the shift is 0),
/// 188 64-byte stores to a line boundary or 4 bytes past one, 188 independent vpermt2d, and the core clock from
/// chains of 10,000 dependent adds, scalar and on 64-byte vectors (the AVX-512 frequency license).
/// </summary>
[Config(typeof(SingleProbeConfig))]
public unsafe class TupleCostProbe
{
    private const int Count = 1000;

    [Params(0, 8, 16, 24, 32, 40, 48, 56)]
    public int SourceShift;

    private static readonly byte* Base = (byte*)NativeMemory.AlignedAlloc(1 << 16, 64);
    private static readonly byte* Stores = (byte*)NativeMemory.AlignedAlloc(1 << 16, 64);
    private (int, long)[] _values = Array.Empty<(int, long)>();
    private readonly NinoArrayBufferWriter _nino = new(1 << 16);
    private readonly System.Buffers.ArrayBufferWriter<byte> _memoryPack = new(1 << 16);

    static TupleCostProbe()
    {
        // What the C library chose for copies on this machine (Buffer.Memmove calls memmove past 2 KB on x64), and where
        // CollectionsTest's list lands: printed once per process into the job log.
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("/lib64/ld-linux-x86-64.so.2", "--list-diagnostics")
                { RedirectStandardOutput = true };
            using var process = System.Diagnostics.Process.Start(start);
            foreach (var line in process!.StandardOutput.ReadToEnd().Split('\n'))
                if (line.Contains("rep_movsb") || line.Contains("non_temporal") || line.Contains("rep_stosb") ||
                    line.Contains("cpu_features.basic") || line.Contains("preferred"))
                    Console.WriteLine("// probe glibc " + line);
        }
        catch (Exception e)
        {
            Console.WriteLine("// probe glibc diagnostics: " + e.Message);
        }

        var list = new System.Collections.Generic.List<(int, long)>(Count);
        for (int i = 0; i < Count; i++)
            list.Add((i, i * 31L));
        fixed ((int, long)* at = CollectionsMarshal.AsSpan(list))
            Console.WriteLine($"// probe a new list's values at {(nuint)at % 64} past a line; X86Base " +
                              $"{System.Runtime.Intrinsics.X86.X86Base.CpuId(1, 0).Eax:X}");
    }

    [GlobalSetup]
    public void Setup()
    {
        // Pinned arrays until one starts SourceShift bytes past a line.
        var keep = new System.Collections.Generic.List<(int, long)[]>();
        while (true)
        {
            var values = GC.AllocateUninitializedArray<(int, long)>(Count, pinned: true);
            if ((nuint)Unsafe.AsPointer(ref values[0]) % 64 == (nuint)SourceShift)
            {
                _values = values;
                break;
            }

            keep.Add(values);
        }

        for (int i = 0; i < Count; i++)
        {
            _values[i] = (i, i * 31L);
            ((long*)Base)[i * 2] = i;
            ((long*)Base)[i * 2 + 1] = i * 31L;
        }
    }

    [Benchmark(Baseline = true)]
    public int Nino()
    {
        _nino.ResetWrittenCount();
        NinoSerializer.Serialize(_values, _nino);
        return _nino.WrittenCount;
    }

    [Benchmark]
    public int MemoryPack()
    {
        _memoryPack.ResetWrittenCount();
        global::MemoryPack.MemoryPackSerializer.Serialize(_memoryPack, _values);
        return _memoryPack.WrittenCount;
    }

    /// <summary>250 64-byte loads of the values, as Nino's groups load them (split across lines unless shift 0).</summary>
    [Benchmark]
    public int Loads()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        byte* s = Base + SourceShift;
        Vector512<byte> a = default, b = default;
        for (int k = 0; k < 248; k += 2)
        {
            a |= Vector512.Load(s + k * 64);
            b |= Vector512.Load(s + k * 64 + 64);
        }

        return (a | b).GetElement(0);
    }

    /// <summary>188 64-byte stores to a line boundary (shift 0) or <see cref="SourceShift"/> past one.</summary>
    [Benchmark]
    public int Stores64()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        byte* d = Stores + SourceShift;
        var v = Vector512.Create((byte)SourceShift);
        for (int k = 0; k < 188; k += 2)
        {
            v.Store(d + k * 64);
            v.Store(d + k * 64 + 64);
        }

        return 188;
    }

    /// <summary>188 vpermt2d of 64 bytes (independent: throughput, port 5 on Intel).</summary>
    [Benchmark]
    public int Permutes()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        var x = Vector512.Create(SourceShift);
        var y = Vector512.Create(3);
        var i0 = Vector512.Create(0, 17, 2, 19, 4, 21, 6, 23, 8, 25, 10, 27, 12, 29, 14, 31);
        Vector512<int> a = x, b = y, c = x, d = y;
        for (int k = 0; k < 188; k += 4)
        {
            a = System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(x, i0, a);
            b = System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(y, i0, b);
            c = System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(x, i0, c);
            d = System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(y, i0, d);
        }

        return (a + b + c + d).GetElement(1);
    }

    /// <summary>10,000 dependent scalar adds: 10,000 cycles, so 10,000 / ns is the core clock in GHz.</summary>
    [Benchmark]
    public long ClockScalar()
    {
        long x = SourceShift;
        for (int k = 0; k < 2500; k++)
        {
            x += k;
            x ^= 1;
            x += 3;
            x ^= 2;
        }

        return x;
    }

    /// <summary>ClockScalar on 64-byte vectors (vpaddd zmm, latency 1): the clock under AVX-512.</summary>
    [Benchmark]
    public int ClockZmm()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        var x = Vector512.Create(SourceShift);
        var one = Vector512.Create(1);
        for (int k = 0; k < 2500; k++)
        {
            x += one;
            x ^= one;
            x += one;
            x ^= one;
        }

        return x.GetElement(0);
    }
}
