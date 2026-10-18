using System;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
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
            // Several values as one 64-byte vector, their bytes moved to their places in the block by one byte
            // permute (AVX-512 VBMI); the vector's tail is overwritten by the next one.
            if (NinoBlock<T, TFormatter>.Permutes)
            {
                var toWire = NinoBlock<T, TFormatter>.ToWire64;
                int step = NinoBlock<T, TFormatter>.PerVector;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 64);
                ref byte source = ref Unsafe.As<T, byte>(ref first);
                for (; i <= last; i += step)
                    Vector512.StoreUnsafe(
                        Avx512Vbmi.PermuteVar64x8(
                            Vector512.LoadUnsafe(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>()), toWire),
                        ref destination, (nuint)i * (nuint)size);
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
                    Vector512.StoreUnsafe(
                        Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref source, (nuint)i * (nuint)size), toMemory) &
                        fields,
                        ref destination, (nuint)i * (nuint)Unsafe.SizeOf<T>());
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
        /// CPU shuffles bytes by a variable mask (SSSE3, or Arm64).
        /// </summary>
        public static readonly bool Shuffles = Map != null && !IsMemory && Unsafe.SizeOf<T>() <= 16 &&
                                               NinoBlock.Size<T, TFormatter>() <= 16 &&
                                               (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported);

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
        /// Whether several values at a time are moved by one 64-byte byte permute (AVX-512 VBMI): at least two fit, in
        /// memory and as blocks.
        /// </summary>
        public static readonly bool Permutes = Shuffles && Avx512Vbmi.IsSupported && PerVector >= 2;

        /// <summary><see cref="ToWire"/> for <see cref="PerVector"/> values.</summary>
        public static readonly Vector512<byte> ToWire64 = Mask64(toWire: true);

        /// <summary><see cref="ToMemory"/> for <see cref="PerVector"/> values.</summary>
        public static readonly Vector512<byte> ToMemory64 = Mask64(toWire: false);

        /// <summary>0xFF for the memory bytes of fields, 0 for padding: the permute has no zeroing index.</summary>
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
            Ssse3.IsSupported ? Ssse3.Shuffle(value, mask) : AdvSimd.Arm64.VectorTableLookup(value, mask);
#endif
    }
}
