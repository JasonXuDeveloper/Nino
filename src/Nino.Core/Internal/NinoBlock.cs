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
            if (NinoBlock<T, TFormatter>.Groups)
            {
                int group = NinoBlock<T, TFormatter>.Group;
                int last = NinoBlock<T, TFormatter>.LastGroup(count);
                ref byte source = ref Unsafe.As<T, byte>(ref first);
                // First the values before the first vector boundary of the blocks, one at a time: the blocks
                // usually follow a 4-byte length, and a store across two cache lines costs two (measured on an
                // AVX-512 Xeon, 1,000 12-byte blocks: 230 ns unaligned, 150 to 170 aligned). None when no value
                // starts on a boundary.
                if (last >= group)
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

                if (Avx512Vbmi.IsSupported)
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
            }

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
                        toWire, default, keepAll: true);
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
            // As WriteAll, the other way: padding bytes come out zero.
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            if (NinoBlock<T, TFormatter>.Permutes)
            {
                var toMemory = NinoBlock<T, TFormatter>.ToMemory64;
                var fields = NinoBlock<T, TFormatter>.Fields64;
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
                        toMemory, fields, keepAll: false);
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
        /// One group of values (<see cref="NinoBlock{T, TFormatter}.Groups"/>) with AVX-512 VBMI: vector k of blocks is
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
        /// names, zero for an index from 0x80 up. AVX-512 VBMI's permute (which reads only an index's low six bits:
        /// <paramref name="keep"/> zeroes those bytes, unless <paramref name="keepAll"/>), or on Arm64 four
        /// 4-register table lookups, one per 16 bytes written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64(ref byte source, nuint from, ref byte destination, nuint to,
            Vector512<byte> indices, Vector512<byte> keep, bool keepAll)
        {
            if (Avx512Vbmi.IsSupported)
            {
                var permuted = Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref source, from), indices);
                (keepAll ? permuted : permuted & keep).StoreUnsafe(ref destination, to);
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
        /// The vector width of <see cref="Groups"/>: 64 bytes with AVX-512 VBMI (a two-source byte permute), 32 with
        /// AVX2 (4-byte words only), 16 on Arm64 (a two-register table lookup) and WebAssembly on .NET 10 (two
        /// swizzles); 0 without any.
        /// </summary>
        public static readonly int GroupWidth = Avx512Vbmi.IsSupported ? 64 : Avx2.IsSupported ? 32 :
            AdvSimd.Arm64.IsSupported
#if NET10_0_OR_GREATER
            || PackedSimd.IsSupported
#endif
                ? 16
                : 0;

        /// <summary>
        /// Whether values are written <see cref="Group"/> at a time as <see cref="GroupVectors"/> full vectors of
        /// blocks, each one two-source permute of two consecutive vectors of their memory: blocks are smaller than
        /// values (padding left out), so a value at a time stores a vector for fewer bytes, while a group stores each
        /// vector once (a quarter fewer stores than a copy of the values' memory for 12-byte blocks of 16-byte values).
        /// Only where each vector of blocks k draws on memory vectors k and k + 1, with up to four vectors a group.
        /// </summary>
        public static readonly bool Groups = Map != null && !IsMemory && GroupWidth != 0 &&
                                             NinoBlock.Size<T, TFormatter>() < Unsafe.SizeOf<T>() &&
                                             GroupMasks() != null;

        /// <summary>Values per group: the fewest whose blocks fill whole vectors.</summary>
        public static readonly int Group = Groups ? GroupWidth / Gcd(NinoBlock.Size<T, TFormatter>(), GroupWidth) : 0;

        /// <summary>Vectors of blocks per group (1 to 4); one memory vector more is read.</summary>
        public static readonly int GroupVectors = Groups ? Group * NinoBlock.Size<T, TFormatter>() / GroupWidth : 0;

        /// <summary>
        /// The permute indices of each vector of blocks in a group: bytes (Arm64: the low 16), or with AVX2 the low 32
        /// bytes as one int per word, the sign bit set for a word of the second memory vector.
        /// </summary>
        public static readonly Vector512<byte> Group0 = GroupMask(0), Group1 = GroupMask(1), Group2 = GroupMask(2),
            Group3 = GroupMask(3);

        /// <summary>The last of <paramref name="count"/> values a group may start at: its reads stay inside the values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastGroup(int count) =>
            count - Math.Max(Group, ((GroupVectors + 1) * GroupWidth + Unsafe.SizeOf<T>() - 1) / Unsafe.SizeOf<T>());

        private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

        private static Vector512<byte> GroupMask(int vector)
        {
            if (!Groups || vector >= GroupVectors)
                return default;
            var masks = GroupMasks();
            var mask = new byte[64];
            if (GroupWidth == 32)
            {
                // Words: the byte index of a word's first byte, over 4; the sign bit for the second vector.
                for (int word = 0; word < 8; word++)
                {
                    int index = masks[vector * 32 + word * 4] / 4;
                    Unsafe.WriteUnaligned(ref mask[word * 4], index | (index >= 8 ? int.MinValue : 0));
                }
            }
            else
            {
                Array.Copy(masks, vector * GroupWidth, mask, 0, GroupWidth);
            }

            return Vector512.Create(mask);
        }

        /// <summary>
        /// For each byte of each vector of blocks in a group, the byte of memory vectors k and k + 1 it comes from;
        /// null when a group would take more than four vectors or a vector of blocks draws on other memory vectors.
        /// </summary>
        private static byte[] GroupMasks()
        {
            int width = GroupWidth, size = NinoBlock.Size<T, TFormatter>();
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

            // AVX2 moves 4-byte words: each word of blocks is one whole word of memory.
            if (width == 32)
                for (int word = 0; word < masks.Length; word += 4)
                    if (masks[word] % 4 != 0 || masks[word + 1] != masks[word] + 1 || masks[word + 2] != masks[word] + 2 ||
                        masks[word + 3] != masks[word] + 3)
                        return null;
            return masks;
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

        /// <summary>0xFF for the memory bytes of fields, 0 for padding: VBMI's permute has no zeroing index.</summary>
        public static readonly Vector512<byte> Fields64 = Vector512.LessThan(ToMemory64, Vector512.Create((byte)0x80));

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
