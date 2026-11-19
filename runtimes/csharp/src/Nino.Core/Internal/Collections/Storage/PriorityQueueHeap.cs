#if NET10_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    internal static class PriorityQueueHeap
    {
        internal static readonly bool Usable = ObjectTypes.Passes(Check);

        /// <summary>
        /// Writes the count and the queue's heap, its nodes as one run of pairs through the run loops (raw pairs and
        /// blocks as one block, others pair by pair through the pair's formatter <typeparamref name="TFormatter"/>),
        /// whatever the element and the priority are (#350). The writer by value and returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer Write<TElement, TPriority, TFormatter>(PriorityQueue<TElement, TPriority> queue,
            Writer writer) where TFormatter : struct, INinoFormatter<(TElement, TPriority)>
        {
            int version = Fields<TElement, TPriority>.Version(queue);
            var nodes = Fields<TElement, TPriority>.Nodes(queue);
            int count = Fields<TElement, TPriority>.Size(queue);
            if ((uint)count > (uint)nodes.Length)
                NinoThrow.ModifiedDuringWrite();
            NinoCollections.WriteSpan<(TElement, TPriority), TFormatter>(
                new System.ReadOnlySpan<(TElement, TPriority)>(nodes, 0, count), ref writer);
            if (Fields<TElement, TPriority>.Version(queue) != version)
                NinoThrow.ModifiedDuringWrite();
            return writer;
        }

        /// <summary>
        /// Reads <paramref name="count"/> pairs straight into the empty queue's heap through the run's read loop, then
        /// publishes them when they are in heap order under its comparer (each priority at least its parent's, as a
        /// heap Nino wrote is): enqueued in that order, the queue would hold the same heap. Out of order under this
        /// comparer, they are enqueued one by one. Nothing is read (<paramref name="adopted"/> false) where the queue
        /// is not empty or its heap has no room for them. The queue stays empty until published: a read rejected half
        /// way leaves it empty.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Reader Adopt<TElement, TPriority, TFormatter>(PriorityQueue<TElement, TPriority> queue,
            int count, Reader reader, out bool adopted) where TFormatter : struct, INinoFormatter<(TElement, TPriority)>
        {
            var nodes = Fields<TElement, TPriority>.Nodes(queue);
            adopted = queue.Count == 0 && nodes.Length >= count;
            if (!adopted)
                return reader;
            var heap = new System.Span<(TElement, TPriority)>(nodes, 0, count);
            if (!NinoElements.ReadOverwrites<(TElement, TPriority), TFormatter>())
                heap.Clear();
            NinoRuns.Read<(TElement, TPriority), TFormatter>(heap, ref reader);
            if (InHeapOrder(heap, queue.Comparer))
            {
                Fields<TElement, TPriority>.Size(queue) = count;
                Fields<TElement, TPriority>.Version(queue) += count;
                return reader;
            }

            // Not a heap under this comparer: enqueued one by one from a copy.
            var pairs = heap.ToArray();
            heap.Clear();
            foreach (var (element, priority) in pairs)
                queue.Enqueue(element, priority);
            return reader;
        }

        /// <summary>Whether every priority is at least its parent's (a 4-ary heap: the parent of i is (i - 1) / 4).</summary>
        private static bool InHeapOrder<TElement, TPriority>(System.ReadOnlySpan<(TElement, TPriority)> heap,
            IComparer<TPriority> comparer)
        {
            if (typeof(TPriority).IsValueType && ReferenceEquals(comparer, Comparer<TPriority>.Default))
            {
                // Comparer<TPriority>.Default devirtualized and inlined for value types.
                for (int i = 1; i < heap.Length; i++)
                    if (Comparer<TPriority>.Default.Compare(heap[i].Item2, heap[(i - 1) >> 2].Item2) < 0)
                        return false;
                return true;
            }

            for (int i = 1; i < heap.Length; i++)
                if (comparer.Compare(heap[i].Item2, heap[(i - 1) >> 2].Item2) < 0)
                    return false;
            return true;
        }

        private static bool Check()
        {
            var queue = new PriorityQueue<int, int>(8);
            for (int i = 0; i < 8; i++)
                queue.Enqueue(20 + i, i);
            var nodes = Fields<int, int>.Nodes(queue);
            if (nodes.Length != 8 || Fields<int, int>.Size(queue) != 8 || Fields<int, int>.Version(queue) != 8 ||
                Unsafe.SizeOf<(int, int)>() != 8)
                return false;

            ref byte first = ref Unsafe.As<(int, int), byte>(ref nodes[0]);
            if (NinoUnsafe.Read<int>(ref first) != 20 || NinoUnsafe.Read<int>(ref Unsafe.Add(ref first, 4)) != 0)
                return false;

            queue.Enqueue(99, -1);
            nodes = Fields<int, int>.Nodes(queue);
            return queue.Count == 9 && Fields<int, int>.Version(queue) == 9 &&
                   nodes[0] == (99, -1) && nodes[8] == (21, 1);
        }

        private static class Fields<TElement, TPriority>
        {
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_nodes")]
            internal static extern ref (TElement, TPriority)[] Nodes(PriorityQueue<TElement, TPriority> queue);
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
            internal static extern ref int Size(PriorityQueue<TElement, TPriority> queue);
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
            internal static extern ref int Version(PriorityQueue<TElement, TPriority> queue);
        }
    }
}
#endif
