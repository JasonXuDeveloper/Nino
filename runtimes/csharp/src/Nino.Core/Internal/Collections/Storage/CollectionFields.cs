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
        /// <summary>An entry of <see cref="HashSet{T}"/> (HashCode, Next, Value; hashCode, next, value
        /// elsewhere).</summary>
        internal struct SetEntry<T>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public int HashCode;
            public int Next;
            public T Value;
#pragma warning restore CS0649
        }

        /// <summary>
        /// The raw value of a <see cref="SetEntry{T}"/> as a block (<see cref="INinoFormatter{T}.BlockMap"/>, its one
        /// item placed by <see cref="NinoBlockMap.Item{TItem, TFormatter}"/>), so that a set's entries are written by
        /// <see cref="NinoBlockCollections.WriteAll{T, TFormatter}"/>: several entries at a time, their values packed
        /// out of the strided entries by vector permutes (or the registered kernels in the netstandard2.1 build), one
        /// at a time where there are none or the value has padding. Writes only.
        /// </summary>
        internal struct SetEntryBlock<T, TFormatter> : INinoFormatter<SetEntry<T>>
            where TFormatter : struct, INinoFormatter<T>
        {
            public int FixedSize => NinoBlocks.ItemSize<T, TFormatter>();

            public NinoBlockMap BlockMap()
            {
                var entry = default(SetEntry<T>);
                return new NinoBlockMap().Item<T, TFormatter>(NinoBlocks.Offset(ref entry, ref entry.Value));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteFixed(in SetEntry<T> value, ref byte destination) =>
                NinoBlocks.WriteItem<T, TFormatter>(value.Value, ref destination);

            public void Serialize(SetEntry<T> value, ref Writer writer) => throw new NotSupportedException();
            public void Deserialize(ref Reader reader, ref SetEntry<T> value) => throw new NotSupportedException();
        }

        /// <summary>
        /// The value of a <see cref="SetEntry{T}"/> moved as its bytes, whatever they hold: for values the caller
        /// makes the wire's bytes after the move (slots: <see cref="INinoFormatter{T}.SlotsAreMemory"/>), where
        /// <see cref="SetEntryBlock{T, TFormatter}"/> takes only values that are the wire's bytes already. Writes only.
        /// </summary>
        internal struct SetEntryBytes<T> : INinoFormatter<SetEntry<T>>
        {
            public int FixedSize => Unsafe.SizeOf<T>();

            public NinoBlockMap BlockMap()
            {
                var entry = default(SetEntry<T>);
                return new NinoBlockMap().Bytes(NinoBlocks.Offset(ref entry, ref entry.Value), 0, Unsafe.SizeOf<T>());
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteFixed(in SetEntry<T> value, ref byte destination) =>
                NinoUnsafe.Write(ref destination, value.Value);

            public void Serialize(SetEntry<T> value, ref Writer writer) => throw new NotSupportedException();
            public void Deserialize(ref Reader reader, ref SetEntry<T> value) => throw new NotSupportedException();
        }

        /// <summary>
        /// The raw key and value of a <see cref="NinoDictionaryEntry{TKey,TValue}"/> as one block, as
        /// <see cref="SetEntryBlock{T, TFormatter}"/> for a set's entries.
        /// </summary>
        internal struct MapEntryBlock<TKey, TValue, TKeyFormatter, TValueFormatter>
            : INinoFormatter<NinoDictionaryEntry<TKey, TValue>>
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            public int FixedSize =>
                NinoBlocks.ItemSize<TKey, TKeyFormatter>() + NinoBlocks.ItemSize<TValue, TValueFormatter>();

            public NinoBlockMap BlockMap()
            {
                var entry = default(NinoDictionaryEntry<TKey, TValue>);
                return new NinoBlockMap().Item<TKey, TKeyFormatter>(NinoBlocks.Offset(ref entry, ref entry.Key))
                    .Item<TValue, TValueFormatter>(NinoBlocks.Offset(ref entry, ref entry.Value));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteFixed(in NinoDictionaryEntry<TKey, TValue> value, ref byte destination)
            {
                NinoBlocks.WriteItem<TKey, TKeyFormatter>(value.Key, ref destination);
                NinoBlocks.WriteItem<TValue, TValueFormatter>(value.Value,
                    ref Unsafe.Add(ref destination, NinoBlocks.ItemSize<TKey, TKeyFormatter>()));
            }

            public void Serialize(NinoDictionaryEntry<TKey, TValue> value, ref Writer writer) => throw new NotSupportedException();

            public void Deserialize(ref Reader reader, ref NinoDictionaryEntry<TKey, TValue> value) =>
                throw new NotSupportedException();
        }

        /// <summary>
        /// The entries of <paramref name="set"/> when all of its first Count are in use (none removed since), else
        /// null. The entries' layout is the runtime's for the element type, the same as the mirror's (references included).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SetEntry<T>[] Entries<T>(HashSet<T> set, int count) =>
            ObjectFields.At<int>(set, CollectionViews.HashSetBound) == count
                ? Unsafe.As<SetEntry<T>[]>(ObjectFields.At<object>(set, CollectionViews.HashSetEntries))
                : null;

        /// <summary>
        /// As <see cref="Entries{T}(HashSet{T}, int)"/>, for a dictionary, and also null where
        /// <see cref="CollectionViews.Dictionary"/> is not set: read in place of the enumerator, a loop over an array,
        /// where the enumerator stays in memory and checks the version per pair. At least <paramref name="count"/> long.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoDictionaryEntry<TKey, TValue>[] Entries<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count)
        {
            if (!CollectionViews.Dictionary || ObjectFields.At<int>(dictionary, CollectionViews.DictionaryBound) != count)
                return null;
            var entries = Unsafe.As<NinoDictionaryEntry<TKey, TValue>[]>(
                ObjectFields.At<object>(dictionary, CollectionViews.DictionaryEntries));
            return entries != null && count <= entries.Length ? entries : null;
        }

        /// <summary>
        /// The <see cref="List{T}"/> that <paramref name="collection"/> wraps (what its constructors without a list
        /// make, and <c>ObservableCollection</c>'s), or null when it wraps another <see cref="IList{T}"/>. Call where
        /// <see cref="CollectionViews.Collection"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static List<T> List<T>(System.Collections.ObjectModel.Collection<T> collection)
        {
            object items = ObjectFields.At<object>(collection, CollectionViews.CollectionItems);
            return items != null && items.GetType() == typeof(List<T>) ? Unsafe.As<List<T>>(items) : null;
        }

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
