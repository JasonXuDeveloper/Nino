using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>The lengths of a multidimensional array (1 past its rank) and its element count.</summary>
    internal struct ArrayShape
    {
        public int A, B, C, D, Total;
    }

    /// <summary>The lengths of the multidimensional array formatters.</summary>
    internal static class MultiDim
    {
        /// <summary>Writes the length of each dimension of <paramref name="value"/> (not null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLengths(Array value, int rank, ref Writer writer)
        {
            ref byte p = ref writer.Reserve(rank * NinoWire.LengthSize);
            WriteLengths(value, rank, ref p);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteLengths(Array value, int rank, ref byte p)
        {
            NinoUnsafe.Write(ref p, 0, value.GetLength(0));
            NinoUnsafe.Write(ref p, NinoWire.LengthSize, value.GetLength(1));
            if (rank > 2)
                NinoUnsafe.Write(ref p, 2 * NinoWire.LengthSize, value.GetLength(2));
            if (rank > 3)
                NinoUnsafe.Write(ref p, 3 * NinoWire.LengthSize, value.GetLength(3));
        }

#if NET8_0_OR_GREATER
        internal static bool TryWriteRawExternal<T, TFormatter>(Array value, int rank, IBufferWriter<byte> buffer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!BitConverter.IsLittleEndian || !RawFormatter.Is<T, TFormatter>() || value == null)
                return false;

            int count = value.Length;
            int header = rank * NinoWire.LengthSize;
            long size = (long)count * Unsafe.SizeOf<T>() + header;
            if (size > int.MaxValue)
                return false;

            // The normal formatter starts at the zero index in each dimension.
            if (count != 0 && (value.GetLowerBound(0) != 0 || value.GetLowerBound(1) != 0 ||
                rank > 2 && value.GetLowerBound(2) != 0 || rank > 3 && value.GetLowerBound(3) != 0))
                return false;

            int payload = (int)size - header;
            var source = MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetArrayDataReference(value), payload);
            var destination = buffer.GetSpan((int)size);
            // An external destination can overlap the source. Write the shape after copying the elements.
            NinoLayout<T>.CopyRawExternal(source, destination.Slice(header, payload), count);
            WriteLengths(value, rank, ref MemoryMarshal.GetReference(destination));
            buffer.Advance((int)size);
            return true;
        }
#endif

        /// <summary>
        /// Reads the <paramref name="rank"/> lengths (false when the first is -1, for null) and checks them: rejects
        /// negative lengths, lengths no array can have and more elements than bytes left (every element takes at
        /// least <paramref name="minElementSize"/> bytes, <see cref="NinoElements.MinWireSize{T,TFormatter}"/>). The
        /// first length alone is not checked against the remaining bytes: another dimension may be 0, so a single
        /// length says nothing about the payload.
        /// </summary>
        public static bool TryReadShape(ref Reader reader, int rank, int minElementSize, out ArrayShape shape)
        {
            int a;
            int headerSize = rank * NinoWire.LengthSize;
            ref byte tail = ref reader.Peek();
            if (!NinoLimits.LengthCapped && reader.Remaining >= headerSize &&
                (a = NinoUnsafe.Read<int>(ref tail)) >= 0)
            {
                reader.UnsafeAdvance(headerSize);
                tail = ref Unsafe.Add(ref tail, NinoWire.LengthSize);
            }
            else
            {
                // A null header ends at the first length. Preserve its checks before reading the tail.
                if (!reader.TryReadLength(0, out a))
                {
                    shape = default;
                    return false;
                }
                tail = ref reader.ReadBlock(headerSize - NinoWire.LengthSize);
            }

            int b = NinoUnsafe.Read<int>(ref tail);
            int c = rank > 2 ? NinoUnsafe.Read<int>(ref tail, NinoWire.LengthSize) : 1;
            int d = rank > 3 ? NinoUnsafe.Read<int>(ref tail, 2 * NinoWire.LengthSize) : 1;
            // Any sign bit or bit 30 requires an exact dimension check.
            if (((a | b | c | d) & unchecked((int)0xC0000000)) != 0)
                CheckDimensions(a, b, c, d);
            long total = (long)a * b;
            if (rank > 2)
            {
                // Check each prefix before multiplying to keep the next product in range.
                // CoreCLR also rejects a trailing-zero shape whose leading dimensions exceed this limit.
                if (total > uint.MaxValue)
                    NinoThrow.InvalidLength(int.MaxValue);
                total *= c;
                if (rank > 3)
                {
                    if (total > uint.MaxValue)
                        NinoThrow.InvalidLength(int.MaxValue);
                    total *= d;
                }
            }

            // Once the final product fits in a uint, multiplying by the minimum wire size fits in a long.
            if (total > uint.MaxValue || total * minElementSize > reader.Remaining ||
                NinoLimits.LengthCapped && total > NinoLimits.MaxCollectionLength)
                NinoThrow.InvalidLength((int)Math.Min(total, int.MaxValue));
            shape.A = a;
            shape.B = b;
            shape.C = c;
            shape.D = d;
            shape.Total = (int)total;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckDimensions(int a, int b, int c, int d)
        {
            if (b < 0 || c < 0 || d < 0)
                NinoThrow.InvalidLength(Math.Min(b, Math.Min(c, d)));
            // Large dimensions may still fit the runtime limit.
            int longest = Math.Max(Math.Max(a, b), Math.Max(c, d));
            if (longest > NinoUnsafe.MaxArrayLength)
                NinoThrow.InvalidLength(longest);
        }

        /// <summary>
        /// Copies <paramref name="items"/> into the elements of a multidimensional array starting at
        /// <paramref name="first"/>, as many as it holds.
        /// </summary>
        public static void Fill<T>(T[] items, ref T first)
        {
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
            {
                items.AsSpan().CopyTo(MemoryMarshal.CreateSpan(ref first, items.Length));
                return;
            }

            for (int i = 0; i < items.Length; i++)
                Unsafe.Add(ref first, i) = items[i];
        }

        /// <summary>Whether <paramref name="array"/> is not null and of <paramref name="shape"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SameShape(Array array, int rank, in ArrayShape shape)
        {
            if (array == null || array.GetLength(0) != shape.A || array.GetLength(1) != shape.B)
                return false;
            return (rank < 3 || array.GetLength(2) == shape.C) && (rank < 4 || array.GetLength(3) == shape.D);
        }
    }
}
