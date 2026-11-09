using System;
using System.Buffers;
using System.Runtime.CompilerServices;

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
        public const int StackBytes = 512;

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
                span.CopyTo(destination);
                destination = destination.Slice(span.Length);
                if (position.GetObject() == null)
                    break;
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
