using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#if NET10_0_OR_GREATER
using System.Runtime.Intrinsics.Wasm;
#endif
using System.Runtime.Intrinsics.X86;
#endif
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>Collections of values written by an <see cref="INinoBlockFormatter{T}"/>.</summary>
    internal static class NinoBlock
    {
        /// <summary>
        /// The <see cref="INinoBlockFormatter{T}.BlockSize"/> of <typeparamref name="TFormatter"/>, -1 when it does not
        /// implement it. A JIT-time constant; -1 without a lookup for references (shared code).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Size<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() ? NinoBlock<T, TFormatter>.Size : -1;

        /// <summary>The byte offset of <paramref name="field"/> inside <paramref name="value"/>.</summary>
        public static int Offset<T, TField>(ref T value, ref TField field) =>
            (int)Unsafe.ByteOffset(ref Unsafe.As<T, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));

        /// <summary>
        /// Where the bytes of an item that <paramref name="fill"/> stores into a value land in its memory: the offset of
        /// the first byte of the run of <paramref name="size"/> marked bytes, or -1. For values whose fields are private
        /// (<c>KeyValuePair</c>).
        /// </summary>
        public static int Find<T>(int size, Func<T> fill) where T : struct
        {
            T value = fill();
            ref byte bytes = ref Unsafe.As<T, byte>(ref value);
            for (int start = 0; start + size <= Unsafe.SizeOf<T>(); start++)
            {
                int i = 0;
                while (i < size && Unsafe.Add(ref bytes, start + i) == 0xA5)
                    i++;
                if (i == size)
                    return start;
            }

            return -1;
        }

        /// <summary>An item marked for <see cref="Find{T}"/>: every byte 0xA5 (raw items only).</summary>
        public static TItem Marked<TItem>()
        {
            TItem item = default!;
            Unsafe.InitBlockUnaligned(ref Unsafe.As<TItem, byte>(ref item), 0xA5, (uint)Unsafe.SizeOf<TItem>());
            return item;
        }

        /// <summary>Writes <paramref name="count"/> values at <paramref name="destination"/>, <paramref name="size"/> bytes apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteAll<T, TFormatter>(ref byte destination, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlock<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.Copy(ref destination, ref Unsafe.As<T, byte>(ref first), (uint)(count * size));
                return;
            }

            WriteEach<T, TFormatter>(ref destination, ref first, count);
        }

        /// <summary>
        /// <see cref="WriteAll{T, TFormatter}"/> of values whose blocks are not laid out as in memory. Out of line: its
        /// vector paths take many locals, and inlined into a collection formatter (also where the elements are copied
        /// whole and it is never reached) they left the reader or writer of the method around it on the stack.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void WriteEach<T, TFormatter>(ref byte destination, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            // The block size from its field rather than a parameter (here and in the other methods out of line): a
            // JIT-time constant, so the divisions by it (LastVector) and the multiplications are shifts and adds.
            int size = NinoBlock<T, TFormatter>.Size;
            int i = 0;
#if NET8_0_OR_GREATER
            // A group of values at a time, as full vectors of blocks: one two-source permute and one store each, none
            // overlapping (Groups).
            if (NinoBlock<T, TFormatter>.Groups && NinoBlock<T, TFormatter>.LastGroup(count) >= NinoBlock<T, TFormatter>.Group)
                i = WriteGroups<T, TFormatter>(ref destination, ref first, count);
            // Without AVX-512, a group of values at a time, each 16-byte lane shuffled from one or two loads (Lanes).
            else if (NinoBlock<T, TFormatter>.LanePlan<NinoWrites>.Worth(count))
                i = Lanes<T, TFormatter, NinoWrites>(ref Unsafe.As<T, byte>(ref first), ref destination, count);

            // Several values as 64 bytes, their bytes moved to their places in the block by one 64-byte byte permute
            // (Permute64); the tail written is overwritten by the next one.
            if (NinoBlock<T, TFormatter>.Permutes)
            {
                var toWire = NinoBlock<T, TFormatter>.ToWire64;
                int step = NinoBlock<T, TFormatter>.PerVector;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 64);
                ref byte source = ref Unsafe.As<T, byte>(ref first);
                for (; i <= last; i += step)
                    Permute64(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination, (nuint)i * (nuint)size,
                        toWire);
                // The values after the last vector by one more, over values already written (the same bytes), rather
                // than one at a time.
                if (i < last + step && last >= 0)
                {
                    Permute64(ref source, (nuint)last * (nuint)Unsafe.SizeOf<T>(), ref destination,
                        (nuint)last * (nuint)size, toWire);
                    i = last + step;
                }
            }

            // Then each value as one 16-byte vector, by one shuffle. The last values, whose vector would reach past
            // the end of either side, one item at a time.
            if (NinoBlock<T, TFormatter>.Shuffles)
            {
                var toWire = NinoBlock<T, TFormatter>.ToWire;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 16);
                ref byte source = ref Unsafe.As<T, byte>(ref first);
                for (; i <= last; i++)
                    Vector128.StoreUnsafe(
                        NinoBlock<T, TFormatter>.Shuffle(
                            Vector128.LoadUnsafe(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>()), toWire),
                        ref destination, (nuint)i * (nuint)size);
            }
#endif
            for (; i < count; i++)
                ((INinoBlockFormatter<T>)default(TFormatter)).WriteBlock(
                    ref Unsafe.Add(ref destination, (nint)i * size), in Unsafe.Add(ref first, i));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// The groups of <see cref="WriteAll{T, TFormatter}"/>, two or more (one is cheaper as single vectors than as a
        /// call): returns the values written. Not inlined, so its dynamic PGO profile is that of long runs only: compiled
        /// inside WriteAll after short ones, the loop took 3.5 times as long (1,000 12-byte blocks: 577 ns instead of 160).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int WriteGroups<T, TFormatter>(ref byte destination, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            int i = 0;
            int group = NinoBlock<T, TFormatter>.Group;
            int last = NinoBlock<T, TFormatter>.LastGroup(count);
            ref byte source = ref Unsafe.As<T, byte>(ref first);
            // The groups from the first value whose blocks start on a vector boundary: the blocks usually follow a
            // 4-byte length, and a store across two cache lines costs two (measured on an AVX-512 Xeon, 1,000
            // 12-byte blocks: 230 ns unaligned, 150 to 170 aligned). The values before it, and those after the last
            // group, by one more group each, unaligned, over values the aligned groups also write (the same bytes):
            // one at a time, up to a group less one at each end took up to a fifth of the time (1,000 (int, long):
            // 123 to 149 ns by the alignment of the blocks, 100 to 125 with the two groups).
            int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), size, 0, group, 63);
            if (skip > 0)
            {
                WriteGroup<T, TFormatter>(ref source, ref destination, 0, size);
                i = skip;
            }

            for (; i <= last; i += group)
                WriteGroup<T, TFormatter>(ref source, ref destination, i, size);
            if (i < last + group)
            {
                WriteGroup<T, TFormatter>(ref source, ref destination, last, size);
                i = last + group;
            }

            return i;
        }

        /// <summary>The group of values from value <paramref name="i"/>: <see cref="Permute64x2Words{T, TFormatter}"/> or <see cref="Permute64x2{T, TFormatter}"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteGroup<T, TFormatter>(ref byte source, ref byte destination, int i, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (Avx512F.IsSupported && NinoBlock<T, TFormatter>.GroupWords)
                Permute64x2Words<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                    (nuint)i * (nuint)size);
            else
                Permute64x2<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                    (nuint)i * (nuint)size);
        }
