using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// For Nino's own collections and packages (Nino.Immutable): a collection that is not one contiguous run (a tree,
    /// linked nodes, an enumerator), walked a chunk at a time by <see cref="NinoGathered"/>. Implemented by a struct,
    /// so every call is direct.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoGatherSource<T>
    {
        /// <summary>
        /// Copies the next elements into <paramref name="chunk"/>, as many as it holds, fewer only at the end; returns
        /// how many.
        /// </summary>
        int Fill(Span<T> chunk);

        /// <summary>
        /// After the last element the writer takes: throws <see cref="InvalidOperationException"/> where the
        /// collection holds more than it said or changed during the walk, and gives back what the walk took.
        /// </summary>
        void Finish();
    }

    /// <summary>
    /// For Nino's own packages (Nino.Immutable): the one write of a collection that is not one contiguous run, as
    /// Nino.Core's own (a <c>SortedSet</c>, a <c>LinkedList</c>) are written: its elements walked by a
    /// <see cref="INinoGatherSource{T}"/> into a chunk of Nino's per-thread buffer at a time, each chunk written by
    /// the run loops every collection writes through (raw values and blocks as one block, slots, strings, a
    /// polymorphic base's run loop), or, for values whose memory is their wire form, straight into the writer's buffer.
    /// The package says how to walk its collection; Nino.Core decides how each element is written.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoGathered
    {
        /// <summary>
        /// Writes the count and the elements of <paramref name="source"/>: <paramref name="count"/> of them, or, for
        /// -1, as many as it gives (the count patched after them).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter, TSource>(TSource source, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
            where TSource : struct, INinoGatherSource<T> =>
            writer = NinoGather.Write<T, TFormatter, TSource>(source, count, writer);

        /// <summary>
        /// Writes the count and the elements of <paramref name="source"/> in the reverse of the order it gives them
        /// (a stack walked from its top, written bottom to top so that pushing them in order rebuilds it).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteReversed<T, TFormatter, TSource>(TSource source, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
            where TSource : struct, INinoGatherSource<T> =>
            writer = NinoGather.WriteReversed<T, TFormatter, TSource>(source, writer);

        /// <summary>
        /// The elements of an enumerator, the collection's own struct one where it has one (direct calls; through
        /// <see cref="IEnumerable{T}"/> each step is two interface calls).
        /// </summary>
        public struct Enumerated<T, TEnumerator> : INinoGatherSource<T> where TEnumerator : IEnumerator<T>
        {
            private TEnumerator _items;

            /// <summary>The elements <paramref name="items"/> has yet to give.</summary>
            public Enumerated(TEnumerator items) => _items = items;

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Fill(Span<T> chunk)
            {
                // By reference: the chunk's length is not known to the JIT to bound what the enumerator gives.
                ref T at = ref MemoryMarshal.GetReference(chunk);
                int filled = 0;
                while (filled < chunk.Length && _items.MoveNext())
                {
                    Unsafe.Add(ref at, filled) = _items.Current;
                    filled++;
                }

                return filled;
            }

            /// <inheritdoc/>
            public void Finish()
            {
                bool more = _items.MoveNext();
                _items.Dispose();
                if (more)
                    NinoThrow.ModifiedDuringWrite();
            }
        }
    }

    /// <summary>
    /// For Nino's own collections and packages (Nino.Immutable): a binary tree walked in order (a <c>SortedSet</c>'s
    /// red-black tree, the immutable collections' AVL trees), a node at a time, resumed chunk after chunk of a gather
    /// (<see cref="NinoGathered"/>): the one walk of every tree, where each collection says only where a node's
    /// children are and what its element is. The nodes are any class with their two children at byte offsets
    /// (<see cref="OffsetOf"/>); a subtree ends at null or at the tree's own empty node. The path to the next node is
    /// kept on a stack (a chunk of the gather's buffer of objects, far deeper than a balanced tree of any count): a
    /// deeper path, or a null child where subtrees end at an empty node, is a tree changed during the walk.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NinoTreeWalk
    {
        private object _node;
        private int _depth, _deepest;
        private readonly object _end;
        private readonly object[] _stack;
        private readonly int _left, _right;

        /// <summary>
        /// The walk of the tree under <paramref name="root"/>, whose subtrees end at <paramref name="end"/> (null, or
        /// its empty node), with each node's children at the offsets <paramref name="left"/> and
        /// <paramref name="right"/>. Give its stack back with <see cref="Finish"/>.
        /// </summary>
        public NinoTreeWalk(object root, object end, int left, int right)
        {
            _node = root;
            _end = end;
            _left = left;
            _right = right;
            _stack = NinoGather<object>.Take().Array;
            _depth = _deepest = 0;
        }

        /// <summary>The byte offset of <paramref name="field"/>, a field of <paramref name="node"/>.</summary>
        public static int OffsetOf<TField>(object node, ref TField field) => ObjectFields.OffsetOf(node, ref field);

        /// <summary>The next node in order, or null after the last.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Next()
        {
            object node = _node;
            int depth = _depth;
            object top = Step(ref node, ref depth, _stack, _end, _left, _right);
            if (depth >= _deepest)
                _deepest = depth + 1;
            _node = node;
            _depth = depth;
            return top;
        }

        /// <summary>
        /// Fills <paramref name="chunk"/> with the elements of the next nodes, each the <typeparamref name="T"/> at the
        /// offset <paramref name="item"/> (<see cref="NinoTreeItems{T}"/>): <see cref="Next"/>'s walk with its state in
        /// locals for the loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Fill<T>(Span<T> chunk, int item)
        {
            // The walk's state in locals for the loop, written back once.
            object node = _node, end = _end;
            int depth = _depth, deepest = _deepest, left = _left, right = _right;
            object[] stack = _stack;
            ref T at = ref MemoryMarshal.GetReference(chunk);
            int filled = 0;
            for (; filled < chunk.Length; filled++)
            {
                object top = Step(ref node, ref depth, stack, end, left, right);
                if (depth >= deepest)
                    deepest = depth + 1;
                if (top == null)
                    break;
                Unsafe.Add(ref at, filled) = ObjectFields.At<T>(top, item);
            }

            _node = node;
            _depth = depth;
            _deepest = deepest;
            return filled;
        }

        /// <summary>
        /// Gives the stack back; throws <see cref="InvalidOperationException"/> where the tree holds nodes the walk did
        /// not reach (more than the collection's count, or changed during the walk).
        /// </summary>
        public void Finish()
        {
            bool more = _node != _end || _depth != 0;
            NinoGather<object>.Return(new NinoGather<object>.Chunk(_stack, 0), _deepest);
            if (more)
                NinoThrow.ModifiedDuringWrite();
        }

        /// <summary>
        /// Descends from <paramref name="node"/> along left children, pushing each, then pops the next node in order
        /// and goes on from its right child; null when the stack is empty (the depth then counts nothing popped).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object Step(ref object node, ref int depth, object[] stack, object end, int left, int right)
        {
            ref object slot = ref NinoUnsafe.GetArrayDataReference(stack);
            for (object at = node; at != end; at = ObjectFields.At<object>(at, left))
            {
                if (at == null || (uint)depth >= (uint)stack.Length)
                    NinoThrow.ModifiedDuringWrite();
                Unsafe.Add(ref slot, depth++) = at;
            }

            if (depth == 0)
            {
                node = end;
                return null;
            }

            object top = Unsafe.Add(ref slot, --depth);
            node = ObjectFields.At<object>(top, right);
            return top;
        }
    }

    /// <summary>
    /// For Nino's own collections and packages (Nino.Immutable): the elements of a tree whose nodes hold their element
    /// whole (a <c>SortedSet</c>'s, an <c>ImmutableList</c>'s, an <c>ImmutableSortedSet</c>'s), the
    /// <typeparamref name="T"/> at one offset of each node, in order (<see cref="NinoTreeWalk"/>).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NinoTreeItems<T> : INinoGatherSource<T>
    {
        private NinoTreeWalk _nodes;
        private readonly int _item;

        /// <summary>
        /// The elements of the tree under <paramref name="root"/> (as <see cref="NinoTreeWalk"/> takes it), each at the
        /// offset <paramref name="item"/> of its node.
        /// </summary>
        public NinoTreeItems(object root, object end, int left, int right, int item)
        {
            _nodes = new NinoTreeWalk(root, end, left, right);
            _item = item;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Fill(Span<T> chunk) => _nodes.Fill(chunk, _item);

        /// <inheritdoc/>
        public void Finish() => _nodes.Finish();
    }
}
