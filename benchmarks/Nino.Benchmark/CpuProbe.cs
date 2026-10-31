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

/// <summary>
/// Experiment (round 104): the (int, long) write groups of Ice Lake (NinoBlockCollections.WriteAligned: memory loaded
/// from 64-byte lines, one vpermt2d per vector of blocks, two when its bytes span three loads) on 64-byte vectors
/// against the same on 32-byte ones (vpermt2d ymm, loads from 32-byte boundaries, twice as many stores and more
/// permutes, but no 512-bit instruction for the AVX-512 frequency license to slow), over 992 values
/// <see cref="SourceShift"/> bytes past a line into a 64-byte aligned buffer.
/// </summary>
[Config(typeof(SingleProbeConfig))]
public unsafe class TupleGroupProbe
{
    private const int Groups = 62; // 16 values each

    [Params(0, 8, 16, 24, 32, 40, 48, 56)]
    public int SourceShift;

    private static readonly byte* Source = (byte*)NativeMemory.AlignedAlloc(1 << 16, 64);
    private static readonly byte* Destination = (byte*)NativeMemory.AlignedAlloc(1 << 16, 64);

    // Per vector of blocks: the first aligned load (in vectors from the group's aligned start), the permute of it and
    // the next, and when its dwords reach a third load, the permute that brings those in (-1: none).
    private Vector512<int>[] _zFirst = new Vector512<int>[3], _zSecond = new Vector512<int>[3];
    private int[] _zAt = new int[3];
    private bool[] _zThree = new bool[3];
    private Vector256<int>[] _yFirst = new Vector256<int>[6], _ySecond = new Vector256<int>[6];
    private int[] _yAt = new int[6];
    private bool[] _yThree = new bool[6];

    [GlobalSetup]
    public void Setup()
    {
        for (int i = 0; i < Groups * 16 + 64; i++)
        {
            ((long*)(Source + SourceShift))[i * 2] = i;
            ((long*)(Source + SourceShift))[i * 2 + 1] = i * 31L;
        }

        Plan(16, _zAt, _zThree, (o, a, b) => { _zFirst[o] = Vector512.Create(a); _zSecond[o] = Vector512.Create(b); });
        Plan(8, _yAt, _yThree, (o, a, b) => { _yFirst[o] = Vector256.Create(a); _ySecond[o] = Vector256.Create(b); });
        // The kernels must write Nino's bytes: 4 of the int, 8 of the long per value.
        if (!System.Runtime.Intrinsics.X86.Avx512F.VL.IsSupported) return;
        foreach (var kernel in new Action[] { () => Wide(), () => Narrow() })
        {
            new Span<byte>(Destination, 192 * Groups).Clear();
            kernel();
            for (int v = 0; v < Groups * 16; v++)
                if (*(int*)(Destination + v * 12) != v || *(long*)(Destination + v * 12 + 4) != v * 31L)
                    throw new InvalidOperationException($"value {v} wrong at shift {SourceShift}");
        }
    }

    /// <summary>Tables per vector of <paramref name="width"/> dwords: output dword j holds memory dword m(j).</summary>
    private void Plan(int width, int[] at, bool[] three, Action<int, int[], int[]> set)
    {
        int s = SourceShift / 4;
        for (int o = 0; o < 48 / width; o++)
        {
            var d = new int[width];
            for (int k = 0; k < width; k++)
            {
                int j = o * width + k, v = j / 3, p = j % 3;
                d[k] = v * 4 + (p == 0 ? 0 : p + 1) + s;
            }

            int lo = System.Linq.Enumerable.Min(d), hi = System.Linq.Enumerable.Max(d), a = lo / width;
            at[o] = a;
            three[o] = hi >= (a + 2) * width;
            var first = new int[width];
            var second = new int[width];
            for (int k = 0; k < width; k++)
            {
                int r = d[k] - a * width;
                first[k] = r < 2 * width ? r : 0;
                second[k] = r < 2 * width ? k : width + r - 2 * width;
            }

            set(o, first, second);
        }
    }

    [Benchmark(Baseline = true)]
    public int Wide()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        byte* from = Source, to = Destination;
        var f0 = _zFirst[0]; var f1 = _zFirst[1]; var f2 = _zFirst[2];
        var s0 = _zSecond[0]; var s1 = _zSecond[1]; var s2 = _zSecond[2];
        nint a0 = _zAt[0] * 64, a1 = _zAt[1] * 64, a2 = _zAt[2] * 64;
        bool t0 = _zThree[0], t1 = _zThree[1], t2 = _zThree[2];
        for (int g = 0; g < Groups; g++)
        {
            Z(from + a0, f0, s0, t0).Store((int*)to);
            Z(from + a1, f1, s1, t1).Store((int*)(to + 64));
            Z(from + a2, f2, s2, t2).Store((int*)(to + 128));
            from += 256;
            to += 192;
        }

