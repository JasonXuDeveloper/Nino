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
        /// An entry of <see cref="Dictionary{TKey,TValue}"/> as the runtime lays it out (hashCode, next, key, value; the
        /// startup checks of <see cref="CollectionViews"/> compare it with the runtime's).
        /// </summary>
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
        /// What <see cref="EntryBlock{TEntry, TItems}"/> moves of an entry: the items of its element (a set's value, a
        /// dictionary's key and value) at their offsets in the entry, so that a run of entries is written by
        /// <see cref="NinoBlockCollections.WriteAll{T, TFormatter}"/> and new entries are read by
        /// <see cref="NinoBlockCollections.ReadAll{T, TFormatter}"/>: several entries at a time, the items packed out of
        /// the strided entries, or scattered into them, by vector permutes (or the registered kernels in the
        /// netstandard2.1 build), one entry at a time where there are none or an item has padding. A read writes every
        /// byte of an entry, its hash code and link as 0, which the read's caller then sets
        /// (<see cref="HashTables"/>).
        /// </summary>
        internal interface IEntryItems<TEntry>
        {
            /// <summary>The wire bytes of the items (-1: not of a fixed size).</summary>
            int Size { get; }

            /// <summary>The items' block map, at their offsets in the entry.</summary>
            NinoBlockMap Map();

            /// <summary>Writes the items of <paramref name="entry"/> into the <see cref="Size"/> bytes at
            /// <paramref name="destination"/>.</summary>
            void Write(in TEntry entry, ref byte destination);

            /// <summary>Reads the items of <paramref name="entry"/> from the <see cref="Size"/> bytes at
            /// <paramref name="source"/>, with the checks a read of each makes.</summary>
            void Read(ref TEntry entry, ref byte source);
        }

        /// <summary>
        /// An entry of a set or a dictionary as a block: its element's items (<typeparamref name="TItems"/>), the
        /// same bytes as the element's formatter writes. The one block formatter of every hashed collection's entries.
        /// </summary>
        internal struct EntryBlock<TEntry, TItems> : INinoFormatter<TEntry>
            where TItems : struct, IEntryItems<TEntry>
        {
            public int FixedSize => default(TItems).Size;

            public NinoBlockMap BlockMap() => default(TItems).Map();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void WriteFixed(in TEntry value, ref byte destination) =>
                default(TItems).Write(in value, ref destination);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void ReadBlock(ref TEntry value, ref byte source) => default(TItems).Read(ref value, ref source);

            public void Serialize(TEntry value, ref Writer writer) => throw new NotSupportedException();
            public void Deserialize(ref Reader reader, ref TEntry value) => throw new NotSupportedException();
        }

        /// <summary>A set entry's value, raw or a block (placed by <see cref="NinoBlockMap.Item{TItem,
        /// TFormatter}"/>).</summary>
        internal struct SetValue<T, TFormatter> : IEntryItems<SetEntry<T>>
            where TFormatter : struct, INinoFormatter<T>
        {
            public int Size => NinoBlocks.ItemSize<T, TFormatter>();

            public NinoBlockMap Map() => new NinoBlockMap().Item<T, TFormatter>(SetLayout<T>.Value);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(in SetEntry<T> entry, ref byte destination) =>
                NinoBlocks.WriteItem<T, TFormatter>(entry.Value, ref destination);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ref SetEntry<T> entry, ref byte source) =>
                NinoBlocks.ReadItem<T, TFormatter>(ref entry.Value, ref source);
        }

        /// <summary>
        /// A set entry's value moved as its bytes, whatever they hold: for values the caller makes the wire's bytes
        /// after the move (slots: <see cref="INinoFormatter{T}.SlotsAreMemory"/>), where <see cref="SetValue{T,
        /// TFormatter}"/> takes only values that are the wire's bytes already.
        /// </summary>
        internal struct SetValueBytes<T> : IEntryItems<SetEntry<T>>
        {
            public int Size => Unsafe.SizeOf<T>();

            public NinoBlockMap Map() => new NinoBlockMap().Bytes(SetLayout<T>.Value, 0, Unsafe.SizeOf<T>());

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(in SetEntry<T> entry, ref byte destination) =>
                NinoUnsafe.Write(ref destination, entry.Value);

            // The bytes checked by the caller first (INinoFormatter<T>.CheckSlots).
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ref SetEntry<T> entry, ref byte source) => entry.Value = NinoUnsafe.Read<T>(ref source);
        }

        /// <summary>A dictionary entry's key and value (its element, the pair), each raw or a block.</summary>
        internal struct MapPair<TKey, TValue, TKeyFormatter, TValueFormatter> : IEntryItems<MapEntry<TKey, TValue>>
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            public int Size
            {
                get
                {
                    int key = NinoBlocks.ItemSize<TKey, TKeyFormatter>();
                    int value = NinoBlocks.ItemSize<TValue, TValueFormatter>();
                    return key < 0 || value < 0 ? -1 : key + value;
                }
            }

            public NinoBlockMap Map() => new NinoBlockMap().Item<TKey, TKeyFormatter>(MapLayout<TKey, TValue>.Key)
                .Item<TValue, TValueFormatter>(MapLayout<TKey, TValue>.Value);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(in MapEntry<TKey, TValue> entry, ref byte destination)
            {
                NinoBlocks.WriteItem<TKey, TKeyFormatter>(entry.Key, ref destination);
                NinoBlocks.WriteItem<TValue, TValueFormatter>(entry.Value,
                    ref Unsafe.Add(ref destination, NinoBlocks.ItemSize<TKey, TKeyFormatter>()));
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ref MapEntry<TKey, TValue> entry, ref byte source)
            {
                NinoBlocks.ReadItem<TKey, TKeyFormatter>(ref entry.Key, ref source);
                NinoBlocks.ReadItem<TValue, TValueFormatter>(ref entry.Value,
                    ref Unsafe.Add(ref source, NinoBlocks.ItemSize<TKey, TKeyFormatter>()));
            }
        }

        /// <summary>
        /// The offsets in a <see cref="SetEntry{T}"/> (the runtime may move the fields: references first), and the
        /// <see cref="NinoRun.InUse"/> of its runs with free entries.
        /// </summary>
        internal static class SetLayout<T>
        {
            private static SetEntry<T> s_entry;

            public static readonly int Value = NinoBlocks.Offset(ref s_entry, ref s_entry.Value);

            public static readonly int InUse = InUseAt(CollectionViews.SetFree,
                NinoBlocks.Offset(ref s_entry, ref s_entry.HashCode), NinoBlocks.Offset(ref s_entry, ref s_entry.Next),
                Value);
        }

        /// <summary>As <see cref="SetLayout{T}"/>, for a <see cref="MapEntry{TKey,TValue}"/>.</summary>
        internal static class MapLayout<TKey, TValue>
        {
            private static MapEntry<TKey, TValue> s_entry;

            public static readonly int Key = NinoBlocks.Offset(ref s_entry, ref s_entry.Key);

            public static readonly int Value = NinoBlocks.Offset(ref s_entry, ref s_entry.Value);

            public static readonly int InUse = InUseAt(CollectionViews.MapFree,
                NinoBlocks.Offset(ref s_entry, ref s_entry.HashCode), NinoBlocks.Offset(ref s_entry, ref s_entry.Next),
                Value);
        }

        private static int InUseAt(CollectionViews.FreeEntries free, int hashCode, int next, int value) => free switch
        {
            CollectionViews.FreeEntries.Next => NinoRun.InUseAt(next - value, -1),
            CollectionViews.FreeEntries.HashCode => NinoRun.InUseAt(hashCode - value, 0),
            _ => NinoRun.AllInUse,
        };

        /// <summary>
        /// The run of <paramref name="set"/>'s entries (<paramref name="count"/> of them in use, not 0), its values in
        /// enumeration order, the free ones skipped; false where the runtime's layout, or how it frees entries, is
        /// not known (enumerate it then). A loop over an array, where the enumerator checks the version per element.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Run<T>(HashSet<T> set, int count, out NinoRun run)
        {
            run = default;
            if (!CollectionViews.HashSet)
                return false;
            int bound = ObjectFields.At<int>(set, CollectionViews.HashSetBound);
            var entries = Unsafe.As<SetEntry<T>[]>(ObjectFields.At<object>(set, CollectionViews.HashSetEntries));
            if (entries == null || bound > entries.Length || bound < count ||
                bound != count && CollectionViews.SetFree == CollectionViews.FreeEntries.Unknown)
                return false;
            run = new NinoRun(ref Unsafe.As<T, byte>(ref entries[0].Value), bound, Unsafe.SizeOf<SetEntry<T>>(),
                NinoRun.NoKey, bound == count ? NinoRun.AllInUse : SetLayout<T>.InUse);
            return true;
        }

        /// <summary>
        /// As <see cref="Run{T}(HashSet{T}, int, out NinoRun)"/>, for a dictionary: each value of the run after its key,
        /// written as <paramref name="key"/> says (a <see cref="NinoRun.Key"/> without its offset, which this adds:
        /// the key's bytes, a string, or <see cref="NinoRun.NoKey"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Run<TKey, TValue>(Dictionary<TKey, TValue> dictionary, int count, int key, out NinoRun run)
        {
            run = default;
            if (!CollectionViews.Dictionary)
                return false;
            int bound = ObjectFields.At<int>(dictionary, CollectionViews.DictionaryBound);
            var entries = Unsafe.As<MapEntry<TKey, TValue>[]>(
                ObjectFields.At<object>(dictionary, CollectionViews.DictionaryEntries));
            if (entries == null || bound > entries.Length || bound < count ||
                bound != count && CollectionViews.MapFree == CollectionViews.FreeEntries.Unknown)
                return false;
            run = new NinoRun(ref Unsafe.As<TValue, byte>(ref entries[0].Value), bound,
                Unsafe.SizeOf<MapEntry<TKey, TValue>>(),
                key == NinoRun.NoKey ? key : key + ((MapLayout<TKey, TValue>.Key - MapLayout<TKey, TValue>.Value) << 8),
                bound == count ? NinoRun.AllInUse : MapLayout<TKey, TValue>.InUse);
            return true;
        }

        /// <summary>
        /// The run of the first <paramref name="count"/> (not 0) of a new set's <paramref name="entries"/>, which a read
        /// fills (<see cref="NinoRuns.ReadSet{T,TFormatter}"/>), every one in use.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoRun Run<T>(SetEntry<T>[] entries, int count) =>
            new(ref Unsafe.As<T, byte>(ref entries[0].Value), count, Unsafe.SizeOf<SetEntry<T>>(), NinoRun.NoKey,
                NinoRun.AllInUse);

        /// <summary>As <see cref="Run{T}(SetEntry{T}[], int)"/>, for a new dictionary's entries
        /// (<see cref="NinoRuns.ReadPairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoRun Run<TKey, TValue>(MapEntry<TKey, TValue>[] entries, int count) =>
            new(ref Unsafe.As<TValue, byte>(ref entries[0].Value), count, Unsafe.SizeOf<MapEntry<TKey, TValue>>(),
                NinoRun.NoKey, NinoRun.AllInUse);

        /// <summary>The entry whose value <paramref name="value"/> is (a run's <see cref="NinoRun.First"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref SetEntry<T> SetEntryOf<T>(ref byte value) =>
            ref Unsafe.As<byte, SetEntry<T>>(ref Unsafe.Subtract(ref value, SetLayout<T>.Value));

        /// <summary>As <see cref="SetEntryOf{T}"/>, for a dictionary's entry.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref MapEntry<TKey, TValue> MapEntryOf<TKey, TValue>(ref byte value) =>
            ref Unsafe.As<byte, MapEntry<TKey, TValue>>(ref Unsafe.Subtract(ref value, MapLayout<TKey, TValue>.Value));

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
