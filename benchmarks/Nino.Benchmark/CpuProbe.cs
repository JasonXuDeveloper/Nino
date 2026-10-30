using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
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
/// Experiment: 1,000 (int, long) written as 12-byte blocks (List&lt;(int, long)&gt;, TupleList), Nino and MemoryPack
/// against the kernels Nino could use: groups of 16 values as three two-source 4-byte-word permutes of 64-byte
/// vectors (vpermt2d zmm, Nino's AVX-512 groups), groups of 8 as three of 32-byte vectors (vpermt2d ymm, AVX-512VL),
/// and two values a step by one one-source word permute of a 32-byte vector (vpermd, AVX2) stored over the next
/// step's first bytes. Stores from the first value whose blocks start on a vector boundary, as Nino's groups; the
/// source <see cref="SourceShift"/> bytes past a 64-byte boundary (a list's array: elements 16 bytes into an 8-byte
/// aligned object), the destination 4 past one (after the length).
/// </summary>
[Config(typeof(ProbeConfig))]
public unsafe class TuplePackProbe
{
    private const int Count = 1000;

    [Params(8, 40)]
    public int SourceShift;

    private static readonly List<(int, long)> List = Enumerable.Range(0, Count).Select(i => (i, (long)i * 31)).ToList();
    private static readonly byte* SourceBase = (byte*)NativeMemory.AlignedAlloc(1 << 15, 64);
    private static readonly byte* Destination = (byte*)NativeMemory.AlignedAlloc(1 << 15, 64) + 4;
    private static readonly Vector512<int> Words512A = Indices512(0), Words512B = Indices512(1), Words512C = Indices512(2);
    private static readonly Vector256<int> Words256A = Indices256(0), Words256B = Indices256(1), Words256C = Indices256(2);
    private static readonly Vector256<int> Pack2 = Vector256.Create(0, 2, 3, 4, 6, 7, 0, 0);
    private readonly NinoArrayBufferWriter _nino = new(1 << 16);
    private readonly ArrayBufferWriter<byte> _memoryPack = new(1 << 16);

