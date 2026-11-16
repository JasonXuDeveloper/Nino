using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Strings: the default encoding (NinoStrings) and UTF-8.
    public ref partial struct Writer
    {
        /// <summary>
        /// Writes a string: one byte per char when all its chars are Latin-1 (ASCII included), UTF-16 otherwise
        /// (see <see cref="NinoWire"/>).
        /// </summary>
        // Inlined, with the encoding out of line taking values (see WriteUtf8).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string value) => Skip(NinoStrings.Write(ref Ensure(NinoStrings.MaxSize(value)), value));

        /// <summary>Longest string <see cref="WriteUtf8"/> reserves 3 bytes per char for.</summary>
        private const int Utf8WorstCaseMaxChars = 1024;

        /// <summary>
        /// Writes a string as UTF-8: its UTF-16 length (char count) plus one (0 for null, 1 for empty, with nothing
        /// after either), then the UTF-8 byte count and the bytes, both counts headers (see <see cref="NinoStrings"/>).
        /// Unpaired surrogates are written as U+FFFD, one char each, so the char count is that of the string read back.
        /// </summary>
        // Inlined, with the counting and the encoding out of line taking values: an instance call would take the
        // writer's address, which keeps all its fields in memory in the caller.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUtf8(string value)
        {
            if (value == null)
            {
                Ensure(1) = NinoStrings.NullHeader;
                Skip(1);
                return;
            }

            // Short strings reserve the worst case (3 bytes per char) and encode in one pass; long ones count their
            // bytes first, so the buffer does not grow to three times the string (or overflow the count).
            int maxBytes = value.Length <= Utf8WorstCaseMaxChars
                ? value.Length * 3
                : Utf8ByteCount(value);
            // Room for the worst case; only the bytes written (counts included) are kept.
            ref byte dst = ref Ensure(2 * NinoStrings.MaxHeaderSize + maxBytes);
            Skip(EncodeUtf8(value, ref dst, maxBytes));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Utf8ByteCount(string value)
        {
            int count = Encoding.UTF8.GetByteCount(value);
            // The counts and the bytes must fit in an int (see Grow).
            return count > int.MaxValue - 2 * NinoStrings.MaxHeaderSize ? -1 : count;
        }

        /// <summary>
        /// Writes the counts and the UTF-8 bytes of <paramref name="value"/> (at most <paramref name="capacity"/>)
        /// at <paramref name="destination"/> (room for 10 bytes more); returns the bytes written, counts included.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int EncodeUtf8(string value, ref byte destination, int capacity)
        {
            int length = value.Length;
            if (length == 0)
            {
                destination = NinoStrings.EmptyHeader;
                return 1;
            }

            int countSize = NinoStrings.WriteHeader(ref destination, (uint)length + 1);
            ref byte sizeAt = ref Unsafe.Add(ref destination, countSize);
            // Long strings already have an exact byte count. For short ones, leave room for the smallest
            // possible count and move the bytes up only if they need a wider header.
            int sizeSize = NinoStrings.HeaderSize((uint)(length <= Utf8WorstCaseMaxChars ? length : capacity));
            ref byte bytes = ref Unsafe.Add(ref sizeAt, sizeSize);
#if NET8_0_OR_GREATER
            // Without replacement, the transcoder's call is all that is inlined; an unpaired surrogate stops it.
            var status = System.Text.Unicode.Utf8.FromUtf16(value.AsSpan(), MemoryMarshal.CreateSpan(ref bytes, capacity),
                out int consumed, out int written, replaceInvalidSequences: false);
            if (status != System.Buffers.OperationStatus.Done)
                written = EncodeUtf8Replacing(value, ref bytes, capacity, consumed, written);
#else
            int written = EncodeUtf8Replacing(value, ref bytes, capacity);
#endif
            int actual = NinoStrings.HeaderSize((uint)written);
            if (actual != sizeSize)
            {
                Move(ref bytes, ref Unsafe.Add(ref sizeAt, actual), written);
                sizeSize = actual;
            }

            NinoStrings.WriteHeader(ref sizeAt, (uint)written);
            return countSize + sizeSize + written;
        }

        /// <summary>Moves <paramref name="count"/> bytes up to <paramref name="to"/>, overlapping (rare: out of line).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void Move(ref byte from, ref byte to, int count)
        {
            fixed (byte* source = &from)
            fixed (byte* destination = &to)
                Buffer.MemoryCopy(source, destination, count, count);
        }

#if NET8_0_OR_GREATER
        /// <summary>Finishes the UTF-8 suffix after strict encoding stops at an unpaired surrogate.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int EncodeUtf8Replacing(string value, ref byte destination, int capacity, int consumed, int written) =>
            written + Encoding.UTF8.GetBytes(value.AsSpan(consumed),
                MemoryMarshal.CreateSpan(ref Unsafe.Add(ref destination, written), capacity - written));
#else
        /// <summary>The UTF-8 bytes of <paramref name="value"/>, each unpaired surrogate written as U+FFFD.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int EncodeUtf8Replacing(string value, ref byte destination, int capacity)
        {
            fixed (char* src = value)
            fixed (byte* dst = &destination)
            {
                int length = value.Length, ascii = 0;
                // The netstandard2.1 build (Unity, Mono): the registered kernel writes the leading ASCII chars
                // (a byte each, their UTF-8), the encoder the rest from the first other char, which is the start of a
                // char's bytes: the same bytes as the encoder's alone.
                if (length >= NinoKernelHook.MinNarrowAscii && capacity >= length)
                {
                    ascii = NinoKernelHook.NarrowAsciiPrefix(src, dst, length);
                    if (ascii == length)
                        return length;
                }
                return ascii + Encoding.UTF8.GetBytes(src + ascii, length - ascii, dst + ascii, capacity - ascii);
            }
        }
#endif
    }
}
