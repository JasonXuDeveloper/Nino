using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace Nino.Core
{
    /// <summary>
    /// The string encoding (<see cref="Writer.Write(string)"/>): an <c>int32</c> header, then the chars.
    /// <list type="bullet">
    /// <item>A string whose chars are all at most U+00FF (Latin-1, which covers ASCII): <c>~length</c> (-2 for one
    /// char, -3 for two, ...), then one byte per char.</item>
    /// <item>Any other string: its length, then its UTF-16 code units. The empty string is <c>0</c>.</item>
    /// <item>Null: <c>-1</c>.</item>
    /// </list>
    /// ASCII text (names, ids, URLs, JSON) takes half the bytes of UTF-16, and text that is not Latin-1 (CJK, emoji)
    /// keeps UTF-16, which is smaller than UTF-8 for CJK. Both directions are a straight copy that narrows or widens
    /// each char, vectorized on .NET 8 and later.
    /// </summary>
    internal static class NinoStrings
    {
        /// <summary>The most bytes <see cref="Write"/> writes for <paramref name="value"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MaxSize(string value) =>
            value == null ? NinoWire.LengthSize : NinoWire.LengthSize + value.Length * sizeof(char);

        /// <summary>
        /// Writes <paramref name="value"/> at <paramref name="destination"/>, which has room for
        /// <see cref="MaxSize"/> bytes. Returns the bytes written.
        /// </summary>
        public static int Write(ref byte destination, string value)
        {
            if (value == null)
            {
                NinoUnsafe.Write(ref destination, NinoWire.NullLength);
                return NinoWire.LengthSize;
            }

            return WriteChars(ref destination, ref MemoryMarshal.GetReference(value.AsSpan()), value.Length);
        }

        /// <summary>
        /// Writes the <paramref name="length"/> chars at <paramref name="chars"/> like a string of them, at
        /// <paramref name="destination"/> (room for <c>4 + 2 * length</c> bytes). Returns the bytes written.
        /// </summary>
        public static int WriteChars(ref byte destination, ref char chars, int length)
        {
            ref byte body = ref Unsafe.Add(ref destination, NinoWire.LengthSize);
            if (Narrow(ref chars, ref body, length))
            {
                NinoUnsafe.Write(ref destination, length == 0 ? 0 : ~length);
                return NinoWire.LengthSize + length;
            }

            return WriteUtf16(ref destination, ref chars, length);
        }

        // Not Latin-1: the narrowed prefix is overwritten. Out of line: Copy of a size not known in advance takes all
        // its branches, which inlined grew every method writing a string by 300 to 600 bytes of code.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteUtf16(ref byte destination, ref char chars, int length)
        {
            NinoUnsafe.Write(ref destination, length);
            NinoUnsafe.Copy(ref Unsafe.Add(ref destination, NinoWire.LengthSize), ref Unsafe.As<char, byte>(ref chars),
                (uint)length * sizeof(char));
            return NinoWire.LengthSize + length * sizeof(char);
        }


        /// <summary>
        /// Reads the string at <paramref name="source"/> (<paramref name="available"/> bytes), setting
        /// <paramref name="size"/> to the bytes it takes. Returns <paramref name="current"/> when it holds the same
        /// chars, so reading into an existing object keeps an unchanged string instead of allocating a copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string Read(ref byte source, int available, string current, out int size)
        {
            if ((uint)available < NinoWire.LengthSize)
                NinoThrow.EndOfData();
            int header = NinoUnsafe.Read<int>(ref source);
            ref byte chars = ref Unsafe.Add(ref source, NinoWire.LengthSize);
            available -= NinoWire.LengthSize;
            if (header < NinoWire.NullLength)
            {
                int length = ~header;
                // One bounds check: the string is only allocated after it.
                if ((uint)length > (uint)available)
                    NinoThrow.EndOfData();
                size = NinoWire.LengthSize + length;
                return current != null && current.Length == length && SameLatin1(ref chars, current)
                    ? current
                    : NewLatin1(ref chars, length);
            }

            if (header > 0)
            {
                ulong bytes = (ulong)(uint)header * sizeof(char);
                if (bytes > (uint)available)
                    NinoThrow.EndOfData();
                size = NinoWire.LengthSize + (int)bytes;
                return current != null && current.Length == header && SameChars(ref chars, current)
                    ? current
                    : New(ref chars, header);
            }

            size = NinoWire.LengthSize;
            return header == 0 ? string.Empty : null;
        }

        /// <summary>
        /// Narrows the <paramref name="length"/> chars at <paramref name="source"/> to bytes at
        /// <paramref name="destination"/>; false (with part of them written) when one is above U+00FF.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Narrow(ref char source, ref byte destination, int length)
        {
            nuint n = (uint)length;
            nuint i = 0;
#if NET8_0_OR_GREATER
            if (Vector128.IsHardwareAccelerated && n >= 8)
            {
                if (n > 16)
                    return NarrowVector(ref Unsafe.As<char, ushort>(ref source), ref destination, n);
                // 8 to 16 chars (most names and ids), inline: the first eight and the last eight.
                ref ushort chars = ref Unsafe.As<char, ushort>(ref source);
                var first = Vector128.LoadUnsafe(ref chars);
                var last = Vector128.LoadUnsafe(ref chars, n - 8);
                if (((first | last) & Vector128.Create((ushort)0xFF00)) != Vector128<ushort>.Zero)
                    return false;
                var bytes = Vector128.Narrow(first, last).AsUInt64();
                NinoUnsafe.Write(ref destination, bytes.ToScalar());
                NinoUnsafe.Write(ref Unsafe.Add(ref destination, (nint)(n - 8)), bytes.GetElement(1));
                return true;
            }

            if (n - 4 < 4)
            {
                // 4 to 7 chars: the first four and the last four.
                ulong head = NinoUnsafe.Read<ulong>(ref Unsafe.As<char, byte>(ref source));
                ulong tail = NinoUnsafe.Read<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref source, (nint)(n - 4))));
                if (((head | tail) & HighBytes) != 0)
                    return false;
                NinoUnsafe.Write(ref destination, Pack4(head));
                NinoUnsafe.Write(ref Unsafe.Add(ref destination, (nint)(n - 4)), Pack4(tail));
                return true;
            }