    private byte* Source => SourceBase + SourceShift;

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < Count; i++)
        {
            *(int*)(Source + i * 16) = i;
            *(int*)(Source + i * 16 + 4) = 0;
            *(long*)(Source + i * 16 + 8) = i * 31L;
        }

        // Each kernel writes Nino's blocks (after its 4-byte length).
        var expected = NinoSerializer.Serialize(List).AsSpan(4);
        foreach (var kernel in new Func<int>[] { Words512, Words256, Pairs256, Pairs256x2 })
        {
            new Span<byte>(Destination, 12000).Clear();
            if (kernel() != 0 && !new ReadOnlySpan<byte>(Destination, 12000).SequenceEqual(expected))
                throw new InvalidOperationException(kernel.Method.Name + " wrote other bytes");
        }
    }

    // Block word w of a group holds memory word w / 3 * 4 + (0, 2, 3)[w % 3]; vector k reads memory vectors k and k + 1.
    private static Vector512<int> Indices512(int k)
    {
        var x = new int[16];
        for (int b = 0; b < 16; b++)
        {
            int w = 16 * k + b;
            x[b] = w / 3 * 4 + (w % 3 == 0 ? 0 : w % 3 + 1) - 16 * k;
        }

        return Vector512.Create(x);
    }

    private static Vector256<int> Indices256(int k)
    {
        var x = new int[8];
        for (int b = 0; b < 8; b++)
        {
            int w = 8 * k + b;
            x[b] = w / 3 * 4 + (w % 3 == 0 ? 0 : w % 3 + 1) - 8 * k;
        }

        return Vector256.Create(x);
    }

    [Benchmark(Baseline = true)]
    public int Nino()
    {
        _nino.ResetWrittenCount();
        NinoSerializer.Serialize(List, _nino);
        return _nino.WrittenCount;
    }

    [Benchmark]
    public int MemoryPack()
    {
        _memoryPack.ResetWrittenCount();
        global::MemoryPack.MemoryPackSerializer.Serialize(_memoryPack, List);
        return _memoryPack.WrittenCount;
    }

    [Benchmark]
    public int Memmove16K()
    {
        Buffer.MemoryCopy(Source, Destination, 16000, 16000);
        return 16000;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Group512(byte* s, byte* d, Vector512<int> ia, Vector512<int> ib, Vector512<int> ic)
    {
        var a = Vector512.Load((int*)s);
        var b = Vector512.Load((int*)(s + 64));
        var c = Vector512.Load((int*)(s + 128));
        var e = Vector512.Load((int*)(s + 192));
        Avx512F.PermuteVar16x32x2(a, ia, b).Store((int*)d);
        Avx512F.PermuteVar16x32x2(b, ib, c).Store((int*)(d + 64));
        Avx512F.PermuteVar16x32x2(c, ic, e).Store((int*)(d + 128));
    }

    [Benchmark]
    public int Words512()
    {
        if (!Avx512F.IsSupported) return 0;
        byte* s = Source, d = Destination;
        Vector512<int> ia = Words512A, ib = Words512B, ic = Words512C;
        Group512(s, d, ia, ib, ic);
        int i = 5; // (4 + 12 * 5) % 64 == 0
        for (; i + 16 <= Count; i += 16)
            Group512(s + i * 16, d + i * 12, ia, ib, ic);
        Group512(s + (Count - 16) * 16, d + (Count - 16) * 12, ia, ib, ic);
        return Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Group256(byte* s, byte* d, Vector256<int> ia, Vector256<int> ib, Vector256<int> ic)
    {
        var a = Vector256.Load((int*)s);
        var b = Vector256.Load((int*)(s + 32));
        var c = Vector256.Load((int*)(s + 64));
        var e = Vector256.Load((int*)(s + 96));
        Avx512F.VL.PermuteVar8x32x2(a, ia, b).Store((int*)d);
        Avx512F.VL.PermuteVar8x32x2(b, ib, c).Store((int*)(d + 32));
        Avx512F.VL.PermuteVar8x32x2(c, ic, e).Store((int*)(d + 64));
    }

    [Benchmark]
    public int Words256()
    {
        if (!Avx512F.VL.IsSupported) return 0;
        byte* s = Source, d = Destination;
        Vector256<int> ia = Words256A, ib = Words256B, ic = Words256C;
        Group256(s, d, ia, ib, ic);
        int i = 5; // (4 + 12 * 5) % 32 == 0
        for (; i + 8 <= Count; i += 8)
            Group256(s + i * 16, d + i * 12, ia, ib, ic);
        Group256(s + (Count - 8) * 16, d + (Count - 8) * 12, ia, ib, ic);
        return Count;
    }

    [Benchmark]
    public int Pairs256()
    {
        if (!Avx2.IsSupported) return 0;
        byte* s = Source, d = Destination;
        var p = Pack2;
        // Two values a step, each store 8 bytes past their blocks, rewritten by the next; the last pair by itself.
        int n = (Count - 2) / 2;
        for (int k = 0; k < n; k++)
        {
            Avx2.PermuteVar8x32(Vector256.Load((int*)s), p).Store((int*)d);
            s += 32;
            d += 24;
        }

        var last = Avx2.PermuteVar8x32(Vector256.Load((int*)(Source + (Count - 2) * 16)), p);
        Vector128.Store(last.GetLower(), (int*)(Destination + (Count - 2) * 12));
        Unsafe.WriteUnaligned(Destination + (Count - 2) * 12 + 16, last.AsInt64().GetElement(2));
        return Count;
    }

    [Benchmark]
    public int Pairs256x2()
    {
        if (!Avx2.IsSupported) return 0;
        byte* s = Source, d = Destination;
        var p = Pack2;
        // Pairs256 with four values a step: two loads, two permutes, two stores.
        int n = (Count - 4) / 4;
        for (int k = 0; k < n; k++)
        {
            var a = Avx2.PermuteVar8x32(Vector256.Load((int*)s), p);
            var b = Avx2.PermuteVar8x32(Vector256.Load((int*)(s + 32)), p);
            a.Store((int*)d);
            b.Store((int*)(d + 24));
            s += 64;
            d += 48;
        }

        for (int i = n * 4; i < Count - 2; i += 2)
            Avx2.PermuteVar8x32(Vector256.Load((int*)(Source + i * 16)), p).Store((int*)(Destination + i * 12));
        var last = Avx2.PermuteVar8x32(Vector256.Load((int*)(Source + (Count - 2) * 16)), p);
        Vector128.Store(last.GetLower(), (int*)(Destination + (Count - 2) * 12));
        Unsafe.WriteUnaligned(Destination + (Count - 2) * 12 + 16, last.AsInt64().GetElement(2));
        return Count;
    }
}

/// <summary>
/// Experiment: 1,000 <see cref="Waypoint"/>s (24 bytes in memory, 15-byte blocks: bytes 0 to 13 and 16) written as
/// blocks, Nino and MemoryPack against two-source byte permutes: four values a step from 128 bytes of memory into 64
/// of blocks (vpermt2b zmm, Nino's pair permutes), two a step from 64 into 32 (vpermt2b ymm, AVX-512 VBMI with VL), and
/// that twice a step. For Ice Lake, where the 64-byte permute lost to 32-byte lane groups.
/// </summary>
[Config(typeof(ProbeConfig))]
public unsafe class WaypointPackProbe
{
    private const int Count = 1000, Size = 15, Memory = 24;
    private static readonly Waypoint[] Values = Enumerable.Range(0, Count)
        .Select(i => new Waypoint { X = i * 0.5f, Y = -i, Stamp = (i * 16, (short)(i % 7)), Visited = i % 4 == 0 }).ToArray();
    private static readonly byte* Source = (byte*)NativeMemory.AlignedAlloc(1 << 15, 64) + 16;
    private static readonly byte* Destination = (byte*)NativeMemory.AlignedAlloc(1 << 15, 64) + 4;
    private static readonly Vector512<byte> Four = Indices512();
    private static readonly Vector256<byte> Two = Indices256();
    private readonly NinoArrayBufferWriter _nino = new(1 << 16);
    private readonly ArrayBufferWriter<byte> _memoryPack = new(1 << 16);

    private static int MemoryAt(int wire) => wire / Size * Memory + (wire % Size < 14 ? wire % Size : 16);

    private static Vector512<byte> Indices512()
    {
        var x = new byte[64];
        for (int b = 0; b < 64; b++)
            x[b] = (byte)(b < 4 * Size ? MemoryAt(b) : 0);
        return Vector512.Create(x);
    }

    private static Vector256<byte> Indices256()
    {
        var x = new byte[32];
        for (int b = 0; b < 32; b++)
            x[b] = (byte)(b < 2 * Size ? MemoryAt(b) : 0);
        return Vector256.Create(x);
    }

    [GlobalSetup]
    public void Setup()
    {
        if (Unsafe.SizeOf<Waypoint>() != Memory)
            throw new InvalidOperationException("Waypoint is " + Unsafe.SizeOf<Waypoint>() + " bytes in memory");
        fixed (Waypoint* p = Values)
            Buffer.MemoryCopy(p, Source, Count * Memory, Count * Memory);
        var expected = NinoSerializer.Serialize(Values).AsSpan(4);
        foreach (var kernel in new Func<int>[] { Four512, Two256, Two256x2 })
        {
            new Span<byte>(Destination, Count * Size).Clear();
            if (kernel() != 0 && !new ReadOnlySpan<byte>(Destination, Count * Size).SequenceEqual(expected))
                throw new InvalidOperationException(kernel.Method.Name + " wrote other bytes");
        }
    }

    [Benchmark(Baseline = true)]
    public int Nino()
    {
        _nino.ResetWrittenCount();
        NinoSerializer.Serialize(Values, _nino);
        return _nino.WrittenCount;
    }

    [Benchmark]
    public int MemoryPack()
    {
        _memoryPack.ResetWrittenCount();
        global::MemoryPack.MemoryPackSerializer.Serialize(_memoryPack, Values);
        return _memoryPack.WrittenCount;
    }

    // Cursors bumped a step at a time and a count down, as Nino's loops; the values after the steps one at a time.
    [Benchmark]
    public int Four512()
    {
        if (!Avx512Vbmi.IsSupported) return 0;
        var p = Four;
        byte* s = Source, d = Destination;
        int n = (Count - 6) / 4; // 128 bytes of loads and 64 of stores stay inside the values and blocks
        for (int k = n; k != 0; k--)
        {
            Avx512Vbmi.PermuteVar64x8x2(Vector512.Load(s), p, Vector512.Load(s + 64)).Store(d);
            s += 4 * Memory;
            d += 4 * Size;
        }

        for (int i = n * 4; i < Count; i++)
            WriteOne(Source + i * Memory, Destination + i * Size);
        return Count;
    }

    [Benchmark]
    public int Two256()
    {
        if (!Avx512Vbmi.VL.IsSupported) return 0;
        var p = Two;
        byte* s = Source, d = Destination;
        int n = (Count - 3) / 2;
        for (int k = n; k != 0; k--)
        {
            Avx512Vbmi.VL.PermuteVar32x8x2(Vector256.Load(s), p, Vector256.Load(s + 32)).Store(d);
            s += 2 * Memory;
            d += 2 * Size;
        }

        for (int i = n * 2; i < Count; i++)
            WriteOne(Source + i * Memory, Destination + i * Size);
        return Count;
    }

    [Benchmark]
    public int Two256x2()
    {
        if (!Avx512Vbmi.VL.IsSupported) return 0;
        var p = Two;
        byte* s = Source, d = Destination;
        int n = (Count - 5) / 4;
        for (int k = n; k != 0; k--)
        {
            var a = Avx512Vbmi.VL.PermuteVar32x8x2(Vector256.Load(s), p, Vector256.Load(s + 32));
            var b = Avx512Vbmi.VL.PermuteVar32x8x2(Vector256.Load(s + 48), p, Vector256.Load(s + 80));
            a.Store(d);
            b.Store(d + 2 * Size);
            s += 4 * Memory;
            d += 4 * Size;
        }

        for (int i = n * 4; i < Count; i++)
            WriteOne(Source + i * Memory, Destination + i * Size);
        return Count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteOne(byte* s, byte* d)
    {
        Buffer.MemoryCopy(s, d, 14, 14);
        d[14] = s[16];
    }
}