#endif

        /// <summary>Reads <paramref name="count"/> values from <paramref name="source"/>, <paramref name="size"/> bytes apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadAll<T, TFormatter>(ref byte source, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlock<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref first), ref source, (uint)(count * size));
                return;
            }

            ReadEach<T, TFormatter>(ref source, ref first, count);
        }

        /// <summary>
        /// <see cref="ReadAll{T, TFormatter}"/> of values whose blocks are not laid out as in memory, out of line as
        /// <see cref="WriteEach{T, TFormatter}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void ReadEach<T, TFormatter>(ref byte source, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            int i = 0;
#if NET8_0_OR_GREATER
            // As WriteAll, the other way. Padding is unspecified, as when a value is read a field at a time.
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            if (NinoBlock<T, TFormatter>.ReadPlan<NinoPhase0>.Enabled &&
                NinoBlock<T, TFormatter>.ReadPlan<NinoPhase0>.Last(count) >= NinoBlock<T, TFormatter>.Group)
                i = ReadGroups<T, TFormatter>(ref source, ref first, count);
            else if (NinoBlock<T, TFormatter>.LanePlan<NinoReads>.Worth(count))
                i = Lanes<T, TFormatter, NinoReads>(ref source, ref destination, count);

            if (NinoBlock<T, TFormatter>.Permutes)
            {
                var toMemory = NinoBlock<T, TFormatter>.ToMemory64;
                int step = NinoBlock<T, TFormatter>.PerVector;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 64);
                // Where the values fill whole vectors (64 bytes a multiple of their size), the stores do not overlap
                // and are exactly the values': from the first value on the destination's first boundary (64 bytes with
                // AVX-512, 16 for the four Arm64 stores), so that none straddles two cache lines, and the values
                // before it, and those after the last vector, by one more vector each, unaligned (as WriteGroups).
                bool whole = 64 % Unsafe.SizeOf<T>() == 0;
                if (whole && i == 0 && last >= step)
                {
                    int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), Unsafe.SizeOf<T>(), 0, step,
                        Avx512Vbmi.IsSupported ? 63u : 15u);
                    if (skip > 0)
                    {
                        Permute64(ref source, 0, ref destination, 0, toMemory);
                        i = skip;
                    }
                }

                for (; i <= last; i += step)
                    Permute64(ref source, (nuint)i * (nuint)size, ref destination, (nuint)i * (nuint)Unsafe.SizeOf<T>(),
                        toMemory);
                if (whole && i < last + step && last >= 0)
                {
                    Permute64(ref source, (nuint)last * (nuint)size, ref destination,
                        (nuint)last * (nuint)Unsafe.SizeOf<T>(), toMemory);
                    i = last + step;
                }
            }

            if (NinoBlock<T, TFormatter>.Shuffles)
            {
                var toMemory = NinoBlock<T, TFormatter>.ToMemory;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 16);
                for (; i <= last; i++)
                    Vector128.StoreUnsafe(
                        NinoBlock<T, TFormatter>.Shuffle(Vector128.LoadUnsafe(ref source, (nuint)i * (nuint)size),
                            toMemory),
                        ref destination, (nuint)i * (nuint)Unsafe.SizeOf<T>());
            }