#endif
            // Without vectors: four chars at a time.
            for (; i + 4 <= n; i += 4)
            {
                ulong four = NinoUnsafe.Read<ulong>(ref Unsafe.As<char, byte>(ref Unsafe.Add(ref source, (nint)i)));
                if ((four & HighBytes) != 0)
                    return false;
                NinoUnsafe.Write(ref Unsafe.Add(ref destination, (nint)i), Pack4(four));
            }

            for (; i < n; i++)
            {
                char c = Unsafe.Add(ref source, (nint)i);
                if (c > 0xFF)
                    return false;
                Unsafe.Add(ref destination, (nint)i) = (byte)c;
            }

            return true;
        }

#if NET8_0_OR_GREATER
        // n > 16. Whole vectors, then the last one again overlapping the previous (the chars are the same).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe bool NarrowVector(ref ushort source, ref byte destination, nuint n)
        {
            // ASCII through the runtime's own narrowing (512-bit and unrolled where it can be): 1,000 chars in 22 ns
            // against 40 for the loop below. It stops at the first char above U+007F; Latin-1 text with such chars
            // is narrowed again below.
            if (n >= 32 && System.Text.Ascii.FromUtf16(
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<ushort, char>(ref source), (int)n),
                    MemoryMarshal.CreateSpan(ref destination, (int)n), out _) == System.Buffers.OperationStatus.Done)
                return true;
            if (Vector256.IsHardwareAccelerated && n >= 32)
            {
                var high = Vector256.Create((ushort)0xFF00);
                nuint i = 0;
                while (true)
                {
                    var a = Vector256.LoadUnsafe(ref source, i);
                    var b = Vector256.LoadUnsafe(ref source, i + 16);
                    if (((a | b) & high) != Vector256<ushort>.Zero)
                        return false;
                    Vector256.Narrow(a, b).StoreUnsafe(ref destination, i);
                    if (i + 32 == n)
                        return true;
                    // After the first vector, on to the destination's next 32-byte boundary (overlapping it): the
                    // bytes follow a 4-byte length, and a store across two cache lines costs two.
                    nuint next = i == 0 ? 32 - ((nuint)Unsafe.AsPointer(ref destination) & 31) : i + 32;
                    i = next + 32 <= n ? next : n - 32;
                }
            }

            // 17 to 31 chars (or no 256-bit vectors).
            var high128 = Vector128.Create((ushort)0xFF00);
            nuint j = 0;
            while (true)
            {
                var a = Vector128.LoadUnsafe(ref source, j);
                var b = Vector128.LoadUnsafe(ref source, j + 8);
                if (((a | b) & high128) != Vector128<ushort>.Zero)
                    return false;
                Vector128.Narrow(a, b).StoreUnsafe(ref destination, j);
                if (j + 16 == n)
                    return true;
                j = j + 32 <= n ? j + 16 : n - 16;
            }
        }
