using System;
using System.Buffers;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using Nino.Core;

namespace Nino.Benchmark;

/// <summary>
/// Experiment: SerializeInventory as the suite runs it, with the addresses its process landed on printed ("// ADDR")
/// for the log; and its copies alone at those addresses.
/// </summary>
[Config(typeof(InventoryProbeConfig))]
public unsafe class InventoryRealProbe
{
    private static readonly InventoryItem[] Inventory = Enumerable.Range(0, 1000)
        .Select(i => new InventoryItem { ItemId = i, Count = (short)(i % 100), Bound = i % 3 == 0 }).ToArray();
    private NinoArrayBufferWriter _nino;
    private ArrayBufferWriter<byte> _mp;
    private static readonly Vector512<byte> M512 = Vector512.Create(0x00FFFFFFFFFFFFFFUL).AsByte();

    [GlobalSetup]
    public void Setup()
    {
        _nino = new NinoArrayBufferWriter(1 << 20);
        _mp = new ArrayBufferWriter<byte>(1 << 20);
        fixed (byte* d = _nino.GetSpan(8004))
        fixed (byte* m = _mp.GetSpan(8004))
        fixed (InventoryItem* s = Inventory)
            Console.WriteLine($"// ADDR nino d-s mod 4K {((nuint)(d + 4) - (nuint)s) & 4095}, mp {((nuint)(m + 4) - (nuint)s) & 4095}, s&63 {(nuint)s & 63}, d&63 {(nuint)d & 63}, m&63 {(nuint)m & 63}");
    }

    [Benchmark(Baseline = true)]
    public int Nino() { _nino.ResetWrittenCount(); NinoSerializer.Serialize(Inventory, _nino); return _nino.WrittenCount; }

    [Benchmark]
    public int MemoryPack() { _mp.ResetWrittenCount(); MemoryPackSerializer.Serialize(_mp, Inventory); return _mp.WrittenCount; }

    [Benchmark]
    public int KernelAtNino()
    {
        fixed (byte* d = _nino.GetSpan(8004))
        fixed (InventoryItem* s = Inventory)
            Fwd512(d + 4, (byte*)s, 8000, M512);
        return 8000;
    }

    [Benchmark]
    public int MemmoveAtNino()
    {
        fixed (byte* d = _nino.GetSpan(8004))
        fixed (InventoryItem* s = Inventory)
            Buffer.MemoryCopy(s, d + 4, 8000, 8000);
        return 8000;
    }

    [Benchmark]
    public int MemmoveAtMp()
    {
        fixed (byte* d = _mp.GetSpan(8004))
        fixed (InventoryItem* s = Inventory)
            Buffer.MemoryCopy(s, d + 4, 8000, 8000);
        return 8000;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void Fwd512(byte* d, byte* s, nuint size, Vector512<byte> v)
    {
        (Vector512.Load(s) & v).Store(d);
        nuint i = 64 - ((nuint)d & 63), end = size - 64;
        for (; i + 192 <= end; i += 256)
        {
            var a = Vector512.Load(s + i) & v; var b = Vector512.Load(s + i + 64) & v;
            var c = Vector512.Load(s + i + 128) & v; var e = Vector512.Load(s + i + 192) & v;
            a.Store(d + i); b.Store(d + i + 64); c.Store(d + i + 128); e.Store(d + i + 192);
        }
        for (; i <= end; i += 64) (Vector512.Load(s + i) & v).Store(d + i);
        (Vector512.Load(s + end) & v).Store(d + end);
    }
}
