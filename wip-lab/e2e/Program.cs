using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Nino.Core;

public struct S3 { public byte A, B; }
[StructLayout(LayoutKind.Sequential, Pack = 1)] public struct S6 { public int A; public byte B; }
public struct S12 { public float X, Y; }
public struct V3 { public float X, Y, Z; }
public struct S20 { public int A, B, C, D; }
public struct S24 { public long A, B; }
public struct S32 { public long A, B, C; }
public struct S40 { public long A, B, C, D; }
public struct S72 { public long A, B, C, D, E, F, G, H; }
public struct G32 { public int A; public long B, C; }
public struct P8 { public short A, B; }

public static class P
{
    static readonly NinoArrayBufferWriter W = new NinoArrayBufferWriter(1 << 20);
    static int N = 1000;

    static T?[] Make<T>(Func<int, T> f) where T : unmanaged => Enumerable.Range(0, N).Select(i => i % 5 == 0 ? (T?)null : f(i)).ToArray();
    static NinoNullable<T>[] MakeN<T>(Func<int, T> f) where T : unmanaged => Enumerable.Range(0, N).Select(i => i % 5 == 0 ? default : new NinoNullable<T>(f(i))).ToArray();

    // A pinned copy of the array placed so that (other - its data) mod 4096 is near Apart (2048 by default): a load
    // then never has the low 12 address bits of a store just made (4K aliasing), whatever the allocator did.
    static int Apart = 2048;
    static unsafe TE[] Place<TE>(TE[] values, nint other)
    {
        TE[] best = null; int bestDistance = int.MaxValue;
        for (int i = 0; i < 200 && bestDistance > 64; i++)
        {
            var candidate = GC.AllocateUninitializedArray<TE>(values.Length, pinned: true);
            int distance;
            distance = Math.Abs((int)(((other - Address(candidate)) % 4096 + 4096) % 4096) - Apart);
            if (distance < bestDistance) { best = candidate; bestDistance = distance; }
        }
        values.CopyTo(best, 0);
        return best;
    }
    static unsafe nint Address<TE>(TE[] values) => (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(values));
    static unsafe nint Buffer() { W.ResetWrittenCount(); fixed (byte* p = W.GetSpan(16)) return (nint)p + 4; }

    static void Time(string name, Func<int> body)
    {
        for (int r = 0; r < 3; r++) { for (int i = 0; i < 40000; i++) body(); System.Threading.Thread.Sleep(150); }
        double best = double.MaxValue; var all = new List<double>();
        int calls = 20000;
        for (int r = 0; r < Rounds; r++)
        {
            long t = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++) body();
            double ns = Stopwatch.GetElapsedTime(t).TotalNanoseconds / calls;
            all.Add(ns); best = Math.Min(best, ns);
        }
        all.Sort();
        Console.WriteLine($"{name,-28} best {best,8:F1} ns  median {all[all.Count / 2],8:F1} ns");
    }

    static string Filter; static int Rounds = 10;

    static void Case<T>(string name, T?[] a, Func<T?[], int> w, Func<byte[], T?[], int> r, Func<T?[], byte[]> bytes) where T : unmanaged
    {
        if (Filter != null && !name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;
        a = Place(a, Buffer());
        Time("w " + name, () => w(a));
        var d = GC.AllocateUninitializedArray<T?>(N, pinned: true);
        var b = Place(bytes(a), Address(d) - 4);
        Time("r " + name, () => r(b, d));
    }
    static void CaseN<T>(string name, NinoNullable<T>[] a, Func<NinoNullable<T>[], int> w, Func<byte[], NinoNullable<T>[], int> r, Func<NinoNullable<T>[], byte[]> bytes) where T : unmanaged
    {
        if (Filter != null && !name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;
        a = Place(a, Buffer());
        Time("w " + name, () => w(a));
        var d = GC.AllocateUninitializedArray<NinoNullable<T>>(N, pinned: true);
        var b = Place(bytes(a), Address(d) - 4);
        Time("r " + name, () => r(b, d));
    }

    public static void Main(string[] args)
    {
        Filter = args.Length > 0 && args[0] != "-" ? args[0] : null;
        if (args.Length > 1) Rounds = int.Parse(args[1]);
        if (args.Length > 2) N = int.Parse(args[2]);
        if (args.Length > 3) Apart = int.Parse(args[3]);
        Console.WriteLine($"avx512vbmi {System.Runtime.Intrinsics.X86.Avx512Vbmi.IsSupported} avx2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported} gfni {System.Runtime.Intrinsics.X86.Gfni.IsSupported} ssse3 {System.Runtime.Intrinsics.X86.Ssse3.IsSupported} arm {System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported}");

        { var a = Make(i => new S3 { A = (byte)i, B = 1 }); Case("S3?", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new S6 { A = i, B = 1 }); Case("S6?", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new S12 { X = i, Y = 1 }); Case("S12?", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new V3 { X = i, Y = 1 }); Case("V3?(16)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new Guid(i, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)); Case("Guid?(20)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => (decimal)i + 0.5m); Case("decimal?(24)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new S32 { A = i, B = 1 }); Case("S32?", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new G32 { A = i, B = 1 }); Case("G32?", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new S40 { A = i, B = 1 }); Case("S40?", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = Make(i => new S72 { A = i, B = 1 }); Case("S72?", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }

        { var a = MakeN(i => new P8 { A = (short)i, B = 1 }); CaseN("NN<P8>(8>6)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => new S12 { X = i, Y = 1 }); CaseN("NN<S12>(16>12)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => new V3 { X = i, Y = 1 }); CaseN("NN<V3>(24>16)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => new Guid(i, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11)); CaseN("NN<Guid>(32>20)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => (decimal)i + 0.5m); CaseN("NN<decimal>(32>24)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => new S32 { A = i, B = 1 }); CaseN("NN<S32>(48>32)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => new G32 { A = i, B = 1 }); CaseN("NN<G32>(48>32)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => new S40 { A = i, B = 1 }); CaseN("NN<S40>(64>40)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
        { var a = MakeN(i => new S72 { A = i, B = 1 }); CaseN("NN<S72>(128>72)", a, v => { W.ResetWrittenCount(); NinoSerializer.Serialize(v, W); return W.WrittenCount; }, (b, d) => { NinoDeserializer.Deserialize(b, ref d); return d.Length; }, v => NinoSerializer.Serialize(v)); }
    }
}
