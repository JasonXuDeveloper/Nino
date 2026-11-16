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
    /// <c>Memory&lt;T&gt;</c>: the bytes of a <c>T[]</c> of its elements (length, then the elements; raw ones as one
    /// block). Read into the current memory when its length matches, as an array is, else into a new array; an
    /// array's null reads as empty memory.
    /// </summary>
    public struct MemoryFormatter<T, TFormatter> : INinoFormatter<Memory<T>>
        , INinoExternalFormatter<Memory<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool INinoExternalFormatter<Memory<T>>.TryWriteExternal(Memory<T> value, IBufferWriter<byte> buffer) =>
            NinoCollections.TryWriteRawExternal<T, TFormatter>((ReadOnlyMemory<T>)value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(Memory<T> value, ref Writer writer) =>
            NinoCollections.WriteSpan<T, TFormatter>(value.Span, ref writer);

        public void Deserialize(ref Reader reader, ref Memory<T> value)
        {
            if (!reader.TryReadLength(NinoElements.MinWireSize<T, TFormatter>(), out int length))
            {
                value = default;
                return;
            }

            // A new array is left uninitialized only when the read copies it in whole.
            if (value.Length != length)
            {
                // Out of line where the elements take far more memory than the data (see ArrayFormatter).
                if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
                {
                    T[] grown = null;
                    reader = NinoElements.ReadGrowing<T, TFormatter>(ref grown, length, reader);
                    value = grown;
                    return;
                }

                value = NinoUnsafe.NewArray<T>(length, NinoElements.CopiedWhole<T, TFormatter>());
            }
            NinoElements.Read<T, TFormatter>(value.Span, ref reader, prechecked: true);
        }
    }

    /// <summary>
    /// <c>ReadOnlyMemory&lt;T&gt;</c>: as <see cref="MemoryFormatter{T,TFormatter}"/>, always read into a new array
    /// (its memory is not the reader's to overwrite).
    /// </summary>
    public struct ReadOnlyMemoryFormatter<T, TFormatter> : INinoFormatter<ReadOnlyMemory<T>>
        , INinoExternalFormatter<ReadOnlyMemory<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool INinoExternalFormatter<ReadOnlyMemory<T>>.TryWriteExternal(ReadOnlyMemory<T> value, IBufferWriter<byte> buffer) =>
            NinoCollections.TryWriteRawExternal<T, TFormatter>(value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ReadOnlyMemory<T> value, ref Writer writer) =>
            NinoCollections.WriteSpan<T, TFormatter>(value.Span, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref ReadOnlyMemory<T> value) =>
            value = NinoCollections.ReadArray<T, TFormatter>(ref reader);
    }
}
