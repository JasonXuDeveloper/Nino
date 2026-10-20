#if NET6_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// <c>PriorityQueue&lt;TElement, TPriority&gt;</c>: count (-1 = null), then (element, priority) pairs in the
    /// queue's unordered (heap) order. An existing queue is cleared and refilled. Only on .NET 6+ builds of Nino.Core
    /// (the netstandard builds, used by Unity, have no <c>PriorityQueue</c>).
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

            writer = WriteEach(value, writer);
        }

        // The writer by value and returned, a local whose cursor stays in registers (as HashSetFormatter's).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEach(PriorityQueue<TElement, TPriority> value, Writer writer)
        {
            writer.WriteLength(value.Count);
            foreach (var (element, priority) in value.UnorderedItems)
            {
                default(TElementFormatter).Serialize(element, ref writer);
                default(TPriorityFormatter).Serialize(priority, ref writer);
            }

            return writer;
        }

        public void Deserialize(ref PriorityQueue<TElement, TPriority> value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, 1, out int length))
                return;

            var queue = value;
            if (queue == null)
            {
                queue = new PriorityQueue<TElement, TPriority>(length);
                value = queue;
            }
            else
            {
                NinoRecycler.DiscardPairs(queue.UnorderedItems);
                queue.Clear();
            }

            // 1,000 int pairs in 3.5 µs instead of 4.7.
            Reader local = default;
            reader.CopyTo(ref local);
            for (int i = 0; i < length; i++)
            {
                TElement element = default;
                TPriority priority = default;
                default(TElementFormatter).Deserialize(ref element, ref local);
                default(TPriorityFormatter).Deserialize(ref priority, ref local);
                queue.Enqueue(element, priority);
            }

            local.CopyTo(ref reader);
        }
    }
}
#endif
