using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>ConcurrentQueue&lt;T&gt;</c>, <c>ConcurrentStack&lt;T&gt;</c>, <c>ConcurrentBag&lt;T&gt;</c> and other
    /// <c>IProducerConsumerCollection&lt;T&gt;</c>s: count (-1 = null), then the elements in the order they have to
    /// be added back (stacks bottom to top).
    /// </summary>
    public struct ProducerConsumerFormatter<[NinoCreated] TCollection, T, TFormatter> : INinoFormatter<TCollection>
        where TCollection : class, IProducerConsumerCollection<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TCollection value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (value.GetType() == typeof(ConcurrentQueue<T>))
            {
                if (NinoElements.BlockStride<T, TFormatter>() <= 0)
                    WriteQueueSnapshot(value, ref writer);
                else
                    WriteQueueBlock(Unsafe.As<ConcurrentQueue<T>>(value), ref writer);
                return;
            }

#if NET8_0_OR_GREATER
            if (RawFormatter.Is<T, TFormatter>() && value.GetType() == typeof(ConcurrentStack<T>) &&
                ConcurrentStackNodes<T>.TryWrite(Unsafe.As<ConcurrentStack<T>>(value), ref writer))
                return;
#endif
            WriteOtherSnapshot(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteQueueBlock(ConcurrentQueue<T> value, ref Writer writer) =>
            NinoCollections.WriteSpan<T, TFormatter>(value.ToArray(), ref writer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteOtherSnapshot(TCollection value, ref Writer writer)
        {
            var items = value.ToArray(); // snapshot, safe against concurrent modification
            // ToArray() of a stack or bag is in pop order, and adding reverses it: written reversed.
            if (value is ConcurrentStack<T> || value is ConcurrentBag<T>)
                Array.Reverse(items);
            NinoCollections.WriteSpan<T, TFormatter>(items, ref writer);
        }

        // The queue enumerator takes its own snapshot; Count could describe a different one.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteQueueSnapshot(TCollection value, ref Writer writer) =>
            NinoCollections.WriteEnumerated<T, TFormatter>(value, -1, ref writer);

        public void Deserialize(ref Reader reader, ref TCollection value)
        {
            // Each element takes at least its fewest bytes (NinoElements.MinWireSize), which bounds what the
            // collection allocates ahead of them (NinoElements.Heavy).
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            if (value == null)
            {
                value = BclCollections<T, TFormatter>.TryCreate<TCollection>(length, ref reader);
                if (value != null)
                    return;
            }

            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            var collection = value ??= NinoActivator<TCollection>.Create();
            NinoRecycler.DrainAndDiscard(collection);

            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                collection.TryAdd(item);
            }
        }
    }
}
