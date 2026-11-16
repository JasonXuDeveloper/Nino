using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary><c>T[]</c>: length (-1 = null), then the elements. Reuses an existing array of the same length.</summary>
    public struct ArrayFormatter<T, TFormatter> : INinoFormatter<T[]>, INinoSizedFormatter<T[]>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoSizedFormatter<T[]>.Size(T[] value) =>
            NinoElements.SizedBytes<T, TFormatter>(value == null ? -1 : value.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoSizedFormatter<T[]>.WriteSized(T[] value, ref byte destination) =>
            NinoElements.WriteSized<T, TFormatter>(ref destination, value, value == null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer)
        {
            if (!writer.TryWriteNullLength(value))
                NinoCollections.WriteSpan<T, TFormatter>(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref T[] value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            // Elements far larger in memory than on the wire: out of line, grown as they are read where the data
            // could not fill as many. A constant false for the others, and nothing compiled.
            if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
            {
                reader = NinoElements.ReadGrowing<T, TFormatter>(ref value, length, reader);
                return;
            }

            NinoElements.Read<T, TFormatter>(
                NinoElements.EnsureLength(ref value, length, NinoElements.CopiedWhole<T, TFormatter>(),
                    NinoElements.ReadOverwrites<T, TFormatter>()), ref reader, prechecked: true);
        }
    }
}
