using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <see cref="CollectionFormatter{TCollection,T,TFormatter}"/> for the runtime's own collections (and types derived
    /// from them): each one through its own methods, or its fields where <see cref="CollectionViews"/> checked their
    /// layout, rather than through <see cref="ICollection{T}"/>. The bytes are those of the general path.
    /// </summary>
    internal static class BclCollections<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
        /// <summary>Writes <paramref name="value"/> (not null) if it is one of these collections.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWrite(ICollection<T> value, ref Writer writer)
        {
            switch (value)
            {
                case SortedSet<T> sorted:
                    writer = WriteSorted(sorted, writer);
                    return true;
                case LinkedList<T> linked:
                    writer = WriteLinked(linked, writer);
                    return true;
                case System.Collections.ObjectModel.Collection<T> wrapper:
                    // The list it wraps, written as a list is (a subclass too: reading its items runs none of its
                    // code). Through ICollection it was enumerated or copied out, an element at a time.
                    if (!CollectionViews.Collection || CollectionFields.List(wrapper) is not { } wrapped)
                        return false;
                    NinoCollections.WriteSpan<T, TFormatter>(ListAccess.AsSpan<T>(wrapped), ref writer);
                    return true;
                default:
                    return false;
            }
        }


        /// <summary>
        /// Walked out of its tree a chunk at a time (its enumerator keeps a stack of nodes and checks the version per
        /// element), else through that enumerator. Out of line, the writer by value: inlined, the walk's setup took
        /// registers and stack from every caller (asm.py: SortedSet&lt;int&gt;'s write 55 to 76 stack accesses).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteSorted(SortedSet<T> sorted, Writer writer)
        {
            object root = SortedSetTrees.RootOf(sorted);
            return root != null
                ? NinoGather.Write<T, TFormatter, NinoTreeItems<T>>(SortedSetNodes<T>.Items(root), sorted.Count,
                    writer)
                : NinoGather.Write<T, TFormatter, NinoGathered.Enumerated<T, SortedSet<T>.Enumerator>>(
                    new(sorted.GetEnumerator()), sorted.Count, writer);
        }

        /// <summary>Walked along its ring of nodes a chunk at a time where its layout was checked, else its enumerator.
        /// Out of line as <see cref="WriteSorted"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteLinked(LinkedList<T> linked, Writer writer) =>
            CollectionViews.LinkedList
                ? NinoGather.Write<T, TFormatter, Ring>(new Ring(linked), linked.Count, writer)
                : NinoGather.Write<T, TFormatter, NinoGathered.Enumerated<T, LinkedList<T>.Enumerator>>(
                    new(linked.GetEnumerator()), linked.Count, writer);

        /// <summary>
        /// Reads <paramref name="length"/> elements into <paramref name="collection"/> (empty) if it is one of these
        /// collections.
        /// </summary>
        public static bool TryRead(ICollection<T> collection, int length, ref Reader reader)
        {
            switch (collection)
            {
                case SortedSet<T> sorted:
                    ReadSorted(sorted, length, ref reader);
                    return true;
                case LinkedList<T> linked:
                    reader = ReadLinked(linked, length, reader);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A new <typeparamref name="TCollection"/> of the <paramref name="length"/> elements that follow, if it is a
        /// concurrent stack, queue or bag, which the runtime builds faster from an array than element by element;
        /// otherwise null, and nothing is read.
        /// </summary>
        public static TCollection TryCreate<TCollection>(int length, ref Reader reader) where TCollection : class
        {
            if (typeof(TCollection) != typeof(ConcurrentStack<T>) && typeof(TCollection) != typeof(ConcurrentQueue<T>) &&
                typeof(TCollection) != typeof(ConcurrentBag<T>))
                return null;
            var items = NinoCollections.ReadRented<T, TFormatter>(length, ref reader);
            try
            {
                if (typeof(TCollection) == typeof(ConcurrentQueue<T>))
                    // Its first segment sized for them, filled without contention.
                    return (TCollection)(object)new ConcurrentQueue<T>(new RentedItems<T>(items, length));
                if (typeof(TCollection) == typeof(ConcurrentBag<T>))
                    // Added to this thread's own queue by the constructor, in the order TryAdd would add them.
                    return (TCollection)(object)new ConcurrentBag<T>(new RentedItems<T>(items, length));
                // One linked chain swapped in at once, where a push per element swaps the top each time (2x faster).
                var stack = new ConcurrentStack<T>();
                if (length != 0)
                    stack.PushRange(items, 0, length);
                return (TCollection)(object)stack;
            }
            finally
            {
                NinoCollections.ReturnRented(items);
            }
        }

        /// <summary>
        /// The items of a <see cref="LinkedList{T}"/> along its ring of nodes (<see cref="CollectionFields.LinkedNode{T}"/>),
        /// a chunk at a time: the list's count of them, then back at its head with its version unchanged, or it changed
        /// during the walk.
        /// </summary>
        private struct Ring : INinoGatherSource<T>
        {
            private readonly LinkedList<T> _list;
            private readonly LinkedListNode<T> _head;
            private LinkedListNode<T> _node;
            private int _left;
            private readonly int _version;

            public Ring(LinkedList<T> list)
            {
                _list = list;
                _head = _node = CollectionFields.Head(list);
                _left = list.Count;
                _version = CollectionFields.Version(list);
            }

            public int Fill(Span<T> chunk)
            {
                int filled = Math.Min(_left, chunk.Length);
                var node = _node;
                ref T at = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(chunk);
                for (int i = 0; i < filled; i++)
                {
                    if (node == null)
                        NinoThrow.ModifiedDuringWrite();
                    var fields = Unsafe.As<CollectionFields.LinkedNode<T>>(node);
                    Unsafe.Add(ref at, i) = fields.item;
                    node = fields.next;
                }

                _node = node;
                _left -= filled;
                return filled;
            }

            public void Finish()
            {
                if (_node != _head || CollectionFields.Version(_list) != _version)
                    NinoThrow.ModifiedDuringWrite();
            }
        }

        /// <summary>
        /// Each chunk's items linked as new nodes, and the list's fields set once at the end (<see cref="Close"/>), where
        /// its layout was checked: AddLast checks the node, links it in and bumps the count and the version per element
        /// (a third of the time a list of ints takes to build). Until then the list is empty: a read rejected half way
        /// leaves no node in it.
        /// </summary>
        private struct Links : INinoGatherTarget<T>
        {
            private readonly LinkedList<T> _list;
            private LinkedListNode<T> _first, _last;
            private int _count;

            public Links(LinkedList<T> list)
            {
                _list = list;
                _first = _last = null;
                _count = 0;
            }

            public void Add(Span<T> chunk)
            {
                var last = _last;
                foreach (var item in chunk)
                {
                    var node = new LinkedListNode<T>(item);
                    CollectionFields.Append(_list, node, last);
                    _first ??= node;
                    last = node;
                }

                _last = last;
                _count += chunk.Length;
            }

            public void Close()
            {
                if (_last == null)
                    return;
                CollectionFields.Close(_first, _last);
                CollectionFields.Head(_list) = _first;
                CollectionFields.Count(_list) = _count;
                CollectionFields.Version(_list)++;
            }
        }

        private struct AddLast : INinoAdder<LinkedList<T>, T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(LinkedList<T> target, T item) => target.AddLast(item);
        }

        /// <summary>
        /// Read a chunk at a time and linked (<see cref="Links"/>), or added through AddLast where the list's layout
        /// is not known or it is not empty. Out of line with the reader by value: the target's fields and both calls
        /// inlined into every read of a list took stack there (asm.py).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadLinked(LinkedList<T> linked, int length, Reader reader)
        {
            if (!CollectionViews.LinkedList || linked.Count != 0)
            {
                var adds = new NinoGather.Adds<LinkedList<T>, T, AddLast>(linked);
                return NinoGather.ReadAll<T, TFormatter, NinoGather.Adds<LinkedList<T>, T, AddLast>>(ref adds, length,
                    reader);
            }

            var links = new Links(linked);
            reader = NinoGather.ReadAll<T, TFormatter, Links>(ref links, length, reader);
            links.Close();
            return reader;
        }

        /// <summary>
        /// Read as an array, then built in one pass where the tree can be moved in (<see cref="SortedSetTrees.Fill{T}"/>),
        /// otherwise added one by one. Duplicates are dropped, as Add drops them.
        /// </summary>
        private static void ReadSorted(SortedSet<T> set, int length, ref Reader reader)
        {
            var items = NinoCollections.ReadRented<T, TFormatter>(length, ref reader);
            try
            {
                if (SortedSetTrees.CanFill(set))
                {
                    SortedSetTrees.Fill(set, items, length);
                    return;
                }

                for (int i = 0; i < length; i++)
                    set.Add(items[i]);
            }
            finally
            {
                NinoCollections.ReturnRented(items);
            }
        }
    }
}
