using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.Internal;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace Nino.Core.CompilerServices
{
    // Block copies (Copy and its parts by size); the memory access is in NinoUnsafe.cs.
    public static partial class NinoUnsafe
    {
        /// <summary>
        /// Copies <paramref name="size"/> bytes. Up to 64 (most strings) inline, as overlapping loads and stores of the
        /// largest width that fits (33 to 64 bytes: two 32-byte vectors where they are accelerated, else four 16-byte
        /// moves); more out of line (<see cref="CopyOver64"/>, as every size in a 32-bit process, where up to 64 bytes
        /// are copied the same way on x86 and where <see cref="UnalignedWide32"/>), 65 to 128 as four 32-byte vectors
        /// where they are accelerated: a block copy of a size that is not a constant calls Memmove. Beyond, up to 32 KB
        /// with 64-byte vectors where they are accelerated, and from there on Intel (else from 256 bytes) to 1 MB with
        /// 32-byte ones (<see cref="CopyVectors{TVector}"/>), and Memmove beyond. Measured on an AVX-512 Xeon (.NET 10,
        /// source and destination of different alignments), with AVX-512 on or off: 33 to 64 bytes in 4.0 to 4.5 ns
        /// instead of 4.7 to 5.4 (Memmove), 65 to 128 in about 4.2 ns instead of 5.4 to 6.6. A loop of 16-byte vectors
        /// (WebAssembly, SSE only) is slower than Memmove from 2 KB, so those CPUs call Memmove from 65 bytes; Arm64
        /// copies up to 64 KB with <see cref="CopyPairs"/>, out of line.
        /// </summary>
        // In parts, each AggressiveInlining: the JIT checks an inline against the caller's inlining budget by the
        // callee's whole IL, before the branches the sizes and the CPU decide fold away. Copy in one method (662 bytes
        // of IL) was over the budget of every caller of less than 30 bytes (a generated body that forwards to its
        // members' writes) and stayed a call there; inline, its parts for more than 64 bytes took a large share of the
        // budget of every method that copies (a collection of an Immutable or polymorphic element, ...) for the sizes
        // where a call costs least. The inline parts have at most 173 bytes of IL.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy(ref byte destination, ref byte source, uint size)
        {
            if (Is64Bit && size <= 64)
            {
                if (size > 32)
                    Copy33To64(ref destination, ref source, size);
                else if (size >= 16)
                    Copy16To32(ref destination, ref source, size);
                else
                    CopyUpTo15(ref destination, ref source, size);
                return;
            }

            CopyOver64(ref destination, ref source, size);
        }

        /// <summary>33 to 64 bytes: two 32-byte vectors where they are accelerated, else four 16-byte moves.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Copy33To64(ref byte destination, ref byte source, uint size)
        {
#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated)
            {
                var head = Vector256.LoadUnsafe(ref source);
                var tail = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 32));
                head.StoreUnsafe(ref destination);
                tail.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 32));
                return;
            }
