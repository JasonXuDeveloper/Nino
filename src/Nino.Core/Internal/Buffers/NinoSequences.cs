using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The contiguous copy of a <see cref="ReadOnlySequence{T}"/> of several segments, which the sequence overloads of
    /// <see cref="NinoDeserializer"/> and <c>NinoDirect</c> read from (one segment is read in place, with no copy).
    /// </summary>
    internal static class NinoSequences
    {
        /// <summary>
        /// Data up to this many bytes is copied to the stack instead (the module skips zeroing locals), saving the
        /// pool's rent and return on the small messages pipes most often split.
        /// </summary>
        public const int StackBytes = 1024;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasTwoSegments(in ReadOnlySequence<byte> data) =>
            data.Start.GetObject() is ReadOnlySequenceSegment<byte> first &&
            ReferenceEquals(first.Next, data.End.GetObject());

        /// <summary>Reads a complete byte array whose header and payload fit in two segments.</summary>
        public static bool TryReadByteArray(in ReadOnlySequence<byte> data, long length, ref byte[] value)
        {
            if (NinoRawArray<byte[]>.Formatter is not UnmanagedArrayFormatter<byte>)
                return false;

            var position = data.Start;
            if (!data.TryGet(ref position, out var first))
                return false;
            if (first.Length < NinoWire.LengthSize)
                return TryReadSplitByteArray(data, position, first, length, ref value);
            if (!data.TryGet(ref position, out var second) || position.GetObject() != null ||
                (long)first.Length + second.Length != length)
                return false;
            var prefix = first.Span;
            int count = NinoUnsafe.Read<int>(ref MemoryMarshal.GetReference(prefix));
            if (count <= 0 || (long)count + NinoWire.LengthSize != length || NinoLimits.TooLong(count))
                return false;

            return CopyByteArray(prefix.Slice(NinoWire.LengthSize), second.Span, count, ref value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CopyByteArray(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int count, ref byte[] value)
        {
            if (value != null)
            {
                if (value.Length != count)
                    return ReplaceByteArray(left, right, count, ref value);
                if (left.Overlaps(value) || right.Overlaps(value))
                    return false;
            }

            var array = value ?? NinoUnsafe.NewArray<byte>(count, uninitialized: true);
            CopyPayload(left, right, array);
            value = array;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReplaceByteArray(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int count,
            ref byte[] value)
        {
            // Recycling may mutate the input. The regular path snapshots it before invoking callbacks.
            if (NinoRecycler<byte>.MayRecycle || NinoRecycler<byte[]>.MayRecycle)
                return false;

            var replacement = NinoUnsafe.NewArray<byte>(count, uninitialized: true);
            CopyPayload(left, right, replacement);
            value = replacement;
            return true;
        }

        // The callers checked that the array does not overlap either span, and that the two fill it. CopyToNew, as the
        // raw array reads: the array is new (often uninitialized) or one the caller keeps.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyPayload(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, byte[] array)
        {
            ref byte destination = ref NinoUnsafe.GetArrayDataReference(array);
            NinoUnsafe.CopyToNew(ref destination, ref MemoryMarshal.GetReference(left), (uint)left.Length);
            NinoUnsafe.CopyToNew(ref Unsafe.Add(ref destination, left.Length), ref MemoryMarshal.GetReference(right),
                (uint)right.Length);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadSplitByteArray(in ReadOnlySequence<byte> data, SequencePosition position,
            ReadOnlyMemory<byte> first, long length, ref byte[] value)
        {
            if (length < NinoWire.LengthSize || !data.TryGet(ref position, out var second) ||
                position.GetObject() != null || (long)first.Length + second.Length != length)
                return false;

            var right = second.Span;
            int rest = NinoWire.LengthSize - first.Length;
            var left = first.Span;
            // The two spans were checked to contain the full header.
            int count = first.Length switch
            {
                0 => BinaryPrimitives.ReadInt32LittleEndian(right),
                1 => left[0] | BinaryPrimitives.ReadUInt16LittleEndian(right) << 8 | right[2] << 24,
                2 => BinaryPrimitives.ReadUInt16LittleEndian(left) | BinaryPrimitives.ReadUInt16LittleEndian(right) << 16,
                _ => BinaryPrimitives.ReadUInt16LittleEndian(left) | left[2] << 16 | right[0] << 24,
            };
            if (!BitConverter.IsLittleEndian)
                count = BinaryPrimitives.ReverseEndianness(count);
            if (count <= 0 || (long)count + NinoWire.LengthSize != length || NinoLimits.TooLong(count))
                return false;

            return CopyByteArray(default, right.Slice(rest), count, ref value);
        }

        /// <summary>
        /// Copies <paramref name="data"/> into an array rented from the shared pool, <paramref name="length"/> bytes
        /// long; give it back with <see cref="Return"/> once read. Nothing a read creates keeps a reference to its
        /// input, so the array can go back as soon as the read returns.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static byte[] Rent(in ReadOnlySequence<byte> data, long length)
        {
            // A span holds at most int.MaxValue bytes; the data is already in memory, so the copy is in proportion.
            if (length > int.MaxValue)
                throw new ArgumentException("Nino: data longer than int.MaxValue bytes.", nameof(data));
            byte[] rented = ArrayPool<byte>.Shared.Rent((int)length);
            try
            {
                CopySegments(data, rented.AsSpan(0, (int)length));
                return rented;
            }
            catch
            {
                Return(rented);
                throw;
            }
        }

        /// <summary>Copies a segmented sequence into a destination sized for its reported length.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void CopySegments(in ReadOnlySequence<byte> data, Span<byte> destination)
        {
            var position = data.Start;
            while (data.TryGet(ref position, out var segment))
            {
                var span = segment.Span;
                if (!span.IsEmpty)
                {
                    // A custom segment's links can supply more bytes than its RunningIndex reports, too.
                    if (span.Length > destination.Length)
                        InvalidLength();
                    if (span.Overlaps(destination))
                        span.CopyTo(destination);
                    else
                        NinoUnsafe.Copy(ref MemoryMarshal.GetReference(destination), ref MemoryMarshal.GetReference(span),
                            (uint)span.Length);
                    destination = destination.Slice(span.Length);
                }
            }

            // A custom segment's RunningIndex can report more bytes than its links supply.
            if (!destination.IsEmpty)
                InvalidLength();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InvalidLength() => throw new ArgumentException("Nino: inconsistent sequence length.", "data");

        public static void Return(byte[] rented) => ArrayPool<byte>.Shared.Return(rented);
    }
}
