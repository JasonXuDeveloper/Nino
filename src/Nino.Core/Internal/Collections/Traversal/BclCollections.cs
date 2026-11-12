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
                    object root = SortedSetTrees.RootOf(sorted);
#if NET8_0_OR_GREATER
                    if (!NinoUnsafe.IsReferenceOrContainsReferences<T>())
                    {
                        int count = sorted.Count;
                        if (count >= 32 && TryWritePackedTree(count, root, ref writer))
                            return true;
                    }
#endif
                    // Copied out of its tree: its enumerator keeps a stack of nodes and checks the version per
                    // element.
                    NinoCollections.WriteCopied<T, TFormatter>(sorted, root, ref writer);
                    return true;
                case LinkedList<T> linked:
#if NET8_0_OR_GREATER
                    if (linked.Count >= 32 && CollectionViews.LinkedList && RawFormatter.Is<T, TFormatter>())
                    {
                        WriteLinkedRing(linked, ref writer);
                        return true;
                    }
#endif
                    WriteLinked(linked, ref writer);
                    return true;
                default:
                    return false;
            }
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWritePackedTree(int count, object root, ref Writer writer)
        {
            if (!NinoUnsafe.Is64Bit || root == null ||
                !RawFormatter.Is<T, TFormatter>() || NinoLayout<T>.HasPadding)
                return false;

            SortedSetNodes<T>.WritePacked(root, count, ref writer);
            return true;
        }
#endif

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
                    ReadLinked(linked, length, ref reader);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A new <typeparamref name="TCollection"/> of the <paramref name="length"/> elements that follow, if it is a
        /// concurrent stack or queue, which the runtime builds faster from an array than element by element;
        /// otherwise null, and nothing is read.
        /// </summary>
        public static TCollection TryCreate<TCollection>(int length, ref Reader reader) where TCollection : class
        {
            if (typeof(TCollection) != typeof(ConcurrentStack<T>) && typeof(TCollection) != typeof(ConcurrentQueue<T>))
                return null;
            var items = NinoCollections.ReadRented<T, TFormatter>(length, ref reader);
            try
            {
                if (typeof(TCollection) == typeof(ConcurrentQueue<T>))
                    // Its first segment sized for them, filled without contention.
                    return (TCollection)(object)new ConcurrentQueue<T>(new Run(items, length));
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
        /// The first <c>count</c> items of a rented array, for a constructor that takes an
        /// <see cref="IEnumerable{T}"/>: its own enumerator, read once, where an <see cref="ArraySegment{T}"/> boxes
        /// a struct enumerator and checks its range per item (89738283: 7% slower on x64 runners).
        /// </summary>
        private sealed class Run : ICollection<T>, IEnumerator<T>
        {
            private readonly T[] _items;
            private readonly int _count;
            private int _index = -1;

            public Run(T[] items, int count)
            {
                _items = items;
                _count = count;
            }

            public int Count => _count;
            public bool IsReadOnly => true;
            public T Current => _items[_index];
            object System.Collections.IEnumerator.Current => Current;

            public bool MoveNext() => ++_index < _count;
            public IEnumerator<T> GetEnumerator() => this;
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this;
            public void CopyTo(T[] array, int arrayIndex) => Array.Copy(_items, 0, array, arrayIndex, _count);
            public void Reset() => _index = -1;
            public void Dispose() { }
            public void Add(T item) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(T item) => throw new NotSupportedException();
            public bool Remove(T item) => throw new NotSupportedException();
        }

        /// <summary>Node by node, with the writer as a local; raw elements into one reserved block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteLinked(LinkedList<T> linked, ref Writer writer)
        {
            int count = linked.Count;
            if (RawFormatter.Is<T, TFormatter>())
            {
                int size = Unsafe.SizeOf<T>();
                ref byte elements = ref writer.ReserveCounted(count, size);
                ref byte next = ref elements;
                for (var node = linked.First; node != null; node = node.Next)
                {
                    if (--count < 0)
                        NinoThrow.ModifiedDuringWrite();
#if NET8_0_OR_GREATER
                    // The value in place, not a copy of it.
                    Unsafe.WriteUnaligned(ref next, node.ValueRef);
#else
                    NinoUnsafe.Write(ref next, node.Value);
#endif
                    next = ref Unsafe.Add(ref next, size);
                }

                NinoLayout<T>.ClearPadding(ref elements, linked.Count);
                return;
            }

            Writer local = default;
            writer.CopyTo(ref local);
            local.WriteLength(count);
            for (var node = linked.First; node != null; node = node.Next)
                default(TFormatter).Serialize(node.Value, ref local);
            local.CopyTo(ref writer);
        }

#if NET8_0_OR_GREATER
        // Inline the node helper from the first call.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void WriteLinkedRing(LinkedList<T> linked, ref Writer writer)
        {
            int version = CollectionFields.Version(linked);
            int count = linked.Count;
            int size = Unsafe.SizeOf<T>();
            ref byte first = ref writer.ReserveCounted(count, size);
            ref byte destination = ref first;
            var head = linked.First;
            var node = head;
            int left = count;
            for (; left >= 2; left -= 2)
            {
                node = WriteLinkedNode(node, ref destination);
                node = WriteLinkedNode(node, ref Unsafe.Add(ref destination, size));
                destination = ref Unsafe.Add(ref destination, 2 * size);
            }
            if (left != 0)
                node = WriteLinkedNode(node, ref destination);

            if (node != head || CollectionFields.Version(linked) != version)
                NinoThrow.ModifiedDuringWrite();
            NinoLayout<T>.ClearPadding(ref first, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static LinkedListNode<T> WriteLinkedNode(LinkedListNode<T> node, ref byte destination)
        {
            var fields = Unsafe.As<CollectionFields.LinkedNode<T>>(node);
            var next = fields.next;
            NinoUnsafe.WriteIn(ref destination, in fields.item);
            return next;
        }
#endif

        /// <summary>
        /// Each node linked as it is read, and the list's fields set once at the end, where its layout was checked:
        /// AddLast checks the node, links it in and bumps the count and the version per element (a third of the time
        /// a list of ints takes to build).
        /// </summary>
        private static void ReadLinked(LinkedList<T> linked, int length, ref Reader reader)
        {
            if (!CollectionViews.LinkedList || length == 0 || linked.Count != 0)
            {
                reader = AddEach(linked, length, reader);
                return;
            }

            Reader local = default;
            reader.CopyTo(ref local);
            LinkedListNode<T> first = null, last = null;
            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref local, ref item);
                var node = new LinkedListNode<T>(item);
                CollectionFields.Append(linked, node, last);
                if (last == null)
                    first = node;
                last = node;
            }

            CollectionFields.Close(first, last);
            CollectionFields.Head(linked) = first;
            CollectionFields.Count(linked) = length;
            CollectionFields.Version(linked)++;
            local.CopyTo(ref reader);
        }

        /// <summary>Through AddLast, where the list's layout is not known.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader AddEach(LinkedList<T> linked, int length, Reader reader)
        {
            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref reader, ref item);
                linked.AddLast(item);
            }

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