#endif
            var a = Unsafe.ReadUnaligned<Bytes16>(ref source);
            var b = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, 16));
            var c = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 32));
            var d = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, (nint)size - 16));
            Unsafe.WriteUnaligned(ref destination, a);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, 16), b);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 32), c);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 16), d);
        }

        /// <summary>16 to 32 bytes: two overlapping 16-byte moves.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Copy16To32(ref byte destination, ref byte source, uint size)
        {
            var head = Unsafe.ReadUnaligned<Bytes16>(ref source);
            if (size != 16)
            {
                nint tailOffset = (nint)size - 16;
                var tail = Unsafe.ReadUnaligned<Bytes16>(ref Unsafe.Add(ref source, tailOffset));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, tailOffset), tail);
            }
            Unsafe.WriteUnaligned(ref destination, head);
        }

        /// <summary>0 to 15 bytes: two overlapping moves of the largest width that fits.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyUpTo15(ref byte destination, ref byte source, uint size)
        {
            if (size >= 8)
            {
                ulong head = Unsafe.ReadUnaligned<ulong>(ref source);
                ulong tail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, (nint)size - 8));
                Unsafe.WriteUnaligned(ref destination, head);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 8), tail);
            }
            else if (size >= 4)
            {
                uint head = Unsafe.ReadUnaligned<uint>(ref source);
                uint tail = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref source, (nint)size - 4));
                Unsafe.WriteUnaligned(ref destination, head);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 4), tail);
            }
            else if (size >= 2)
            {
                ushort head = Unsafe.ReadUnaligned<ushort>(ref source);
                ushort tail = Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref source, (nint)size - 2));
                Unsafe.WriteUnaligned(ref destination, head);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)size - 2), tail);
            }
            else if (size != 0)
                destination = source;
        }

        /// <summary>
        /// More than 64 bytes, or any size in a 32-bit process (up to 64 as Copy does on x86 and where
        /// <see cref="UnalignedWide32"/>): 65 to 128 as
        /// four 32-byte vectors where they are accelerated, then <see cref="CopyVectors{TVector}"/> or
        /// <see cref="CopyBlock"/>. Out of line (see <see cref="Copy"/>), with the vector loops inlined: as calls of
        /// their own, a copy of 129 bytes to 32 KB made two calls (rc.7 made one, from the inlined Copy), and 100
        /// 12-byte structs serialized in up to 1.27 times rc.7's time.
        /// </summary>
        // Not AggressiveOptimization: fully optimized code does not fold the static readonly fields below (it tests
        // NinoUnsafe's class init and loads NinoSimd.Amd and IceLakeMemmove) and saved four registers in the prolog of the
        // 65 to 128 byte copies, the ones most callers make.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CopyOver64(ref byte destination, ref byte source, uint size) =>
            CopyOver64Inline(ref destination, ref source, size);

        /// <summary><see cref="CopyOver64"/>'s body, inlined there and into <see cref="CopyToNew"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyOver64Inline(ref byte destination, ref byte source, uint size)
        {
            // Up to 64 bytes in a 32-bit process that accesses 8 bytes at any address: the parts Copy inlines, here
            // so that Copy's IL, which callers' inlining budgets count, is the same in every process (written in Copy,
            // this test changed what the JIT inlined into an ImmutableSortedDictionary write on x64).
            if (!Is64Bit && (IsX86 || UnalignedWide32) && size <= 64)
            {
                if (size > 32)
                    Copy33To64(ref destination, ref source, size);
                else if (size >= 16)
                    Copy16To32(ref destination, ref source, size);
                else
                    CopyUpTo15(ref destination, ref source, size);
                return;
            }

#if NET8_0_OR_GREATER
            if (Vector256.IsHardwareAccelerated)
            {
                if (size <= 128)
                {
                    // 65 to 128 bytes (processes that skip the small sizes above go to Memmove).
                    if (Is64Bit || IsX86 || UnalignedWide32)
                    {
                        var a = Vector256.LoadUnsafe(ref source);
                        var b = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, 32));
                        var c = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 64));
                        var d = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, (nint)size - 32));
                        a.StoreUnsafe(ref destination);
                        b.StoreUnsafe(ref Unsafe.Add(ref destination, 32));
                        c.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 64));
                        d.StoreUnsafe(ref Unsafe.Add(ref destination, (nint)size - 32));
                        return;
                    }
                }
                else if ((Vector512.IsHardwareAccelerated
                              ? size <= (NinoSimd.Amd ? MaxVectorCopy : MaxVector256Copy)
                              : size - 256 <= MaxVector256Copy - 256) &&
                         !(IceLakeMemmove && size - MinIceLakeMemmove <= MaxVectorCopy - MinIceLakeMemmove))
                {
                    // With 64-byte vectors up to MaxVectorCopy, then on Intel 32-byte ones up to MaxVector256Copy:
                    // beyond 32 KB the 64-byte loop lost to Memmove, the 32-byte one still beats it there (GitHub's
                    // Xeon Platinum 8370C, 160,000 bytes into a new zeroed array: 3.18 µs instead of 3.51); on AMD's
                    // Zen 5 Memmove beats it into such an array (EPYC 9V45: 1.81 µs against 1.67). CopyToNew is the
                    // other way round.
                    if (Vector512.IsHardwareAccelerated && size <= MaxVectorCopy)
                        CopyVectors<Vector512<byte>>(ref destination, ref source, size);
                    else
                        CopyVectors<Vector256<byte>>(ref destination, ref source, size);
                    return;
                }
            }

