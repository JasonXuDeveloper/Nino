#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>PriorityQueue&lt;TElement, TPriority&gt;</c>: count (-1 = null), then (element, priority) pairs in the
    /// queue's unordered (heap) order, each as a <c>(TElement, TPriority)</c> tuple is written. An existing queue is
    /// cleared and refilled. Only on .NET 6+ builds of Nino.Core (the netstandard2.1 build, used by Unity, has no
    /// <c>PriorityQueue</c>).
    /// </summary>
    public struct PriorityQueueFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>
        : INinoFormatter<PriorityQueue<TElement, TPriority>>
        where TElementFormatter : struct, INinoFormatter<TElement>
        where TPriorityFormatter : struct, INinoFormatter<TPriority>
    {
        public void Serialize(PriorityQueue<TElement, TPriority> value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            // As HashSetFormatter's: an empty queue skips the call.
            if (value.Count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            writer = Write(value, writer);
        }

        /// <summary>
        /// Its heap as one run of pairs (<see cref="PriorityQueueHeap"/>, .NET 10, where its layout was checked), else
        /// its unordered items gathered a chunk at a time; either way through the run loops, whatever the elements and
        /// priorities are (#350).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer Write(PriorityQueue<TElement, TPriority> value, Writer writer)
        {
#if NET10_0_OR_GREATER
            if (PriorityQueueHeap.Usable)
                return PriorityQueueHeap.Write<TElement, TPriority, ValueTupleFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>>(value, writer);
#endif
            return NinoGather.Write<(TElement, TPriority),
                ValueTupleFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>,
                NinoGather.Enumerated<(TElement, TPriority),
                    PriorityQueue<TElement, TPriority>.UnorderedItemsCollection.Enumerator>>(
                new(value.UnorderedItems.GetEnumerator()), value.Count, writer);
        }

        public void Deserialize(ref Reader reader, ref PriorityQueue<TElement, TPriority> value)
        {
            // Each pair takes at least its fewest bytes, which bounds what the queue allocates ahead of them.
            int minPair = NinoElements.MinSingleSize<TElement, TElementFormatter>() +
                          NinoElements.MinSingleSize<TPriority, TPriorityFormatter>();
            if (!reader.TryReadLengthOrDiscard(ref value, minPair, out int length))
                return;

            var queue = value;
            if (queue == null)
            {
                // Sized for the pairs, unless they would take far more memory than the data (large elements or
                // priorities): then it grows as they are added.
                queue = new PriorityQueue<TElement, TPriority>(
                    Unsafe.SizeOf<(TElement, TPriority)>() > 2 * NinoElements.Amplification &&
                    NinoElements.Heavy(Unsafe.SizeOf<(TElement, TPriority)>(), minPair)
                        ? NinoElements.Capacity(length, Unsafe.SizeOf<(TElement, TPriority)>(), reader.Remaining)
                        : length);
                value = queue;
            }
            else
            {
                NinoRecycler.DiscardAll<(TElement, TPriority),
                    ValueTupleFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>>(queue.UnorderedItems);
                queue.Clear();
            }

            if (length == 0)
                return;
            reader = Read(queue, length, reader);
        }

        /// <summary>
        /// Read into its heap and published where the pairs are in heap order under its comparer, as Nino writes them
        /// (<see cref="PriorityQueueHeap.Adopt"/>, .NET 10), else enqueued a chunk at a time.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader Read(PriorityQueue<TElement, TPriority> queue, int length, Reader reader)
        {
#if NET10_0_OR_GREATER
            if (PriorityQueueHeap.Usable)
            {
                // An existing queue's heap grown for them as enqueuing them would grow it (step by step, to the same
                // capacity), unless it would take far more memory than the data.
                if (NinoElements.Fits(length, Unsafe.SizeOf<(TElement, TPriority)>(), reader.Remaining))
                {
                    for (int capacity = queue.EnsureCapacity(0); capacity < length;)
                        capacity = queue.EnsureCapacity(capacity + 1);
                }
                reader = PriorityQueueHeap.Adopt<TElement, TPriority, ValueTupleFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>>(queue, length, reader, out bool adopted);
                if (adopted)
                    return reader;
            }
#endif
            var enqueues = new NinoGather.Adds<PriorityQueue<TElement, TPriority>, (TElement, TPriority), Enqueue>(queue);
            reader = NinoGather.ReadAll<(TElement, TPriority),
                ValueTupleFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>,
                NinoGather.Adds<PriorityQueue<TElement, TPriority>, (TElement, TPriority), Enqueue>>(ref enqueues,
                length, reader);
            return reader;
        }

        private struct Enqueue : INinoAdder<PriorityQueue<TElement, TPriority>, (TElement, TPriority)>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(PriorityQueue<TElement, TPriority> target, (TElement, TPriority) item) =>
                target.Enqueue(item.Item1, item.Item2);
        }
    }
}
#endif
