using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The fields of the runtime's collections at the offsets <see cref="CollectionViews"/> found; use each only when
    /// its flag there is set.
    /// </summary>
    internal static class CollectionFields
    {
        /// <summary>An entry of <see cref="HashSet{T}"/> (HashCode, Next, Value; hashCode, next, value elsewhere).</summary>
        internal struct SetEntry<T>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public int HashCode;
            public int Next;
            public T Value;
#pragma warning restore CS0649
        }

        /// <summary>An entry of <see cref="Dictionary{TKey,TValue}"/> (hashCode, next, key, value).</summary>
        internal struct MapEntry<TKey, TValue>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public int HashCode;
            public int Next;
            public TKey Key;
            public TValue Value;
#pragma warning restore CS0649
        }

        /// <summary>
        /// The raw value of a <see cref="SetEntry{T}"/> as a block (<see cref="INinoBlockFormatter{T}"/>), so that a
        /// set's entries are written by <see cref="NinoBlock.WriteAll{T, TFormatter}"/>: several entries at a time,
        /// their values packed out of the strided entries by vector permutes (or the registered kernels in the
        /// netstandard builds), one at a time where there are none or the value has padding. Writes only.
        /// </summary>
        internal struct SetEntryBlock<T> : INinoFormatter<SetEntry<T>>, INinoBlockFormatter<SetEntry<T>>
        {
            public int FixedSize => Unsafe.SizeOf<T>();

            public NinoBlockMap BlockMap()
            {
                var entry = default(SetEntry<T>);
                return new NinoBlockMap().Raw<T>(NinoBlocks.Offset(ref entry, ref entry.Value), 0);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteFixed(in SetEntry<T> value, ref byte destination)
            {
                NinoUnsafe.Write(ref destination, value.Value);
                NinoLayout<T>.ClearPadding(ref destination);
            }

            public void ReadBlock(ref SetEntry<T> value, ref byte source) => throw new NotSupportedException();
            public void Serialize(SetEntry<T> value, ref Writer writer) => throw new NotSupportedException();
            public void Deserialize(ref SetEntry<T> value, ref Reader reader) => throw new NotSupportedException();
        }

        /// <summary>
        /// The raw key and value of a <see cref="MapEntry{TKey,TValue}"/> as one block, as
        /// <see cref="SetEntryBlock{T}"/> for a set's entries.
        /// </summary>
        internal struct MapEntryBlock<TKey, TValue> : INinoFormatter<MapEntry<TKey, TValue>>,
            INinoBlockFormatter<MapEntry<TKey, TValue>>
        {
            public int FixedSize => Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>();

            public NinoBlockMap BlockMap()
            {
                var entry = default(MapEntry<TKey, TValue>);
                return new NinoBlockMap().Raw<TKey>(NinoBlocks.Offset(ref entry, ref entry.Key), 0)
                    .Raw<TValue>(NinoBlocks.Offset(ref entry, ref entry.Value), Unsafe.SizeOf<TKey>());
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteFixed(in MapEntry<TKey, TValue> value, ref byte destination)
            {
                NinoUnsafe.Write(ref destination, 0, value.Key);
                NinoUnsafe.Write(ref destination, Unsafe.SizeOf<TKey>(), value.Value);
                NinoLayout<TKey>.ClearPadding(ref destination);
                NinoLayout<TValue>.ClearPadding(ref Unsafe.Add(ref destination, Unsafe.SizeOf<TKey>()));
            }

            public void ReadBlock(ref MapEntry<TKey, TValue> value, ref byte source) =>
                throw new NotSupportedException();

            public void Serialize(MapEntry<TKey, TValue> value, ref Writer writer) => throw new NotSupportedException();

            public void Deserialize(ref MapEntry<TKey, TValue> value, ref Reader reader) =>
                throw new NotSupportedException();
        }

        /// <summary>
        /// The entries of <paramref name="set"/> when all of its first Count are in use (none removed since), else
        /// null. Read for element types without references only, whose entries are laid out in field order.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SetEntry<T>[] Entries<T>(HashSet<T> set, int count) =>
            ObjectFields.At<int>(set, CollectionViews.HashSetBound) == count
                ? Unsafe.As<SetEntry<T>[]>(ObjectFields.At<object>(set, CollectionViews.HashSetEntries))
                : null;

        /// <summary>As <see cref="Entries{T}(HashSet{T}, int)"/>, for a dictionary.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static MapEntry<TKey, TValue>[] Entries<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count) =>
            ObjectFields.At<int>(dictionary, CollectionViews.DictionaryBound) == count
                ? Unsafe.As<MapEntry<TKey, TValue>[]>(ObjectFields.At<object>(dictionary, CollectionViews.DictionaryEntries))
                : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Items<T>(List<T> list) => ref ObjectFields.At<T[]>(list, CollectionViews.ListItems);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(List<T> list) => ref ObjectFields.At<int>(list, CollectionViews.ListSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Array<T>(Stack<T> stack) =>
            ref ObjectFields.At<T[]>(stack, CollectionViews.StackArray);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(Stack<T> stack) => ref ObjectFields.At<int>(stack, CollectionViews.StackSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(Stack<T> stack) =>
            ref ObjectFields.At<int>(stack, CollectionViews.StackVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Array<T>(Queue<T> queue) =>
            ref ObjectFields.At<T[]>(queue, CollectionViews.QueueArray);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Head<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueHead);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Tail<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueTail);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(Queue<T> queue) =>
            ref ObjectFields.At<int>(queue, CollectionViews.QueueVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TKey[] Keys<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<TKey[]>(list, CollectionViews.SortedListKeys);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TValue[] Values<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<TValue[]>(list, CollectionViews.SortedListValues);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.SortedListSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.SortedListVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref IComparer<TKey> Comparer<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<IComparer<TKey>>(list, CollectionViews.SortedListComparer);

        /// <summary>The root node of the set's red-black tree (an internal type).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref object Root<T>(SortedSet<T> set) => ref ObjectFields.At<object>(set, CollectionViews.SortedSetRoot);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Count<T>(SortedSet<T> set) => ref ObjectFields.At<int>(set, CollectionViews.SortedSetCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(SortedSet<T> set) =>
            ref ObjectFields.At<int>(set, CollectionViews.SortedSetVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref LinkedListNode<T> Head<T>(LinkedList<T> list) =>
            ref ObjectFields.At<LinkedListNode<T>>(list, CollectionViews.LinkedListHead);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Count<T>(LinkedList<T> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.LinkedListCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(LinkedList<T> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.LinkedListVersion);

        /// <summary>
        /// <see cref="LinkedListNode{T}"/>'s fields, in its order on every known implementation (checked by
        /// <see cref="CollectionViews"/>): the list, then the next and previous nodes (a ring: the last node's next is
        /// the first), then the item.
        /// </summary>
        [SuppressMessage("ReSharper", "InconsistentNaming")]
        internal sealed class LinkedNode<T>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public LinkedList<T> list;
            public LinkedListNode<T> next;
            public LinkedListNode<T> prev;
            public T item;
#pragma warning restore CS0649
        }

        /// <summary>
        /// Links <paramref name="node"/> (new) into <paramref name="list"/> after <paramref name="last"/> (null: as the
        /// first). The list's own fields are set at the end, by the caller and <see cref="Close{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Append<T>(LinkedList<T> list, LinkedListNode<T> node, LinkedListNode<T> last)
        {
            var m = Unsafe.As<LinkedNode<T>>(node);
            m.list = list;
            if (last != null)
            {
                m.prev = last;
                Unsafe.As<LinkedNode<T>>(last).next = node;
            }
        }

        /// <summary>Closes the ring of nodes <see cref="Append{T}"/> linked.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Close<T>(LinkedListNode<T> first, LinkedListNode<T> last)
        {
            Unsafe.As<LinkedNode<T>>(first).prev = last;
            Unsafe.As<LinkedNode<T>>(last).next = first;
        }

        /// <summary>The set of the dictionary's pairs, ordered by key.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SortedSet<KeyValuePair<TKey, TValue>> Set<TKey, TValue>(
            SortedDictionary<TKey, TValue> dictionary) =>
            ObjectFields.At<SortedSet<KeyValuePair<TKey, TValue>>>(dictionary, CollectionViews.SortedDictionarySet);
    }
}
