using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>StringBuilder</c>: its text, as a string is written. An existing builder is cleared and refilled.
    /// </summary>
    public struct StringBuilderFormatter : INinoFormatter<StringBuilder>
    {
        public void Serialize(StringBuilder value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoStrings.NullHeader);
                return;
            }

            // The string encoding with UTF-16 chars (header: twice the length), which the reader of strings reads
            // too: a copy of the builder's chunks. Narrowing Latin-1 text to one byte a char would halve the bytes
            // but cost a widening pass on every read, where a StringBuilder cannot take chars without copying them.
            // The empty builder is the empty string (a UTF-16 header of 0 would be null).
            int length = value.Length;
            if (length == 0)
            {
                writer.UnsafeWrite(NinoStrings.EmptyHeader);
                return;
            }

            // Up to 4 GB of chars.
            CopyText(value, length, ref writer.ReserveText((uint)length * sizeof(char)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CopyText(StringBuilder value, int length, ref byte body)
        {
#if NET8_0_OR_GREATER
            if (length == 1)
            {
                NinoUnsafe.Write(ref body, value[0]);
                return;
            }

            CopyChunks(value, length, ref body);
#else
            value.CopyTo(0, MemoryMarshal.CreateSpan(ref Unsafe.As<byte, char>(ref body), length), length);
#endif
        }

#if NET8_0_OR_GREATER
        // Out of line: the loop kept inline enlarged every caller for the one-character case (optimize,
        // jit-formatters.md). Each chunk through NinoUnsafe.Copy, never StringBuilder.CopyTo: CopyTo copies with the
        // BCL's Memmove, which serialized a 1,000-char builder 2.6 times slower on an EPYC 9V74 and 1.55 times on
        // Neoverse-N2 (runner A/B, rc.10 review head f26881a1 against main 51a7bd18), where NinoUnsafe.Copy's vector
        // loops are tuned per CPU. GetChunks allocates only for a builder of more than 8 chunks.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CopyChunks(StringBuilder value, int length, ref byte body)
        {
            if (length == 2)
            {
                NinoUnsafe.Write(ref body, (uint)value[0] | (uint)value[1] << 16);
                return;
            }

            foreach (var chunk in value.GetChunks())
            {
                var bytes = MemoryMarshal.AsBytes(chunk.Span);
                NinoUnsafe.Copy(ref body, ref MemoryMarshal.GetReference(bytes), (uint)bytes.Length);
                body = ref Unsafe.Add(ref body, bytes.Length);
            }
        }
#endif

        public unsafe void Deserialize(ref Reader reader, ref StringBuilder value)
        {
            int available = reader.Remaining;
            if (available <= 0)
                NinoThrow.EndOfData();
            ulong read = NinoStrings.ReadHeaderWide(ref reader.Peek(), available);
            uint header = (uint)read;
            reader.UnsafeAdvance((int)(read >> 32));
            if (header == NinoStrings.NullHeader)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            // Odd: Latin-1 chars, one byte each; even: UTF-16. Checked against the bytes left before the builder is
            // sized for it (in 64 bits: a UTF-16 length up to int.MaxValue doubles past an int).
            int length = (int)(header >> 1);
            bool latin1 = (header & 1) != 0;
            if ((ulong)(uint)length * (latin1 ? 1u : sizeof(char)) > (uint)reader.Remaining)
                NinoThrow.EndOfData();
            var builder = value ??= new StringBuilder(length);
            builder.Clear();
            if (length == 0)
                return;

            // Both advances use the exact byte length checked above, including two bytes per UTF-16 char.
            if (latin1)
            {
                NinoStrings.AppendLatin1(builder, ref reader.UnsafeAdvance(length), length);
                return;
            }

            fixed (byte* p = &reader.UnsafeAdvance(length * sizeof(char)))
            {
                builder.Append((char*)p, length);
            }
        }
    }
}