#endif

        /// <summary>A new string of the <paramref name="length"/> Latin-1 chars at <paramref name="source"/> (length &gt; 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string NewLatin1(ref byte source, int length)
        {
            string value = Allocate(length);
            Widen(ref source, ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(value.AsSpan())),
                (uint)length);
            return value;
        }

        /// <summary>
        /// Fills the empty <paramref name="builder"/> with the <paramref name="length"/> Latin-1 chars at
        /// <paramref name="source"/>.
        /// </summary>
        public static unsafe void AppendLatin1(System.Text.StringBuilder builder, ref byte source, int length)
        {
#if NET8_0_OR_GREATER
            // Widened straight into the builder's chunks, which Append sized (and zeroed): one pass, no copy.
            builder.Append('\0', length);
            int done = 0;
            foreach (var chunk in builder.GetChunks())
            {
                ref char destination = ref MemoryMarshal.GetReference(chunk.Span);
                Widen(ref Unsafe.Add(ref source, done), ref Unsafe.As<char, ushort>(ref destination),
                    (uint)chunk.Length);
                done += chunk.Length;
            }
#else
            const int bufferChars = 256;
            char* buffer = stackalloc char[bufferChars];
            for (int start = 0; start < length; start += bufferChars)
            {
                int count = Math.Min(bufferChars, length - start);
                Widen(ref Unsafe.Add(ref source, start), ref *(ushort*)buffer, (uint)count);
                builder.Append(buffer, count);
            }
#endif
        }

        /// <summary>The high bytes of four UTF-16 chars in a ulong: set for a char above U+00FF.</summary>
        private const ulong HighBytes = 0xFF00FF00FF00FF00;

        /// <summary>The low bytes of four UTF-16 chars, packed into four bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Pack4(ulong chars)
        {
            chars &= 0x00FF00FF00FF00FF;
            chars = (chars | (chars >> 8)) & 0x0000FFFF0000FFFF;
            return (uint)(chars | (chars >> 16));
        }

        /// <summary>Four bytes as four UTF-16 chars.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Spread4(uint bytes)
        {
            ulong chars = bytes;
            chars = (chars | (chars << 16)) & 0x0000FFFF0000FFFF;
            return (chars | (chars << 8)) & 0x00FF00FF00FF00FF;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void Widen(ref byte source, ref ushort destination, nuint n)
        {
            nuint i = 0;
#if NET8_0_OR_GREATER
            if (Vector128.IsHardwareAccelerated && n >= 8)
            {
                if (Vector256.IsHardwareAccelerated && n >= 32)
                {
                    while (true)
                    {
                        var (lower, upper) = Vector256.Widen(Vector256.LoadUnsafe(ref source, i));
                        lower.StoreUnsafe(ref destination, i);
                        upper.StoreUnsafe(ref destination, i + 16);
                        if (i + 32 == n)
                            return;
                        // After the first 32 chars, on to the next 32-byte boundary of the string's chars (an
                        // even address, so a whole number of chars away), as in NarrowVector.
                        nuint next = i == 0 ? (32 - ((nuint)Unsafe.AsPointer(ref destination) & 31)) / 2 : i + 32;
                        i = next + 32 <= n ? next : n - 32;
                    }
                }

                if (n >= 16)
                {
                    while (true)
                    {
                        var (lower, upper) = Vector128.Widen(Vector128.LoadUnsafe(ref source, i));
                        lower.StoreUnsafe(ref destination, i);
                        upper.StoreUnsafe(ref destination, i + 8);
                        if (i + 16 == n)
                            return;
                        i = i + 32 <= n ? i + 16 : n - 16;
                    }
                }

                // 8 to 15 bytes: the first eight and the last eight.
                Vector128.WidenLower(Vector128.CreateScalarUnsafe(NinoUnsafe.Read<ulong>(ref source)).AsByte())
                    .StoreUnsafe(ref destination);
                Vector128.WidenLower(Vector128.CreateScalarUnsafe(
                        NinoUnsafe.Read<ulong>(ref Unsafe.Add(ref source, (nint)(n - 8)))).AsByte())
                    .StoreUnsafe(ref destination, n - 8);
                return;
            }

            if (n - 4 < 4)
            {
                // 4 to 7 chars: the first four and the last four.
                NinoUnsafe.Write(ref Unsafe.As<ushort, byte>(ref destination),
                    Spread4(NinoUnsafe.Read<uint>(ref source)));
                NinoUnsafe.Write(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref destination, (nint)(n - 4))),
                    Spread4(NinoUnsafe.Read<uint>(ref Unsafe.Add(ref source, (nint)(n - 4)))));
                return;
            }
