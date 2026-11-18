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
                if (NinoKind.Stride<T, TFormatter>() <= 0)
                    WriteQueueSnapshot(value, ref writer);
                else if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                    WriteQueueSlots(Unsafe.As<ConcurrentQueue<T>>(value), ref writer);
                else
                    WriteQueueBlock(Unsafe.As<ConcurrentQueue<T>>(value), ref writer);
                return;
            }

#if NET8_0_OR_GREATER
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw && value.GetType() == typeof(ConcurrentStack<T>) &&
                ConcurrentStackNodes<T>.TryWrite(Unsafe.As<ConcurrentStack<T>>(value), ref writer))
                return;
#endif
            WriteOtherSnapshot(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteQueueBlock(ConcurrentQueue<T> value, ref Writer writer) =>
            NinoCollections.WriteSpan<T, TFormatter>(value.ToArray(), ref writer);

        /// <summary>
        /// Slots (raw <c>T?</c>): the enumerator's snapshot gathered in a rented array, then written as one run.
        /// Walking the queue is what a write of it costs, and <c>ToArray</c> walks it into a new array besides
        /// (1,000 <c>int?</c>: 5,000 ns against 3,700 for the enumerator, local).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteQueueSlots(ConcurrentQueue<T> value, ref Writer writer)
        {
            var pool = ArrayPool<T>.Shared;
            // Sized by Count: started small and grown instead, the write took 4,500 ns where it takes 4,200 (1,000
            // int?, local).
            var rented = pool.Rent(Math.Max(value.Count, 16));
            try
            {
                int count = 0;
                foreach (var item in value)
                {
                    if (count == rented.Length)
                    {
                        var larger = pool.Rent(checked(count * 2));
                        Array.Copy(rented, larger, count);
                        pool.Return(rented);
                        rented = larger;
                    }

                    rented[count++] = item;
                }

                NinoCollections.WriteSpan<T, TFormatter>(new ReadOnlySpan<T>(rented, 0, count), ref writer);
            }
            finally
            {
                pool.Return(rented);
            }
        }

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

            // Slots (raw T?): read as one run, as an array of them is, then added.
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
            {
                NinoCollections.AddSlots<TCollection, T, TFormatter, Adder>(collection, length, ref reader);
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                collection.TryAdd(item);
            }
        }

        private struct Adder : INinoAdder<TCollection, T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(TCollection target, T item) => target.TryAdd(item);
        }
    }
}
