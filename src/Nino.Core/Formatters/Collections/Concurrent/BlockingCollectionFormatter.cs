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
            // An exact array (one block copy for raw elements) handed to the queue: renting one and wrapping it in an
            // ArraySegment instead (89738283) enumerated a boxed segment enumerator and was 7% slower on x64
            // runners (runs 43041103376 and 43151569585).
            var items = NinoCollections.ReadArray<T, TFormatter>(ref reader);
            if (items != null)
                value = new BlockingCollection<T>(new ConcurrentQueue<T>(items));
        }
    }
}
