using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        public static bool TryWrite(ICollection<T> value, ref Writer writer)
        {
            switch (value)
            {
                case SortedSet<T> sorted:
                    // Copied out by its own recursive walk: its enumerator keeps a stack of nodes and checks the
                    // version per element.
                    NinoCollections.WriteCopied<T, TFormatter>(sorted, ref writer);
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
                        NinoThrow.InvalidData("collection was modified during serialization");
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

        private static void ReadLinked(LinkedList<T> linked, int length, ref Reader reader)
        {
            Reader local = default;
            reader.CopyTo(ref local);
            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref local);
                linked.AddLast(item);
            }

            local.CopyTo(ref reader);
        }

        /// <summary>
        /// Read as an array, then built in one pass where the tree can be moved in (<see cref="SortedTrees.Fill{T}"/>),
        /// otherwise added one by one. Duplicates are dropped, as Add drops them.
        /// </summary>
        private static void ReadSorted(SortedSet<T> set, int length, ref Reader reader)
        {
            var items = NinoCollections.ReadRented<T, TFormatter>(length, ref reader);
            try
            {
                if (SortedTrees.CanFill(set))
                {
                    SortedTrees.Fill(set, items, length);
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

    /// <summary>
    /// <see cref="DictionaryLikeFormatter{TDictionary,TKey,TValue,TKeyFormatter,TValueFormatter}"/> for the runtime's
    /// own dictionaries (and types derived from them), as <see cref="BclCollections{T,TFormatter}"/> for collections.
    /// </summary>
    internal static class BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        /// <summary>Writes <paramref name="value"/> (not null) if it is one of these dictionaries.</summary>
        public static bool TryWrite(IDictionary<TKey, TValue> value, ref Writer writer)
        {
            switch (value)
            {
                case SortedDictionary<TKey, TValue> sorted:
                    // Copied out by its own recursive walk, then written as an array of pairs (raw ones as one block):
                    // its enumerator keeps a stack of nodes and checks the version per pair.
                    NinoCollections.WriteCopiedPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(sorted, ref writer);
                    return true;
                case SortedList<TKey, TValue> list when CollectionViews.SortedList:
                    WriteSortedList(list, ref writer);
                    return true;
                case ConcurrentDictionary<TKey, TValue> concurrent:
                    WriteConcurrent(concurrent, ref writer);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> pairs into <paramref name="dictionary"/> (empty) if it is one of these
        /// dictionaries. A key that is null or there twice is reported as the general path reports it.
        /// </summary>
        public static bool TryRead(IDictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            switch (dictionary)
            {
                case SortedDictionary<TKey, TValue> sorted:
                    ReadSorted(sorted, length, ref reader);
                    return true;
                case SortedList<TKey, TValue> list:
                    ReadSortedList(list, length, ref reader);
                    return true;
                case ConcurrentDictionary<TKey, TValue> concurrent:
                    ReadConcurrent(concurrent, length, ref reader);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A new <typeparamref name="TDictionary"/> of the <paramref name="length"/> pairs that follow, if it is one the
        /// runtime builds faster from a collection than pair by pair; otherwise null, and nothing is read.
        /// </summary>
        public static TDictionary TryCreate<TDictionary>(int length, ref Reader reader) where TDictionary : class =>
            typeof(TDictionary) == typeof(ConcurrentDictionary<TKey, TValue>)
                ? (TDictionary)(object)CreateConcurrent(length, ref reader)
                : null;

        /// <summary>
        /// From the pairs read as an array: its constructor sizes the table for them and adds them without the locks,
        /// where TryAdd takes a lock per pair and grows the table as it fills.
        /// </summary>
        private static ConcurrentDictionary<TKey, TValue> CreateConcurrent(int length, ref Reader reader)
        {
            var pairs = NinoCollections.ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(length,
                ref reader);
            try
            {
                return new ConcurrentDictionary<TKey, TValue>(
                    new ArraySegment<KeyValuePair<TKey, TValue>>(pairs, 0, length));
            }
            catch (ArgumentException e) when (!(e is ArgumentNullException))
            {
                NinoThrow.DuplicateKey(typeof(ConcurrentDictionary<TKey, TValue>));
                throw;
            }
            finally
            {
                NinoCollections.ReturnRented(pairs);
            }
        }

        /// <summary>From its two arrays.</summary>
        private static void WriteSortedList(SortedList<TKey, TValue> list, ref Writer writer)
        {
            int count = CollectionFields.Size(list);
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if ((uint)count > (uint)keys.Length || (uint)count > (uint)values.Length)
                NinoThrow.InvalidData("collection was modified during serialization");
            Writer local = default;
            writer.CopyTo(ref local);
            local.WriteLength(count);
            for (int i = 0; i < count; i++)
            {
                default(TKeyFormatter).Serialize(keys[i], ref local);
                default(TValueFormatter).Serialize(values[i], ref local);
            }

            local.CopyTo(ref writer);
        }

        /// <summary>
        /// Through its own enumerator, typed here rather than left to the shared loop of
        /// <see cref="NinoCollections.WriteEnumerable{T,TFormatter}"/>, whose guess at the dictionary type is made
        /// once for every dictionary with these keys and values (4x slower when a Dictionary was seen first), and
        /// without its count (which takes every lock, and may not match the pairs enumerated after it).
        /// </summary>
        private static void WriteConcurrent(ConcurrentDictionary<TKey, TValue> concurrent, ref Writer writer)
        {
            Writer local = default;
            writer.CopyTo(ref local);
            int countPos = local.ReserveInt32();
            int written = 0;
            foreach (var pair in concurrent)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref local);
                default(TValueFormatter).Serialize(pair.Value, ref local);
                written++;
            }

            local.PatchInt32(countPos, written);
            local.CopyTo(ref writer);
        }

        /// <summary>
        /// Through TryAdd: its Count takes every lock, and the general path reads it per pair (27x slower).
        /// </summary>
        private static void ReadConcurrent(ConcurrentDictionary<TKey, TValue> concurrent, int length, ref Reader reader)
        {
            Reader local = default;
            reader.CopyTo(ref local);
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref local);
                default(TValueFormatter).Deserialize(ref item, ref local);
                if (!concurrent.TryAdd(key, item))
                    NinoThrow.DuplicateKey(concurrent.GetType());
            }

            local.CopyTo(ref reader);
        }

        /// <summary>
        /// Read as an array of pairs, then built in one pass where the tree can be moved in and the keys are not null
        /// or repeated, otherwise added one by one.
        /// </summary>
        private static void ReadSorted(SortedDictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            var pairs = NinoCollections.ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(length,
                ref reader);
            try
            {
                if (CollectionViews.SortedDictionary && !HasNullKey(pairs, length))
                {
                    var set = CollectionFields.Set(dictionary);
                    if (SortedTrees.Fill(set, pairs, length) == length)
                        return;
                    set.Clear();
                }

                AddEach(dictionary, pairs, length);
            }
            finally
            {
                NinoCollections.ReturnRented(pairs);
            }
        }

        /// <summary>
        /// Read as an array of pairs, then split into its two arrays when the keys come in order under its comparer
        /// (as Nino writes them), otherwise added one by one.
        /// </summary>
        private static void ReadSortedList(SortedList<TKey, TValue> list, int length, ref Reader reader)
        {
            var pairs = NinoCollections.ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(length,
                ref reader);
            try
            {
                if (!CollectionViews.SortedList || !TryAdopt(list, pairs, length))
                    AddEach(list, pairs, length);
            }
            finally
            {
                NinoCollections.ReturnRented(pairs);
            }
        }

        private static bool TryAdopt(SortedList<TKey, TValue> list, KeyValuePair<TKey, TValue>[] pairs, int length)
        {
            if (length == 0 || !InOrder(pairs, length, CollectionFields.Comparer(list)))
                return false;
            // Its own arrays when they are large enough (Clear cleared them), otherwise new ones.
            // Plain locals: a store through a ref to the field could alias the array stores below.
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if (keys.Length < length)
                CollectionFields.Keys(list) = keys = new TKey[length];
            if (values.Length < length)
                CollectionFields.Values(list) = values = new TValue[length];
            for (int i = 0; i < length; i++)
            {
                keys[i] = pairs[i].Key;
                values[i] = pairs[i].Value;
            }

            CollectionFields.Size(list) = length;
            CollectionFields.Version(list)++;
            return true;
        }

        /// <summary>Whether the keys are not null and strictly ascending under <paramref name="comparer"/>.</summary>
        private static bool InOrder(KeyValuePair<TKey, TValue>[] pairs, int count, IComparer<TKey> comparer)
        {
            if (HasNullKey(pairs, count))
                return false;
            if (typeof(TKey).IsValueType && ReferenceEquals(comparer, Comparer<TKey>.Default))
            {
                // Comparer<TKey>.Default devirtualized and inlined for value types.
                for (int i = 1; i < count; i++)
                    if (Comparer<TKey>.Default.Compare(pairs[i - 1].Key, pairs[i].Key) >= 0)
                        return false;
                return true;
            }

            for (int i = 1; i < count; i++)
                if (comparer.Compare(pairs[i - 1].Key, pairs[i].Key) >= 0)
                    return false;
            return true;
        }

        private static bool HasNullKey(KeyValuePair<TKey, TValue>[] pairs, int count)
        {
            if (typeof(TKey).IsValueType)
                return false;
            for (int i = 0; i < count; i++)
                if (pairs[i].Key == null)
                    return true;
            return false;
        }

        /// <summary>
        /// Pair by pair through the indexer, which throws for a null key; a repeated key is reported.
        /// </summary>
        private static void AddEach(IDictionary<TKey, TValue> dictionary, KeyValuePair<TKey, TValue>[] pairs,
            int length)
        {
            for (int i = 0; i < length; i++)
                NinoCollections.AddUnique(dictionary, pairs[i].Key, pairs[i].Value, i);
        }
    }

    /// <summary>
    /// <see cref="SortedSet{T}"/> trees built in one pass: the set's constructor sorts the elements (found in order,
    /// as Nino writes them), drops duplicates and builds a balanced tree from the array, where Add would insert and
    /// rebalance per element. Its nodes do not point back to the set, so the tree is moved into the set being read
    /// (<see cref="CollectionFields"/>; also the set under a <see cref="SortedDictionary{TKey,TValue}"/>).
    /// </summary>
    internal static class SortedTrees
    {
        /// <summary>
        /// Whether <see cref="Fill{T}"/> applies to <paramref name="set"/>: its layout was checked, and it is not a
        /// view of another set (<see cref="SortedSet{T}.GetViewBetween"/>, a type of the runtime's own).
        /// </summary>
        public static bool CanFill<T>(SortedSet<T> set) =>
            CollectionViews.SortedSet && (set.GetType() == typeof(SortedSet<T>) ||
                                          set.GetType().Assembly != typeof(SortedSet<T>).Assembly);

        /// <summary>
        /// Fills the empty <paramref name="set"/> with <c>items[0..count)</c>; returns how many it holds (fewer when
        /// some were the same under its comparer).
        /// </summary>
        public static int Fill<T>(SortedSet<T> set, T[] items, int count)
        {
            var built = new SortedSet<T>(new ArraySegment<T>(items, 0, count), set.Comparer);
            CollectionFields.Root(set) = CollectionFields.Root(built);
            CollectionFields.Count(set) = built.Count;
            CollectionFields.Version(set)++;
            return built.Count;
        }
    }

    internal static partial class NinoCollections
    {
        /// <summary>
        /// Writes the count of <paramref name="source"/>, then its elements in the order
        /// <see cref="ICollection{T}.CopyTo"/> puts them, as an array is written (raw elements as one block).
        /// </summary>
        public static void WriteCopied<T, TFormatter>(ICollection<T> source, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = source.Count;
            var array = ArrayPool<T>.Shared.Rent(count);
            try
            {
                source.CopyTo(array, 0);
                WriteSpan<T, TFormatter>(new ReadOnlySpan<T>(array, 0, count), ref writer);
            }
            finally
            {
                ReturnRented(array);
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> elements as an array is read, into a rented array (give it back with
        /// <see cref="ReturnRented{T}"/>).
        /// </summary>
        public static T[] ReadRented<T, TFormatter>(int length, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            var array = ArrayPool<T>.Shared.Rent(length);
            var span = new Span<T>(array, 0, length);
            // Over default values: a rented array holds old ones (unless every element is overwritten).
            if (!NinoElements.ReadOverwrites<T, TFormatter>())
                span.Clear();
            try
            {
                NinoElements.Read<T, TFormatter>(span, ref reader);
            }
            catch
            {
                ReturnRented(array);
                throw;
            }

            return array;
        }

        /// <summary>
        /// <see cref="WriteCopied{T,TFormatter}"/> for pairs, as
        /// <see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/> writes them.
        /// </summary>
        public static void WriteCopiedPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(
            ICollection<KeyValuePair<TKey, TValue>> source, ref Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            WriteCopied<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(
                source, ref writer);

        /// <summary><see cref="ReadRented{T,TFormatter}"/> for pairs.</summary>
        public static KeyValuePair<TKey, TValue>[] ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(
            int length, ref Reader reader)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            ReadRented<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(
                length, ref reader);

        public static void ReturnRented<T>(T[] array) =>
            ArrayPool<T>.Shared.Return(array, NinoUnsafe.IsReferenceOrContainsReferences<T>());
    }
}