#endif
            for (; i < count; i++)
                ((INinoBlockFormatter<T>)default(TFormatter)).ReadBlock(
                    ref Unsafe.Add(ref source, (nint)i * size), ref Unsafe.Add(ref first, i));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// The read groups of <see cref="ReadAll{T, TFormatter}"/>, two or more: returns the values read. Not inlined,
        /// as <see cref="WriteGroups{T, TFormatter}"/>. Stores are aligned: the plan whose vectors start on a 64-byte
        /// boundary at a value, or 8 bytes into one.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int ReadGroups<T, TFormatter>(ref byte source, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            nuint at = (nuint)Unsafe.AsPointer(ref first);
            if (NinoBlock<T, TFormatter>.ReadPlan<NinoPhase8>.Enabled && at % 16 == 8)
                return ReadGroups<T, TFormatter, NinoPhase8>(ref source, ref first, count, size, at);
            return ReadGroups<T, TFormatter, NinoPhase0>(ref source, ref first, count, size, at);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadGroups<T, TFormatter, TPhase>(ref byte source, ref T first, int count, int size,
            nuint at)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            int i = 0, phase = default(TPhase).Bytes;
            int group = NinoBlock<T, TFormatter>.Group;
            int last = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Last(count);
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            // The groups from the first value whose vectors start on a 64-byte boundary (from the first value,
            // unaligned, when none does); the values before it, and those after the last group, by one more group
            // each, unaligned (as WriteGroups). With a phase, the first value is read whole too: a group's vectors
            // start past its first bytes, and each group's last vector writes those of the next.
            int skip = FirstAligned(at + (nuint)phase, Unsafe.SizeOf<T>(), 0, group, 63);
            if (phase > 0)
                ((INinoBlockFormatter<T>)default(TFormatter)).ReadBlock(ref source, ref first);

            // The indices from an array, not constants, so they stay in registers: the JIT reloads a vector
            // constant at every use (1,000 (int, long) into an array: 135 to 150 ns, against 115 to 130 with them in
            // registers).
            var masks = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Masks;
            Vector512<byte> m0 = masks[0], m1 = masks[1], m2 = masks[2], m3 = masks[3];
            if (skip > 0)
            {
                ReadGroup<T, TFormatter, TPhase>(ref source, 0, ref destination, (nuint)phase, m0, m1, m2, m3);
                i = skip;
            }

            for (; i <= last; i += group)
                ReadGroup<T, TFormatter, TPhase>(ref source, (nuint)i * (nuint)size, ref destination,
                    (nuint)i * (nuint)Unsafe.SizeOf<T>() + (nuint)phase, m0, m1, m2, m3);
            if (i < last + group)
            {
                ReadGroup<T, TFormatter, TPhase>(ref source, (nuint)last * (nuint)size, ref destination,
                    (nuint)last * (nuint)Unsafe.SizeOf<T>() + (nuint)phase, m0, m1, m2, m3);
                i = last + group;
            }

            return i;
        }

        /// <summary>One read group: the vectors of blocks loaded once, each memory vector one permute of two.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadGroup<T, TFormatter, TPhase>(ref byte source, nuint from, ref byte destination,
            nuint to, Vector512<byte> m0, Vector512<byte> m1, Vector512<byte> m2, Vector512<byte> m3)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            int loads = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Loads;
            int vectors = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Vectors;
            var w0 = Vector512.LoadUnsafe(ref source, from);
            var w1 = loads > 1 ? Vector512.LoadUnsafe(ref source, from + 64) : default;
            var w2 = loads > 2 ? Vector512.LoadUnsafe(ref source, from + 128) : default;
            var w3 = loads > 3 ? Vector512.LoadUnsafe(ref source, from + 192) : default;
            var w4 = loads > 4 ? Vector512.LoadUnsafe(ref source, from + 256) : default;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base0, w0, w1, w2, w3, w4, m0)
                .StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base1, w0, w1, w2, w3, w4, m1)
                .StoreUnsafe(ref destination, to + 64);
            if (vectors < 3)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base2, w0, w1, w2, w3, w4, m2)
                .StoreUnsafe(ref destination, to + 128);
            if (vectors < 4)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base3, w0, w1, w2, w3, w4, m3)
                .StoreUnsafe(ref destination, to + 192);
        }

        /// <summary>
        /// A memory vector from vectors of blocks <paramref name="pair"/> and the next (a constant); the next is not
        /// loaded (default) when the vector draws on <paramref name="pair"/> alone.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> ReadVector<T, TFormatter, TPhase>(int pair, Vector512<byte> w0,
            Vector512<byte> w1, Vector512<byte> w2, Vector512<byte> w3, Vector512<byte> w4, Vector512<byte> mask)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            var low = pair == 0 ? w0 : pair == 1 ? w1 : pair == 2 ? w2 : w3;
            var high = pair == 0 ? w1 : pair == 1 ? w2 : pair == 2 ? w3 : w4;
            // Padding takes whatever its index points at: as in a value read a field at a time, it is unspecified,
            // and clearing it would take an instruction a vector (vpermt2d cannot zero by itself here).
            return NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Words
                ? Avx512F.PermuteVar16x32x2(low.AsInt32(), mask.AsInt32(), high.AsInt32()).AsByte()
                : Avx512Vbmi.PermuteVar64x8x2(low, mask, high);
        }

        /// <summary>
        /// One group of values (<see cref="NinoBlock{T, TFormatter}.Groups"/>) with AVX-512 VBMI, for blocks that do not
        /// move whole words: vector k of blocks is
        /// one two-source permute of memory vectors k and k + 1, each memory vector loaded once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64x2<T, TFormatter>(ref byte source, nuint from, ref byte destination, nuint to)
            where TFormatter : struct, INinoFormatter<T>
        {
            int vectors = NinoBlock<T, TFormatter>.GroupVectors;
            var a = Vector512.LoadUnsafe(ref source, from);
            var b = Vector512.LoadUnsafe(ref source, from + 64);
            Avx512Vbmi.PermuteVar64x8x2(a, NinoBlock<T, TFormatter>.Group0, b).StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            var c = Vector512.LoadUnsafe(ref source, from + 128);
            Avx512Vbmi.PermuteVar64x8x2(b, NinoBlock<T, TFormatter>.Group1, c).StoreUnsafe(ref destination, to + 64);
            if (vectors < 3)
                return;
            var d = Vector512.LoadUnsafe(ref source, from + 192);
            Avx512Vbmi.PermuteVar64x8x2(c, NinoBlock<T, TFormatter>.Group2, d).StoreUnsafe(ref destination, to + 128);
            if (vectors < 4)
                return;
            var e = Vector512.LoadUnsafe(ref source, from + 256);
            Avx512Vbmi.PermuteVar64x8x2(d, NinoBlock<T, TFormatter>.Group3, e).StoreUnsafe(ref destination, to + 192);
        }

        /// <summary><see cref="Permute64x2{T, TFormatter}"/> for <see cref="NinoBlock{T, TFormatter}.GroupWords"/>: 4-byte words (vpermt2d).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64x2Words<T, TFormatter>(ref byte source, nuint from, ref byte destination, nuint to)
            where TFormatter : struct, INinoFormatter<T>
        {
            int vectors = NinoBlock<T, TFormatter>.GroupVectors;
            var a = Vector512.LoadUnsafe(ref source, from).AsInt32();
            var b = Vector512.LoadUnsafe(ref source, from + 64).AsInt32();
            Avx512F.PermuteVar16x32x2(a, NinoBlock<T, TFormatter>.Group0.AsInt32(), b).AsByte()
                .StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            var c = Vector512.LoadUnsafe(ref source, from + 128).AsInt32();
            Avx512F.PermuteVar16x32x2(b, NinoBlock<T, TFormatter>.Group1.AsInt32(), c).AsByte()
                .StoreUnsafe(ref destination, to + 64);
            if (vectors < 3)
                return;
            var d = Vector512.LoadUnsafe(ref source, from + 192).AsInt32();
            Avx512F.PermuteVar16x32x2(c, NinoBlock<T, TFormatter>.Group2.AsInt32(), d).AsByte()
                .StoreUnsafe(ref destination, to + 128);
            if (vectors < 4)
                return;
            var e = Vector512.LoadUnsafe(ref source, from + 256).AsInt32();
            Avx512F.PermuteVar16x32x2(d, NinoBlock<T, TFormatter>.Group3.AsInt32(), e).AsByte()
                .StoreUnsafe(ref destination, to + 192);
        }

        /// <summary>
        /// The first of the <paramref name="count"/> values from <paramref name="from"/>, <paramref name="stride"/>
        /// bytes apart from <paramref name="at"/>, that starts on a boundary of <paramref name="mask"/> + 1 bytes;
        /// <paramref name="from"/> when none does.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FirstAligned(nuint at, int stride, int from, int count, nuint mask)
        {
            for (int i = from; i < from + count; i++)
                if (((at + (nuint)(i * stride)) & mask) == 0)
                    return i;
            return from;
        }

        /// <summary>
        /// Moves values a group at a time as <see cref="NinoBlock{T, TFormatter}.LanePlan{TDirection}"/> describes, from
        /// their memory to their blocks (<see cref="NinoWrites"/>) or back: returns the values moved. Not inlined, as
        /// <see cref="WriteGroups{T, TFormatter}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int Lanes<T, TFormatter, TDirection>(ref byte source, ref byte destination, int count)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int size = NinoBlock<T, TFormatter>.Size;
            bool writes = typeof(TDirection) == typeof(NinoWrites);
            int from = writes ? Unsafe.SizeOf<T>() : size, to = writes ? size : Unsafe.SizeOf<T>();
            int group = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Group;
            int width = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Width;
            int last = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Last(count, from, to);
            // The first value whose loads start inside the source (a window may start before the group's values),
            // then the first from there whose vectors start on a boundary of their width, so that no store straddles
            // two cache lines (when one does). The values before the first one at a time; those from it to the
            // aligned one, and those after the last group, by one more group each, unaligned (as WriteGroups; the
            // stores of a group are exactly its values).
            int start = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Start;
            int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), to, start, group, (nuint)(width - 1));
            int i = 0;
            for (; i < start; i++)
                Move<T, TFormatter, TDirection>(ref source, ref destination, i, size);

            // The indices from an array, so they stay in registers (see ReadGroups).
            if (width == 32)
            {
                var masks = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Masks32;
                Vector256<byte> a0 = masks[0], b0 = masks[1], a1 = masks[2], b1 = masks[3], a2 = masks[4],
                    b2 = masks[5], a3 = masks[6], b3 = masks[7];
                if (skip > start && start <= last)
                {
                    Lanes32<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)start * from),
                        ref Unsafe.Add(ref destination, (nint)start * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = skip;
                }

                for (; i <= last; i += group)
                    Lanes32<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)i * from),
                        ref Unsafe.Add(ref destination, (nint)i * to), a0, b0, a1, b1, a2, b2, a3, b3);
                if (i < last + group && last >= start)
                {
                    Lanes32<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)last * from),
                        ref Unsafe.Add(ref destination, (nint)last * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = last + group;
                }
            }
            else
            {
                var masks = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Masks16;
                Vector128<byte> a0 = masks[0], b0 = masks[1], a1 = masks[2], b1 = masks[3], a2 = masks[4],
                    b2 = masks[5], a3 = masks[6], b3 = masks[7];
                if (skip > start && start <= last)
                {
                    Lanes16<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)start * from),
                        ref Unsafe.Add(ref destination, (nint)start * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = skip;
                }

                for (; i <= last; i += group)
                    Lanes16<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)i * from),
                        ref Unsafe.Add(ref destination, (nint)i * to), a0, b0, a1, b1, a2, b2, a3, b3);
                if (i < last + group && last >= start)
                {
                    Lanes16<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)last * from),
                        ref Unsafe.Add(ref destination, (nint)last * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = last + group;
                }
            }

            return i;
        }

        /// <summary>Value <paramref name="i"/> moved alone, in the direction of <typeparamref name="TDirection"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Move<T, TFormatter, TDirection>(ref byte source, ref byte destination, int i, int size)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            if (typeof(TDirection) == typeof(NinoWrites))
                ((INinoBlockFormatter<T>)default(TFormatter)).WriteBlock(ref Unsafe.Add(ref destination, (nint)i * size),
                    in Unsafe.Add(ref Unsafe.As<byte, T>(ref source), i));
            else
                ((INinoBlockFormatter<T>)default(TFormatter)).ReadBlock(ref Unsafe.Add(ref source, (nint)i * size),
                    ref Unsafe.Add(ref Unsafe.As<byte, T>(ref destination), i));
        }

        /// <summary>
        /// One group of 32-byte vectors (AVX2): each the bytes of two loads shuffled within their 16-byte lanes
        /// (vpshufb, which zeroes where an index has its high bit set) and ORed, or of one load alone. All are worked
        /// out before any is stored, so that a load repeated across them is one (no store in between).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lanes32<T, TFormatter, TDirection>(ref byte source, ref byte destination,
            Vector256<byte> a0, Vector256<byte> b0, Vector256<byte> a1, Vector256<byte> b1, Vector256<byte> a2,
            Vector256<byte> b2, Vector256<byte> a3, Vector256<byte> b3)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int vectors = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Vectors;
            var v0 = Lane32<T, TFormatter, TDirection>(ref source, 0, a0, b0);
            var v1 = vectors > 1 ? Lane32<T, TFormatter, TDirection>(ref source, 1, a1, b1) : default;
            var v2 = vectors > 2 ? Lane32<T, TFormatter, TDirection>(ref source, 2, a2, b2) : default;
            var v3 = vectors > 3 ? Lane32<T, TFormatter, TDirection>(ref source, 3, a3, b3) : default;
            v0.StoreUnsafe(ref destination);
            if (vectors > 1)
                v1.StoreUnsafe(ref destination, 32);
            if (vectors > 2)
                v2.StoreUnsafe(ref destination, 64);
            if (vectors > 3)
                v3.StoreUnsafe(ref destination, 96);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Lane32<T, TFormatter, TDirection>(ref byte source, int vector, Vector256<byte> a,
            Vector256<byte> b)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            var result = Avx2.Shuffle(Load32(ref source, NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0),
                NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 1)), a);
            if (NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0) == NinoLanes.None)
                return result;
            return result | Avx2.Shuffle(Load32(ref source, NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0),
                NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 1)), b);
        }

        /// <summary>
        /// The 16 bytes at <paramref name="low"/> and the 16 at <paramref name="high"/>: one load when they follow;
        /// on the 16-byte grid, a lane of each of two loads on the 32-byte grid (vperm2i128), loads the group's other
        /// vectors share; else the low lane of one load and the high lane of another, blended (vpblendd issues three
        /// or four a cycle, where vinserti128 issues once a cycle on Intel and Zen 3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Load32(ref byte source, int low, int high)
        {
            if (high == low + 16)
                return Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low));
            if (NinoLanes.Paired(low, high))
            {
                // The lane of each that holds the window: its low (0) or high (1) half.
                var x = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low & ~31));
                var y = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, high & ~31));
                return ((low >> 4) & 1, (high >> 4) & 1) switch
                {
                    (0, 0) => Avx2.Permute2x128(x, y, 0x20),
                    (0, _) => Avx2.Permute2x128(x, y, 0x30),
                    (_, 0) => Avx2.Permute2x128(x, y, 0x21),
                    _ => Avx2.Permute2x128(x, y, 0x31)
                };
            }

            return Avx2.Blend(Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low)).AsInt32(),
                Vector256.LoadUnsafe(ref Unsafe.Add(ref source, high - 16)).AsInt32(), 0xF0).AsByte();
        }

        /// <summary>
        /// One group of 16-byte vectors: each one or two 16-byte loads, as one table lookup of one or two registers on
        /// Arm64 (the second's indices from 16), or each load shuffled (an index from 0x80 giving a zero) and ORed. As
        /// <see cref="Lanes32{T, TFormatter, TDirection}"/>, all before any is stored.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lanes16<T, TFormatter, TDirection>(ref byte source, ref byte destination,
            Vector128<byte> a0, Vector128<byte> b0, Vector128<byte> a1, Vector128<byte> b1, Vector128<byte> a2,
            Vector128<byte> b2, Vector128<byte> a3, Vector128<byte> b3)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int vectors = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Vectors;
            var v0 = Lane16<T, TFormatter, TDirection>(ref source, 0, a0, b0);
            var v1 = vectors > 1 ? Lane16<T, TFormatter, TDirection>(ref source, 1, a1, b1) : default;
            var v2 = vectors > 2 ? Lane16<T, TFormatter, TDirection>(ref source, 2, a2, b2) : default;
            var v3 = vectors > 3 ? Lane16<T, TFormatter, TDirection>(ref source, 3, a3, b3) : default;
            v0.StoreUnsafe(ref destination);
            if (vectors > 1)
                v1.StoreUnsafe(ref destination, 16);
            if (vectors > 2)
                v2.StoreUnsafe(ref destination, 32);
            if (vectors > 3)
                v3.StoreUnsafe(ref destination, 48);
        }

        /// <summary>
        /// For Arm64, <paramref name="a"/> holds the indices into both loads (a two-register lookup); elsewhere
        /// <paramref name="a"/> and <paramref name="b"/> those into each.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Lane16<T, TFormatter, TDirection>(ref byte source, int vector, Vector128<byte> a,
            Vector128<byte> b)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            var low = Vector128.LoadUnsafe(
                ref Unsafe.Add(ref source, NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0)));
            int second = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0);
            if (second == NinoLanes.None)
                return NinoBlock<T, TFormatter>.Shuffle(low, a);
            var high = Vector128.LoadUnsafe(ref Unsafe.Add(ref source, second));
            if (AdvSimd.Arm64.IsSupported)
                return AdvSimd.Arm64.VectorTableLookup((low, high), a);
            return NinoBlock<T, TFormatter>.Shuffle(low, a) | NinoBlock<T, TFormatter>.Shuffle(high, b);
        }

        /// <summary>
        /// One 64-byte byte permute from <paramref name="source"/> + <paramref name="from"/> to
        /// <paramref name="destination"/> + <paramref name="to"/>: each byte written is the source byte its index
        /// names. AVX-512 VBMI's permute, which reads only an index's low six bits (so a padding byte, index 0x80, takes
        /// some byte: unspecified, as when a value is read a field at a time), or on Arm64 four 4-register table
        /// lookups, one per 16 bytes written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64(ref byte source, nuint from, ref byte destination, nuint to,
            Vector512<byte> indices)
        {
            if (Avx512Vbmi.IsSupported)
            {
                Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref source, from), indices)
                    .StoreUnsafe(ref destination, to);
                return;
            }

            var table = (Vector128.LoadUnsafe(ref source, from), Vector128.LoadUnsafe(ref source, from + 16),
                Vector128.LoadUnsafe(ref source, from + 32), Vector128.LoadUnsafe(ref source, from + 48));
            ref var quarters = ref Unsafe.As<Vector512<byte>, Vector128<byte>>(ref indices);
            AdvSimd.Arm64.VectorTableLookup(table, quarters).StoreUnsafe(ref destination, to);
            AdvSimd.Arm64.VectorTableLookup(table, Unsafe.Add(ref quarters, 1)).StoreUnsafe(ref destination, to + 16);
            AdvSimd.Arm64.VectorTableLookup(table, Unsafe.Add(ref quarters, 2)).StoreUnsafe(ref destination, to + 32);
            AdvSimd.Arm64.VectorTableLookup(table, Unsafe.Add(ref quarters, 3)).StoreUnsafe(ref destination, to + 48);
        }
