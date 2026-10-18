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
        public static void WriteAll<T, TFormatter>(ref byte destination, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlock<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.Copy(ref destination, ref Unsafe.As<T, byte>(ref first), (uint)(count * size));
                return;
            }

            int i = 0;
#if NET8_0_OR_GREATER
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
        public static void ReadAll<T, TFormatter>(ref byte source, ref T first, int count, int size)
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
