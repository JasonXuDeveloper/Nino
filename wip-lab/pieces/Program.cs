using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

// Kernel harness for the piece loops: STRIDE from the environment so it folds as a static readonly does in Nino.
public static unsafe class K
{
    static readonly int Stride = int.Parse(Environment.GetEnvironmentVariable("STRIDE") ?? "72");
    static readonly int Offset = int.Parse(Environment.GetEnvironmentVariable("OFFSET") ?? "8");
    static readonly Vector256<byte> Keep0 = MakeKeep();
    static Vector256<byte> MakeKeep() { var b = new byte[32]; for (int i = 0; i < 32; i++) b[i] = i == 0 || (i >= Offset && i < Stride) ? (byte)0xFF : (byte)0; return Vector256.Create(b); }

    // V0: the inner loop over pieces, the first vector stored last (as written first)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint V0(ref byte destination, ref byte source, nuint total)
    {
        nuint width = 32, stride = (nuint)Stride; var table = Keep0; nuint i = 0;
        for (; i + stride <= total; i += stride)
        {
            var first = Vector256.LoadUnsafe(ref source, i);
            var present = Avx2.BroadcastScalarToVector256(first.GetLower());
            if (stride > width)
            {
                for (nuint at = width; at + width < stride; at += width)
                    Gfni.V256.GaloisFieldMultiply(Vector256.LoadUnsafe(ref source, i + at), present).StoreUnsafe(ref destination, i + at);
                nuint last = i + stride - width;
                Gfni.V256.GaloisFieldMultiply(Vector256.LoadUnsafe(ref source, last), present).StoreUnsafe(ref destination, last);
            }
            Gfni.V256.GaloisFieldMultiply(first, present & table).StoreUnsafe(ref destination, i);
        }
        return i;
    }

    // V1: stores in address order (first vector first)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint V1(ref byte destination, ref byte source, nuint total)
    {
        nuint width = 32, stride = (nuint)Stride; var table = Keep0; nuint i = 0;
        for (; i + stride <= total; i += stride)
        {
            var first = Vector256.LoadUnsafe(ref source, i);
            var present = Avx2.BroadcastScalarToVector256(first.GetLower());
            Gfni.V256.GaloisFieldMultiply(first, present & table).StoreUnsafe(ref destination, i);
            if (stride > width)
            {
                for (nuint at = width; at + width < stride; at += width)
                    Gfni.V256.GaloisFieldMultiply(Vector256.LoadUnsafe(ref source, i + at), present).StoreUnsafe(ref destination, i + at);
                nuint last = i + stride - width;
                Gfni.V256.GaloisFieldMultiply(Vector256.LoadUnsafe(ref source, last), present).StoreUnsafe(ref destination, last);
            }
        }
        return i;
    }

    // V2: every load of the element before its stores
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint V2(ref byte destination, ref byte source, nuint total)
    {
        nuint stride = (nuint)Stride; var table = Keep0; nuint i = 0;
        for (; i + stride <= total; i += stride)
        {
            var first = Vector256.LoadUnsafe(ref source, i);
            var b = Vector256.LoadUnsafe(ref source, i + 32);
            var c = Vector256.LoadUnsafe(ref source, i + stride - 32);
            var present = Avx2.BroadcastScalarToVector256(first.GetLower());
            Gfni.V256.GaloisFieldMultiply(first, present & table).StoreUnsafe(ref destination, i);
            Gfni.V256.GaloisFieldMultiply(b, present).StoreUnsafe(ref destination, i + 32);
            Gfni.V256.GaloisFieldMultiply(c, present).StoreUnsafe(ref destination, i + stride - 32);
        }
        return i;
    }

    // V3: the pieces not overlapping: the last piece as a 16-byte or 8-byte store of what is left (stride 72: 32+32+8)
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint V3(ref byte destination, ref byte source, nuint total)
    {
        nuint stride = (nuint)Stride; var table = Keep0; nuint i = 0;
        for (; i + stride <= total; i += stride)
        {
            var first = Vector256.LoadUnsafe(ref source, i);
            var b = Vector256.LoadUnsafe(ref source, i + 32);
            ulong c = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i + 64));
            var present = Avx2.BroadcastScalarToVector256(first.GetLower());
            Gfni.V256.GaloisFieldMultiply(first, present & table).StoreUnsafe(ref destination, i);
            Gfni.V256.GaloisFieldMultiply(b, present).StoreUnsafe(ref destination, i + 32);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + 64), c & (ulong)(-(long)(byte)first.ToScalar()));
        }
        return i;
    }

    // Copy: plain 32-byte copy of the same bytes, the floor
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint Copy(ref byte destination, ref byte source, nuint total)
    {
        Unsafe.CopyBlockUnaligned(ref destination, ref source, (uint)total);
        return total;
    }

    public static void Main(string[] args)
    {
        int n = 1000, rounds = 8;
        byte* raw = (byte*)NativeMemory.AlignedAlloc(1 << 21, 4096);
        var random = new Random(1);
        byte* srcBase = raw + 4096, dstBase = raw + (1 << 20) + 2048;
        for (int i = 0; i < n; i++) { for (int b = 0; b < Stride; b++) srcBase[16 + i * Stride + b] = (byte)random.Next(256); srcBase[16 + i * Stride] = (byte)(i % 5 == 0 ? 0 : 1); }
        double Best(Func<int> body)
        {
            for (int r = 0; r < 3; r++) { for (int i = 0; i < 30000; i++) body(); System.Threading.Thread.Sleep(120); }
            double best = double.MaxValue;
            for (int r = 0; r < rounds; r++)
            {
                long t = Stopwatch.GetTimestamp();
                for (int i = 0; i < 20000; i++) body();
                best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalNanoseconds / 20000);
            }
            return best;
        }
        byte* s = srcBase + 16; nuint total = (nuint)(n * Stride);
        foreach (int d in new[] { 0, 4 })
        {
            byte* t = dstBase + d;
            Console.WriteLine($"stride {Stride} dst+{d}: V0 {Best(() => (int)V0(ref *t, ref *s, total)):F0}  V1 {Best(() => (int)V1(ref *t, ref *s, total)):F0}  V2 {Best(() => (int)V2(ref *t, ref *s, total)):F0}  V3 {Best(() => (int)V3(ref *t, ref *s, total)):F0}  copy {Best(() => (int)Copy(ref *t, ref *s, total)):F0}");
        }
    }
}
