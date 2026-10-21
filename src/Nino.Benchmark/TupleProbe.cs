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
    private byte* _values = (byte*)NativeMemory.AlignedAlloc(Count * 16 + 256, 64);
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


    // Arm64 tbl indices for four values: wire vector j from memory vectors j and j + 1 (writes), memory vector k from
    // wire vectors k * 12 / 16 and the next (reads).
    private static readonly Vector128<byte>[] WriteTables = Tables(writes: true), ReadTables = Tables(writes: false);

    private static Vector128<byte>[] Tables(bool writes)
    {
        var tables = new Vector128<byte>[4];
        for (int j = 0; j < 4; j++)
        {
            var idx = new byte[16];
            for (int b = 0; b < 16; b++)
            {
                if (writes)
                {
                    int w = j * 16 + b, v = w / 12, off = w % 12;
                    int memory = v * 16 + (off < 4 ? 8 + off : off - 4);
                    idx[b] = j < 3 ? (byte)(memory - 16 * j) : (byte)0xFF;
                }
                else
                {
                    int lo = j * 12 / 16, off = b < 8 ? 4 + b : b < 12 ? b - 8 : -1;
                    idx[b] = off < 0 ? (byte)0xFF : (byte)(j * 12 + off - 16 * lo);
                }
            }
            tables[j] = Vector128.Create(idx);
        }
        return tables;
    }

    /// <summary>Four values as four loads, three 2-register table lookups (tbl) and three stores (Arm64).</summary>
    [Benchmark]
    public int WriteTbl()
    {
        if (!AdvSimd.Arm64.IsSupported)
            return -1;
        Vector128<byte> t0 = WriteTables[0], t1 = WriteTables[1], t2 = WriteTables[2];
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 4, v += 64, b += 48)
        {
            var (m0, m1) = AdvSimd.Arm64.LoadPairVector128(v);
            var (m2, m3) = AdvSimd.Arm64.LoadPairVector128(v + 32);
            AdvSimd.Arm64.StorePair(b, AdvSimd.Arm64.VectorTableLookup((m0, m1), t0),
                AdvSimd.Arm64.VectorTableLookup((m1, m2), t1));
            AdvSimd.Store(b + 32, AdvSimd.Arm64.VectorTableLookup((m2, m3), t2));
        }
        return Count;
    }

    /// <summary>Four values as three loads, four 2-register table lookups and four stores (Arm64).</summary>
    [Benchmark]
    public int ReadTbl()
    {
        if (!AdvSimd.Arm64.IsSupported)
            return -1;
        Vector128<byte> t0 = ReadTables[0], t1 = ReadTables[1], t2 = ReadTables[2], t3 = ReadTables[3];
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 4, v += 64, b += 48)
        {
            var (w0, w1) = AdvSimd.Arm64.LoadPairVector128(b);
            var w2 = AdvSimd.LoadVector128(b + 32);
            AdvSimd.Arm64.StorePair(v, AdvSimd.Arm64.VectorTableLookup((w0, w1), t0),
                AdvSimd.Arm64.VectorTableLookup((w0, w1), t1));
            AdvSimd.Arm64.StorePair(v + 32, AdvSimd.Arm64.VectorTableLookup((w1, w2), t2),
                AdvSimd.Arm64.VectorTableLookup((w2, w2), t3));
        }
        return Count;
    }


    /// <summary>
    /// Four values as four 16-byte loads, one at each block, and four 1-register table lookups with one index (the
    /// long from bytes 4 to 11, the int from 0 to 3): four lookups of one micro-op each against ReadTbl's (and Nino's)
    /// 2-register ones of two, for one load more (Arm64).
    /// </summary>
    [Benchmark]
    public int ReadTbl1()
    {
        if (!AdvSimd.Arm64.IsSupported)
            return -1;
        var t = Vector128.Create((byte)4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 0x80, 0x80, 0x80, 0x80);
        byte* v = _values, b = _blocks;
        for (int i = 0; i < Count; i += 4, v += 64, b += 48)
        {
            var a = AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(b), t);
            var c = AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(b + 12), t);
            var d = AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(b + 24), t);
            var e = AdvSimd.Arm64.VectorTableLookup(AdvSimd.LoadVector128(b + 36), t);
            AdvSimd.Arm64.StorePair(v, a, c);
            AdvSimd.Arm64.StorePair(v + 32, d, e);
        }
        return Count;
    }

    /// <summary>
    /// A read into a new array: allocated zeroed (as Nino does for elements moved by shuffles) or uninitialized, then
    /// unpacked by the CPU's kernel (tbl on Arm64, vpermd with AVX2, else one value at a time).
    /// </summary>
    [Benchmark]
    public int ReadNewZeroed() => ReadInto(new (int, long)[Count]);

    /// <inheritdoc cref="ReadNewZeroed"/>
    [Benchmark]
    public int ReadNewUninitialized() => ReadInto(GC.AllocateUninitializedArray<(int, long)>(Count));

    private int ReadInto((int, long)[] array)
    {
        fixed ((int, long)* p = array)
        {
            var values = _values;
            try
            {
                _values = (byte*)p;
                if (AdvSimd.Arm64.IsSupported)
                    ReadTbl();
                else if (Avx2.IsSupported)
                    ReadPermute();
                else
                    ReadScalar();
            }
            finally
            {
                _values = values;
            }
        }

        return array.Length;
    }

    /// <summary>What a read allocates: a list of 1,000 values (its array zeroed), its count set.</summary>
    [Benchmark]
    public int AllocList()
    {
        var list = new List<(int, long)>(Count);
        CollectionsMarshal.SetCount(list, Count);
        return list.Count;
    }

    /// <summary>The same with the array uninitialized, (as MemoryPack and Nino allocate arrays they fill whole).</summary>
    [Benchmark]
    public int AllocUninitialized() => GC.AllocateUninitializedArray<(int, long)>(Count).Length;

    /// <summary>Checks each kernel against Nino's bytes and the values; prints the ones that differ.</summary>
    public static void Verify()
    {
        var probe = new TupleProbe();
        var expected = Bytes.AsSpan(Bytes.Length - Count * 12).ToArray();
        foreach (var (name, write) in new (string, Func<int>)[]
                 {
                     ("WriteScalar", probe.WriteScalar), ("WritePermute", probe.WritePermute),
                     ("WritePermuteFull", probe.WritePermuteFull), ("WriteLanes", probe.WriteLanes),
                     ("WriteZip", probe.WriteZip), ("WriteTbl", probe.WriteTbl)
                 })
        {
            new Span<byte>(probe._blocks, Count * 12).Clear();
            if (write() > 0 && !new Span<byte>(probe._blocks, Count * 12).SequenceEqual(expected))
                Console.WriteLine($"{name} differs: {Convert.ToHexString(new Span<byte>(probe._blocks, 24))} expected {Convert.ToHexString(expected.AsSpan(0, 24))}");
        }

        foreach (var (name, read) in new (string, Func<int>)[]
                 { ("ReadScalar", probe.ReadScalar), ("ReadPermute", probe.ReadPermute), ("ReadZip", probe.ReadZip), ("ReadTbl", probe.ReadTbl), ("ReadTbl1", probe.ReadTbl1) })
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
