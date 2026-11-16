using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>What <see cref="NinoScratch"/> writes into the scratch buffer: a struct, so the call is direct and inlined.</summary>
    internal interface INinoScratchWriter<T>
    {
        void Write(T value, NinoArrayBufferWriter buffer);
    }

    /// <summary>
    /// The per-thread scratch buffer of the top-level calls that do not write into a buffer of the caller's: the value
    /// is written into it, then copied out (into a new array, or another buffer writer).
    /// </summary>
    internal static class NinoScratch
    {
        [ThreadStatic] private static NinoArrayBufferWriter t_scratch;

        /// <summary>Largest scratch buffer kept per thread between calls.</summary>
        private const int MaxRetainedScratch = 1024 * 1024;

        /// <summary>
        /// Writes <paramref name="value"/> with <paramref name="writer"/> into the scratch buffer and returns a copy of
        /// the bytes. AggressiveInlining: the JIT counts the inlines under a method it inlined without the attribute
        /// against the caller's budget, which left the copies and writes under it as calls.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] ToArray<T, TWriter>(T value, TWriter writer) where TWriter : struct, INinoScratchWriter<T>
        {
            var buffer = Rent();
            try
            {
                writer.Write(value, buffer);
                return buffer.ToArray();
            }
            finally
            {
                Return(buffer);
            }
        }

        /// <summary>Writes <paramref name="value"/> with <paramref name="writer"/> into the scratch buffer, then copies it to <paramref name="target"/>.</summary>
        public static void CopyTo<T, TWriter>(T value, TWriter writer, IBufferWriter<byte> target)
            where TWriter : struct, INinoScratchWriter<T>
        {
            var buffer = Rent();
            try
            {
                writer.Write(value, buffer);
                CopyWritten(buffer, target);
            }
            finally
            {
                Return(buffer);
            }
        }

        /// <summary>
        /// Copies the bytes written into <paramref name="buffer"/> to <paramref name="target"/> with Nino's block copy
        /// (BuffersExtensions.Write copies with Memmove: glibc's rep movsb on AMD, see NinoUnsafe.CopyOver64). Out of
        /// line: one body for every value type.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CopyWritten(NinoArrayBufferWriter buffer, IBufferWriter<byte> target)
        {
            var written = buffer.WrittenSpan;
            var destination = target.GetSpan(written.Length);
            if (destination.Length >= written.Length && !written.Overlaps(destination))
            {
                NinoUnsafe.Copy(ref MemoryMarshal.GetReference(destination), ref MemoryMarshal.GetReference(written),
                    (uint)written.Length);
                target.Advance(written.Length);
            }
            else
                // A writer that hands out less than asked for, in chunks; or a span over the scratch buffer.
                target.Write(written);
        }

        // The thread's scratch buffer stays in its slot and is marked in use (a byte store on the instance) instead
        // of being taken out and put back: returning it then neither reads the thread-static again nor stores a
        // reference into it (a write barrier). A nested call on the same thread (from a custom formatter) finds it in
        // use and gets a buffer of its own. Internal for the Stream overloads (NinoStreams); give it back with Return.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static NinoArrayBufferWriter Rent()
        {
            var buffer = t_scratch;
            if (buffer == null || buffer.ScratchState != NinoArrayBufferWriter.ScratchIdle)
                return RentOther();
            buffer.ScratchState = NinoArrayBufferWriter.ScratchInUse;
            buffer.Index = 0;
            return buffer;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoArrayBufferWriter RentOther()
        {
            var buffer = new NinoArrayBufferWriter();
            if (t_scratch == null)
            {
                buffer.ScratchState = NinoArrayBufferWriter.ScratchInUse;
                t_scratch = buffer;
            }

            return buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Return(NinoArrayBufferWriter buffer)
        {
            if (buffer.ScratchState == NinoArrayBufferWriter.ScratchInUse && buffer.Buffer.Length <= MaxRetainedScratch)
                buffer.ScratchState = NinoArrayBufferWriter.ScratchIdle;
            else
                Release(buffer);
        }

        /// <summary>A buffer that is not kept: one a nested call created, or the scratch buffer grown too large.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Release(NinoArrayBufferWriter buffer)
        {
            if (ReferenceEquals(t_scratch, buffer))
                t_scratch = null;
            buffer.ScratchState = 0;
            buffer.Dispose();
        }
    }
}
