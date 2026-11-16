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
    /// <c>BlockingCollection&lt;T&gt;</c>: count (-1 = null), then a snapshot of its elements in the order it hands them
    /// out. Read into a new, unbounded collection over a <c>ConcurrentQueue&lt;T&gt;</c> (its default).
    /// </summary>
    public struct BlockingCollectionFormatter<T, TFormatter> : INinoFormatter<BlockingCollection<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(BlockingCollection<T> value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            NinoCollections.WriteSpan<T, TFormatter>(value.ToArray(), ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref BlockingCollection<T> value)
        {
            NinoRecycler.Discard(ref value);
            if (reader.TryReadLength(NinoElements.MinWireSize<T, TFormatter>(), out int length))
                reader = ReadQueue(length, reader, out value);
        }

        // Out of line with the reader by value: the queue is built from a rented array (no array of its own to
        // collect), whose try/finally and constructor would otherwise be inlined into every caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadQueue(int length, Reader reader, out BlockingCollection<T> value)
        {
            value = new BlockingCollection<T>(
                BclCollections<T, TFormatter>.TryCreate<ConcurrentQueue<T>>(length, ref reader));
            return reader;
        }
    }
}
