using System;
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
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() && default(TFormatter) is INinoBlockFormatter<T>
                ? ((INinoBlockFormatter<T>)default(TFormatter)).BlockSize
                : -1;

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
        public static unsafe void WriteAll<T, TFormatter>(ref byte destination, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlock<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.Copy(ref destination, ref Unsafe.As<T, byte>(ref first), (uint)(count * size));
                return;
            }

            int i = 0;
#if NET8_0_OR_GREATER
            // A group of values at a time, as full vectors of blocks: one two-source permute and one store each, none
            // overlapping (Groups).
            if (NinoBlock<T, TFormatter>.Groups && NinoBlock<T, TFormatter>.LastGroup(count) >= NinoBlock<T, TFormatter>.Group)
                i = WriteGroups<T, TFormatter>(ref destination, ref first, count, size);

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
        private static unsafe int WriteGroups<T, TFormatter>(ref byte destination, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            int i = 0;
            int group = NinoBlock<T, TFormatter>.Group;
            int last = NinoBlock<T, TFormatter>.LastGroup(count);
            ref byte source = ref Unsafe.As<T, byte>(ref first);
            // First the values before the first vector boundary of the blocks, one at a time: the blocks
            // usually follow a 4-byte length, and a store across two cache lines costs two (measured on an
            // AVX-512 Xeon, 1,000 12-byte blocks: 230 ns unaligned, 150 to 170 aligned). None when no value
            // starts on a boundary.
            {
                nuint mask = (nuint)NinoBlock<T, TFormatter>.GroupWidth - 1;
                nuint at = (nuint)Unsafe.AsPointer(ref destination);
                int skip = 0;
                while (skip < group && ((at + (nuint)(skip * size)) & mask) != 0)
                    skip++;
                if (skip < group)
                    for (; i < skip; i++)
                        ((INinoBlockFormatter<T>)default(TFormatter)).WriteBlock(
                            ref Unsafe.Add(ref destination, (nint)i * size), in Unsafe.Add(ref first, i));
            }

            if (Avx512F.IsSupported && NinoBlock<T, TFormatter>.GroupWords)
            {
                for (; i <= last; i += group)
                    Permute64x2Words<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                        (nuint)i * (nuint)size);
            }
            else if (Avx512Vbmi.IsSupported)
            {
                for (; i <= last; i += group)
                    Permute64x2<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                        (nuint)i * (nuint)size);
            }
            else if (Avx2.IsSupported)
            {
                for (; i <= last; i += group)
                    Permute32x2<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                        (nuint)i * (nuint)size);
            }
            else
            {
                for (; i <= last; i += group)
                    Lookup16x2<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                        (nuint)i * (nuint)size);
            }
            return i;
        }
#endif

        /// <summary>Reads <paramref name="count"/> values from <paramref name="source"/>, <paramref name="size"/> bytes apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void ReadAll<T, TFormatter>(ref byte source, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlock<T, TFormatter>.IsMemory)
            {
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref first), ref source, (uint)(count * size));
                return;
            }

            int i = 0;
