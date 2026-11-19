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

            // Its enumerator's snapshot, gathered a chunk at a time (Count could describe a different one; ToArray walks
            // it into a new array besides).
            if (value.GetType() == typeof(ConcurrentQueue<T>))
            {
                writer = WriteQueue(Unsafe.As<ConcurrentQueue<T>>(value), writer);
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
        private static Writer WriteQueue(ConcurrentQueue<T> value, Writer writer) =>
            NinoGather.Write<T, TFormatter, NinoGather.Enumerated<T, IEnumerator<T>>>(new(value.GetEnumerator()), -1,
                writer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteOtherSnapshot(TCollection value, ref Writer writer)
        {
            var items = value.ToArray(); // snapshot, safe against concurrent modification
            // ToArray() of a stack or bag is in pop order, and adding reverses it: written reversed.
            if (value is ConcurrentStack<T> || value is ConcurrentBag<T>)
                Array.Reverse(items);
            NinoCollections.WriteSpan<T, TFormatter>(items, ref writer);
        }

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
            NinoRecycler.DrainAndDiscard<T, TFormatter>(collection);

            // A chunk at a time where the run's read loop moves several, then added.
            reader = NinoCollections.AddAll<TCollection, T, TFormatter, Adder>(collection, length, reader);
        }

        private struct Adder : INinoAdder<TCollection, T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(TCollection target, T item) => target.TryAdd(item);
        }
    }
}
