using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

// Kernel harness: the 64-byte permuted kernels of NinoNullables with the source and the destination placed at chosen
// offsets from a 64-byte boundary. Decisions only.
public static unsafe class K
{
    // ---- CopyPermuted: T? run of a stride that does not tile a vector (vpermb flags, AND, gf2p8mulb)
    public static int Stride, Offset, PerVector;
    static Vector512<byte> FlagIndex, KeptBytes;

    public static void SetClear(int stride, int offset)
    {
        Stride = stride; Offset = offset; PerVector = 64 / stride;
        var idx = new byte[64]; var keep = new byte[64];
        for (int i = 0; i < PerVector * stride; i++)
        {
            int at = i % stride;
            idx[i] = (byte)(i - at);
            keep[i] = at == 0 || at >= offset ? (byte)0xFF : (byte)0;
        }
        FlagIndex = Vector512.Create(idx); KeptBytes = Vector512.Create(keep);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static nuint CopyPermuted(ref byte destination, ref byte source, nuint total)
    {
        nuint step = (nuint)(PerVector * Stride);
        var index = FlagIndex; var kept = KeptBytes;
        nuint i = 0;
        for (; i + 64 <= total; i += step)
        {
            var e = Vector512.LoadUnsafe(ref source, i);
            var f = Avx512Vbmi.PermuteVar64x8(e, index) & kept;
            Gfni.V512.GaloisFieldMultiply(e, f).StoreUnsafe(ref destination, i);
        }
        return i;
    }

    // ---- WritePacked / ReadPacked: NinoNullable<T> slots (size bytes of presence, size of value) to the wire and back
    public static int Size, Slot, Per;
    static Vector512<byte> PackIndex, PackKeep, UnpackIndex, UnpackKeep;
    static Vector512<byte>[] PhaseIndex, PhaseKeep; // unpack at a destination phase (bytes into a slot the store starts at)

    public static void SetPack(int size, int offset)
    {
        Size = size; Offset = offset; Slot = 2 * size; Stride = offset + size; Per = 64 / Slot;
        (PackIndex, PackKeep) = Table(true); (UnpackIndex, UnpackKeep) = Table(false);
        PhaseIndex = new Vector512<byte>[Slot]; PhaseKeep = new Vector512<byte>[Slot];
        for (int phase = 0; phase < Slot; phase += 8)
        {
            // The 64 bytes of memory from `phase` bytes into slot 0; wire bytes relative to the wire offset of the
            // first byte taken (Base(phase)).
            var idx = new byte[64]; var keep = new byte[64];
            int first = -1;
            for (int j = 0; j < 64; j++)
            {
                int m = phase + j, slot = m / Slot, at = m % Slot;
                int wire = at == 0 ? slot * Stride : at >= size ? slot * Stride + offset + at - size : -1;
                if (wire < 0) continue;
                if (first < 0) first = wire;
                if (wire - first > 63) throw new Exception("span");
                idx[j] = (byte)(wire - first); keep[j] = 0xFF;
            }
            PhaseIndex[phase] = Vector512.Create(idx); PhaseKeep[phase] = Vector512.Create(keep); PhaseBase[phase] = first;
        }
    }
    public static int[] PhaseBase = new int[64];

    static (Vector512<byte>, Vector512<byte>) Table(bool pack)
    {
        var idx = new byte[64]; var keep = new byte[64];
        int to = pack ? Stride : Slot, from = pack ? Slot : Stride;
        for (int i = 0; i < Per * to; i++)
        {
            int element = i / to, at = i % to, head = pack ? Offset : Size;
            bool value = at >= head;
            keep[i] = at == 0 || value ? (byte)0xFF : (byte)0;
            idx[i] = (byte)(element * from + (value ? (pack ? Size : Offset) + at - head : 0));
        }
        return (Vector512.Create(idx), Vector512.Create(keep));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int WritePacked(ref byte destination, ref byte source, int count)
    {
        var index = PackIndex; var keep = PackKeep; int i = 0;
        for (; (count - i) * Stride >= 64; i += Per)
            (Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref source, (nuint)(i * Slot)), index) & keep)
                .StoreUnsafe(ref destination, (nuint)(i * Stride));
        return i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ReadPacked(ref byte destination, ref byte source, int count)
    {
        var index = UnpackIndex; var keep = UnpackKeep; int i = 0;
        for (; (count - i) * Stride >= 64; i += Per)
            (Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref source, (nuint)(i * Stride)), index) & keep)
                .StoreUnsafe(ref destination, (nuint)(i * Slot));
        return i;
    }

    // Unpack with every store on a 64-byte boundary of the destination, for slots that divide 64 bytes (one phase):
    // the slots that start before the first boundary the usual way, then one store a line, its bytes gathered from
    // the wire by the table of the line's phase in the slots; the caller reads the slots from the returned one on.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ReadPackedAligned(byte* destination, byte* source, int count)
    {
        int slot = Slot, stride = Stride, per = Per;
        int total = count * slot;
        int head = (int)((64 - (nint)destination % 64) % 64);        // bytes before the first boundary
        if (total < head + 128 || 64 % slot != 0) return ReadPacked(ref *destination, ref *source, count);
        var index = UnpackIndex; var keep = UnpackKeep;
        int before = (head + slot - 1) / slot;
        for (int i = 0; i < before; i += per)
            (Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref *source, (nuint)(i * stride)), index) & keep)
                .StoreUnsafe(ref *destination, (nuint)(i * slot));
        int phase = head % slot; var pi = PhaseIndex[phase]; var pk = PhaseKeep[phase];
        // The line at `head` starts `phase` bytes into slot head / slot; its wire bytes start at:
        byte* src = source + head / slot * stride + PhaseBase[phase];
        byte* last = source + count * stride - 64;
        byte* dst = destination + head, end = destination + total - 64;
        nint advance = 64 / slot * stride;
        for (; dst <= end && src <= last; dst += 64, src += advance)
            (Avx512Vbmi.PermuteVar64x8(Vector512.Load(src), pi) & pk).Store(dst);
        return (int)((dst - destination) / slot);
    }

    public static void Main(string[] args)
    {
        int n = 1000, rounds = args.Length > 0 ? int.Parse(args[0]) : 8;
        byte* raw = (byte*)NativeMemory.AlignedAlloc(1 << 20, 4096);
        var random = new Random(1);
        for (int i = 0; i < 1 << 20; i++) raw[i] = 0;
        byte* srcBase = raw + 4096, dstBase = raw + 4096 * 64 + 2048; // 2048: no load aliases a store 4096 bytes away

        void Fill(byte* p, int stride, int flagAt, int count) { for (int i = 0; i < count; i++) { for (int b = 0; b < stride; b++) p[i * stride + b] = (byte)random.Next(256); p[i * stride + flagAt] = (byte)(i % 5 == 0 ? 0 : 1); } }

        double Best(Func<int> body)
        {
            for (int r = 0; r < 3; r++) { for (int i = 0; i < 60000; i++) body(); System.Threading.Thread.Sleep(120); }
            double best = double.MaxValue;
            for (int r = 0; r < rounds; r++)
            {
                long t = Stopwatch.GetTimestamp();
                for (int i = 0; i < 50000; i++) body();
                best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalNanoseconds / 50000);
            }
            return best;
        }

        int[] srcOffsets = { 0, 16, 8 }, dstOffsets = { 0, 4, 20, 36 };
        foreach (var (stride, offset) in new[] { (24, 8), (20, 4), (40, 8), (12, 4) })
        {
            SetClear(stride, offset);
            Console.WriteLine($"CopyPermuted stride {stride}, {n} elements ({n * stride} bytes): ns by source offset (rows) and destination offset (columns {string.Join(", ", dstOffsets)})");
            foreach (int so in srcOffsets)
            {
                Fill(srcBase + so, stride, 0, n);
                Console.Write($"  src+{so,-3}");
                foreach (int d in dstOffsets) { byte* s = srcBase + so, t = dstBase + d; Console.Write($" {Best(() => (int)CopyPermuted(ref *t, ref *s, (nuint)(n * stride))),7:F1}"); }
                Console.WriteLine();
            }
        }

        foreach (var (size, offset) in new[] { (16, 8), (16, 4), (8, 4), (24, 8) })
        {
            SetPack(size, offset);
            Console.WriteLine($"WritePacked size {size} align {offset} (slot {Slot} -> wire {Stride}), {n} values: ns by source offset (rows) and destination offset (columns {string.Join(", ", dstOffsets)})");
            foreach (int so in srcOffsets)
            {
                Fill(srcBase + so, Slot, 0, n);
                Console.Write($"  src+{so,-3}");
                foreach (int d in dstOffsets) { byte* s = srcBase + so, t = dstBase + d; Console.Write($" {Best(() => WritePacked(ref *t, ref *s, n)),7:F1}"); }
                Console.WriteLine();
            }
            int[] wireOffsets = { 0, 4, 20 }, memOffsets = { 0, 16, 8, 24, 40 };
            Console.WriteLine($"ReadPacked size {size} align {offset} (wire {Stride} -> slot {Slot}): ns by wire offset (rows) and memory offset (columns {string.Join(", ", memOffsets)}); second row of each: stores aligned to the destination");
            foreach (int so in wireOffsets)
            {
                Fill(srcBase + so, Stride, 0, n);
                Console.Write($"  wire+{so,-2}");
                foreach (int d in memOffsets) { byte* s = srcBase + so, t = dstBase + d; Console.Write($" {Best(() => ReadPacked(ref *t, ref *s, n)),7:F1}"); }
                Console.WriteLine();
                if (Slot % 8 != 0) continue;
                Console.Write($"  aligned ");
                foreach (int d in memOffsets)
                {
                    byte* s = srcBase + so, t = dstBase + d;
                    var a = new byte[n * Slot]; var b = new byte[n * Slot];
                    for (int i = 0; i < n * Slot; i++) t[i] = 0xCD;
                    int ra = ReadPacked(ref *t, ref *s, n); new Span<byte>(t, ra * Slot).CopyTo(a);
                    for (int i = 0; i < n * Slot; i++) t[i] = 0xCD;
                    int rb = ReadPackedAligned(t, s, n); new Span<byte>(t, rb * Slot).CopyTo(b);
                    int common = Math.Min(ra, rb) * Slot;
                    if (rb < n - 4 || !a.AsSpan(0, common).SequenceEqual(b.AsSpan(0, common))) { Console.Write("  WRONG "); continue; }
                    Console.Write($" {Best(() => ReadPackedAligned(t, s, n)),7:F1}");
                }
                Console.WriteLine();
            }
        }
    }
}
