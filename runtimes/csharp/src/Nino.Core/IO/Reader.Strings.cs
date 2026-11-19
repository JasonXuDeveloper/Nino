using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Strings: the default encoding (NinoStrings) and UTF-8, new or reusing the current value.
    public ref partial struct Reader
    {
        /// <summary>Reads a string written by <see cref="Writer.Write(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out string value) => value = ReadString();

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUtf8(out string value) => value = ReadUtf8();

        /// <summary>Reads a string written by <see cref="Writer.Write(string)"/>.</summary>
        // The header and the chars with the position and the data loaded once and stored once. Not inlined: a
        // formatter reading several strings stays small.
        public string ReadString()
        {
            string value = NinoStrings.Read(ref Cur, Avail, null, out int size);
            Skip(size);
            return value;
        }

        /// <summary>
        /// <see cref="ReadString()"/> without the shared strings of one char: for a formatter that never reads one, so
        /// its inlined read has no branch for them.
        /// </summary>
        internal string ReadStringUnshared()
        {
            string value = NinoStrings.Read(ref Cur, Avail, null, out int size, share: false);
            Skip(size);
            return value;
        }

        /// <summary>
        /// <see cref="ReadStringUnshared()"/> with the string decoded out of line, for one that is rarely there or
        /// rarely short: only the call and the position's update inline. The data, not the reader, goes to the call, so the
        /// reader stays in registers.
        /// </summary>
        // Null, the usual case of such a string, inline: a call for it made TimeZoneInfo reads 1.09-1.17 times as long
        // as rc.7's, which read it inline.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal string ReadStringOutOfLine()
        {
            if (Avail > 0 && Cur == NinoStrings.NullHeader)
            {
                Skip(1);
                return null;
            }

            SizedString read = ReadStringAt(ref Cur, Avail);
            Skip(read.Size);
            return read.Value;
        }

        // Returned in registers: an out size kept a stack slot in the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SizedString ReadStringAt(ref byte source, int available)
        {
            string value = NinoStrings.Read(ref source, available, null, out int size, share: false);
            return new SizedString(value, size);
        }

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        // Inlined for a string of 1 to 126 chars and up to 127 bytes (both counts one byte), with the transcoding
        // out of line taking values: an instance call would take the reader's address, which keeps all its fields in
        // memory in the caller. Anything else (null, empty, longer counts) is read out of line too.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadUtf8()
        {
            int remaining = Avail;
            ref byte start = ref Cur;
            if (remaining >= 2)
            {
                // The header is the char count plus one; 2 to 127: one byte, at least one char.
                uint header = start;
                uint size = Unsafe.Add(ref start, 1);
                if (header - 2 < 0x7E && size < 0x80)
                {
                    if (size > (uint)(remaining - 2))
                        NinoThrow.EndOfData();
                    // Moved past the string before decoding it, so nothing is kept across the call (a throw ends
                    // the read).
                    Skip(2 + (int)size);
                    return DecodeUtf8(ref Unsafe.Add(ref start, 2), (int)size, (int)header - 1);
                }
            }

            SizedString read = ReadUtf8At(ref start, remaining);
            Skip(read.Size);
            return read.Value;
        }

        /// <summary>
        /// The UTF-8 string at <paramref name="source"/> (<paramref name="available"/> bytes) and the bytes it takes,
        /// as <see cref="ReadUtf8(string)"/> reads it: <paramref name="current"/> when it holds the same text (the
        /// cursor form of <c>Utf8StringFormatter</c>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static SizedString ReadUtf8At(ref byte source, int available, string current) =>
            current == null ? ReadUtf8At(ref source, available) : ReadUtf8Over(ref source, available, current);

        /// <summary>
        /// A UTF-8 string at <paramref name="source"/> (<paramref name="available"/> bytes) of any size, null or
        /// empty: its char count plus one (0 null, 1 empty), then for a string of chars its byte count and the bytes,
        /// both counts headers (see <see cref="NinoStrings.ReadHeader"/>), each at most 2^31 - 1, and the bytes it
        /// takes.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SizedString ReadUtf8At(ref byte source, int available)
        {
            if (available <= 0)
                NinoThrow.EndOfData();
            uint header, bytes;
            int at;
            // Both counts of three bytes (64 to 32,766 chars of up to 65,535 bytes: descriptions, paragraphs) read
            // here at constant offsets, with no call before the decoding (which is inlined here too).
            if (source == NinoStrings.Header16 && available >= 6 && Unsafe.Add(ref source, 3) == NinoStrings.Header16)
            {
                header = NinoUnsafe.Read<ushort>(ref Unsafe.Add(ref source, 1));
                bytes = NinoUnsafe.Read<ushort>(ref Unsafe.Add(ref source, 4));
                if (header < 0x80 || bytes < 0x80)
                    NinoThrow.InvalidData("string header not minimal.");
                at = 6;
            }
            else
            {
                ulong read = NinoStrings.ReadHeader(ref source, available);
                header = (uint)read;
                at = (int)(read >> 32);
                if (header < 2)
                    return new SizedString(header == 0 ? null : string.Empty, at);

                if (header > (uint)int.MaxValue + 1)
                    NinoThrow.InvalidData("invalid UTF-8 string.");
                if (at >= available)
                    NinoThrow.EndOfData();
                read = NinoStrings.ReadHeader(ref Unsafe.Add(ref source, at), available - at);
                bytes = (uint)read;
                at += (int)(read >> 32);
                if (bytes > int.MaxValue)
                    NinoThrow.InvalidData("invalid UTF-8 string.");
            }

            if (bytes > (uint)(available - at))
                NinoThrow.EndOfData();
            return new SizedString(Decode(ref Unsafe.Add(ref source, at), (int)bytes, (int)(header - 1)),
                at + (int)bytes);
        }

        /// <summary>
        /// Reads a string like <see cref="ReadString()"/>, but returns <paramref name="current"/> when it holds the
        /// same chars: reading into an existing object keeps an unchanged string instead of allocating a copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadString(string current)
        {
            if (current == null)
                return ReadString();
            // Out of line taking the data, not the reader: an instance call would take the reader's address, which
            // keeps all its fields in memory in the caller (every generated body reading a string member).
            SizedString read = ReadStringOver(ref Cur, Avail, current);
            Skip(read.Size);
            return read.Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SizedString ReadStringOver(ref byte source, int available, string current)
        {
            // The same short Latin-1 string again (a reused object's names, ids, keys) compared inline: through
            // NinoStrings.Read it took a second call (SameLatin1), where rc.7 read the header inline and made one, and
            // items read into existing objects took up to 1.14 times rc.7's time on Neoverse-N2 (dotnet-ab run
            // 41413072278). Here, out of line, it costs the callers' inlining budgets nothing.
            if (NinoStrings.TryReuseShortLatin1(ref source, available, ref current, out int same))
                return new SizedString(current, same);
            string value = NinoStrings.Read(ref source, available, current, out int size);
            return new SizedString(value, size);
        }

        /// <summary>
        /// Reads a UTF-8 string like <see cref="ReadUtf8()"/>, but returns <paramref name="current"/> when it holds
        /// the same text (checked for ASCII text on .NET 8 and later): reading into an existing object keeps an
        /// unchanged string instead of allocating a copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadUtf8(string current)
        {
            if (current == null)
                return ReadUtf8();
            // Out of line taking the data, as ReadString(string).
            SizedString read = ReadUtf8Over(ref Cur, Avail, current);
            Skip(read.Size);
            return read.Value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SizedString ReadUtf8Over(ref byte source, int available, string current)
        {
            int length = current.Length;
            // ASCII text, as many bytes as chars, compared without decoding: up to 126 chars both counts one byte,
            // 128 to 65,534 both three bytes (Header16, then the count).
            if ((uint)(length - 1) < 0x7E)
            {
                if (available >= 2 + length && source == length + 1 && Unsafe.Add(ref source, 1) == length &&
                    NinoStrings.SameAscii(ref Unsafe.Add(ref source, 2), current))
                    return new SizedString(current, 2 + length);
            }
            else if ((uint)(length - 0x80) < 0xFFFF - 0x80 && available - 6 >= length &&
                     source == NinoStrings.Header16 && Unsafe.Add(ref source, 3) == NinoStrings.Header16 &&
                     NinoUnsafe.Read<ushort>(ref Unsafe.Add(ref source, 1)) == length + 1 &&
                     NinoUnsafe.Read<ushort>(ref Unsafe.Add(ref source, 4)) == length &&
                     NinoStrings.SameAscii(ref Unsafe.Add(ref source, 6), current))
            {
                return new SizedString(current, 6 + length);
            }

            return ReadUtf8At(ref source, available);
        }

        /// <summary>
        /// Decodes exactly the <paramref name="size"/> bytes at <paramref name="src"/> into a string of
        /// <paramref name="length"/> (&gt; 0) chars, in one pass. Bytes that are not UTF-8, or that decode to another
        /// number of chars, throw; a char count above the byte count throws before the string is allocated.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string DecodeUtf8(ref byte src, int size, int length) => Decode(ref src, size, length);

        /// <summary><see cref="DecodeUtf8"/> inline, for <see cref="ReadUtf8At"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe string Decode(ref byte src, int size, int length)
        {
            // Every char (UTF-16 code unit) takes 1 to 3 bytes (a surrogate pair 4 for 2): size >= length bounds the
            // allocation by the data. size > 3 * length decodes to more chars than the string holds, which the
            // transcoder reports (DestinationTooSmall).
            if (size < length)
                NinoThrow.InvalidData("invalid UTF-8 string.");
            var value = NinoStrings.Allocate(length);
#if NET8_0_OR_GREATER
            // Done means every byte was decoded; the chars must then fill the string exactly.
            var status = System.Text.Unicode.Utf8.ToUtf16(MemoryMarshal.CreateReadOnlySpan(ref src, size),
                MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in value.GetPinnableReference()), length),
                out _, out int written, replaceInvalidSequences: false);
            if (status != System.Buffers.OperationStatus.Done || written != length)
                NinoThrow.InvalidData("invalid UTF-8 string.");
#else
            fixed (byte* p = &src)
            fixed (char* d = value)
            {
                // The registered kernel widens the leading ASCII bytes (each its own char, always valid), the strict
                // decoder the rest from the first other byte, which a valid string starts a char with: what the
                // decoder accepts and rejects is what it does alone. At most length bytes are widened (size >= length).
                int ascii = length >= NinoKernelHook.MinWidenAscii ? NinoKernelHook.WidenAsciiPrefix(p, d, length) : 0;

                int written;
                if (ascii == length)
                {
                    // Every char written: any byte left over decodes to more chars than the string holds.
                    written = size == length ? length : -1;
                }
                else
                {
                    try
                    {
                        // Strict: invalid bytes, or more chars than the string holds, throw ArgumentException.
                        written = ascii + StrictUtf8.GetChars(p + ascii, size - ascii, d + ascii, length - ascii);
                    }
                    catch (ArgumentException)
                    {
                        written = -1;
                    }
                }

                if (written != length)
                    NinoThrow.InvalidData("invalid UTF-8 string.");
            }
#endif
            return value;
        }

#if !NET8_0_OR_GREATER
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
#endif
    }
}