#endif
            CopyBlock(ref destination, ref source, size);
        }

        /// <summary>
        /// Memmove, or <see cref="CopyPairs"/> on Arm64. A call of its own, so Copy's code is the same size on every
        /// platform: with the Arm64 branch written in Copy, the JIT inlined Copy where it had not before (an
        /// ImmutableSortedDictionary write) and left the writer in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyBlock(ref byte destination, ref byte source, uint size)
        {
#if NET8_0_OR_GREATER
            if (AdvSimd.Arm64.IsSupported)
            {
                CopyPairs(ref destination, ref source, size);
                return;
            }
#endif
            Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Ice Lake on Linux: <see cref="CopyOver64"/> leaves blocks from <see cref="MinIceLakeMemmove"/> to
        /// <see cref="MaxVectorCopy"/> to Memmove, which glibc runs there as one rep movsb from a 64-byte aligned
        /// destination (FSRM; rep_movsb_threshold 2,112). Nino's vector loop stores aligned but loads wherever the
        /// source is, and on GitHub's Xeon Platinum 8370C a 64-byte load across two lines costs about twice an aligned
        /// one (16,000 bytes: 190 ns from a line, 377 from 8 bytes past one; rep movsb 117 to 247). Linux only: other C
        /// runtimes' memmove was not measured there.
        /// </summary>
        private static readonly bool IceLakeMemmove = NinoSimd.IceLake && OperatingSystem.IsLinux();

        /// <summary>The smallest block <see cref="IceLakeMemmove"/> hands to Memmove: glibc's rep movsb threshold with FSRM.</summary>
        private const uint MinIceLakeMemmove = 2112;
#endif
        /// <summary>
        /// <see cref="Copy"/> into memory that may have just been allocated (a read into a new array): Memmove from
        /// <see cref="MinUninitializedArrayBytes"/>, where a new array is uninitialized and vector stores read each of
        /// its lines from memory first (Memmove's rep movsb writes whole lines without reading them). Below, where a
        /// new array was zeroed into the cache, as <see cref="Copy"/>. Measured on an AVX-512 Xeon (.NET 10): 1,000
        /// ints read into a new uninitialized array in 470 to 710 ns with Memmove, 760 to 830 with vector stores; into
        /// an existing array of 250, 14 ns instead of 23 with vector stores. Out of line: a size that is not a constant
        /// takes every branch of Copy, and inlined into each raw list and array read that code slowed the reads around
        /// it (a list of 8 classes holding a List&lt;int&gt; each, read in place: 680 to 710 ns inline, 570 to 610 out
        /// of line).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CopyToNew(ref byte destination, ref byte source, uint size)
        {
#if NET8_0_OR_GREATER
            if (size >= MinUninitializedArrayBytes && !NinoSimd.Amd)
            {
                Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
                return;
            }

            // On AMD with 64-byte vectors, 32-byte ones beyond 32 KB too, where Copy calls Memmove: into a new
            // uninitialized array they beat it (GitHub's EPYC 9V45 and 9V74, 160,000 bytes: 0.92 to 0.96 times the
            // time).
            if (NinoSimd.Amd && Vector512.IsHardwareAccelerated &&
                size - (MaxVectorCopy + 1) <= MaxVector256Copy - (MaxVectorCopy + 1))
            {
                CopyVectors<Vector256<byte>>(ref destination, ref source, size);
                return;
            }
#endif
            // CopyOver64 inline: a call of its own made two calls of a 65 to 128 byte read into a list (10 DateTimes)
            // where rc.7 made one.
            if (Is64Bit && size <= 64)
                Copy(ref destination, ref source, size);
            else
                CopyOver64Inline(ref destination, ref source, size);
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Largest copy done with 64-byte vectors by <see cref="CopyVectors{TVector}"/>; Memmove (the C library's from
        /// 2 KB) beyond. Measured on an AVX-512 Xeon (.NET 10, the destination 4 bytes past a 64-byte boundary): 16,000
        /// bytes in 111 ns instead of 145, 32,000 in 249 instead of 283, on par at 64,000, slower beyond (160,000: 3.2
        /// µs instead of 2.9).
        /// </summary>
        private const uint MaxVectorCopy = 32 * 1024;

        /// <summary>
        /// The range copied with 32-byte vectors where 64-byte ones are not accelerated (AVX2 without AVX-512, most
        /// desktop CPUs). Measured on a Xeon with AVX-512 turned off (.NET 10, source and destination of different
        /// alignments) against Memmove: 256 bytes 3.9 ns instead of 6.3, 1,200 bytes 12.4 instead of 15.7, 8,000 bytes
        /// 68 instead of 85; on par from 129 to 255 bytes. On GitHub's AMD EPYC runners (7763 and 9V74), whose C
        /// library copies with rep movsb, it is 2 to 30 times as fast up to 16,000 bytes (8,000: 83 to 85 ns instead of
        /// 236 to 2,775). Larger blocks written into a serializer's buffer keep a gain, even against the C library's
        /// AVX-512 copy on that Xeon: 32,000 bytes in 745 ns instead of 837, 160,000 in 2.9 µs instead of 3.2, 480,000
        /// in 8.8 µs instead of 10.0. Beyond 1 MB, Memmove, whose stores bypass the cache for blocks larger than it.
        /// </summary>
        private const uint MaxVector256Copy = 1024 * 1024;

        /// <summary>
        /// Copies at least one vector (<typeparamref name="TVector"/>, a <c>Vector256</c> or <c>Vector512</c> of bytes)
        /// with vector loads and stores: the first one unaligned, then four at a time with aligned stores (an unaligned
        /// store straddles two cache lines), and the last one unaligned, overlapping. Measured on an AVX-512 Xeon
        /// against Memmove (.NET 10), source and destination of different alignments, with 64-byte vectors: 256 bytes
        /// 5.9 ns instead of 7.9 ns, 1,200 bytes 11.9 ns instead of 18.6 ns, 4,000 bytes 34 ns instead of 48 ns.
        /// </summary>
        // Inlined into CopyOver64 and CopyToNew (Tier1, so their static readonly tests fold).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CopyVectors<TVector>(ref byte destination, ref byte source, uint size)
            where TVector : struct
        {
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<TVector>(ref source));
            nuint i = width - ((nuint)Unsafe.AsPointer(ref destination) & (width - 1));
            nuint end = size - width;
            for (; i + 3 * width <= end; i += 4 * width)
            {
                var a = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i));
                var b = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + width));
                var c = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + 2 * width));
                var d = Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i + 3 * width));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i), a);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + width), b);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + 2 * width), c);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i + 3 * width), d);
            }

            for (; i <= end; i += width)
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                    Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, i)));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, end),
                Unsafe.ReadUnaligned<TVector>(ref Unsafe.Add(ref source, end)));
        }
