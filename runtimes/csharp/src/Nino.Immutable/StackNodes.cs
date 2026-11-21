using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableStack&lt;T&gt;</c> and <c>ImmutableQueue&lt;T&gt;</c> walked through their nodes for a write, through
    /// mirror classes (<see cref="StackNode{T}"/>, <see cref="QueueView{T}"/>): their enumerators call the stack's
    /// <c>Pop</c> and <c>Peek</c> for every element, neither inlined, each checking the stack is not empty.
    /// </summary>
    internal static class StackNodes
    {
        /// <summary>
        /// Whether the layouts below are the ones of this runtime's System.Collections.Immutable: checked against
        /// collections with known contents, walked both ways.
        /// </summary>
        public static readonly bool Usable = ObjectTypes.Passes(Check);

        private static bool Check()
        {
            // Int fields first (a reference may sit where another layout has an int, never read as one).
            var ints = StackNode<int>.Of(ImmutableStack<int>.Empty.Push(7).Push(9));
            if (ints._head != 9 || ints._tail._head != 7 || ints._tail._tail._tail != null)
                return false;

            // A queue grown at its back (its backwards stack not reversed yet), then enumerated (which reverses it
            // once), then dequeued past its front: each walked in its enumerator's order where it can be.
            var queue = ImmutableQueue<string>.Empty;
            for (int i = 0; i < 6; i++)
                queue = queue.Enqueue("q" + i.ToString(CultureInfo.InvariantCulture));
            var view = QueueView<string>.Of(queue);
            if (view._backwardsReversed != null || !ReferenceEquals(view._forwards._head, queue.Peek()) ||
                StackNode<string>.Walkable(queue, out _))
                return false;
            foreach (var _ in queue)
            {
            }

            if (!Walks(queue) || !Walks(queue.Dequeue().Dequeue()) || !Walks(ImmutableQueue<string>.Empty))
                return false;

            var stack = ImmutableStack<string>.Empty;
            for (int i = 0; i < 6; i++)
                stack = stack.Push("s" + i.ToString(CultureInfo.InvariantCulture));
            return Same(new StackItems<string>(StackNode<string>.Of(stack), null), stack, 6);
        }

        // A queue walked through the mirror is its enumerator's order (a queue whose back is not reversed yet is left
        // to the enumerator, and passes).
        private static bool Walks(ImmutableQueue<string> queue)
        {
            int count = 0;
            foreach (var _ in queue)
                count++;
            return !StackNode<string>.Walkable(queue, out var items) || Same(items, queue, count);
        }

        private static bool Same(StackItems<string> items, IEnumerable<string> collection, int count)
        {
            var walked = new string[count + 1];
            if (items.Fill(walked) != count)
                return false;
            int at = 0;
            foreach (var item in collection)
                if (!ReferenceEquals(walked[at++], item))
                    return false;
            return true;
        }
    }

    /// <summary><c>ImmutableStack&lt;T&gt;</c>: a node, its element and the rest; the empty stack's rest is null.</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal sealed class StackNode<T>
    {
#pragma warning disable CS0649 // laid out only
        public T _head;
        public StackNode<T> _tail;
#pragma warning restore CS0649

        public static StackNode<T> Of(ImmutableStack<T> stack) => Unsafe.As<StackNode<T>>(stack);

        /// <summary>
        /// The elements of <paramref name="queue"/> front to back: its front stack, then its back one reversed, which
        /// the queue keeps once its enumerator or a dequeue has made it; false until then, or where the mirror does not
        /// match.
        /// </summary>
        public static bool TryItems(ImmutableQueue<T> queue, out StackItems<T> items)
        {
            items = default;
            return StackNodes.Usable && Walkable(queue, out items);
        }

        /// <summary><see cref="TryItems"/> without the layout check, for the check itself.</summary>
        internal static bool Walkable(ImmutableQueue<T> queue, out StackItems<T> items)
        {
            var view = QueueView<T>.Of(queue);
            if (view._backwards._tail == null)
            {
                items = new StackItems<T>(view._forwards, null);
                return true;
            }

            if (view._backwardsReversed is { } reversed)
            {
                items = new StackItems<T>(view._forwards, reversed);
                return true;
            }

            items = default;
            return false;
        }
    }

    /// <summary><c>ImmutableQueue&lt;T&gt;</c>: two stacks, and the back one reversed once it has been.</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal sealed class QueueView<T>
    {
#pragma warning disable CS0649 // laid out only
        public StackNode<T> _backwards;
        public StackNode<T> _forwards;
        public StackNode<T> _backwardsReversed;
#pragma warning restore CS0649

        public static QueueView<T> Of(ImmutableQueue<T> queue) => Unsafe.As<QueueView<T>>(queue);
    }

    /// <summary>The elements of a stack's nodes top to bottom, then of a second stack's.</summary>
    internal struct StackItems<T> : INinoGatherSource<T>
    {
        private StackNode<T> _node, _then;

        public StackItems(StackNode<T> node, StackNode<T> then)
        {
            _node = node;
            _then = then;
        }

        public int Fill(Span<T> chunk)
        {
            ref T at = ref MemoryMarshal.GetReference(chunk);
            var node = _node;
            int filled = 0;
            while (filled < chunk.Length)
            {
                var tail = node._tail;
                if (tail == null)
                {
                    // The empty stack at the end of the first: on to the second.
                    if (_then == null)
                        break;
                    node = _then;
                    _then = null;
                    continue;
                }

                Unsafe.Add(ref at, filled++) = node._head;
                node = tail;
            }

            _node = node;
            return filled;
        }

        public void Finish()
        {
        }
    }
}