#endif
    }

#if NET8_0_OR_GREATER
    /// <summary>A constant number of bytes, as a type argument (<see cref="NinoBlock{T, TFormatter}.ReadPlan{TPhase}"/>).</summary>
    internal interface INinoPhase
    {
        int Bytes { get; }
    }

    internal struct NinoPhase0 : INinoPhase
    {
        public int Bytes => 0;
    }

    internal struct NinoPhase8 : INinoPhase
    {
        public int Bytes => 8;
    }

    /// <summary>A direction values move in, as a type argument (<see cref="NinoBlock{T, TFormatter}.LanePlan{TDirection}"/>).</summary>
    internal interface INinoDirection
    {
    }

    /// <summary>From their blocks to memory.</summary>
    internal struct NinoReads : INinoDirection
    {
    }

    /// <summary>From memory to their blocks.</summary>
    internal struct NinoWrites : INinoDirection
    {
    }

    /// <summary>
    /// How <see cref="NinoBlock{T, TFormatter}.LanePlan{TDirection}"/> moves a group: worked out once, read into its
    /// constants. Each vector is the bytes of one or two loads, each load two 16-byte windows of the source (one per
    /// lane of a 32-byte vector; one window for 16-byte vectors), shuffled within each lane.
    /// </summary>
    internal sealed class NinoLanes
    {
        /// <summary>A second load not taken.</summary>
        public const int None = int.MinValue;

        public int Width, Group, Vectors, Start, End;

        /// <summary>For each vector, load and lane, where its window starts from the group's first value.</summary>
        public readonly int[] Windows = new int[16];

        // For each vector and load, the shuffle index of each byte (0x80 for none); Arm64: both loads in the first.
        private byte[][] _masks;

        /// <summary>
        /// The plan, or null when a group would take more than four vectors or a lane draws on bytes no two windows
        /// cover. <paramref name="map"/> is the block map (memory offset, block offset, size triples).
        /// </summary>
        public static NinoLanes Solve(int width, int[] map, int size, int memorySize, bool writes, bool tables)
        {
            int target = writes ? size : memorySize, source = writes ? memorySize : size;
            int group = width / Gcd(target, width);
            var plan = Plan(width, map, size, memorySize, writes, tables, group, grid: false);
            // AVX2 and Arm64 take whichever costs less a value: windows anywhere (fewest loads when each lane's bytes
            // follow) or on a 16-byte grid (loads repeat across a group's vectors; with AVX2 two lanes far apart come
            // from them by one lane permute rather than two more loads). Arm64's lookups take two registers, so its
            // grid groups are as many values as also start their source on the grid: 16-byte values from 12-byte
            // blocks four at a time, from three loads rather than four.
            if (width == 32 || tables && (writes ? NinoExperiment.GridWrites : NinoExperiment.GridReads))
            {
                int gridGroup = tables ? group / Gcd(group, 16 / Gcd(source, 16)) * (16 / Gcd(source, 16)) : group;
                var gridded = Plan(width, map, size, memorySize, writes, tables, gridGroup, grid: true);
                if (plan == null || gridded != null && gridded.Cost() * plan.Group < plan.Cost() * gridGroup)
                    plan = gridded;
            }

            return plan;
        }

        private static NinoLanes Plan(int width, int[] map, int size, int memorySize, bool writes, bool tables,
            int group, bool grid)
        {
            int sourceSize = writes ? memorySize : size, vectors = group * (writes ? size : memorySize) / width;
            if (vectors > 4)
                return null;
            var plan = new NinoLanes { Width = width, Group = group, Vectors = vectors, _masks = new byte[8][] };
            for (int v = 0; v < vectors; v++)
            {
                // The source byte of each byte of the vector (-1: padding, any value).
                var sources = new int[width];
                for (int b = 0; b < width; b++)
                    sources[b] = Source(map, size, memorySize, writes, v * width + b);
                if (!Cover(sources, width, grid, out int[] first, out int[] second))
                    return null;
                var a = new byte[width];
                var c = new byte[width];
                for (int b = 0; b < width; b++)
                {
                    int lane = b / 16, s = sources[b];
                    a[b] = c[b] = 0x80;
                    if (s < 0)
                        continue;
                    if (s >= first[lane] && s < first[lane] + 16)
                        a[b] = (byte)(s - first[lane]);
                    else if (tables)
                        a[b] = (byte)(16 + s - second[lane]);
                    else
                        c[b] = (byte)(s - second[lane]);
                }

                plan._masks[v * 2] = a;
                plan._masks[v * 2 + 1] = c;
                for (int lane = 0; lane < 2; lane++)
                {
                    plan.Windows[v * 4 + lane] = lane < width / 16 ? first[lane] : first[0] + 16;
                    plan.Windows[v * 4 + 2 + lane] =
                        second == null ? None : lane < width / 16 ? second[lane] : second[0] + 16;
                }
            }

            // The bytes loaded, from the group's first value; loads from before it: groups start where they stay
            // inside the source.
            int low = 0, high = 0;
            foreach (int at in plan.Loads())
            {
                low = Math.Min(low, at);
                high = Math.Max(high, at + width);
            }

            plan.Start = (-low + sourceSize - 1) / sourceSize;
            plan.End = high;
            return plan;
        }

        /// <summary>Whether the windows <paramref name="low"/> and <paramref name="high"/> of a 32-byte load are lanes of
        /// loads on the 32-byte grid, permuted (vperm2i128): both on the 16-byte grid, not one load.</summary>
        public static bool Paired(int low, int high) => high != low + 16 && low % 16 == 0 && high % 16 == 0;

        /// <summary>Where each load of a group starts (a vector width each), repeated loads once.</summary>
        private List<int> Loads()
        {
            var loads = new List<int>();
            for (int v = 0; v < Vectors; v++)
            for (int load = 0; load < 2; load++)
            {
                int low = Windows[v * 4 + load * 2], high = Windows[v * 4 + load * 2 + 1];
                if (low == None)
                    continue;
                if (Width == 16 || high == low + 16)
                    Add(low);
                else if (Paired(low, high))
                {
                    Add(low & ~31);
                    Add(high & ~31);
                }
                else
                {
                    Add(low);
                    Add(high - 16);
                }
            }

            return loads;

            void Add(int at)
            {
                if (!loads.Contains(at))
                    loads.Add(at);
            }
        }

        // Loads cost more than the shuffles: a load issues at most twice a cycle on Zen 3 for 32 bytes, and twice when
        // it straddles cache lines; a blend issues on any vector port, a lane permute on one or two.
        private int Cost()
        {
            int cost = 3 * Loads().Count;
            for (int v = 0; v < Vectors; v++)
            for (int load = 0; load < 2; load++)
            {
                int low = Windows[v * 4 + load * 2], high = Windows[v * 4 + load * 2 + 1];
                if (low != None && high != low + 16)
                    cost += Paired(low, high) ? 3 : 2;
            }

            return cost;
        }

        /// <summary>The source byte of byte <paramref name="at"/> of a group's targets, or -1 for padding.</summary>
        private static int Source(int[] map, int size, int memorySize, bool writes, int at)
        {
            int value = at / (writes ? size : memorySize), offset = at % (writes ? size : memorySize);
            for (int i = 0; i < map.Length; i += 3)
            {
                int memory = map[i], wire = map[i + 1], length = map[i + 2];
                if (writes && offset >= wire && offset < wire + length)
                    return value * memorySize + memory + offset - wire;
                if (!writes && offset >= memory && offset < memory + length)
                    return value * size + wire + offset - memory;
            }

            return -1;
        }

        /// <summary>
        /// One window for each lane, or two: the first (the lowest that covers the lane's lowest byte, so it reaches
        /// furthest) and a second for the rest; null second when one is enough for every lane. On the
        /// <paramref name="grid"/>, both start on a multiple of 16, the second where the first ends.
        /// </summary>
        private static bool Cover(int[] sources, int width, bool grid, out int[] first, out int[] second)
        {
            int lanes = width / 16;
            first = new int[lanes];
            second = null;
            var rest = new int[lanes];
            bool two = false;
            for (int lane = 0; lane < lanes; lane++)
            {
                int min = int.MaxValue, max = int.MinValue;
                for (int b = lane * 16; b < lane * 16 + 16; b++)
                    if (sources[b] >= 0)
                    {
                        min = Math.Min(min, sources[b]);
                        max = Math.Max(max, sources[b]);
                    }

                if (min == int.MaxValue)
                {
                    first[lane] = rest[lane] = lane == 0 ? 0 : first[0] + 16 * lane;
                    continue;
                }

                if (grid)
                {
                    first[lane] = min & ~15;
                    if (max >= first[lane] + 32)
                        return false;
                    rest[lane] = max >= first[lane] + 16 ? first[lane] + 16 : first[lane];
                    two |= rest[lane] != first[lane];
                    continue;
                }

                first[lane] = min;
                int restMin = int.MaxValue, restMax = int.MinValue;
                for (int b = lane * 16; b < lane * 16 + 16; b++)
                    if (sources[b] >= first[lane] + 16)
                    {
                        restMin = Math.Min(restMin, sources[b]);
                        restMax = Math.Max(restMax, sources[b]);
                    }

                if (restMin == int.MaxValue)
                {
                    rest[lane] = first[lane];
                    continue;
                }

                if (restMax - restMin >= 16)
                    return false;
                rest[lane] = restMax - 15;
                two = true;
            }

            // Windows a lane apart make one 32-byte load: align the first lane's windows to the second's where they
            // still cover it.
            if (lanes == 2)
            {
                Align(sources, first);
                if (two)
                    Align(sources, rest);
            }

            if (two)
                second = rest;
            return true;
        }

        /// <summary>Moves lane 0's window to 16 bytes before lane 1's when it still covers lane 0's bytes there.</summary>
        private static void Align(int[] sources, int[] windows)
        {
            int wanted = windows[1] - 16;
            if (wanted == windows[0])
                return;
            for (int b = 0; b < 16; b++)
            {
                int s = sources[b];
                if (s >= 0 && (s >= windows[0] && s < windows[0] + 16) && (s < wanted || s >= wanted + 16))
                    return;
            }

            windows[0] = wanted;
        }

        public Vector256<byte>[] Masks32()
        {
            var masks = new Vector256<byte>[8];
            for (int i = 0; i < 8; i++)
                if (_masks[i] != null && Width == 32)
                    masks[i] = Vector256.Create(_masks[i]);
            return masks;
        }

        public Vector128<byte>[] Masks16()
        {
            var masks = new Vector128<byte>[8];
            for (int i = 0; i < 8; i++)
                if (_masks[i] != null && Width == 16)
                    masks[i] = Vector128.Create(_masks[i]);
            return masks;
        }

        internal static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }
