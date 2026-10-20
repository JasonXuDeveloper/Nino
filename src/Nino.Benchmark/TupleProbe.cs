using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using Nino.Core;

namespace Nino.Benchmark;

/// <summary>
/// Experiment: 1,000 (int, long) values (16 bytes in memory, the long first: ValueTuple is auto layout; 12 as
/// blocks, the int first) moved by hand-written kernels, against
/// Nino and MemoryPack end to end and a copy of their memory. Writes pack the blocks, reads unpack them. A kernel the
/// CPU cannot run returns -1 at once.
/// </summary>
[Config(typeof(ProbeConfig))]
public unsafe class TupleProbe
{
    private const int Count = 1000;

    private static readonly List<(int, long)> Tuples = Enumerable.Range(0, Count).Select(i => (i, (long)i * 31 - (1L << 40))).ToList();
    private static readonly byte[] Bytes = NinoSerializer.Serialize(Tuples);
    private static readonly byte[] MemoryPackBytes = MemoryPackSerializer.Serialize(Tuples);

    // Values, blocks, each with room past the end for the kernels' overlapping stores; pinned, 64-byte aligned.
    private readonly byte* _values = (byte*)NativeMemory.AlignedAlloc(Count * 16 + 256, 64);
    private readonly byte* _blocks = (byte*)NativeMemory.AlignedAlloc(Count * 12 + 256, 64) + 4;
    private readonly byte* _copy = (byte*)NativeMemory.AlignedAlloc(Count * 16 + 256, 64) + 4;
    private readonly NinoArrayBufferWriter _nino = new(1 << 16);
    private readonly System.Buffers.ArrayBufferWriter<byte> _mp = new(1 << 16);

    public TupleProbe()
    {
        CollectionsMarshal.AsSpan(Tuples).CopyTo(new Span<(int, long)>(_values, Count));
        WriteScalar();
    }

    [Benchmark] public int NinoWrite() { _nino.ResetWrittenCount(); NinoSerializer.Serialize(Tuples, _nino); return _nino.WrittenCount; }
    [Benchmark] public int MemoryPackWrite() { _mp.ResetWrittenCount(); MemoryPackSerializer.Serialize(_mp, Tuples); return _mp.WrittenCount; }
    [Benchmark] public int NinoRead() => NinoDeserializer.Deserialize<List<(int, long)>>(Bytes).Count;
    [Benchmark] public int MemoryPackRead() => MemoryPackSerializer.Deserialize<List<(int, long)>>(MemoryPackBytes)!.Count;

    /// <summary>The values' memory copied (what MemoryPack writes).</summary>
    [Benchmark]
    public int Memcpy()
    {
        Buffer.MemoryCopy(_values, _copy, Count * 16 + 252, Count * 16);
        return Count;
    }