#endif
            // Without vectors: four chars at a time.
            for (; i + 4 <= n; i += 4)
            {
                NinoUnsafe.Write(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref destination, (nint)i)),
                    Spread4(NinoUnsafe.Read<uint>(ref Unsafe.Add(ref source, (nint)i))));
            }

            for (; i < n; i++)
                Unsafe.Add(ref destination, (nint)i) = Unsafe.Add(ref source, (nint)i);
        }

        /// <summary>Whether the Latin-1 chars at <paramref name="source"/> are those of <paramref name="value"/> (as long).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SameLatin1(ref byte source, string value)
        {
#if NET8_0_OR_GREATER
            // ASCII text compared vectorized; other Latin-1 text is read again.
            return SameAscii(ref source, value);
#else
            for (int i = 0; i < value.Length; i++)
            {
                if (Unsafe.Add(ref source, i) != value[i])
                    return false;
            }

            return true;
#endif
        }

        /// <summary>
        /// Whether the bytes at <paramref name="source"/> are the chars of <paramref name="value"/> (as long), all
        /// ASCII.
        /// </summary>
        public static bool SameAscii(ref byte source, string value)
        {
#if NET8_0_OR_GREATER
            return System.Text.Ascii.Equals(MemoryMarshal.CreateReadOnlySpan(ref source, value.Length), value.AsSpan());
#else
            for (int i = 0; i < value.Length; i++)
            {
                byte b = Unsafe.Add(ref source, i);
                if (b >= 0x80 || b != value[i])
                    return false;
            }

            return true;
#endif
        }

        /// <summary>Whether the UTF-16 chars at <paramref name="source"/> are those of <paramref name="value"/> (as long).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe bool SameChars(ref byte source, string value)
        {
            fixed (byte* p = &source)
            {
                return new ReadOnlySpan<char>(p, value.Length).SequenceEqual(value.AsSpan());
            }
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
#if NETSTANDARD2_0
            fixed (byte* p = &chars)
            {
                return new string((char*)p, 0, length);
            }
#else
            return new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref chars), length));
#endif
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
}
