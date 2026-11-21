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
                // By reference: the chunk's length is not known to the JIT to bound what the enumerator gives. The
                // enumerator in a local for the loop, written back once: a field of the source, reached through it,
                // is read and written in memory each step.
                ref T at = ref MemoryMarshal.GetReference(chunk);
                var items = _items;
                int filled = 0;
                while (filled < chunk.Length && items.MoveNext())
                {
                    Unsafe.Add(ref at, filled) = items.Current;
                    filled++;
                }

                _items = items;
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
    /// For Nino's own collections and packages (Nino.Immutable): what a tree walked by <see cref="NinoTreeWalk"/> holds
    /// in a node. Implemented by a struct, so the call is direct.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoTreeNodes<T>
    {
        /// <summary>
        /// Writes the element of <paramref name="node"/> to <paramref name="item"/>; false ends the fill after it, for a
        /// node that holds further elements (a hash bucket of keys that share a hash), which the caller adds before
        /// the next fill.
        /// </summary>
        bool Take(object node, ref T item);
    }

    /// <summary>
    /// For Nino's own collections and packages (Nino.Immutable): a binary tree walked in order (a <c>SortedSet</c>'s
    /// red-black tree, the immutable collections' AVL trees) into a chunk of a gather at a time
    /// (<see cref="NinoGathered"/>): the one walk of every tree, where each collection says only where a node's
    /// children are (byte offsets, <see cref="OffsetOf"/>) and what its element is (<see cref="INinoTreeNodes{T}"/>);
    /// a subtree ends at null or at the tree's own empty node. Recursive, so the path to a node lives in the frames:
    /// only when a fill stops inside the tree is it saved, on a stack of nodes (a chunk of the gather's buffer of
    /// objects, taken then), and resumed from there; a tree written in one fill takes none. A path deeper than any
    /// balanced tree, or a null child where subtrees end at an empty node, is a tree changed during the walk.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NinoTreeWalk
    {
        /// <summary>
        /// Deeper than any balanced tree of up to <see cref="int.MaxValue"/> nodes (a red-black tree's height is at
        /// most 62, an AVL tree's 45), and far less than a stack of nodes holds.
        /// </summary>
        private const int MaxDepth = 128;

        // The subtree walked next, then the nodes on the stack under _depth, each with its right subtree.
        private object _node;
        private int _depth, _deepest;
        private readonly object _end;
        private object[] _stack;
        private readonly int _left, _right;

        /// <summary>
        /// The walk of the tree under <paramref name="root"/>, whose subtrees end at <paramref name="end"/> (null, or
        /// its empty node), with each node's children at the offsets <paramref name="left"/> and
        /// <paramref name="right"/>. Give back the stack it may take with <see cref="Finish"/>.
        /// </summary>
        public NinoTreeWalk(object root, object end, int left, int right)
        {
            _node = root;
            _end = end;
            _left = left;
            _right = right;
            _stack = null;
            _depth = _deepest = 0;
        }

        /// <summary>The byte offset of <paramref name="field"/>, a field of <paramref name="node"/>.</summary>
        public static int OffsetOf<TField>(object node, ref TField field) => ObjectFields.OffsetOf(node, ref field);

        /// <summary>
        /// Fills <paramref name="chunk"/> with the elements of the next nodes in order (<paramref name="nodes"/>' take of
        /// each); returns how many: fewer than it holds only after the last node, or where a node ended the fill.
        /// </summary>
        public int Fill<T, TNodes>(Span<T> chunk, ref TNodes nodes) where TNodes : struct, INinoTreeNodes<T>
        {
            var path = new Path(_end, _stack, _left, _right, chunk.Length);
            ref T at = ref MemoryMarshal.GetReference(chunk);
            object next = _node;
            int depth = _depth;
            while (true)
            {
                if (next != path.End && !Walk(next, depth, ref at, ref path, ref nodes))
                {
                    next = path.Next;
                    depth = path.Top;
                    break;
                }

                next = path.End;
                if (depth == 0 || path.Filled == path.Length)
                    break;
                object top = path.Stack[--depth];
                next = ObjectFields.At<object>(top, _right);
                if (!nodes.Take(top, ref Unsafe.Add(ref at, path.Filled++)))
                    break;
            }

            _node = next;
            _depth = depth;
            _stack = path.Stack;
            if (depth > _deepest)
                _deepest = depth;
            return path.Filled;
        }

        /// <summary>
        /// Gives the stack back; throws <see cref="InvalidOperationException"/> where the tree holds nodes the walk did
        /// not reach (more than the collection's count, or changed during the walk).
        /// </summary>
        public void Finish()
        {
            bool more = _node != _end || _depth != 0;
            if (_stack != null)
                NinoGather<object>.Return(new NinoGather<object>.Chunk(_stack, 0), _deepest);
            if (more)
                NinoThrow.ModifiedDuringWrite();
        }

        /// <summary>
        /// Walks the subtree under <paramref name="node"/> (not the end) in order into the chunk: true when it is done,
        /// false when the chunk filled or a node ended the fill first, the rest saved (<see cref="Path.Next"/>, then
        /// this frame's node and its ancestors' on the stack from <paramref name="depth"/> down).
        /// </summary>
        private static bool Walk<T, TNodes>(object node, int depth, ref T at, ref Path path, ref TNodes nodes)
            where TNodes : struct, INinoTreeNodes<T>
        {
            do
            {
                if (node == null || (uint)depth >= MaxDepth)
                    NinoThrow.ModifiedDuringWrite();
                object left = ObjectFields.At<object>(node, path.Left);
                if (left != path.End && !Walk(left, depth + 1, ref at, ref path, ref nodes))
                {
                    path.Stack[depth] = node;
                    return false;
                }

                if (path.Filled == path.Length)
                {
                    path.Save(depth, node);
                    path.Top = depth + 1;
                    path.Next = path.End;
                    return false;
                }

                object right = ObjectFields.At<object>(node, path.Right);
                if (!nodes.Take(node, ref Unsafe.Add(ref at, path.Filled++)))
                {
                    path.Take();
                    path.Top = depth;
                    path.Next = right;
                    return false;
                }

                node = right;
            } while (node != path.End);

            return true;
        }

        /// <summary>
        /// A reference as the element of a fill (<see cref="NinoTreeItems{T}"/> of a class <c>T</c>): a struct, so that
        /// the walk over it is not code shared over every class.
        /// </summary>
        internal struct Reference
        {
            public object Value;
        }

        /// <summary>The reference at one offset of each node.</summary>
        internal readonly struct ReferenceAt : INinoTreeNodes<Reference>
        {
            private readonly int _offset;

            public ReferenceAt(int offset) => _offset = offset;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Take(object node, ref Reference item)
            {
                item.Value = ObjectFields.At<object>(node, _offset);
                return true;
            }
        }

        /// <summary>A fill's shape, and where it stopped.</summary>
        private struct Path
        {
            public readonly object End;
            public object[] Stack;
            public readonly int Left, Right, Length;
            public int Filled, Top;
            public object Next;

            public Path(object end, object[] stack, int left, int right, int length)
            {
                End = end;
                Stack = stack;
                Left = left;
                Right = right;
                Length = length;
                Filled = Top = 0;
                Next = null;
            }

            /// <summary>The stack the path is saved on, taken by the fill that first stops inside the tree.</summary>
            public void Take()
            {
                if (Stack == null)
                    Stack = NinoGather<object>.Take().Array;
            }

            /// <summary>Where the innermost frame stopped: its node, before its element, saved first.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Save(int depth, object node)
            {
                Take();
                Stack[depth] = node;
            }
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
        public int Fill(Span<T> chunk)
        {
            if (!typeof(T).IsValueType)
                return FillReferences(chunk);
            var items = new At(_item);
            return _nodes.Fill(chunk, ref items);
        }

        /// <summary>
        /// Class elements (a polymorphic base's) as references in a struct of one field: code shared over a class T
        /// calls the take of each element through a lookup, an indirect call (the walk is generic over the take, a
        /// struct generic over T), where over a struct the walk is its own instantiation, the take inlined into it. A
        /// method of its own: inline in <see cref="Fill"/>, its IL (though folded away) kept a value element's walk
        /// from inlining there (asm.py: a call per chunk in NinoTreeItems&lt;int&gt;.Fill).
        /// </summary>
        private int FillReferences(Span<T> chunk)
        {
            var references = new NinoTreeWalk.ReferenceAt(_item);
            return _nodes.Fill(MemoryMarshal.CreateSpan(
                ref Unsafe.As<T, NinoTreeWalk.Reference>(ref MemoryMarshal.GetReference(chunk)), chunk.Length),
                ref references);
        }

        /// <inheritdoc/>
        public void Finish() => _nodes.Finish();

        private readonly struct At : INinoTreeNodes<T>
        {
            private readonly int _offset;

            public At(int offset) => _offset = offset;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Take(object node, ref T item)
            {
                item = ObjectFields.At<T>(node, _offset);
                return true;
            }
        }
    }
}
