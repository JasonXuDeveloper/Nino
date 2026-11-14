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
    // Latin-1 chars narrowed, widened, appended and compared; the vector kernels are NinoSimd.Text.
    internal static partial class NinoStrings
    {
        /// <summary>
        /// Narrows the <paramref name="n"/> chars at <paramref name="source"/> (more than 16; 8 or more without
        /// vectors) to bytes at <paramref name="destination"/>; false (with part of them written) when one is above
        /// U+00FF.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Narrow(ref char source, ref byte destination, nuint n)
        {
#if NET8_0_OR_GREATER
            if (Vector128.IsHardwareAccelerated)
                return NarrowVector(ref Unsafe.As<char, ushort>(ref source), ref destination, n);
#endif
            return NarrowWords(ref source, ref destination, n); // without vectors
        }

        /// <summary>As <see cref="Narrow"/>, for fewer than 8 chars: inline.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NarrowSmall(ref char source, ref byte destination, nuint n)
        {
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

            for (nuint i = 0; i < n; i++)
            {
                char c = Unsafe.Add(ref source, (nint)i);
                if (c > 0xFF)
                    return false;
                Unsafe.Add(ref destination, (nint)i) = (byte)c;
            }

            return true;
        }

        // n >= 8 without vectors: four chars at a time, then the rest. Inlined into its one caller, WriteLong.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool NarrowWords(ref char source, ref byte destination, nuint n)
        {
#if !NET8_0_OR_GREATER
            // The netstandard2.1 build (Unity, Mono): the registered kernels (NinoKernels), for long text; else
            // Vector<T> where it is fast (TextVectors).
            if (n >= (uint)NinoKernelHook.MinNarrow)
            {
                int narrowed = NinoKernelHook.TryNarrow(ref source, ref destination, (int)n);
                if (narrowed >= 0)
                    return narrowed != 0;
            }

            if (TextVectors.CanNarrow(n))
                return TextVectors.Narrow(ref source, ref destination, n);
#endif
            nuint i = 0;
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

        /// <summary>A new string of the <paramref name="length"/> Latin-1 chars at <paramref name="source"/> (length &gt; 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static string NewLatin1(ref byte source, int length, bool share = true, bool longHeader = false)
        {
            // A string of one char (a digit, a letter, a separator) is shared, as char.ToString shares those of
            // ASCII chars on .NET 8 and later: no allocation. A constant share: false leaves the branch out.
            if (share && length == 1)
                return OneChar(source);
            string value = Allocate(length);
            ref ushort destination = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(value.AsSpan()));
#if NET8_0_OR_GREATER
            if (longHeader && TryWidenAligned128(ref source, ref destination, (uint)length))
                return value;
#endif
            Widen(ref source, ref destination, (uint)length);
            return value;
        }

        /// <summary>
        /// Fills the empty <paramref name="builder"/> with the <paramref name="length"/> Latin-1 chars at
        /// <paramref name="source"/>.
        /// </summary>
        public static unsafe void AppendLatin1(System.Text.StringBuilder builder, ref byte source, int length)
        {
            if (length == 1)
            {
                builder.Append((char)source);
                return;
            }

#if NET8_0_OR_GREATER
            if (length <= 16)
            {
                AppendShortLatin1(builder, ref source, length);
                return;
            }

            if (length <= 256)
            {
                AppendMediumLatin1(builder, ref source, length);
                return;
            }

            AppendLatin1Chunks(builder, ref source, length);
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

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AppendLatin1Chunks(System.Text.StringBuilder builder, ref byte source, int length)
        {
            // Widened straight into the builder's chunks, which Append sized (and zeroed): one pass, no copy.
            builder.Append('\0', length);
            int done = 0;
            foreach (var chunk in builder.GetChunks())
            {
                ref char destination = ref MemoryMarshal.GetReference(chunk.Span);
                ref byte input = ref Unsafe.Add(ref source, done);
                ref ushort output = ref Unsafe.As<char, ushort>(ref destination);
                if (!TryWidenAligned128(ref input, ref output, (uint)chunk.Length))
                    Widen(ref input, ref output, (uint)chunk.Length);
                done += chunk.Length;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void AppendMediumLatin1(System.Text.StringBuilder builder, ref byte source, int length)
        {
            char* buffer = stackalloc char[length];
            Widen(ref source, ref *(ushort*)buffer, (uint)length);
            builder.Append(buffer, length);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void AppendShortLatin1(System.Text.StringBuilder builder, ref byte source, int length)
        {
            char* buffer = stackalloc char[16];
            Widen(ref source, ref *(ushort*)buffer, (uint)length);
            builder.Append(buffer, length);
        }
#endif

        /// <summary>The string of the one Latin-1 char <paramref name="c"/>, shared.</summary>
        // Inline: out of line (a call per one-char string) SimpleClass reads were 3% slower on Neoverse-N2 (run
        // 40591340557), and TimeZoneInfo reads, which never take this branch, no faster.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string OneChar(byte c)
        {
#if NET8_0_OR_GREATER
            return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(OneChars.Table), c);
#else
            return OneChars.Table[c];
#endif
        }

        /// <summary>
        /// The strings of one Latin-1 char, indexed by the char (a class of its own, so NinoStrings has no static
        /// constructor).
        /// </summary>
        private static class OneChars
        {
            public static readonly string[] Table = Make();

            private static string[] Make()
            {
                var strings = new string[256];
                for (int c = 0; c < strings.Length; c++)
                    strings[c] = ((char)c).ToString();
                return strings;
            }
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

        /// <summary>
        /// Widens the <paramref name="n"/> Latin-1 chars at <paramref name="source"/> to UTF-16 at
        /// <paramref name="destination"/>. Inline up to 15 chars (most names, ids and keys), as <see cref="Narrow"/>;
        /// longer text in <see cref="NinoSimd.WidenVector"/> or <see cref="WidenWords"/>, out of line: the loops inlined into
        /// every string read (the JIT inlines this method on its profile data even without the attribute) took the
        /// inlining budget of large generated bodies, which then left helpers such as Reader.Skip as calls.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Widen(ref byte source, ref ushort destination, nuint n)
        {
#if NET8_0_OR_GREATER
            if (Vector128.IsHardwareAccelerated && n >= 8)
            {
                if (n >= 16)
                {
                    WidenVector(ref source, ref destination, n);
                    return;
                }

                NinoSimd.Widen8To15(ref source, ref destination, n);
                return;
            }
#endif
            if (n - 4 < 4)
            {
                // 4 to 7 chars: the first four and the last four.
                NinoUnsafe.Write(ref Unsafe.As<ushort, byte>(ref destination),
                    Spread4(NinoUnsafe.Read<uint>(ref source)));
                NinoUnsafe.Write(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref destination, (nint)(n - 4))),
                    Spread4(NinoUnsafe.Read<uint>(ref Unsafe.Add(ref source, (nint)(n - 4)))));
                return;
            }

            if (n >= 8)
            {
                // Without vectors.
                WidenWords(ref source, ref destination, n);
                return;
            }

            for (nuint i = 0; i < n; i++)
                Unsafe.Add(ref destination, (nint)i) = Unsafe.Add(ref source, (nint)i);
        }

        // n >= 8 without vectors: four chars at a time, then the rest.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void WidenWords(ref byte source, ref ushort destination, nuint n)
        {
#if !NET8_0_OR_GREATER
            // As in NarrowWords.
            if (n >= (uint)NinoKernelHook.MinWiden && NinoKernelHook.TryWiden(ref source, ref destination, (int)n))
                return;

            if (TextVectors.CanWiden(n))
            {
                TextVectors.Widen(ref source, ref destination, n);
                return;
            }
#endif
            nuint i = 0;
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
        internal static bool SameLatin1(ref byte source, string value)
        {
            return SameBytes(ref source, value, 0);
        }

#if !NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SameLongLatin1(ref byte source, string value) => SameLatin1(ref source, value);
#endif

        /// <summary>
        /// Whether the bytes at <paramref name="source"/> are the chars of <paramref name="value"/> (as long), all
        /// ASCII.
        /// </summary>
        public static bool SameAscii(ref byte source, string value)
        {
#if NET8_0_OR_GREATER
            return System.Text.Ascii.Equals(MemoryMarshal.CreateReadOnlySpan(ref source, value.Length), value.AsSpan());
#else
            return SameBytes(ref source, value, 0x8080_8080_8080_8080);
#endif
        }

        /// <summary>
        /// Whether the bytes at <paramref name="source"/> are the chars of <paramref name="value"/> (as long), none
        /// with a bit of <paramref name="high"/> set (0x80 in each byte: ASCII only; 0: any Latin-1). Eight at a time
        /// (SWAR): each half of eight bytes widened to four chars in a ulong by two shifts and masks, compared with
        /// the chars as two ulongs; the last eight again, overlapping, rather than one at a time. Byte by byte on a
        /// big-endian CPU and for strings under eight chars.
        /// </summary>
        internal static bool SameBytes(ref byte source, string value, ulong high)
        {
            int length = value.Length;
            ref byte chars = ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(value.AsSpan()));
            if (BitConverter.IsLittleEndian && length >= 8)
            {
                int i = 0;
                while (true)
                {
                    ulong bytes = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i));
                    ref byte at = ref Unsafe.Add(ref chars, i * 2);
                    if (((bytes & high) | (Widen((uint)bytes) ^ Unsafe.ReadUnaligned<ulong>(ref at)) |
                         (Widen((uint)(bytes >> 32)) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref at, 8)))) != 0)
                        return false;
                    if (i == length - 8)
                        return true;
                    i = i + 16 <= length ? i + 8 : length - 8;
                }
            }

            for (int i = 0; i < length; i++)
            {
                byte b = Unsafe.Add(ref source, i);
                if ((b & (byte)high) != 0 || b != value[i])
                    return false;
            }

            return true;
        }

        /// <summary>Four bytes as the four chars they are in Latin-1 (little-endian).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Widen(uint bytes)
        {
            ulong x = bytes;
            x = (x | x << 16) & 0x0000_FFFF_0000_FFFF;
            return (x | x << 8) & 0x00FF_00FF_00FF_00FF;
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
    }
}
