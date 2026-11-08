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
        public static byte[] Rent(in ReadOnlySequence<byte> data, out int length)
        {
            // A span holds at most int.MaxValue bytes; the data is already in memory, so the copy is in proportion.
            if (data.Length > int.MaxValue)
                throw new ArgumentException("Nino: data longer than int.MaxValue bytes.", nameof(data));
            length = (int)data.Length;
            byte[] rented = ArrayPool<byte>.Shared.Rent(length);
            data.CopyTo(rented);
            return rented;
        }

        public static void Return(byte[] rented) => ArrayPool<byte>.Shared.Return(rented);
    }
}