    /// <summary>Each value as an 8-byte store of the long and a 4-byte one of the int (any CPU, Mono).</summary>
    [Benchmark]
    public int WriteScalar()
    {
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i++, v += 16, b += 12)
        {
            Unsafe.WriteUnaligned(b + 4, Unsafe.ReadUnaligned<long>(v));
            Unsafe.WriteUnaligned(b, Unsafe.ReadUnaligned<int>(v + 8));
        }
        return Count;
    }

    /// <summary>
    /// Two values a 32-byte load, their 4-byte words packed by one vpermd (AVX2), stored whole 24 bytes apart (each
    /// store's last 8 bytes are overwritten by the next).
    /// </summary>
    [Benchmark]
    public int WritePermute()
    {
        if (!Avx2.IsSupported)
            return -1;
        var idx = Vector256.Create(2, 0, 1, 6, 4, 5, 7, 7);
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 8, v += 128, b += 96)
        {
            var x0 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)v), idx);
            var x1 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)(v + 32)), idx);
            var x2 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)(v + 64)), idx);
            var x3 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)(v + 96)), idx);
            Avx.Store((int*)b, x0);
            Avx.Store((int*)(b + 24), x1);
            Avx.Store((int*)(b + 48), x2);
            Avx.Store((int*)(b + 72), x3);
        }
        return Count;
    }

    /// <summary>Eight values as three full 32-byte stores, each two vpermd blended (AVX2).</summary>
    [Benchmark]
    public int WritePermuteFull()
    {
        if (!Avx2.IsSupported)
            return -1;
        var pa = Vector256.Create(2, 0, 1, 6, 4, 5, 0, 0);
        var pb0 = Vector256.Create(0, 0, 0, 0, 0, 0, 2, 0);
        var pb1 = Vector256.Create(1, 6, 4, 5, 0, 0, 0, 0);
        var pc0 = Vector256.Create(0, 0, 0, 0, 2, 0, 1, 6);
        var pc1 = Vector256.Create(4, 5, 0, 0, 0, 0, 0, 0);
        var pd = Vector256.Create(0, 0, 2, 0, 1, 6, 4, 5);
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 8, v += 128, b += 96)
        {
            var x0 = Avx.LoadVector256((int*)v);
            var x1 = Avx.LoadVector256((int*)(v + 32));
            var x2 = Avx.LoadVector256((int*)(v + 64));
            var x3 = Avx.LoadVector256((int*)(v + 96));
            Avx.Store((int*)b, Avx2.Blend(Avx2.PermuteVar8x32(x0, pa), Avx2.PermuteVar8x32(x1, pb0), 0b11000000));
            Avx.Store((int*)(b + 32), Avx2.Blend(Avx2.PermuteVar8x32(x1, pb1), Avx2.PermuteVar8x32(x2, pc0), 0b11110000));
            Avx.Store((int*)(b + 64), Avx2.Blend(Avx2.PermuteVar8x32(x2, pc1), Avx2.PermuteVar8x32(x3, pd), 0b11111100));
        }
        return Count;
    }

    /// <summary>
    /// Two values a 32-byte load, each 16-byte lane packed by vpshufd (in-lane: two ports on Intel since Ice Lake and
    /// on Zen, where vpermd has one), stored as two 16-byte halves 12 bytes apart.
    /// </summary>
    [Benchmark]
    public int WriteLanes()
    {
        if (!Avx2.IsSupported)
            return -1;
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 4, v += 64, b += 48)
        {
            var x0 = Avx2.Shuffle(Avx.LoadVector256((int*)v), 0b11_01_00_10);
            var x1 = Avx2.Shuffle(Avx.LoadVector256((int*)(v + 32)), 0b11_01_00_10);
            Sse2.Store((int*)b, x0.GetLower());
            Sse2.Store((int*)(b + 12), x0.GetUpper());
            Sse2.Store((int*)(b + 24), x1.GetLower());
            Sse2.Store((int*)(b + 36), x1.GetUpper());
        }
        return Count;
    }

    /// <summary>Four values a 4-way deinterleaving load (ld4) and a 3-way interleaving store (st3) of 4-byte words (Arm64).</summary>
    [Benchmark]
    public int WriteZip()
    {
        if (!AdvSimd.Arm64.IsSupported)
            return -1;
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 8, v += 128, b += 96)
        {
            var (l0, h0, a0, _) = AdvSimd.Arm64.Load4xVector128AndUnzip((int*)v);
            var (l1, h1, a1, _) = AdvSimd.Arm64.Load4xVector128AndUnzip((int*)(v + 64));
            AdvSimd.Arm64.StoreVectorAndZip((int*)b, (a0, l0, h0));
            AdvSimd.Arm64.StoreVectorAndZip((int*)(b + 48), (a1, l1, h1));
        }
        return Count;
    }

    /// <summary>Each value as a 4-byte and an 8-byte load and store (any CPU, Mono).</summary>
    [Benchmark]
    public int ReadScalar()
    {
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i++, v += 16, b += 12)
        {
            Unsafe.WriteUnaligned(v + 8, Unsafe.ReadUnaligned<int>(b));
            Unsafe.WriteUnaligned(v, Unsafe.ReadUnaligned<long>(b + 4));
        }
        return Count;
    }

    /// <summary>Two values a 32-byte load 24 bytes apart, unpacked by one vpermd (AVX2), stored whole.</summary>
    [Benchmark]
    public int ReadPermute()
    {
        if (!Avx2.IsSupported)
            return -1;
        var idx = Vector256.Create(1, 2, 0, 0, 4, 5, 3, 3);
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 8, v += 128, b += 96)
        {
            var x0 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)b), idx);
            var x1 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)(b + 24)), idx);
            var x2 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)(b + 48)), idx);
            var x3 = Avx2.PermuteVar8x32(Avx.LoadVector256((int*)(b + 72)), idx);
            Avx.Store((int*)v, x0);
            Avx.Store((int*)(v + 32), x1);
            Avx.Store((int*)(v + 64), x2);
            Avx.Store((int*)(v + 96), x3);
        }
        return Count;
    }

    /// <summary>Four values a 3-way deinterleaving load (ld3) and a 4-way interleaving store (st4) (Arm64).</summary>
    [Benchmark]
    public int ReadZip()
    {
        if (!AdvSimd.Arm64.IsSupported)
            return -1;
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 8, v += 128, b += 96)
        {
            var (a0, l0, h0) = AdvSimd.Arm64.Load3xVector128AndUnzip((int*)b);
            var (a1, l1, h1) = AdvSimd.Arm64.Load3xVector128AndUnzip((int*)(b + 48));
            AdvSimd.Arm64.StoreVectorAndZip((int*)v, (l0, h0, a0, a0));
            AdvSimd.Arm64.StoreVectorAndZip((int*)(v + 64), (l1, h1, a1, a1));
        }
        return Count;
    }

    /// <summary>Checks each kernel against Nino's bytes and the values; prints the ones that differ.</summary>
    public static void Verify()
    {
        var probe = new TupleProbe();
        var expected = Bytes.AsSpan(Bytes.Length - Count * 12).ToArray();
        foreach (var (name, write) in new (string, Func<int>)[]
                 {
                     ("WriteScalar", probe.WriteScalar), ("WritePermute", probe.WritePermute),
                     ("WritePermuteFull", probe.WritePermuteFull), ("WriteLanes", probe.WriteLanes),
                     ("WriteZip", probe.WriteZip)
                 })
        {
            new Span<byte>(probe._blocks, Count * 12).Clear();
            if (write() > 0 && !new Span<byte>(probe._blocks, Count * 12).SequenceEqual(expected))
                Console.WriteLine($"{name} differs: {Convert.ToHexString(new Span<byte>(probe._blocks, 24))} expected {Convert.ToHexString(expected.AsSpan(0, 24))}");
        }

        foreach (var (name, read) in new (string, Func<int>)[]
                 { ("ReadScalar", probe.ReadScalar), ("ReadPermute", probe.ReadPermute), ("ReadZip", probe.ReadZip) })
        {
            expected.CopyTo(new Span<byte>(probe._blocks, Count * 12));
            new Span<byte>(probe._values, Count * 16).Clear();
            if (read() <= 0)
                continue;
            var values = new Span<(int, long)>(probe._values, Count);
            for (int i = 0; i < Count; i++)
                if (values[i] != Tuples[i])
                {
                    Console.WriteLine($"{name} differs at {i}");
                    break;
                }
        }

        Console.WriteLine("verified");
    }
}
