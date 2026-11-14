using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

// Kernel harness, decisions only. T? clear kernels for strides 20, 24, 40, 72 and NinoNullable<decimal> pack/unpack,
// each variant checked against the scalar reference, timed with the destination at +0 and +4 from a 64-byte
// boundary, for a run in L1 (N=200) and one in L2 (N=1000).
public static unsafe class K
{
    delegate void Kernel(byte* d, byte* s, int count);
    static readonly List<(string name, int stride, int offset, Kernel k)> Clear = new();

    static Vector256<byte> Keep256(int stride, int offset) { var b = new byte[32]; for (int i = 0; i < 32; i++) b[i] = i < stride && (i == 0 || i >= offset) ? (byte)0xFF : (byte)0; return Vector256.Create(b); }
    static Vector512<byte> Keep512(int stride, int offset, int per) { var b = new byte[64]; for (int i = 0; i < per * stride; i++) b[i] = (i % stride == 0 || i % stride >= offset) ? (byte)0xFF : (byte)0; return Vector512.Create(b); }
    static Vector512<byte> Flag512(int stride, int per) { var b = new byte[64]; for (int i = 0; i < per * stride; i++) b[i] = (byte)(i - i % stride); return Vector512.Create(b); }

    // ---------- stride 20 (Guid?)
    static readonly Vector256<byte> K20 = Keep256(20, 4);
    static readonly Vector128<byte> K20x = K20.GetLower();
    static readonly Vector512<byte> K20z = Keep512(20, 4, 3), F20z = Flag512(20, 3);
    [MethodImpl(MethodImplOptions.NoInlining)] static void S20_ymmSurplus(byte* d, byte* s, int n)
    {   // one 32-byte load and store an element, 12 bytes past it rewritten by the next
        var k = K20; int i = 0;
        for (; i < n - 1; i++, s += 20, d += 20) { var v = Vector256.Load(s); Gfni.V256.GaloisFieldMultiply(v, Avx2.BroadcastScalarToVector256(v.GetLower()) & k).Store(d); }
        S20_xmmOverlap(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S20_xmmOverlap(byte* d, byte* s, int n)
    {   // [0,16) and [4,20)
        var k = K20x;
        for (int i = 0; i < n; i++, s += 20, d += 20)
        {
            var a = Vector128.Load(s); var b = Vector128.Load(s + 4); var f = Avx2.BroadcastScalarToVector128(a);
            Gfni.GaloisFieldMultiply(a, f & k).Store(d); Gfni.GaloisFieldMultiply(b, f).Store(d + 4);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S20_xmmWord(byte* d, byte* s, int n)
    {   // [0,16) and a 4-byte word [16,20)
        var k = K20x;
        for (int i = 0; i < n; i++, s += 20, d += 20)
        {
            var a = Vector128.Load(s); uint w = *(uint*)(s + 16);
            Gfni.GaloisFieldMultiply(a, Avx2.BroadcastScalarToVector128(a) & k).Store(d); *(uint*)(d + 16) = w & (uint)-(int)*s;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S20_zmmPermute(byte* d, byte* s, int n)
    {   // CopyPermuted: three elements a 64-byte load, the store 4 bytes past them
        var idx = F20z; var k = K20z; int i = 0;
        for (; (n - i) * 20 >= 64; i += 3, s += 60, d += 60) { var v = Vector512.Load(s); Gfni.V512.GaloisFieldMultiply(v, Avx512Vbmi.PermuteVar64x8(v, idx) & k).Store(d); }
        S20_xmmWord(d, s, n - i);
    }

    // ---------- stride 24 (decimal?)
    static readonly Vector256<byte> K24 = Keep256(24, 8);
    static readonly Vector128<byte> K24x = K24.GetLower();
    static readonly Vector512<byte> K24z = Keep512(24, 8, 2), F24z = Flag512(24, 2);
    [MethodImpl(MethodImplOptions.NoInlining)] static void S24_ymmSurplus(byte* d, byte* s, int n)
    {
        var k = K24; int i = 0;
        for (; i < n - 1; i++, s += 24, d += 24) { var v = Vector256.Load(s); Gfni.V256.GaloisFieldMultiply(v, Avx2.BroadcastScalarToVector256(v.GetLower()) & k).Store(d); }
        S24_xmmWord(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S24_xmmWord(byte* d, byte* s, int n)
    {
        var k = K24x;
        for (int i = 0; i < n; i++, s += 24, d += 24)
        {
            var a = Vector128.Load(s); ulong w = *(ulong*)(s + 16);
            Gfni.GaloisFieldMultiply(a, Avx2.BroadcastScalarToVector128(a) & k).Store(d); *(ulong*)(d + 16) = w & (ulong)-(long)*s;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S24_words(byte* d, byte* s, int n)
    {
        for (int i = 0; i < n; i++, s += 24, d += 24)
        {
            ulong w0 = *(ulong*)s, w1 = *(ulong*)(s + 8), w2 = *(ulong*)(s + 16); ulong p = (ulong)-(long)(byte)w0;
            *(ulong*)d = w0 & 0xFF; *(ulong*)(d + 8) = w1 & p; *(ulong*)(d + 16) = w2 & p;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S24_zmmPermute(byte* d, byte* s, int n)
    {
        var idx = F24z; var k = K24z; int i = 0;
        for (; (n - i) * 24 >= 64; i += 2, s += 48, d += 48) { var v = Vector512.Load(s); Gfni.V512.GaloisFieldMultiply(v, Avx512Vbmi.PermuteVar64x8(v, idx) & k).Store(d); }
        S24_xmmWord(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S24_zmmExact(byte* d, byte* s, int n)
    {   // the same, the 48 bytes of the two elements stored as 32 and 16
        var idx = F24z; var k = K24z; int i = 0;
        for (; (n - i) * 24 >= 64; i += 2, s += 48, d += 48)
        {
            var v = Vector512.Load(s); var r = Gfni.V512.GaloisFieldMultiply(v, Avx512Vbmi.PermuteVar64x8(v, idx) & k);
            r.GetLower().Store(d); Avx512F.ExtractVector128(r, 2).Store(d + 32);
        }
        S24_xmmWord(d, s, n - i);
    }
    static readonly Vector512<byte> Low48 = MakeLow(48);
    static Vector512<byte> MakeLow(int count) { var b = new byte[64]; for (int i = 0; i < count; i++) b[i] = 0xFF; return Vector512.Create(b); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S24_zmmMasked(byte* d, byte* s, int n)
    {   // the same, a masked store of 48 bytes
        var idx = F24z; var k = K24z; var m = Low48; int i = 0;
        for (; (n - i) * 24 >= 64; i += 2, s += 48, d += 48)
        {
            var v = Vector512.Load(s); var r = Gfni.V512.GaloisFieldMultiply(v, Avx512Vbmi.PermuteVar64x8(v, idx) & k);
            Avx512BW.MaskStore(d, m, r);
        }
        S24_xmmWord(d, s, n - i);
    }

    // ---------- stride 40
    static readonly Vector256<byte> K40 = Keep256(40, 8);
    static readonly Vector512<byte> K40z = Keep512(40, 8, 1);
    [MethodImpl(MethodImplOptions.NoInlining)] static void S40_ymmOverlap(byte* d, byte* s, int n)
    {
        var k = K40;
        for (int i = 0; i < n; i++, s += 40, d += 40)
        {
            var a = Vector256.Load(s); var b = Vector256.Load(s + 8); var f = Avx2.BroadcastScalarToVector256(a.GetLower());
            Gfni.V256.GaloisFieldMultiply(a, f & k).Store(d); Gfni.V256.GaloisFieldMultiply(b, f).Store(d + 8);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S40_ymmWord(byte* d, byte* s, int n)
    {
        var k = K40;
        for (int i = 0; i < n; i++, s += 40, d += 40)
        {
            var a = Vector256.Load(s); ulong w = *(ulong*)(s + 32);
            Gfni.V256.GaloisFieldMultiply(a, Avx2.BroadcastScalarToVector256(a.GetLower()) & k).Store(d); *(ulong*)(d + 32) = w & (ulong)-(long)*s;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S40_zmmSurplus(byte* d, byte* s, int n)
    {   // CopyPermuted's shape for one element a vector: 64-byte store, 24 bytes past the element
        var k = K40z; int i = 0;
        for (; (n - i) * 40 >= 64; i++, s += 40, d += 40) { var v = Vector512.Load(s); Gfni.V512.GaloisFieldMultiply(v, Avx512BW.BroadcastScalarToVector512(v.GetLower().GetLower()) & k).Store(d); }
        S40_ymmWord(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S40_words(byte* d, byte* s, int n)
    {
        for (int i = 0; i < n; i++, s += 40, d += 40)
        {
            ulong w0 = *(ulong*)s, w1 = *(ulong*)(s + 8), w2 = *(ulong*)(s + 16), w3 = *(ulong*)(s + 24), w4 = *(ulong*)(s + 32); ulong p = (ulong)-(long)(byte)w0;
            *(ulong*)d = w0 & 0xFF; *(ulong*)(d + 8) = w1 & p; *(ulong*)(d + 16) = w2 & p; *(ulong*)(d + 24) = w3 & p; *(ulong*)(d + 32) = w4 & p;
        }
    }

    // ---------- stride 48
    static readonly Vector256<byte> K48 = Keep256(48, 8);
    static readonly Vector512<byte> K48z = Keep512(48, 8, 1);
    [MethodImpl(MethodImplOptions.NoInlining)] static void S48_ymmXmm(byte* d, byte* s, int n)
    {
        var k = K48;
        for (int i = 0; i < n; i++, s += 48, d += 48)
        {
            var a = Vector256.Load(s); var b = Vector128.Load(s + 32); var f = Avx2.BroadcastScalarToVector256(a.GetLower());
            Gfni.V256.GaloisFieldMultiply(a, f & k).Store(d); Gfni.GaloisFieldMultiply(b, f.GetLower()).Store(d + 32);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S48_zmmSurplus(byte* d, byte* s, int n)
    {
        var k = K48z; int i = 0;
        for (; (n - i) * 48 >= 64; i++, s += 48, d += 48) { var v = Vector512.Load(s); Gfni.V512.GaloisFieldMultiply(v, Avx512BW.BroadcastScalarToVector512(v.GetLower().GetLower()) & k).Store(d); }
        S48_ymmXmm(d, s, n - i);
    }

    // ---------- stride 72
    static readonly Vector256<byte> K72 = Keep256(72, 8);
    static readonly Vector512<byte> K72z = Vector512.Create(MakeFrom(8));
    static byte[] MakeFrom(int from) { var b = new byte[64]; for (int i = from; i < 64; i++) b[i] = 0xFF; b[0] = 0xFF; return b; }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S72_ymmOverlap(byte* d, byte* s, int n)
    {
        var k = K72;
        for (int i = 0; i < n; i++, s += 72, d += 72)
        {
            var a = Vector256.Load(s); var b = Vector256.Load(s + 32); var c = Vector256.Load(s + 40); var f = Avx2.BroadcastScalarToVector256(a.GetLower());
            Gfni.V256.GaloisFieldMultiply(a, f & k).Store(d); Gfni.V256.GaloisFieldMultiply(b, f).Store(d + 32); Gfni.V256.GaloisFieldMultiply(c, f).Store(d + 40);
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S72_ymmWord(byte* d, byte* s, int n)
    {
        var k = K72;
        for (int i = 0; i < n; i++, s += 72, d += 72)
        {
            var a = Vector256.Load(s); var b = Vector256.Load(s + 32); ulong w = *(ulong*)(s + 64); var f = Avx2.BroadcastScalarToVector256(a.GetLower());
            Gfni.V256.GaloisFieldMultiply(a, f & k).Store(d); Gfni.V256.GaloisFieldMultiply(b, f).Store(d + 32); *(ulong*)(d + 64) = w & (ulong)-(long)*s;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S72_zmmWord(byte* d, byte* s, int n)
    {
        var k = K72z;
        for (int i = 0; i < n; i++, s += 72, d += 72)
        {
            var a = Vector512.Load(s); ulong w = *(ulong*)(s + 64);
            Gfni.V512.GaloisFieldMultiply(a, Avx512BW.BroadcastScalarToVector512(a.GetLower().GetLower()) & k).Store(d); *(ulong*)(d + 64) = w & (ulong)-(long)*s;
        }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void S72_words(byte* d, byte* s, int n)
    {
        for (int i = 0; i < n; i++, s += 72, d += 72)
        {
            ulong p = (ulong)-(long)*s; *(ulong*)d = *s;
            for (int w = 8; w < 72; w += 8) *(ulong*)(d + w) = *(ulong*)(s + w) & p;
        }
    }

    static void Reference(byte* d, byte* s, int n, int stride, int offset)
    {
        for (int i = 0; i < n; i++)
            for (int b = 0; b < stride; b++)
                d[i * stride + b] = b == 0 ? s[i * stride] : b < offset || s[i * stride] == 0 ? (byte)0 : s[i * stride + b];
    }

    // ---------- NinoNullable<decimal> (slot 32: presence 16, value 16) to the wire (24: flag word, value) and back
    static readonly Vector512<byte> PackIdx = PackTable(false), PackKeep = PackTable(true);
    static Vector512<byte> PackTable(bool keep)
    {
        var b = new byte[64];
        for (int i = 0; i < 48; i++) { int e = i / 24, at = i % 24; b[i] = keep ? (at == 0 || at >= 8 ? (byte)0xFF : (byte)0) : (byte)(e * 32 + (at >= 8 ? 16 + at - 8 : 0)); }
        return Vector512.Create(b);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pack_zmm(byte* d, byte* s, int n)
    {
        var idx = PackIdx; var k = PackKeep; int i = 0;
        for (; (n - i) * 24 >= 64; i += 2, s += 64, d += 48) (Avx512Vbmi.PermuteVar64x8(Vector512.Load(s), idx) & k).Store(d);
        Pack_words(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pack_zmmExact(byte* d, byte* s, int n)
    {
        var idx = PackIdx; var k = PackKeep; int i = 0;
        for (; (n - i) * 24 >= 64; i += 2, s += 64, d += 48) { var r = Avx512Vbmi.PermuteVar64x8(Vector512.Load(s), idx) & k; r.GetLower().Store(d); Avx512F.ExtractVector128(r, 2).Store(d + 32); }
        Pack_words(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pack_zmmMasked(byte* d, byte* s, int n)
    {
        var idx = PackIdx; var k = PackKeep; var m = Low48; int i = 0;
        for (; (n - i) * 24 >= 64; i += 2, s += 64, d += 48) Avx512BW.MaskStore(d, m, Avx512Vbmi.PermuteVar64x8(Vector512.Load(s), idx) & k);
        Pack_words(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pack_ymmPermq(byte* d, byte* s, int n)
    {   // AVX2: a slot a load, 32-byte store 8 bytes past the element
        var k = Vector256.Create(0xFFUL, ulong.MaxValue, ulong.MaxValue, 0); int i = 0;
        for (; i + 2 <= n; i++, s += 32, d += 24) (Avx2.Permute4x64(Vector256.Load((ulong*)s), 0b11_11_10_00) & k).AsByte().Store(d);
        Pack_words(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pack_ymmExact(byte* d, byte* s, int n)
    {   // AVX2: the same, stored as 16 and 8 bytes
        var k = Vector256.Create(0xFFUL, ulong.MaxValue, ulong.MaxValue, 0);
        for (int i = 0; i < n; i++, s += 32, d += 24) { var r = Avx2.Permute4x64(Vector256.Load((ulong*)s), 0b11_11_10_00) & k; r.GetLower().AsByte().Store(d); *(ulong*)(d + 16) = *(ulong*)(s + 24); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pack_window(byte* d, byte* s, int n)
    {   // the value as 16 bytes, then the flag word
        for (int i = 0; i < n; i++, s += 32, d += 24) { var v = Vector128.Load(s + 16); ulong f = *s; *(ulong*)d = f; v.Store(d + 8); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Pack_words(byte* d, byte* s, int n)
    {
        for (int i = 0; i < n; i++, s += 32, d += 24) { ulong f = *s, a = *(ulong*)(s + 16), b = *(ulong*)(s + 24); *(ulong*)d = f; *(ulong*)(d + 8) = a; *(ulong*)(d + 16) = b; }
    }

    static readonly Vector512<byte> UnpackIdx = UnpackTable(false), UnpackKeep = UnpackTable(true);
    static Vector512<byte> UnpackTable(bool keep)
    {
        var b = new byte[64];
        for (int i = 0; i < 64; i++) { int e = i / 32, at = i % 32; b[i] = keep ? (at == 0 || at >= 16 ? (byte)0xFF : (byte)0) : (byte)(e * 24 + (at >= 16 ? 8 + at - 16 : 0)); }
        return Vector512.Create(b);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Unpack_zmm(byte* d, byte* s, int n)
    {
        var idx = UnpackIdx; var k = UnpackKeep; int i = 0;
        for (; (n - i) * 24 >= 64; i += 2, s += 48, d += 64) (Avx512Vbmi.PermuteVar64x8(Vector512.Load(s), idx) & k).Store(d);
        Unpack_words(d, s, n - i);
    }
    // Stores on 64-byte boundaries of the destination: the slots up to the first boundary by words, then a line a
    // store, gathered by the table of the phase (bytes into a slot the boundary falls at: 0, 8, 16 or 24).
    static readonly Vector512<byte>[] PhaseIdx = new Vector512<byte>[4], PhaseKeep = new Vector512<byte>[4];
    static readonly int[] PhaseBase = MakePhases();
    static int[] MakePhases()
    {
        var bases = new int[4];
        for (int p = 0; p < 4; p++)
        {
            var idx = new byte[64]; var keep = new byte[64]; int first = -1;
            for (int j = 0; j < 64; j++)
            {
                int m = p * 8 + j, slot = m / 32, at = m % 32;
                int wire = at == 0 ? slot * 24 : at >= 16 ? slot * 24 + 8 + at - 16 : -1;
                if (wire < 0) continue;
                if (first < 0) first = wire;
                idx[j] = (byte)(wire - first); keep[j] = 0xFF;
            }
            PhaseIdx[p] = Vector512.Create(idx); PhaseKeep[p] = Vector512.Create(keep); bases[p] = first;
        }
        return bases;
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Unpack_zmmAligned(byte* d, byte* s, int n)
    {
        int head = (int)((64 - (nint)d % 64) % 64);
        if (n < 8 || head % 8 != 0) { Unpack_zmm(d, s, n); return; }
        int before = (head + 31) / 32;
        Unpack_words(d, s, before);
        int phase = head % 32 / 8; var idx = PhaseIdx[phase]; var k = PhaseKeep[phase];
        byte* src = s + head / 32 * 24 + PhaseBase[phase], last = s + n * 24 - 64, dst = d + head, end = d + n * 32 - 64;
        for (; dst <= end && src <= last; dst += 64, src += 48) (Avx512Vbmi.PermuteVar64x8(Vector512.Load(src), idx) & k).StoreAligned(dst);
        int done = (int)((dst - d) / 32);
        Unpack_words(d + done * 32, s + done * 24, n - done);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Unpack_ymmPermd(byte* d, byte* s, int n)
    {
        var idx = Vector256.Create(0, 0, 0, 0, 2, 3, 4, 5); var k = Vector256.Create(0xFFUL, 0, ulong.MaxValue, ulong.MaxValue).AsByte(); int i = 0;
        for (; i + 2 <= n; i++, s += 24, d += 32) (Avx2.PermuteVar8x32(Vector256.Load((int*)s), idx).AsByte() & k).Store(d);
        Unpack_words(d, s, n - i);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Unpack_window(byte* d, byte* s, int n)
    {
        for (int i = 0; i < n; i++, s += 24, d += 32) { var v = Vector128.Load(s + 8); ulong f = *s; *(ulong*)d = f; *(ulong*)(d + 8) = 0; v.Store(d + 16); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Unpack_words(byte* d, byte* s, int n)
    {
        for (int i = 0; i < n; i++, s += 24, d += 32) { ulong f = *s, a = *(ulong*)(s + 8), b = *(ulong*)(s + 16); *(ulong*)d = f; *(ulong*)(d + 8) = 0; *(ulong*)(d + 16) = a; *(ulong*)(d + 24) = b; }
    }

    static double Best(Action body, int rounds)
    {
        for (int r = 0; r < 3; r++) { for (int i = 0; i < 8000; i++) body(); System.Threading.Thread.Sleep(100); }
        double best = double.MaxValue;
        for (int r = 0; r < rounds; r++)
        {
            long t = Stopwatch.GetTimestamp();
            for (int i = 0; i < 5000; i++) body();
            best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalNanoseconds / 5000);
        }
        return best;
    }

    public static void Main(string[] args)
    {
        int rounds = args.Length > 0 ? int.Parse(args[0]) : 8;
        string filter = args.Length > 1 ? args[1] : "";
        byte* raw = (byte*)NativeMemory.AlignedAlloc(1 << 22, 4096);
        new Span<byte>(raw, 1 << 22).Clear();
        byte* srcBase = raw + 4096 + 16, dstBase = raw + (1 << 21) + 2048, refBase = raw + (3 << 20);
        var random = new Random(1);
        var clears = new (string, int, int, Kernel)[]
        {
            ("S20 ymm, 12 past", 20, 4, S20_ymmSurplus), ("S20 xmm+xmm overlap", 20, 4, S20_xmmOverlap), ("S20 xmm+word", 20, 4, S20_xmmWord), ("S20 zmm permute", 20, 4, S20_zmmPermute),
            ("S24 ymm, 8 past", 24, 8, S24_ymmSurplus), ("S24 xmm+word", 24, 8, S24_xmmWord), ("S24 words", 24, 8, S24_words), ("S24 zmm permute", 24, 8, S24_zmmPermute), ("S24 zmm, 32+16 stores", 24, 8, S24_zmmExact), ("S24 zmm, masked store", 24, 8, S24_zmmMasked),
            ("S40 ymm+ymm overlap", 40, 8, S40_ymmOverlap), ("S40 ymm+word", 40, 8, S40_ymmWord), ("S40 zmm, 24 past", 40, 8, S40_zmmSurplus), ("S40 words", 40, 8, S40_words),
            ("S48 ymm+xmm", 48, 8, S48_ymmXmm), ("S48 zmm, 16 past", 48, 8, S48_zmmSurplus),
            ("S72 ymm x3 overlap", 72, 8, S72_ymmOverlap), ("S72 ymm x2+word", 72, 8, S72_ymmWord), ("S72 zmm+word", 72, 8, S72_zmmWord), ("S72 words", 72, 8, S72_words),
        };
        foreach (int n in new[] { 4000 })
        {
            Console.WriteLine($"N = {n}: ns with the destination at +0 / +4 / +36 from a 64-byte boundary");
            foreach (var (name, stride, offset, kernel) in clears)
            {
                if (!name.Contains(filter)) continue;
                for (int i = 0; i < n; i++) { for (int b = 0; b < stride; b++) srcBase[i * stride + b] = (byte)random.Next(256); srcBase[i * stride] = (byte)(i % 5 == 0 ? 0 : 1); }
                Reference(refBase, srcBase, n, stride, offset);
                new Span<byte>(dstBase, n * stride + 64).Fill(0xCD);
                kernel(dstBase, srcBase, n);
                bool ok = new Span<byte>(dstBase, n * stride).SequenceEqual(new Span<byte>(refBase, n * stride)) && dstBase[n * stride] == 0xCD;
                Console.Write($"  {name,-24} {(ok ? "" : "WRONG ")}");
                foreach (int o in new[] { 0, 4, 36 }) { byte* t = dstBase + o; Console.Write($" {Best(() => kernel(t, srcBase, n), rounds),7:F0}"); }
                Console.WriteLine();
            }
            var packs = new (string, Kernel)[] { ("pack zmm, 16 past", Pack_zmm), ("pack zmm, 32+16 stores", Pack_zmmExact), ("pack zmm, masked store", Pack_zmmMasked), ("pack ymm vpermq, 8 past", Pack_ymmPermq), ("pack ymm, 16+8 stores", Pack_ymmExact), ("pack window (16+flag)", Pack_window), ("pack words", Pack_words) };
            foreach (var (name, kernel) in packs)
            {
                if (!name.Contains(filter)) continue;
                for (int i = 0; i < n; i++) { for (int b = 0; b < 32; b++) srcBase[i * 32 + b] = b < 16 ? (byte)0 : (byte)random.Next(256); srcBase[i * 32] = (byte)(i % 5 == 0 ? 0 : 1); }
                Pack_words(refBase, srcBase, n);
                new Span<byte>(dstBase, n * 24 + 64).Fill(0xCD);
                kernel(dstBase, srcBase, n);
                bool ok = new Span<byte>(dstBase, n * 24).SequenceEqual(new Span<byte>(refBase, n * 24)) && dstBase[n * 24] == 0xCD;
                Console.Write($"  {name,-24} {(ok ? "" : "WRONG ")}");
                foreach (int o in new[] { 0, 4, 36 }) { byte* t = dstBase + o; Console.Write($" {Best(() => kernel(t, srcBase, n), rounds),7:F0}"); }
                Console.WriteLine();
            }
            Console.WriteLine("  unpack: the wire at +4, memory at +0 / +16 / +8 / +40 from a 64-byte boundary");
            var unpacks = new (string, Kernel)[] { ("unpack zmm", Unpack_zmm), ("unpack zmm aligned", Unpack_zmmAligned), ("unpack ymm vpermd", Unpack_ymmPermd), ("unpack window", Unpack_window), ("unpack words", Unpack_words) };
            foreach (var (name, kernel) in unpacks)
            {
                if (!name.Contains(filter)) continue;
                byte* wire = srcBase + 4 - 16;
                for (int i = 0; i < n; i++) { for (int b = 0; b < 24; b++) wire[i * 24 + b] = b < 8 ? (byte)0 : (byte)random.Next(256); wire[i * 24] = (byte)(i % 5 == 0 ? 0 : 1); }
                Console.Write($"  {name,-24}");
                foreach (int o in new[] { 0, 16, 8, 40 })
                {
                    byte* t = dstBase + o;
                    Unpack_words(refBase, wire, n);
                    new Span<byte>(t, n * 32 + 64).Fill(0xCD);
                    kernel(t, wire, n);
                    bool ok = new Span<byte>(t, n * 32).SequenceEqual(new Span<byte>(refBase, n * 32)) && t[n * 32] == 0xCD;
                    Console.Write($" {(ok ? "" : "WRONG ")}{Best(() => kernel(t, wire, n), rounds),7:F0}");
                }
                Console.WriteLine();
            }
        }
    }
}
