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
    /// <summary><c>Queue&lt;T&gt;</c>: count (-1 = null), then the elements from head to tail.</summary>
    public struct QueueFormatter<T, TFormatter> : INinoFormatter<Queue<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(Queue<T> value, ref Writer writer) =>
            default(QueueFormatter<Queue<T>, T, TFormatter>).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref Queue<T> value) =>
            default(QueueFormatter<Queue<T>, T, TFormatter>).Deserialize(ref reader, ref value);
    }

    /// <summary>
    /// A type derived from <c>Queue&lt;T&gt;</c> (and <c>Queue&lt;T&gt;</c> itself): count (-1 = null), then the
    /// elements from head to tail. An existing queue is cleared and refilled.
    /// </summary>
    public struct QueueFormatter<[NinoCreated] TQueue, T, TFormatter> : INinoFormatter<TQueue>
        where TQueue : Queue<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TQueue value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (CollectionViews.Queue)
            {
                // Head to tail: the backing ring as at most two spans (a block each for raw elements).
                var ring = CollectionFields.Array<T>(value);
                int count = CollectionFields.Size<T>(value);
                int head = CollectionFields.Head<T>(value);
                int first = Math.Min(count, ring.Length - head);
                int block = NinoElements.BlockStride<T, TFormatter>();
                if (block > 0)
                {
                    // The prefix and both parts of a wrapped ring fit in the same reservation.
                    ref byte target = ref writer.ReserveCounted(count, block);
                    // The ring bounds both runs: head to the end of the array, then the remaining elements at zero.
                    ref T start = ref NinoUnsafe.GetArrayDataReference(ring);
                    if (first != 0)
                        NinoElements.WriteBlock<T, TFormatter>(ref target, ref Unsafe.Add(ref start, head), first);
                    if (first != count)
                        NinoElements.WriteBlock<T, TFormatter>(ref Unsafe.Add(ref target, first * block),
                            ref start, count - first);
                    return;
                }

                writer.WriteLength(count);
                NinoElements.Write<T, TFormatter>(new ReadOnlySpan<T>(ring, head, first), ref writer);
                NinoElements.Write<T, TFormatter>(new ReadOnlySpan<T>(ring, 0, count - first), ref writer);
                return;
            }

            writer = WriteEnumerated(value, writer);
        }

        /// <summary>
        /// The count and the elements through the enumerator, where the queue's fields are not known: out of line,
        /// the writer by value and returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEnumerated(TQueue value, Writer writer)
        {
            writer.WriteLength(value.Count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
            return writer;
        }

        public void Deserialize(ref Reader reader, ref TQueue value)
        {
            // Each element takes at least its fewest bytes (NinoElements.MinWireSize), which bounds what the
            // collection allocates ahead of them (NinoElements.Heavy).
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            var queue = value ??= typeof(TQueue) == typeof(Queue<T>)
                ? (TQueue)new Queue<T>(Unsafe.SizeOf<T>() > NinoElements.Amplification &&
                    NinoElements.HeavyElements<T, TFormatter>.Value
                    ? NinoElements.Capacity(length, Unsafe.SizeOf<T>(), reader.Remaining)
                    : length)
                : NinoActivator<TQueue>.Create();
            if (NinoRecycler.DiscardAll(queue, queue.Count))
                queue.Clear();

            if (CollectionViews.Queue)
            {
                // Read in place from the start of the backing array (a block for raw elements), then the ring set
                // around them: no capacity check and version bump per element.
                var ring = NinoCollectionViews.ReadInto<T, TFormatter>(ref CollectionFields.Array<T>(queue), length,
                    ref reader);
                CollectionFields.Head<T>(queue) = 0;
                CollectionFields.Tail<T>(queue) = length == ring.Length ? 0 : length;
                CollectionFields.Size<T>(queue) = length;
                CollectionFields.Version<T>(queue)++;
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref reader, ref item);
                queue.Enqueue(item);
            }
        }
    }
}
