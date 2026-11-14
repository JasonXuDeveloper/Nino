#if NET8_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>PriorityQueue&lt;TElement, TPriority&gt;</c>: count (-1 = null), then (element, priority) pairs in the
    /// queue's unordered (heap) order. An existing queue is cleared and refilled. Only on .NET 6+ builds of Nino.Core
    /// (the netstandard2.1 build, used by Unity, has no <c>PriorityQueue</c>).
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

            // As HashSetFormatter's: an empty queue skips the call, and elements or priorities with references go
            // through the writer parameter.
            if (value.Count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            if (NinoElements.WritesOnLocal<TElement>() && NinoElements.WritesOnLocal<TPriority>())
                writer = WriteEach(value, writer);
            else
                WriteEach(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteRawPairs(PriorityQueue<TElement, TPriority> queue, int count, ref Writer writer)
        {
#if NET10_0_OR_GREATER
            if (PriorityQueueHeap.TryWrite<TElement, TPriority,
                    ValueTupleFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>>(
                    queue, count, ref writer))
                return;
#endif
            int elementSize = Unsafe.SizeOf<TElement>();
            int pairSize = elementSize + Unsafe.SizeOf<TPriority>();
            ref byte target = ref writer.ReserveCounted(count, pairSize);
            foreach (var (element, priority) in queue.UnorderedItems)
            {
                if (count-- == 0)
                    NinoThrow.ModifiedDuringWrite();
                NinoUnsafe.WriteValueIn(ref target, 0, in element);
                NinoUnsafe.WriteValueIn(ref target, elementSize, in priority);
                target = ref Unsafe.Add(ref target, pairSize);
            }

            if (count != 0)
                NinoThrow.ModifiedDuringWrite();
        }

        // The writer by value and returned, a local whose cursor stays in registers (as HashSetFormatter's).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEach(PriorityQueue<TElement, TPriority> value, Writer writer)
        {
            int count = value.Count;
            if (count >= 8 && RawFormatter.Is<TElement, TElementFormatter>() &&
                RawFormatter.Is<TPriority, TPriorityFormatter>())
                WriteRawPairs(value, count, ref writer);
            else
                WriteEach(value, ref writer);
            return writer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteEach(PriorityQueue<TElement, TPriority> value, ref Writer writer)
        {
            writer.WriteLength(value.Count);
            foreach (var (element, priority) in value.UnorderedItems)
            {
                default(TElementFormatter).Serialize(element, ref writer);
                default(TPriorityFormatter).Serialize(priority, ref writer);
            }
        }

        public void Deserialize(ref Reader reader, ref PriorityQueue<TElement, TPriority> value)
        {
            // Each pair takes at least its fewest bytes, which bounds what the queue allocates ahead of them.
            int minPair = NinoElements.MinWireSize<TElement, TElementFormatter>() +
                          NinoElements.MinWireSize<TPriority, TPriorityFormatter>();
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
                NinoRecycler.DiscardPairs(queue.UnorderedItems);
                queue.Clear();
            }

#if NET10_0_OR_GREATER
            if (length >= 16 && typeof(TElement) == typeof(int) && typeof(TPriority) == typeof(int) &&
                RawFormatter.Is<TElement, TElementFormatter>() && RawFormatter.Is<TPriority, TPriorityFormatter>() &&
                PriorityQueueHeap.TryAdopt(Unsafe.As<PriorityQueue<int, int>>(queue), length, ref reader))
                return;
#endif

            // 1,000 int pairs in 3.5 µs instead of 4.7.
            Reader local = default;
            reader.CopyTo(ref local);
            for (int i = 0; i < length; i++)
            {
                TElement element = default;
                TPriority priority = default;
                default(TElementFormatter).Deserialize(ref local, ref element);
                default(TPriorityFormatter).Deserialize(ref local, ref priority);
                queue.Enqueue(element, priority);
            }

            local.CopyTo(ref reader);
        }
    }
}
#endif
