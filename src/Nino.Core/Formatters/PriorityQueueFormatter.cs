#if NET6_0_OR_GREATER
using System.Collections.Generic;

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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Count);
            foreach (var (element, priority) in value.UnorderedItems)
            {
                default(TElementFormatter).Serialize(element, ref writer);
                default(TPriorityFormatter).Serialize(priority, ref writer);
            }
        }

        public void Deserialize(ref PriorityQueue<TElement, TPriority> value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

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

            for (int i = 0; i < length; i++)
            {
                TElement element = default;
                TPriority priority = default;
                default(TElementFormatter).Deserialize(ref element, ref reader);
                default(TPriorityFormatter).Deserialize(ref priority, ref reader);
                queue.Enqueue(element, priority);
            }
        }
    }
}
#endif
