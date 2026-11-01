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
                    // Copied out of its tree: its enumerator keeps a stack of nodes and checks the version per
                    // element.
                    NinoCollections.WriteCopied<T, TFormatter>(sorted, SortedSetTrees.RootOf(sorted), ref writer);
                    return true;
                case LinkedList<T> linked:
                    WriteLinked(linked, ref writer);
                    return true;
                default:
                    return false;
            }
        }

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
                    return (TCollection)(object)new ConcurrentQueue<T>(new ArraySegment<T>(items, 0, length));
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

        /// <summary>Node by node, with the writer as a local; raw elements into one reserved block.</summary>
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
                default(TFormatter).Deserialize(ref item, ref local);
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
                default(TFormatter).Deserialize(ref item, ref reader);
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
