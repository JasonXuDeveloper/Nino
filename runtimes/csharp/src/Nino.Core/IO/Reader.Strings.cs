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

        /// <summary>
        /// Reads <paramref name="count"/> strings into the slots starting at <paramref name="first"/> (the
        /// elements of a string collection): <see cref="ReadString"/> in one loop that keeps the position and the
        /// data in registers, where through the reader's reference they are loaded, and the position stored, for
        /// every string.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadStrings(ref string first, int count) =>
            Skip(ReadStrings(ref Cur, Avail, ref first, count));

        // Static, the loop's state in five values (data, position, end, slot, last slot): as many as x64 has
        // callee-saved registers to keep them in across the string allocations. Returns the bytes read.
        private static int ReadStrings(ref byte data, int end, ref string first, int count)
        {
            int pos = 0;
            ref string slot = ref first;
            ref string last = ref Unsafe.Add(ref first, count);
            while (Unsafe.IsAddressLessThan(ref slot, ref last))
            {
                // An element read in place keeps its string when the data holds the same chars.
                slot = NinoStrings.Read(ref Unsafe.Add(ref data, pos), end - pos, slot, out int size);
                pos += size;
                slot = ref Unsafe.Add(ref slot, 1);
            }

            return pos;
        }

        /// <summary>
        /// Reads <paramref name="count"/> pairs of a raw key (its memory is its wire layout) and a string into
        /// <paramref name="dictionary"/>: <see cref="ReadStrings"/> for a <c>Dictionary&lt;TKey, string&gt;</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadStringPairs<TKey>(System.Collections.Generic.Dictionary<TKey, string> dictionary, int count) =>
            Skip(ReadStringPairs(dictionary, ref Cur, Avail, count));

        // Generic over the key only, so a value-type key makes this unshared code (the dictionary's methods inlined
        // with no run-time lookups), with its state (dictionary, data, position, end, pairs left) kept in registers
        // across the string and dictionary allocations.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadStringPairs<TKey>(System.Collections.Generic.Dictionary<TKey, string> dictionary,
            ref byte data, int end, int count)
        {
            // Returns the bytes read.
            int keySize = Unsafe.SizeOf<TKey>();
            int pos = 0;
            // Added here where the dictionary is as expected, else by it and the key's bucket walked after (hash
            // flooding, HashTables).
            bool insert = Internal.HashTables.CanInsert(dictionary, count);
            while (count-- > 0)
            {
                if ((uint)(end - pos) < (uint)keySize)
                    NinoThrow.EndOfData();
                if (!NinoLayout<TKey>.CopiesAsIs)
                    NinoChecks.Values<TKey>(ref Unsafe.Add(ref data, pos), 1, keySize);
                TKey key = NinoUnsafe.Read<TKey>(ref Unsafe.Add(ref data, pos));
                pos += keySize;
                string value = NinoStrings.Read(ref Unsafe.Add(ref data, pos), end - pos, null, out int size);
                pos += size;
                if (insert)
                {
                    Internal.HashTables.Insert(dictionary, key, value);
                }
                else
                {
                    NinoCollections.AddUnique(dictionary, key, value);
                    Internal.HashTables.Added(dictionary, key);
                }
            }

            return pos;
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Merges <paramref name="count"/> pairs of a raw key and a string into an existing
        /// <paramref name="dictionary"/> (NinoDictionary.Refill): each key into <paramref name="seen"/>, each value read
        /// over the one the key maps to (kept when the data holds the same chars). Returns how many keys were already
        /// there. <see cref="ReadStringPairs{TKey}(System.Collections.Generic.Dictionary{TKey, string}, int)"/> for a
        /// dictionary read again.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int RefillStringPairs<TKey>(System.Collections.Generic.Dictionary<TKey, string> dictionary, TKey[] seen,
            int count)
        {
            ulong done = RefillStringPairs(dictionary, seen, ref Cur, Avail, count);
            Skip((int)(done >> 32));
            return (int)done;
        }

        // One loop with the data, the position and the end in registers, the value read inline with the compare its
        // only call: through the general merge each value took the reader's copy in memory and two calls
        // (ReadStringOver, then SameLatin1), and a pooled class with such a dictionary read in 1.11 to 1.20 times
        // rc.7's time (dotnet-ab run 41413072278). Returns the bytes read (high half) and the keys matched (low half).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong RefillStringPairs<TKey>(System.Collections.Generic.Dictionary<TKey, string> dictionary,
            TKey[] seen, ref byte data, int end, int count)
        {
            int keySize = Unsafe.SizeOf<TKey>();
            int pos = 0;
            int matched = 0;
            bool walk = Internal.HashTables.Plan<TKey, string>.Walks;
            ref TKey slot = ref NinoUnsafe.GetArrayDataReference(seen);
            for (int i = 0; i < count; i++)
            {
                if ((uint)(end - pos) < (uint)keySize)
                    NinoThrow.EndOfData();
                if (!NinoLayout<TKey>.CopiesAsIs)
                    NinoChecks.Values<TKey>(ref Unsafe.Add(ref data, pos), 1, keySize);
                TKey key = NinoUnsafe.Read<TKey>(ref Unsafe.Add(ref data, pos));
                pos += keySize;
                Unsafe.Add(ref slot, i) = key;
                ref string value = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                    dictionary, key, out bool exists);
                if (exists)
                    matched++;
                else if (walk)
                    Internal.HashTables.Walk(dictionary, key);
                string read = NinoStrings.Read(ref Unsafe.Add(ref data, pos), end - pos, value, out int size);
                pos += size;
                // An unchanged value (the usual case) is not stored again: no write barrier.
                if (!ReferenceEquals(read, value))
                    value = read;
            }

            return ((ulong)(uint)pos << 32) | (uint)matched;
        }
#endif

        /// <summary>
        /// Reads <paramref name="count"/> pairs of strings into <paramref name="dictionary"/>: <see cref="ReadStrings"/>
        /// for a <c>Dictionary&lt;string, string&gt;</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadStringPairs(System.Collections.Generic.Dictionary<string, string> dictionary, int count) =>
            Skip(Avail - ReadStringPairs(dictionary, ref Cur, Avail, count));

        // The loop's state in a cursor and the bytes left, not a copy of the reader: on Arm64 the reader's six fields
        // took six callee-saved registers across the loop and left each string's size and position on the stack
        // (ReadPairs: 59 to 143 stack accesses once the reader stopped being address-taken). Returns the bytes left.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadStringPairs(System.Collections.Generic.Dictionary<string, string> dictionary,
            ref byte at, int left, int count)
        {
            // Added here where the dictionary is as expected, else by it and the key's bucket walked after (hash
            // flooding, HashTables).
            bool insert = Internal.HashTables.CanInsert(dictionary, count);
            while (count-- > 0)
            {
                string key = NinoStrings.Read(ref at, left, null, out int size);
                at = ref Unsafe.Add(ref at, size);
                left -= size;
                string value = NinoStrings.Read(ref at, left, null, out size);
                at = ref Unsafe.Add(ref at, size);
                left -= size;
                if (key == null)
                    NinoThrow.NullKey(dictionary);
                if (insert)
                {
                    Internal.HashTables.Insert(dictionary, key, value);
                }
                else
                {
                    NinoCollections.AddUnique(dictionary, key, value);
                    Internal.HashTables.Added(dictionary, key);
                }
            }

            return left;
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