#endif

#if NET8_0_OR_GREATER
        /// <summary>
        /// Largest copy done by <see cref="CopyPairs"/> on Arm64 instead of Memmove; above it the two take the same
        /// time.
        /// </summary>
        private const uint MaxArm64VectorCopy = 64 * 1024;

        /// <summary>Smallest payload <see cref="CopyArm64Short"/> takes: one byte more than a single 64-byte chunk.</summary>
        internal const uint MinArm64Short = 65;

        /// <summary>Largest payload <see cref="CopyArm64Short"/> takes: its two 64-byte chunks.</summary>
        internal const uint MaxArm64Short = 128;

        /// <summary>
        /// Whether a raw copy of <paramref name="size"/> bytes takes <see cref="CopyArm64Short"/> (or
        /// <see cref="CopyArm64ShortMayOverlap"/>): on Arm64, from <see cref="MinArm64Short"/> to
        /// <see cref="MaxArm64Short"/> bytes, where the Arm64 JIT turns the two constant 64-byte copies into paired
        /// 128-bit loads and stores (M4: reads 23-45 % and writes 28-37 % faster than the general copy). Test it at the
        /// call site, never inside <see cref="CopyToNew"/> or <see cref="Copy"/>: a branch in the shared dispatcher
        /// slowed copies it did not take (the 1 KiB and 64-byte Int32 controls; `optimize`, vectors-copies.md).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsArm64Short(nuint size) =>
            NinoSimd.HasArm64 && size - MinArm64Short <= MaxArm64Short - MinArm64Short;

        // Two overlapping 64-byte chunks for a 65- to 128-byte payload in distinct storage.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CopyArm64Short(ref byte destination, ref byte source, uint size)
        {
            Unsafe.CopyBlockUnaligned(ref destination, ref source, 64);
            nint tail = (nint)size - 64;
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref destination, tail), ref Unsafe.Add(ref source, tail), 64);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void CopyArm64ShortMayOverlap(ref byte destination, ref byte source, uint size)
        {
            nint offset = Unsafe.ByteOffset(ref source, ref destination);
            if (offset > -(nint)size && offset < (nint)size)
                MemoryMarshal.CreateReadOnlySpan(ref source, (int)size)
                    .CopyTo(MemoryMarshal.CreateSpan(ref destination, (int)size));
            else
                CopyArm64Short(ref destination, ref source, size);
        }

        /// <summary>
        /// Copies more than 64 bytes on Arm64 (Memmove above <see cref="MaxArm64VectorCopy"/>, or in a 32-bit process;
        /// out of line, so Copy inlined into every read and write gains only this call): the first 16 bytes, then from
        /// the first 16-byte aligned source address 64 bytes a step through cursors, four 16-byte loads and stores that
        /// the JIT pairs into two ldp and two stp of q registers, then the last 64 bytes, overlapping. Measured on
        /// GitHub's Arm64 runners (Neoverse N2, .NET 10, the destination 4 bytes past a 64-byte boundary, the source 0,
        /// 4 or 8 bytes past one) against the C library's memcpy: 128 bytes in 0.80 to 0.84 of its time, 1,024 in 0.73
        /// to 0.91, 8,000 in 0.76 to 1.00, 64,000 and more the same. Without aligning the loads the loop was faster
        /// than memcpy from an aligned source but up to 1.31 times slower from one 4 bytes past it.
        /// </summary>
        // Inlined into CopyOver64: one call per copy, as on x64.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CopyPairs(ref byte destination, ref byte source, uint size)
        {
            if (size > MaxArm64VectorCopy || !Is64Bit)
            {
                Unsafe.CopyBlockUnaligned(ref destination, ref source, size);
                return;
            }

            Vector128.LoadUnsafe(ref source).StoreUnsafe(ref destination);
            // 1 to 16 bytes, to the next aligned source address (a moving GC may change it: only slower, still right).
            nint skip = 16 - (nint)((nuint)Unsafe.AsPointer(ref source) & 15);
            ref byte from = ref Unsafe.Add(ref source, skip);
            ref byte to = ref Unsafe.Add(ref destination, skip);
            for (nuint n = (size - (nuint)skip) / 64; n != 0; n--)
            {
                var a = Vector128.LoadUnsafe(ref from);
                var b = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 16));
                var c = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 32));
                var d = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 48));
                a.StoreUnsafe(ref to);
                b.StoreUnsafe(ref Unsafe.Add(ref to, 16));
                c.StoreUnsafe(ref Unsafe.Add(ref to, 32));
                d.StoreUnsafe(ref Unsafe.Add(ref to, 48));
                from = ref Unsafe.Add(ref from, 64);
                to = ref Unsafe.Add(ref to, 64);
            }

            // The last 64 bytes, overlapping what the loop copied.
            from = ref Unsafe.Add(ref source, (nint)size - 64);
            to = ref Unsafe.Add(ref destination, (nint)size - 64);
            var e = Vector128.LoadUnsafe(ref from);
            var f = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 16));
            var g = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 32));
            var h = Vector128.LoadUnsafe(ref Unsafe.Add(ref from, 48));
            e.StoreUnsafe(ref to);
            f.StoreUnsafe(ref Unsafe.Add(ref to, 16));
            g.StoreUnsafe(ref Unsafe.Add(ref to, 32));
            h.StoreUnsafe(ref Unsafe.Add(ref to, 48));
        }
#endif

        /// <summary>16 bytes, moved with one load and one store.</summary>
        private struct Bytes16
        {
#pragma warning disable CS0649
            public ulong A, B;
#pragma warning restore CS0649
        }
    }
}