#endif

    /// <summary>
    /// How values of <typeparamref name="T"/> map to their blocks (<see cref="INinoBlockFormatter{T}.BlockMap"/>),
    /// worked out once. The flags are JIT-time constants.
    /// </summary>
    internal static class NinoBlock<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
        /// <summary>
        /// <see cref="NinoBlock.Size{T, TFormatter}"/>, read from a field: one load where the type test and the size
        /// were each an inlined call, which the JIT counts against what else it inlines into the method.
        /// </summary>
        public static readonly int Size = default(TFormatter) is INinoBlockFormatter<T>
            ? ((INinoBlockFormatter<T>)default(TFormatter)).BlockSize
            : -1;

        private static readonly int[] Map = NinoBlock.Size<T, TFormatter>() > 0
            ? ((INinoBlockFormatter<T>)default(TFormatter)).BlockMap()
            : null;

        /// <summary>Whether a value's memory is its block: a collection of them is one copy.</summary>
        public static readonly bool IsMemory = ComputeIsMemory();

        private static bool ComputeIsMemory()
        {
            if (Map == null || Unsafe.SizeOf<T>() != NinoBlock.Size<T, TFormatter>())
                return false;
            for (int i = 0; i < Map.Length; i += 3)
                if (Map[i] != Map[i + 1])
                    return false;
            return true;
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Whether values are moved to and from their blocks by one 16-byte shuffle each: both fit in 16 bytes, and the
        /// CPU shuffles bytes by a variable mask, an index from 0x80 up giving a zero (SSSE3, Arm64, or WebAssembly
        /// SIMD on .NET 10).
        /// </summary>
        public static readonly bool Shuffles = Map != null && !IsMemory && Unsafe.SizeOf<T>() <= 16 &&
                                               NinoBlock.Size<T, TFormatter>() <= 16 &&
                                               (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported
#if NET10_0_OR_GREATER
                                                || PackedSimd.IsSupported
#endif
                                               );

        /// <summary>For each block byte, the memory byte it comes from (0x80: none, a zero).</summary>
        public static readonly Vector128<byte> ToWire = Mask(toWire: true);

        /// <summary>For each memory byte, the block byte it comes from (0x80: padding, a zero).</summary>
        public static readonly Vector128<byte> ToMemory = Mask(toWire: false);

        private static Vector128<byte> Mask(bool toWire)
        {
            if (!Shuffles)
                return default;
            var mask = new byte[16];
            mask.AsSpan().Fill(0x80);
            for (int i = 0; i < Map.Length; i += 3)
            for (int b = 0; b < Map[i + 2]; b++)
            {
                int memory = Map[i] + b, wire = Map[i + 1] + b;
                if (toWire)
                    mask[wire] = (byte)memory;
                else
                    mask[memory] = (byte)wire;
            }

            return Vector128.Create(mask);
        }

        /// <summary>
        /// The last of <paramref name="count"/> values a <paramref name="width"/>-byte vector may start at: its load
        /// and store stay inside both the values and their blocks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastVector(int count, int size, int width) =>
            count - Math.Max((width + Unsafe.SizeOf<T>() - 1) / Unsafe.SizeOf<T>(), (width + size - 1) / size);

        /// <summary>
        /// Whether values are written <see cref="Group"/> at a time as <see cref="GroupVectors"/> full vectors of
        /// blocks, each one two-source permute of two consecutive vectors of their memory: blocks are smaller than
        /// values (padding left out), so a value at a time stores a vector for fewer bytes, while a group stores each
        /// vector once (a quarter fewer stores than a copy of the values' memory for 12-byte blocks of 16-byte values).
        /// Only where each vector of blocks k draws on memory vectors k and k + 1, with up to four vectors a group.
        /// </summary>
        /// AVX-512 only (a two-source permute of 4-byte words, or with VBMI of bytes); other CPUs use
        /// <see cref="LanePlan{TDirection}"/>.
        public static readonly bool Groups = Map != null && !IsMemory &&
                                             NinoBlock.Size<T, TFormatter>() < Unsafe.SizeOf<T>() &&
                                             Avx512F.IsSupported && GroupMasks() is { } masks &&
                                             (Avx512Vbmi.IsSupported || Words(masks));

        /// <summary>
        /// Whether the 64-byte vectors of blocks move whole 4-byte words (vpermt2d): one micro-op at one a cycle on
        /// Intel, where vpermt2b takes three and issues every other cycle (1,000 12-byte blocks of 16-byte values on an
        /// AVX-512 Xeon: 121 ns with words, 150 to 190 with bytes, 123 for a copy of their memory).
        /// </summary>
        public static readonly bool GroupWords = Groups && Words(GroupMasks());

        /// <summary>Values per group: the fewest whose blocks fill whole vectors.</summary>
        public static readonly int Group = Groups ? 64 / NinoLanes.Gcd(NinoBlock.Size<T, TFormatter>(), 64) : 0;

        /// <summary>Vectors of blocks per group (1 to 4); one memory vector more is read.</summary>
        public static readonly int GroupVectors = Groups ? Group * NinoBlock.Size<T, TFormatter>() / 64 : 0;

        /// <summary>
        /// The permute indices of each vector of blocks in a group: bytes, or for <see cref="GroupWords"/> one int per
        /// word (0 to 31).
        /// </summary>
        public static readonly Vector512<byte> Group0 = GroupMask(0), Group1 = GroupMask(1), Group2 = GroupMask(2),
            Group3 = GroupMask(3);

        /// <summary>The last of <paramref name="count"/> values a group may start at: its reads stay inside the values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastGroup(int count) =>
            count - Math.Max(Group, ((GroupVectors + 1) * 64 + Unsafe.SizeOf<T>() - 1) / Unsafe.SizeOf<T>());

        private static Vector512<byte> GroupMask(int vector)
        {
            if (!Groups || vector >= GroupVectors)
                return default;
            var masks = GroupMasks();
            var mask = new byte[64];
            if (GroupWords)
            {
                // Words: the byte index of a word's first byte, over 4.
                for (int word = 0; word < 16; word++)
                    Unsafe.WriteUnaligned(ref mask[word * 4], masks[vector * 64 + word * 4] / 4);
            }
            else
            {
                Array.Copy(masks, vector * 64, mask, 0, 64);
            }

            return Vector512.Create(mask);
        }

        /// <summary>
        /// For each byte of each 64-byte vector of blocks in a group, the byte of
        /// memory vectors k and k + 1 it comes from; null when a group would take more than four vectors or a vector of
        /// blocks draws on other memory vectors.
        /// </summary>
        private static byte[] GroupMasks()
        {
            const int width = 64;
            int size = NinoBlock.Size<T, TFormatter>();
            int group = width / NinoLanes.Gcd(size, width), vectors = group * size / width;
            if (vectors > 4)
                return null;
            var masks = new byte[vectors * width];
            for (int wire = 0; wire < vectors * width; wire++)
            {
                int value = wire / size, offset = wire % size, memory = -1;
                for (int i = 0; i < Map.Length; i += 3)
                    if (offset >= Map[i + 1] && offset < Map[i + 1] + Map[i + 2])
                        memory = value * Unsafe.SizeOf<T>() + Map[i] + offset - Map[i + 1];
                // Every block byte belongs to a field; vector k reads memory vectors k and k + 1.
                int index = memory - wire / width * width;
                if (memory < 0 || index < 0 || index >= 2 * width)
                    return null;
                masks[wire] = (byte)index;
            }

            return masks;
        }

        /// <summary>Whether each 4-byte word of blocks is one whole, aligned word of memory.</summary>
        private static bool Words(byte[] masks)
        {
            if (masks == null)
                return false;
            for (int word = 0; word < masks.Length; word += 4)
                if (masks[word] % 4 != 0 || masks[word + 1] != masks[word] + 1 || masks[word + 2] != masks[word] + 2 ||
                    masks[word + 3] != masks[word] + 3)
                    return false;
            return true;
        }

        /// <summary>
        /// Values read a group at a time (<see cref="Groups"/> the other way, with AVX-512): each 64-byte vector of
        /// memory is one two-source permute of the two vectors of blocks it draws on, stored at an aligned address.
        /// Its vectors start <typeparamref name="TPhase"/> bytes into a value: arrays are only 8-byte aligned, so
        /// 16-byte values start on a 64-byte boundary in half of them and 8 bytes past one in the other half, where
        /// 64-byte stores at value boundaries would each straddle two cache lines (1,000 12-byte
        /// blocks into a list of 16-byte values on an AVX-512 Xeon: 260 to 275 ns, against 175 to 180 aligned).
        /// </summary>
        internal static class ReadPlan<TPhase> where TPhase : struct, INinoPhase
        {
            // Before the constants read from it (static fields initialize in order).
            private static readonly GroupReads Plan = new GroupReads(default(TPhase).Bytes);

            /// <summary>Whether values are read this way.</summary>
            public static readonly bool Enabled = Plan.Masks != null;

            /// <summary>Whether it moves whole 4-byte words (vpermt2d), else bytes (VBMI).</summary>
            public static readonly bool Words = Plan.Words;

            /// <summary>Memory vectors per group (<see cref="Group"/> values), and vectors of blocks loaded for them.</summary>
            public static readonly int Vectors = Plan.Vectors, Loads = Plan.Loads;

            /// <summary>For memory vector k of a group, the vector of blocks it draws on (with the next).</summary>
            public static readonly int Base0 = Plan.Base(0), Base1 = Plan.Base(1), Base2 = Plan.Base(2),
                Base3 = Plan.Base(3);

            /// <summary>The permute indices of memory vector k (one int per word for <see cref="Words"/>).</summary>
            public static readonly Vector512<byte>[] Masks = { Plan.Mask(0), Plan.Mask(1), Plan.Mask(2), Plan.Mask(3) };

            /// <summary>
            /// The last of <paramref name="count"/> values a group may start at: its loads stay inside the blocks,
            /// and its stores (past the group by the phase) inside the values.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Last(int count) =>
                count - Math.Max(Group + (default(TPhase).Bytes > 0 ? 1 : 0),
                    (Loads * 64 + NinoBlock.Size<T, TFormatter>() - 1) / NinoBlock.Size<T, TFormatter>());
        }

        /// <summary>How <see cref="ReadPlan{TPhase}"/> moves a group: worked out once, read into its constants.</summary>
        private sealed class GroupReads
        {
            public readonly byte[] Masks;
            public readonly int[] Bases = new int[4];
            public readonly bool Words;
            public readonly int Vectors, Loads;

            public GroupReads(int phase)
            {
                int size = NinoBlock.Size<T, TFormatter>(), memorySize = Unsafe.SizeOf<T>();
                if (Map == null || IsMemory || size >= memorySize || !Avx512F.IsSupported)
                    return;
                int group = 64 / NinoLanes.Gcd(size, 64), vectors = group * memorySize / 64;
                // A phase only where values can start that far past a boundary (8-byte aligned arrays).
                if (group * memorySize % 64 != 0 || vectors > 4 || phase >= NinoLanes.Gcd(memorySize, 64))
                    return;
                var masks = new byte[vectors * 64];
                for (int k = 0; k < vectors; k++)
                {
                    // The block byte of each memory byte (-1: padding), and the vector of blocks the lowest is in.
                    var wires = new int[64];
                    int low = int.MaxValue;
                    for (int b = 0; b < 64; b++)
                    {
                        int memory = phase + k * 64 + b, value = memory / memorySize, offset = memory % memorySize;
                        int wire = -1;
                        for (int i = 0; i < Map.Length; i += 3)
                            if (offset >= Map[i] && offset < Map[i] + Map[i + 2])
                                wire = value * size + Map[i + 1] + offset - Map[i];
                        wires[b] = wire;
                        if (wire >= 0)
                            low = Math.Min(low, wire);
                    }

                    int first = low == int.MaxValue ? 0 : low / 64, high = 0;
                    Bases[k] = first;
                    for (int b = 0; b < 64; b++)
                    {
                        int index = wires[b] < 0 ? 0xFF : wires[b] - first * 64;
                        if (wires[b] >= 0 && index >= 128)
                            return;
                        if (wires[b] >= 0)
                            high = Math.Max(high, index);
                        masks[k * 64 + b] = (byte)index;
                    }

                    // The next vector of blocks only when this one reaches into it.
                    Loads = Math.Max(Loads, first + (high >= 64 ? 2 : 1));
                }

                // Words: each memory word padding only, or one whole aligned word of blocks.
                bool words = true;
                for (int w = 0; w < masks.Length && words; w += 4)
                {
                    bool padding = masks[w] == 0xFF && masks[w + 1] == 0xFF && masks[w + 2] == 0xFF &&
                                   masks[w + 3] == 0xFF;
                    words = padding || (masks[w] % 4 == 0 && masks[w + 1] == masks[w] + 1 &&
                                        masks[w + 2] == masks[w] + 2 && masks[w + 3] == masks[w] + 3);
                }

                if (!words && !Avx512Vbmi.IsSupported)
                    return;
                Masks = masks;
                Words = words;
                Vectors = vectors;
            }

            public int Base(int k) => Masks != null && k < Vectors ? Bases[k] : 0;

            public Vector512<byte> Mask(int k)
            {
                if (Masks == null || k >= Vectors)
                    return default;
                var mask = new byte[64];
                for (int b = 0; b < 64; b++)
                {
                    int index = Masks[k * 64 + b] == 0xFF ? 0 : Masks[k * 64 + b];
                    if (!Words)
                        mask[b] = (byte)index;
                    else if (b % 4 == 0)
                        Unsafe.WriteUnaligned(ref mask[b], index / 4);
                }

                return Vector512.Create(mask);
            }
        }

        /// <summary>
        /// Values moved a group at a time without AVX-512, in the direction of <typeparamref name="TDirection"/>:
        /// each vector written (32 bytes with AVX2, else 16: SSSE3, Arm64, WebAssembly on .NET 10) is made of 16-byte
        /// lanes, each the bytes of at most two 16-byte windows of the source shuffled within the lane (vpshufb issues
        /// twice a cycle on Zen 3 and Intel since Ice Lake, where cross-lane permutes issue once). The windows are at
        /// fixed offsets from the group's first value, so a load can start before it or reach past it. Stores start
        /// on a vector boundary where values allow it, else are unaligned.
        /// </summary>
        internal static class LanePlan<TDirection> where TDirection : struct, INinoDirection
        {
            // Before the constants read from it (static fields initialize in order).
            private static readonly NinoLanes Plan = Solve();

            /// <summary>Whether values are moved this way.</summary>
            public static readonly bool Enabled = Plan != null;

            /// <summary>The vector width (32 or 16), the values a group, and its vectors (1 to 4).</summary>
            public static readonly int Width = Plan?.Width ?? 0, Group = Plan?.Group ?? 0, Vectors = Plan?.Vectors ?? 0;

            /// <summary>The first value a group may start at: its windows start inside the source.</summary>
            public static readonly int Start = Plan?.Start ?? 0;

            private static readonly int End = Plan?.End ?? 0;

            // The windows of each vector (Window); NinoLanes.None for a second load not taken.
            private static readonly int W0 = Get(0), W1 = Get(1), W2 = Get(2), W3 = Get(3), W4 = Get(4), W5 = Get(5),
                W6 = Get(6), W7 = Get(7), W8 = Get(8), W9 = Get(9), W10 = Get(10), W11 = Get(11), W12 = Get(12),
                W13 = Get(13), W14 = Get(14), W15 = Get(15);

            /// <summary>The shuffle indices of each vector's first and second load (Arm64: of both, in the first).</summary>
            public static readonly Vector256<byte>[] Masks32 = Plan?.Masks32() ?? new Vector256<byte>[8];

            /// <inheritdoc cref="Masks32"/>
            public static readonly Vector128<byte>[] Masks16 = Plan?.Masks16() ?? new Vector128<byte>[8];

            private static int Get(int index) => Plan == null ? 0 : Plan.Windows[index];

            /// <summary>
            /// Where lane <paramref name="lane"/> of load <paramref name="load"/> of vector <paramref name="vector"/>
            /// starts, from the group's first value. Constants in, a constant out.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Window(int vector, int load, int lane)
            {
                int index = vector * 4 + load * 2 + lane;
                return index switch
                {
                    0 => W0, 1 => W1, 2 => W2, 3 => W3, 4 => W4, 5 => W5, 6 => W6, 7 => W7, 8 => W8, 9 => W9,
                    10 => W10, 11 => W11, 12 => W12, 13 => W13, 14 => W14, _ => W15
                };
            }

            /// <summary>
            /// Whether <paramref name="count"/> values hold three groups past <see cref="Start"/> (and the values moved
            /// alone to align the stores): fewer are moved one at a time.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Worth(int count) => Enabled && count >= Start + 3 * Group + 2;

            /// <summary>
            /// The last of <paramref name="count"/> values a group may start at: its loads stay inside the source
            /// (<paramref name="from"/> bytes a value) and its stores inside the destination (<paramref name="to"/> bytes a value).
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Last(int count, int from, int to)
            {
                long loads = ((long)count * from - End) / from;
                long stores = ((long)count * to - Vectors * Width) / to;
                return (int)Math.Min(loads, stores);
            }

            private static NinoLanes Solve()
            {
                if (Map == null || IsMemory || NinoBlock.Size<T, TFormatter>() > Unsafe.SizeOf<T>())
                    return null;
                bool shuffles = Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;
#if NET10_0_OR_GREATER
                shuffles |= PackedSimd.IsSupported;
#endif
                int width = Avx2.IsSupported ? 32 : shuffles ? 16 : 0;
                if (width == 0)
                    return null;
                var plan = NinoLanes.Solve(width, Map, NinoBlock.Size<T, TFormatter>(), Unsafe.SizeOf<T>(),
                    typeof(TDirection) == typeof(NinoWrites), AdvSimd.Arm64.IsSupported);
                // A value a group is what Shuffles does already, without the group loop around it.
                return plan is { Group: 1 } && Shuffles ? null : plan;
            }
        }

        /// <summary>Values per 64-byte vector (at least 2).</summary>
        public static readonly int PerVector = 64 / Unsafe.SizeOf<T>();

        /// <summary>
        /// Whether several values at a time are moved by one 64-byte byte permute (<see cref="NinoBlock"/>.Permute64):
        /// at least two fit, in memory and as blocks, with AVX-512 VBMI; with Arm64's 4-register table lookups, eight
        /// or more. A 4-register lookup issues at half the rate of a 1-register one (Arm's Neoverse optimization
        /// guides), so the four per 64 bytes pay off over a shuffle and a store per value from eight values on.
        /// </summary>
        public static readonly bool Permutes = Shuffles && PerVector >= 2 &&
                                               (Avx512Vbmi.IsSupported ||
                                                AdvSimd.Arm64.IsSupported && Unsafe.SizeOf<T>() <= 8);

        /// <summary><see cref="ToWire"/> for <see cref="PerVector"/> values.</summary>
        public static readonly Vector512<byte> ToWire64 = Mask64(toWire: true);

        /// <summary><see cref="ToMemory"/> for <see cref="PerVector"/> values.</summary>
        public static readonly Vector512<byte> ToMemory64 = Mask64(toWire: false);

        private static Vector512<byte> Mask64(bool toWire)
        {
            if (!Permutes)
                return default;
            var mask = new byte[64];
            mask.AsSpan().Fill(0x80);
            int size = NinoBlock.Size<T, TFormatter>();
            for (int e = 0; e < PerVector; e++)
            for (int i = 0; i < Map.Length; i += 3)
            for (int b = 0; b < Map[i + 2]; b++)
            {
                int memory = e * Unsafe.SizeOf<T>() + Map[i] + b, wire = e * size + Map[i + 1] + b;
                if (toWire)
                    mask[wire] = (byte)memory;
                else
                    mask[memory] = (byte)wire;
            }

            return Vector512.Create(mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> mask) =>
#if NET10_0_OR_GREATER
            // pshufb, tbl or i8x16.swizzle: one instruction on each platform Shuffles is on for.
            Vector128.ShuffleNative(value, mask);
#else
            Ssse3.IsSupported ? Ssse3.Shuffle(value, mask) : AdvSimd.Arm64.VectorTableLookup(value, mask);
#endif
#endif
    }
}
