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
    /// <c>ReadOnlySequence&lt;T&gt;</c>: as <see cref="MemoryFormatter{T,TFormatter}"/>, its segments written one
    /// after another. Read back as one segment.
    /// </summary>
    public struct ReadOnlySequenceFormatter<T, TFormatter> : INinoFormatter<ReadOnlySequence<T>>
        , INinoExternalFormatter<ReadOnlySequence<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool INinoExternalFormatter<ReadOnlySequence<T>>.TryWriteExternal(ReadOnlySequence<T> value, IBufferWriter<byte> buffer) =>
            value.IsSingleSegment && NinoCollections.TryWriteRawExternal<T, TFormatter>(value.First, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ReadOnlySequence<T> value, ref Writer writer)
        {
            if (value.IsSingleSegment)
            {
                NinoCollections.WriteSpan<T, TFormatter>(value.First.Span, ref writer);
                return;
            }

            writer = WriteSegments(value, writer);
        }

        /// <summary>The length, then each segment's elements: out of line, the writer by value and returned.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteSegments(ReadOnlySequence<T> value, Writer writer)
        {
            int length = checked((int)value.Length);
            if (typeof(TFormatter) == typeof(UnmanagedFormatter<byte>))
            {
                var destination = MemoryMarshal.CreateSpan(ref writer.ReserveCounted(length, 1), length);
                NinoSequences.CopySegments(in Unsafe.As<ReadOnlySequence<T>, ReadOnlySequence<byte>>(ref value), destination);
                return writer;
            }
            writer.WriteLength(length);
            foreach (var segment in value)
                NinoElements.Write<T, TFormatter>(segment.Span, ref writer);
            return writer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref ReadOnlySequence<T> value)
        {
            var array = NinoCollections.ReadArray<T, TFormatter>(ref reader);
            value = array == null ? default : new ReadOnlySequence<T>(array);
        }
    }
}