#if NET8_0_OR_GREATER
            // As WriteAll, the other way. Padding is unspecified, as when a value is read a field at a time.
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            if (NinoBlock<T, TFormatter>.ReadPlan<NinoPhase0>.Enabled &&
                NinoBlock<T, TFormatter>.ReadPlan<NinoPhase0>.Last(count) >= NinoBlock<T, TFormatter>.Group)
                i = ReadGroups<T, TFormatter>(ref source, ref first, count, size);

            if (NinoBlock<T, TFormatter>.Permutes)
            {
                var toMemory = NinoBlock<T, TFormatter>.ToMemory64;
                int step = NinoBlock<T, TFormatter>.PerVector;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 64);
                // Where the values fill whole vectors (64 bytes a multiple of their size), the stores do not overlap:
                // first the values before the destination's first boundary (64 bytes with AVX-512, 16 for the four
                // Arm64 stores), one at a time, so that none straddles two cache lines. None when no value starts on
                // a boundary.
                if (64 % Unsafe.SizeOf<T>() == 0 && last >= step)
                {
                    nuint mask = Avx512Vbmi.IsSupported ? 63u : 15u;
                    nuint at = (nuint)Unsafe.AsPointer(ref destination);
                    int skip = 0;
                    while (skip < step && ((at + (nuint)(skip * Unsafe.SizeOf<T>())) & mask) != 0)
                        skip++;
                    if (skip < step)
                        for (; i < skip; i++)
                            ((INinoBlockFormatter<T>)default(TFormatter)).ReadBlock(
                                ref Unsafe.Add(ref source, (nint)i * size), ref Unsafe.Add(ref first, i));
                }

                for (; i <= last; i += step)
                    Permute64(ref source, (nuint)i * (nuint)size, ref destination, (nuint)i * (nuint)Unsafe.SizeOf<T>(),
                        toMemory);
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
        private static unsafe int ReadGroups<T, TFormatter>(ref byte source, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
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
            // First the values before the first one whose vectors start on a 64-byte boundary, one at a time (from
            // the first value, unaligned, when none does). With a phase, that value is read whole too: the group's
            // vectors start past its first bytes, and each group's last vector writes those of the next.
            int skip = 0;
            while (skip < group && ((at + (nuint)(skip * Unsafe.SizeOf<T>() + phase)) & 63) != 0)
                skip++;
            if (skip == group)
                skip = 0;
            for (; i < skip + (phase > 0 ? 1 : 0); i++)
                ((INinoBlockFormatter<T>)default(TFormatter)).ReadBlock(
                    ref Unsafe.Add(ref source, (nint)i * size), ref Unsafe.Add(ref first, i));
            i = skip;

            // The indices from an array, not constants, so they stay in registers: the JIT reloads a vector
            // constant at every use (1,000 (int, long) into an array: 135 to 150 ns, against 115 to 130 with them in
            // registers).
            var masks = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Masks;
            Vector512<byte> m0 = masks[0], m1 = masks[1], m2 = masks[2], m3 = masks[3];
            for (; i <= last; i += group)
                ReadGroup<T, TFormatter, TPhase>(ref source, (nuint)i * (nuint)size, ref destination,
                    (nuint)i * (nuint)Unsafe.SizeOf<T>() + (nuint)phase, m0, m1, m2, m3);
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
        /// <see cref="Permute64x2{T, TFormatter}"/> with AVX2: 32-byte vectors of 4-byte words, each word permuted from
        /// both memory vectors (vpermd reads an index's low three bits) and taken from the second where its index has
        /// the sign bit set (vblendvps).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute32x2<T, TFormatter>(ref byte source, nuint from, ref byte destination, nuint to)
            where TFormatter : struct, INinoFormatter<T>
        {
            int vectors = NinoBlock<T, TFormatter>.GroupVectors;
            var a = Vector256.LoadUnsafe(ref source, from).AsInt32();
            var b = Vector256.LoadUnsafe(ref source, from + 32).AsInt32();
            Words(a, b, NinoBlock<T, TFormatter>.Group0.GetLower().AsInt32()).StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            var c = Vector256.LoadUnsafe(ref source, from + 64).AsInt32();
            Words(b, c, NinoBlock<T, TFormatter>.Group1.GetLower().AsInt32()).StoreUnsafe(ref destination, to + 32);
            if (vectors < 3)
                return;
            var d = Vector256.LoadUnsafe(ref source, from + 96).AsInt32();
            Words(c, d, NinoBlock<T, TFormatter>.Group2.GetLower().AsInt32()).StoreUnsafe(ref destination, to + 64);
            if (vectors < 4)
                return;
            var e = Vector256.LoadUnsafe(ref source, from + 128).AsInt32();
            Words(d, e, NinoBlock<T, TFormatter>.Group3.GetLower().AsInt32()).StoreUnsafe(ref destination, to + 96);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Words(Vector256<int> low, Vector256<int> high, Vector256<int> indices) =>
            Avx.BlendVariable(Avx2.PermuteVar8x32(low, indices).AsSingle(),
                Avx2.PermuteVar8x32(high, indices).AsSingle(), indices.AsSingle()).AsByte();

        /// <summary>
        /// <see cref="Permute64x2{T, TFormatter}"/> with 16-byte vectors: a two-register table lookup on Arm64, or on
        /// WebAssembly (.NET 10) two swizzles ORed, the second taking indices 16 to 31 (any other index swizzles to 0).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lookup16x2<T, TFormatter>(ref byte source, nuint from, ref byte destination, nuint to)
            where TFormatter : struct, INinoFormatter<T>
        {
            int vectors = NinoBlock<T, TFormatter>.GroupVectors;
            var a = Vector128.LoadUnsafe(ref source, from);
            var b = Vector128.LoadUnsafe(ref source, from + 16);
            Lookup(a, b, NinoBlock<T, TFormatter>.Group0.GetLower().GetLower()).StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            var c = Vector128.LoadUnsafe(ref source, from + 32);
            Lookup(b, c, NinoBlock<T, TFormatter>.Group1.GetLower().GetLower()).StoreUnsafe(ref destination, to + 16);
            if (vectors < 3)
                return;
            var d = Vector128.LoadUnsafe(ref source, from + 48);
            Lookup(c, d, NinoBlock<T, TFormatter>.Group2.GetLower().GetLower()).StoreUnsafe(ref destination, to + 32);
            if (vectors < 4)
                return;
            var e = Vector128.LoadUnsafe(ref source, from + 64);
            Lookup(d, e, NinoBlock<T, TFormatter>.Group3.GetLower().GetLower()).StoreUnsafe(ref destination, to + 48);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Lookup(Vector128<byte> low, Vector128<byte> high, Vector128<byte> indices)
        {
#if NET10_0_OR_GREATER
            if (PackedSimd.IsSupported)
                return PackedSimd.Swizzle(low, indices) | PackedSimd.Swizzle(high, indices - Vector128.Create((byte)16));
#endif
            return AdvSimd.Arm64.VectorTableLookup((low, high), indices);
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
#endif

    /// <summary>
    /// How values of <typeparamref name="T"/> map to their blocks (<see cref="INinoBlockFormatter{T}.BlockMap"/>),
    /// worked out once. The flags are JIT-time constants.
    /// </summary>
    internal static class NinoBlock<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
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
        /// The vector width of <see cref="Groups"/>: 64 bytes with AVX-512 (a two-source permute of 4-byte words, or
        /// with VBMI of bytes), 32 with AVX2 (4-byte words only), 16 on Arm64 (a two-register table lookup) and
        /// WebAssembly on .NET 10 (two swizzles); 0 when none applies.
        /// </summary>
        public static readonly int GroupWidth = ChooseGroupWidth();

        /// <summary>
        /// Whether the 64-byte vectors of blocks move whole 4-byte words (vpermt2d): one micro-op at one a cycle on
        /// Intel, where vpermt2b takes three and issues every other cycle (1,000 12-byte blocks of 16-byte values on an
        /// AVX-512 Xeon: 121 ns with words, 150 to 190 with bytes, 123 for a copy of their memory).
        /// </summary>
        public static readonly bool GroupWords = GroupWidth == 64 && Words(GroupMasks(64));

        /// <summary>
        /// Whether values are written <see cref="Group"/> at a time as <see cref="GroupVectors"/> full vectors of
        /// blocks, each one two-source permute of two consecutive vectors of their memory: blocks are smaller than
        /// values (padding left out), so a value at a time stores a vector for fewer bytes, while a group stores each
        /// vector once (a quarter fewer stores than a copy of the values' memory for 12-byte blocks of 16-byte values).
        /// Only where each vector of blocks k draws on memory vectors k and k + 1, with up to four vectors a group.
        /// </summary>
        public static readonly bool Groups = GroupWidth != 0;

        /// <summary>Values per group: the fewest whose blocks fill whole vectors.</summary>
        public static readonly int Group = Groups ? GroupWidth / Gcd(NinoBlock.Size<T, TFormatter>(), GroupWidth) : 0;

        /// <summary>Vectors of blocks per group (1 to 4); one memory vector more is read.</summary>
        public static readonly int GroupVectors = Groups ? Group * NinoBlock.Size<T, TFormatter>() / GroupWidth : 0;

        /// <summary>
        /// The permute indices of each vector of blocks in a group: bytes (Arm64: the low 16), or for
        /// <see cref="GroupWords"/> one int per word (0 to 31), or with AVX2 the low 32 bytes as one int per word, the
        /// sign bit set for a word of the second memory vector.
        /// </summary>
        public static readonly Vector512<byte> Group0 = GroupMask(0), Group1 = GroupMask(1), Group2 = GroupMask(2),
            Group3 = GroupMask(3);

        /// <summary>The last of <paramref name="count"/> values a group may start at: its reads stay inside the values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastGroup(int count) =>
            count - Math.Max(Group, ((GroupVectors + 1) * GroupWidth + Unsafe.SizeOf<T>() - 1) / Unsafe.SizeOf<T>());

        private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

        private static int ChooseGroupWidth()
        {
            if (Map == null || IsMemory || NinoBlock.Size<T, TFormatter>() >= Unsafe.SizeOf<T>())
                return 0;
            if (Avx512F.IsSupported && GroupMasks(64) is { } masks && (Avx512Vbmi.IsSupported || Words(masks)))
                return 64;
            if (Avx2.IsSupported)
                return Words(GroupMasks(32)) ? 32 : 0;
            bool lookups = AdvSimd.Arm64.IsSupported;
#if NET10_0_OR_GREATER
            lookups |= PackedSimd.IsSupported;
#endif
            return lookups && GroupMasks(16) != null ? 16 : 0;
        }

        private static Vector512<byte> GroupMask(int vector)
        {
            if (!Groups || vector >= GroupVectors)
                return default;
            var masks = GroupMasks(GroupWidth);
            var mask = new byte[64];
            if (GroupWidth == 32 || GroupWords)
            {
                // Words: the byte index of a word's first byte, over 4; with AVX2 the sign bit for the second vector.
                for (int word = 0; word < GroupWidth / 4; word++)
                {
                    int index = masks[vector * GroupWidth + word * 4] / 4;
                    Unsafe.WriteUnaligned(ref mask[word * 4],
                        index | (GroupWidth == 32 && index >= 8 ? int.MinValue : 0));
                }
            }
            else
            {
                Array.Copy(masks, vector * GroupWidth, mask, 0, GroupWidth);
            }

            return Vector512.Create(mask);
        }

        /// <summary>
        /// For each byte of each vector of blocks in a group of <paramref name="width"/>-byte vectors, the byte of
        /// memory vectors k and k + 1 it comes from; null when a group would take more than four vectors or a vector of
        /// blocks draws on other memory vectors.
        /// </summary>
        private static byte[] GroupMasks(int width)
        {
            int size = NinoBlock.Size<T, TFormatter>();
            int group = width / Gcd(size, width), vectors = group * size / width;
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
                int group = 64 / Gcd(size, 64), vectors = group * memorySize / 64;
                // A phase only where values can start that far past a boundary (8-byte aligned arrays).
                if (group * memorySize % 64 != 0 || vectors > 4 || phase >= Gcd(memorySize, 64))
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
