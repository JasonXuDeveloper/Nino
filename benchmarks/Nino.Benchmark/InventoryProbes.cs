using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Environments;
using Perfolizer.Horology;

namespace Nino.Benchmark;

public class InventoryProbeConfig : ManualConfig
{
    public InventoryProbeConfig() =>
        AddJob(Job.Default.WithRuntime(CoreRuntime.Core10_0).WithLaunchCount(1)
            .WithIterationTime(TimeInterval.FromMilliseconds(200)).WithWarmupCount(3)
            .WithMinIterationCount(6).WithMaxIterationCount(10).WithMaxRelativeError(0.02).WithId("P"));
}

/// <summary>
/// Experiment: the masked copy of SerializeInventory (8,000 bytes of 8-byte structs, the last byte of each cleared; the
/// loop of NinoMask.CopyVectors with 64-byte vectors and aligned stores), the raw copy (NinoUnsafe.Copy) and Memmove,
/// by the source's and the destination's offsets from a 64-byte boundary (a managed array's are set by where it lands,
/// so they change from process to process), 2 KB apart modulo 4 KB.
/// </summary>
[Config(typeof(InventoryProbeConfig))]
public unsafe class InventoryAlignProbe
{
    [Params(0, 8, 16, 24, 32, 40, 48, 56)]
    public int SrcAlign;

    [Params(4, 20)]
    public int DstAlign;

    private const int Bytes = 8000;
    private static readonly byte* Base = (byte*)NativeMemory.AlignedAlloc(1 << 16, 4096);
    private static readonly Vector512<byte> M512 = Vector512.Create(0x00FFFFFFFFFFFFFFUL).AsByte();
    private byte* S => Base + SrcAlign;
    private byte* D => Base + 16384 + 2048 + DstAlign;

    [Benchmark(Baseline = true)]
    public int Masked() { Fwd512(D, S, Bytes, M512); return Bytes; }

    [Benchmark]
    public int Raw() { Nino.Core.CompilerServices.NinoUnsafe.Copy(ref *D, ref *S, Bytes); return Bytes; }

    [Benchmark]
    public int Memmove() { Buffer.MemoryCopy(S, D, Bytes, Bytes); return Bytes; }

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