        return Groups;
    }

    [Benchmark]
    public int Narrow()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.VL.IsSupported) return 0;
        byte* from = Source, to = Destination;
        var f0 = _yFirst[0]; var f1 = _yFirst[1]; var f2 = _yFirst[2];
        var f3 = _yFirst[3]; var f4 = _yFirst[4]; var f5 = _yFirst[5];
        var s0 = _ySecond[0]; var s1 = _ySecond[1]; var s2 = _ySecond[2];
        var s3 = _ySecond[3]; var s4 = _ySecond[4]; var s5 = _ySecond[5];
        nint a0 = _yAt[0] * 32, a1 = _yAt[1] * 32, a2 = _yAt[2] * 32, a3 = _yAt[3] * 32, a4 = _yAt[4] * 32,
            a5 = _yAt[5] * 32;
        bool t0 = _yThree[0], t1 = _yThree[1], t2 = _yThree[2], t3 = _yThree[3], t4 = _yThree[4], t5 = _yThree[5];
        for (int g = 0; g < Groups; g++)
        {
            Y(from + a0, f0, s0, t0).Store((int*)to);
            Y(from + a1, f1, s1, t1).Store((int*)(to + 32));
            Y(from + a2, f2, s2, t2).Store((int*)(to + 64));
            Y(from + a3, f3, s3, t3).Store((int*)(to + 96));
            Y(from + a4, f4, s4, t4).Store((int*)(to + 128));
            Y(from + a5, f5, s5, t5).Store((int*)(to + 160));
            from += 256;
            to += 192;
        }

        return Groups;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<int> Z(byte* at, Vector512<int> first, Vector512<int> second, bool three)
    {
        var v = System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(Vector512.Load((int*)at), first,
            Vector512.Load((int*)(at + 64)));
        if (three)
            v = System.Runtime.Intrinsics.X86.Avx512F.PermuteVar16x32x2(v, second, Vector512.Load((int*)(at + 128)));
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Y(byte* at, Vector256<int> first, Vector256<int> second, bool three)
    {
        var v = System.Runtime.Intrinsics.X86.Avx512F.VL.PermuteVar8x32x2(Vector256.Load((int*)at), first,
            Vector256.Load((int*)(at + 32)));
        if (three)
            v = System.Runtime.Intrinsics.X86.Avx512F.VL.PermuteVar8x32x2(v, second, Vector256.Load((int*)(at + 64)));
        return v;
    }
}

/// <summary>
/// Experiment (round 104): the cost of writing 12,000 bytes (1,000 (int, long) blocks) on one CPU, whatever produces
/// them: 188 aligned 64-byte copies (loads and stores, no permutes), 376 aligned 32-byte copies, one rep movsb
/// (glibc's memmove past 2,112 bytes with FSRM, source and destination alike in a line), stores alone (64 and
/// 32 bytes, and non-temporal 64-byte ones with the fence), and the core clock on scalar, 32- and 64-byte vector code.
/// </summary>
[Config(typeof(SingleProbeConfig))]
public unsafe class StorePathProbe
{
    private const int Bytes = 188 * 64;
    private static readonly byte* Source = (byte*)NativeMemory.AlignedAlloc(1 << 16, 64);
    private static readonly byte* Destination = (byte*)NativeMemory.AlignedAlloc(1 << 16, 64);

    [Benchmark(Baseline = true)]
    public int Copy64()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        for (int k = 0; k < Bytes; k += 128)
        {
            var a = Vector512.Load(Source + k);
            var b = Vector512.Load(Source + k + 64);
            a.Store(Destination + k);
            b.Store(Destination + k + 64);
        }

        return Bytes;
    }

    [Benchmark]
    public int Copy32()
    {
        for (int k = 0; k < Bytes; k += 64)
        {
            var a = Vector256.Load(Source + k);
            var b = Vector256.Load(Source + k + 32);
            a.Store(Destination + k);
            b.Store(Destination + k + 32);
        }

        return Bytes;
    }

    [Benchmark]
    public int RepMovsb()
    {
        Buffer.MemoryCopy(Source, Destination, Bytes, Bytes);
        return Bytes;
    }

    [Benchmark]
    public int Stores64()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        var v = Vector512.Create((byte)1);
        for (int k = 0; k < Bytes; k += 128)
        {
            v.Store(Destination + k);
            v.Store(Destination + k + 64);
        }

        return Bytes;
    }

    [Benchmark]
    public int Stores32()
    {
        var v = Vector256.Create((byte)1);
        for (int k = 0; k < Bytes; k += 64)
        {
            v.Store(Destination + k);
            v.Store(Destination + k + 32);
        }

        return Bytes;
    }

    [Benchmark]
    public int StoresNonTemporal()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        var v = Vector512.Create((byte)1);
        for (int k = 0; k < Bytes; k += 128)
        {
            System.Runtime.Intrinsics.X86.Avx512F.StoreAlignedNonTemporal(Destination + k, v);
            System.Runtime.Intrinsics.X86.Avx512F.StoreAlignedNonTemporal(Destination + k + 64, v);
        }

        System.Runtime.Intrinsics.X86.Sse.StoreFence();
        return Bytes;
    }

    /// <summary>10,000 dependent scalar adds: 10,000 cycles, so 10,000 / ns is the core clock in GHz.</summary>
    [Benchmark]
    public long ClockScalar()
    {
        long x = Bytes;
        for (int k = 0; k < 2500; k++)
        {
            x += k;
            x ^= 1;
            x += 3;
            x ^= 2;
        }

        return x;
    }

    /// <summary>ClockScalar on 32-byte vectors (vpaddd ymm, latency 1).</summary>
    [Benchmark]
    public int ClockYmm()
    {
        var x = Vector256.Create(Bytes);
        var one = Vector256.Create(1);
        for (int k = 0; k < 2500; k++)
        {
            x += one;
            x ^= one;
            x += one;
            x ^= one;
        }

        return x.GetElement(0);
    }

    /// <summary>ClockScalar on 64-byte vectors (vpaddd zmm, latency 1): the clock under AVX-512.</summary>
    [Benchmark]
    public int ClockZmm()
    {
        if (!System.Runtime.Intrinsics.X86.Avx512F.IsSupported) return 0;
        var x = Vector512.Create(Bytes);
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
