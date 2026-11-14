#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using static Nino.Core.Internal.NinoSimd;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The string encoding (<see cref="Writer.Write(string)"/>): a header <c>h</c>, then the chars.
    /// <list type="bullet">
    /// <item>A string whose chars are all at most U+00FF (Latin-1, which covers ASCII): <c>2 * length + 1</c> (odd;
    /// <c>1</c> for the empty string), then one byte per char.</item>
    /// <item>Any other string: <c>2 * length</c> (even), then its UTF-16 code units.</item>
    /// <item>Null: <c>0</c>.</item>
    /// </list>
    /// The header's first byte gives its size, as MessagePack's string markers do: <c>h</c> itself up to 0x7F (up to
    /// 63 chars), else 0x80 and <c>h</c> as a <c>ushort</c> (up to 32,767 chars) or 0x81 and <c>h</c> as a
    /// <c>uint</c>, minimal (the shortest form that holds it). One load and a compare read the common header, and a
    /// longer one is a fixed-size load after a marker, never a loop. ASCII text (names, ids, URLs,
    /// JSON) takes half the bytes of UTF-16, and text that is not Latin-1 (CJK, emoji) keeps UTF-16, which is smaller
    /// than UTF-8 for CJK. Both directions are a straight copy that narrows or widens each char, vectorized on .NET 8
    /// and later.
    /// </summary>
    internal static partial class NinoStrings
    {
        /// <summary>The most bytes a string header (a marker and a <c>uint</c>) takes.</summary>
        public const int MaxHeaderSize = 5;

        /// <summary>Header of a null string.</summary>
        public const byte NullHeader = 0;

        /// <summary>Header of the empty string (Latin-1, no chars).</summary>
        public const byte EmptyHeader = 1;

        /// <summary>The most bytes <see cref="Write"/> writes for <paramref name="value"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MaxSize(string value) =>
            // A string has fewer than 2^30 chars, so this does not overflow.
            value == null ? 1 : MaxHeaderSize + value.Length * sizeof(char);

        /// <summary>
        /// Writes <paramref name="value"/> at <paramref name="destination"/>, which has room for
        /// <see cref="MaxSize"/> bytes. Returns the bytes written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Write(ref byte destination, string value)
        {
            if (value == null)
            {
                destination = NullHeader;
                return 1;
            }

            return WriteChars(ref destination, ref MemoryMarshal.GetReference(value.AsSpan()), value.Length);
        }

        /// <summary>
        /// Writes the <paramref name="length"/> chars at <paramref name="chars"/> like a string of them, at
        /// <paramref name="destination"/> (room for <c>5 + 2 * length</c> bytes). Returns the bytes written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WriteChars(ref byte destination, ref char chars, int length)
        {
            // Up to 16 chars (7 without vectors: most names, ids and keys) inline, with a one-byte header; longer
            // text (a one-byte header up to 63 chars) out of line. The tests are the ones the narrowing took before
            // the header had one size, in the same order, so the header adds no branch.
            nuint n = (uint)length;
            ref byte body = ref Unsafe.Add(ref destination, 1);
#if NET8_0_OR_GREATER
            if (Vector128.IsHardwareAccelerated && n >= 8)
            {
                if (n > 16)
                {
                    if (Vector256.IsHardwareAccelerated && (uint)(length - 32) < 32)
                        return WriteMedium256(ref destination, ref chars, n);
                    return WriteLong(ref destination, ref chars, length);
                }
                if (!Narrow8To16(ref Unsafe.As<char, ushort>(ref chars), ref body, n))
                    return WriteUtf16(ref destination, ref chars, length);
            }
            else
#endif
            {
                if (n >= 8)
                    return WriteLong(ref destination, ref chars, length);
                if (!NarrowSmall(ref chars, ref body, n))
                    return WriteUtf16(ref destination, ref chars, length);
            }

            destination = (byte)(length * 2 + 1);
            return 1 + length;
        }

        // More than 16 chars (7 without vectors), up to 63: a header of one byte, written after the chars are narrowed
        // straight to where they go, as before the header had several sizes. Longer text in a method of its own, so the
        // header's size is not a value kept across the narrowing here.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteLong(ref byte destination, ref char chars, int length)
        {
            if (length > 63)
                return WriteLonger(ref destination, ref chars, length);
#if NET8_0_OR_GREATER
            // 17 to 63 chars: at most four 128-bit steps, faster than Ascii.FromUtf16's setup (a URL of 53 chars took
            // 10% longer through it, as the header was moved to its odd offset).
            bool narrowed = Vector128.IsHardwareAccelerated
                ? Narrow16s(ref Unsafe.As<char, ushort>(ref chars), ref Unsafe.Add(ref destination, 1), (uint)length)
                : Narrow(ref chars, ref Unsafe.Add(ref destination, 1), (uint)length);
            if (!narrowed)
            {
                // 33 to 63 chars: 66 to 126 bytes.
                if (NinoUnsafe.IsArm64Short((uint)length * sizeof(char)))
                {
                    int bytes = length * sizeof(char);
                    destination = (byte)bytes;
                    NinoUnsafe.CopyArm64Short(ref Unsafe.Add(ref destination, 1),
                        ref Unsafe.As<char, byte>(ref chars), (uint)bytes);
                    return bytes + 1;
                }

                return WriteUtf16(ref destination, ref chars, length);
            }
#else
            if (!Narrow(ref chars, ref Unsafe.Add(ref destination, 1), (uint)length))
                return WriteUtf16(ref destination, ref chars, length);
#endif
            destination = (byte)(length * 2 + 1);
            return 1 + length;
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteMedium256(ref byte destination, ref char chars, nuint length)
        {
            ref ushort source = ref Unsafe.As<char, ushort>(ref chars);
            ref byte body = ref Unsafe.Add(ref destination, 1);
            if (!TryNarrow32(ref source, ref body, 0) ||
                (length != 32 && !TryNarrow32(ref source, ref body, length - 32)))
                return WriteUtf16(ref destination, ref chars, (int)length);

            destination = (byte)(length * 2 + 1);
            return 1 + (int)length;
        }
#endif

        // 64 chars or more: the header's size depends on the length only (2n and 2n + 1 take as many bytes).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteLonger(ref byte destination, ref char chars, int length)
        {
            uint header = (uint)length * 2;
            int start = HeaderSize(header);
            if (!Narrow(ref chars, ref Unsafe.Add(ref destination, start), (uint)length))
                return WriteUtf16(ref destination, ref chars, length);
            WriteHeader(ref destination, header + 1);
            return start + length;
        }

        // Not Latin-1: the narrowed prefix is overwritten. Out of line: Copy of a size not known in advance takes all
        // its branches, which inlined grew every method writing a string by 300 to 600 bytes of code.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteUtf16(ref byte destination, ref char chars, int length)
        {
            int start = WriteHeader(ref destination, (uint)length * 2);
            NinoUnsafe.Copy(ref Unsafe.Add(ref destination, start), ref Unsafe.As<char, byte>(ref chars),
                (uint)length * sizeof(char));
            return start + length * sizeof(char);
        }

        /// <summary>Marker of a header of three bytes: a <c>ushort</c> from 0x80 to 0xFFFF follows.</summary>
        public const byte Header16 = 0x80;

        /// <summary>Marker of a header of five bytes: a <c>uint</c> from 0x10000 up follows.</summary>
        public const byte Header32 = 0x81;

        /// <summary>The bytes the header <paramref name="value"/> takes: 1, 3 or 5.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int HeaderSize(uint value) => value < 0x80 ? 1 : value <= ushort.MaxValue ? 3 : 5;

        /// <summary>
        /// Writes <paramref name="value"/> as a header at <paramref name="destination"/> (room for 5 bytes) and
        /// returns the bytes it took: the value itself below 0x80, else a marker and the value as a <c>ushort</c> or
        /// a <c>uint</c>, the shortest that holds it.
        /// </summary>
        public static int WriteHeader(ref byte destination, uint value)
        {
            if (value < 0x80)
            {
                destination = (byte)value;
                return 1;
            }

            if (value <= ushort.MaxValue)
            {
                destination = Header16;
                NinoUnsafe.Write(ref Unsafe.Add(ref destination, 1), (ushort)value);
                return 3;
            }

            destination = Header32;
            NinoUnsafe.Write(ref Unsafe.Add(ref destination, 1), value);
            return 5;
        }

        /// <summary>
        /// Reads the string header at <paramref name="source"/> (<paramref name="available"/> bytes, at least one):
        /// returns the header in the low 32 bits and the bytes it takes in the high ones. Inline for a header of one
        /// byte (strings of up to 63 chars); the others out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadHeader(ref byte source, int available)
        {
            uint first = source;
            return first < 0x80 ? first | (1ul << 32) : ReadLongHeader(ref source, available);
        }

        /// <summary>
        /// <see cref="ReadHeader"/> with a header of three bytes (strings of up to 32,767 chars) inline too, for a
        /// reader of long text that is not itself inlined into a larger read (<c>StringBuilder</c>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadHeaderWide(ref byte source, int available)
        {
            uint first = source;
            if (first < 0x80)
                return first | (1ul << 32);
            if (first == Header16 && available >= 3)
            {
                uint value = NinoUnsafe.Read<ushort>(ref Unsafe.Add(ref source, 1));
                if (value >= 0x80)
                    return value | (3ul << 32);
            }

            return ReadLongHeader(ref source, available);
        }

        /// <summary>
        /// <see cref="WriteHeader"/> out of line, for a header of five bytes (strings of 32,768 chars or more, whose
        /// copy takes far longer than the call).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteLongHeader(ref byte destination, uint value) => WriteHeader(ref destination, value);

        /// <summary>
        /// A header of three or five bytes (a first byte of 0x80 or above): as <see cref="ReadHeader"/>. Rejects
        /// another first byte, a header cut by the end of the data and one that a shorter form holds (not minimal).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong ReadLongHeader(ref byte source, int available)
        {
            uint first = source;
            if (first == Header16)
            {
                if (available < 3)
                    NinoThrow.EndOfData();
                uint value = NinoUnsafe.Read<ushort>(ref Unsafe.Add(ref source, 1));
                if (value < 0x80)
                    NinoThrow.InvalidData("string header not minimal.");
                return value | (3ul << 32);
            }

            if (first != Header32)
                NinoThrow.InvalidData("string header out of range.");
            if (available < 5)
                NinoThrow.EndOfData();
            uint wide = NinoUnsafe.Read<uint>(ref Unsafe.Add(ref source, 1));
            if (wide <= ushort.MaxValue)
                NinoThrow.InvalidData("string header not minimal.");
            return wide | (5ul << 32);
        }

        /// <summary>
        /// Reads the string at <paramref name="source"/> (<paramref name="available"/> bytes), setting
        /// <paramref name="size"/> to the bytes it takes. Returns <paramref name="current"/> when it holds the same
        /// chars, so reading into an existing object keeps an unchanged string instead of allocating a copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Read(ref byte source, int available, string current, out int size, bool share = true)
        {
            if (available <= 0)
                NinoThrow.EndOfData();
            uint header = source;
            ref byte chars = ref Unsafe.Add(ref source, 1);
            // A one-byte header (up to 63 chars) inline, with the chars at a constant offset; longer out of line.
            // Latin-1 (an odd header of 3 to 127) is one test, as it was with the int32 header: (header - 3) rotated
            // right by one is below 63 exactly then (an even difference below 125; an odd one sets the top bit).
            uint latin1 = Half(header - 3);
            if (latin1 < 63)
            {
                uint length = latin1 + 1;
                // One bounds check: the string is only allocated after it.
                if (length >= (uint)available)
                    NinoThrow.EndOfData();
                size = 1 + (int)length;
                return current != null && current.Length == (int)length && SameLatin1(ref chars, current)
                    ? current
                    : NewLatin1(ref chars, (int)length, share);
            }

            // UTF-16: an even header of 2 to 126, the same test.
            uint units = Half(header - 2);
            if (units < 63)
            {
                uint length = units + 1;
                uint bytes = length * sizeof(char);
                if (bytes >= (uint)available)
                    NinoThrow.EndOfData();
                size = 1 + (int)bytes;
                return current != null && current.Length == (int)length && SameChars(ref chars, current)
                    ? current
                    : New(ref chars, (int)length);
            }

            return ReadRest(ref source, available, current, out size);
        }

        /// <summary>
        /// Whether the string at <paramref name="source"/> (<paramref name="available"/> bytes) is a Latin-1 string of
        /// up to 63 chars holding the chars of <paramref name="current"/> (not null), setting <paramref name="size"/>
        /// to the bytes it takes. Clears the caller's local value only after a complete, same-length short Latin-1
        /// comparison fails, so the fallback need not compare it again. Other header forms retain the value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReuseShortLatin1(ref byte source, int available, ref string current, out int size)
        {
            uint length = (uint)current.Length;
            size = 1 + (int)length;
            // The one-byte header of a Latin-1 string of this length (odd), with its chars in the data.
            if (length - 1 < 63 && length < (uint)available && source == (byte)(length * 2 + 1))
            {
                if (SameShortLatin1(ref Unsafe.Add(ref source, 1), current))
                    return true;
                current = null;
            }
            return false;
        }

#if !NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SameShortLatin1(ref byte source, string value) => SameLatin1(ref source, value);
#endif

        /// <summary><paramref name="value"/> rotated right by one bit: its half, its low bit moved to the top.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Half(uint value) => (value >> 1) | (value << 31);

        /// <summary><see cref="Read"/> of null, the empty string and a header of three or five bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string ReadRest(ref byte source, int available, string current, out int size)
        {
            uint header = source;
            if (header < 2)
            {
                size = 1;
                return header == NullHeader ? null : string.Empty;
            }

            // Returned in registers (no out: its address would keep a local in memory in the caller).
            SizedString read = ReadLong(ref source, available, current);
            size = read.Size;
            return read.Value;
        }

        /// <summary><see cref="Read"/> for a header of three or five bytes.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SizedString ReadLong(ref byte source, int available, string current)
        {
            // Three bytes (64 to 32,767 chars: descriptions, paths, short documents) here, without a second call.
            if (source == Header16 && available >= 3)
            {
                uint wide = NinoUnsafe.Read<ushort>(ref Unsafe.Add(ref source, 1));
                if (wide < 0x80)
                    NinoThrow.InvalidData("string header not minimal.");
                string three = Body(ref Unsafe.Add(ref source, 3), available - 3, wide, 3, current,
                    out int threeSize);
                return new SizedString(three, threeSize);
            }

            ulong read = ReadLongHeader(ref source, available);
            int start = (int)(read >> 32);
            string value = Body(ref Unsafe.Add(ref source, start), available - start, (uint)read, start, current,
                out int size);
            return new SizedString(value, size);
        }

        /// <summary>
        /// The string whose <paramref name="header"/> takes <paramref name="start"/> bytes, its chars at
        /// <paramref name="chars"/> (<paramref name="available"/> bytes).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string Body(ref byte chars, int available, uint header, int start, string current,
            out int size)
        {
            if (header >= 2)
            {
                uint length = header >> 1;
                if ((header & 1) != 0)
                {
                    // One bounds check: the string is only allocated after it.
                    if (length > (uint)available)
                        NinoThrow.EndOfData();
                    size = start + (int)length;
                    return current != null && current.Length == (int)length && SameLongLatin1(ref chars, current)
                        ? current
                        : NewLatin1(ref chars, (int)length, longHeader: true);
                }

                ulong bytes = (ulong)length * sizeof(char);
                if (bytes > (uint)available)
                    NinoThrow.EndOfData();
                size = start + (int)bytes;
                return current != null && current.Length == (int)length && SameChars(ref chars, current)
                    ? current
                    : New(ref chars, (int)length);
            }

            size = start;
            return header == NullHeader ? null : string.Empty;
        }

        /// <summary>
        /// A new string of <paramref name="length"/> chars to fill in (length &gt; 0): freshly allocated and not yet
        /// published, so it is written in place.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Allocate(int length)
        {
#if NET8_0_OR_GREATER
            if (Allocator.Kind != 0)
                return Allocator.Kind == 2 ? Allocator.Alloc(null, length) : Allocator.AllocInt(null, length);
#endif
            return new string('\0', length);
        }

        /// <summary>
        /// A new string of the <paramref name="length"/> UTF-16 chars at <paramref name="chars"/> (length &gt; 0). Out
        /// of line, as when it was too large to inline: inlined into <see cref="Read"/>, this rare path used up the
        /// inlining budget of the method reading a Latin-1 string, which then called the reader's own helpers
        /// (Reader.Skip) instead of inlining them.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static unsafe string New(ref byte chars, int length)
        {
#if NET8_0_OR_GREATER
            // The string constructor allocates and then calls Memmove, which is most of the time of a short string.
            // Allocated by the runtime's own FastAllocateString and filled with an inline copy: a string of 3 to 12
            // chars 25% faster (12 to 14 ns against 16 to 19).
            if (Allocator.Kind != 0)
            {
                string value = Allocate(length);
                ref byte destination = ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(value.AsSpan()));
                uint size = (uint)length * sizeof(char);
                NinoUnsafe.Copy(ref destination, ref chars, size);
                return value;
            }
#endif
            return new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref chars), length));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// <c>string.FastAllocateString</c>, which takes an <c>nint</c> on .NET 10 and an <c>int</c> on .NET 8 (this
        /// build runs on both). Each is tried once; the string constructor is used when neither is found.
        /// </summary>
        private static class Allocator
        {
            /// <summary>2: the nint overload, 1: the int one, 0: none. Read-only after startup, a constant to Tier1.</summary>
            public static readonly int Kind = Probe();

            [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "FastAllocateString")]
            public static extern string Alloc(string type, nint length);

            [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "FastAllocateString")]
            public static extern string AllocInt(string type, int length);

            private static int Probe()
            {
                try
                {
                    if (Alloc(null, 1)?.Length == 1)
                        return 2;
                }
                catch (MissingMethodException)
                {
                }

                try
                {
                    if (AllocInt(null, 1)?.Length == 1)
                        return 1;
                }
                catch (MissingMethodException)
                {
                }

                return 0;
            }
        }
#endif
    }

    /// <summary>A string read out of line and the bytes it took: returned in registers, where an out parameter would
    /// keep the caller's local in memory.</summary>
    internal readonly struct SizedString
    {
        public readonly string Value;
        public readonly int Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SizedString(string value, int size)
        {
            Value = value;
            Size = size;
        }
    }
}
