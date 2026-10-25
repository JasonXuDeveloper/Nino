using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

    /// <summary>
    /// <see cref="DictionaryLikeFormatter{TDictionary,TKey,TValue,TKeyFormatter,TValueFormatter}"/> for the runtime's
    /// own dictionaries (and types derived from them), as <see cref="BclCollections{T,TFormatter}"/> for collections.
    /// </summary>
    internal static class BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        /// <summary>Writes <paramref name="value"/> (not null) if it is one of these dictionaries.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWrite(IDictionary<TKey, TValue> value, ref Writer writer)
        {
            switch (value)
            {
                case SortedDictionary<TKey, TValue> sorted:
                    // Copied out of its tree, then written as an array of pairs (raw ones as one block): its
                    // enumerator keeps a stack of nodes and checks the version per pair.
                    NinoCollections.WriteCopiedPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(sorted,
                        SortedSetTrees.RootOf(sorted), ref writer);
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
            // Where its table is known, built here instead, each key's bucket counted as it is filled
            // (ConcurrentTables): the constructor gives no point between two pairs to count the keys in a bucket at.
            if (ConcurrentTables.Plan<TKey, TValue>.Builds)
                return CreateConcurrentBuilt(length, ref reader);
            if (ConcurrentTables.Plan<TKey, TValue>.Walks)
                return CreateConcurrentWalked(length, ref reader);
            var pairs = NinoCollections.ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(length,
                ref reader);
            try
            {
                return new ConcurrentDictionary<TKey, TValue>(
                    new ArraySegment<KeyValuePair<TKey, TValue>>(pairs, 0, length));
            }
            catch (ArgumentNullException)
            {
                NinoThrow.NullKey(typeof(ConcurrentDictionary<TKey, TValue>));
                throw;
            }
            catch (ArgumentException)
            {
                NinoThrow.DuplicateKey(typeof(ConcurrentDictionary<TKey, TValue>));
                throw;
            }
            finally
            {
                NinoCollections.ReturnRented(pairs);
            }
        }

        /// <summary>
        /// A new dictionary sized and configured as its constructor from the pairs makes it, its table filled as that
        /// constructor fills it, without locks (<see cref="ConcurrentTables.Builder{TKey,TValue}"/>), each key's
        /// bucket counted (hash flooding, <see cref="NinoSettings.MaxHashCollisions"/>). The first pair of the first
        /// dictionary of these types is added through its public API, to learn the type of its nodes.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConcurrentDictionary<TKey, TValue> CreateConcurrentBuilt(int length, ref Reader reader)
        {
            var concurrent = ConcurrentTables.Create<TKey, TValue>(length);
            IntPtr nodes = ConcurrentTables.Plan<TKey, TValue>.NodeType;
            var builder = nodes != IntPtr.Zero
                ? new ConcurrentTables.Builder<TKey, TValue>(concurrent, nodes)
                : default;
            Reader local = default;
            reader.CopyTo(ref local);
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref local);
                default(TValueFormatter).Deserialize(ref item, ref local);
                if (key == null)
                    NinoThrow.NullKey(concurrent);
                if (nodes == IntPtr.Zero)
                {
                    // Empty, so it cannot hold the key yet.
                    concurrent.TryAdd(key, item);
                    nodes = ConcurrentTables.TypeOfNodes(concurrent, key);
                    ConcurrentTables.Plan<TKey, TValue>.NodeType = nodes;
                    builder = new ConcurrentTables.Builder<TKey, TValue>(concurrent, nodes);
                }
                else if (!builder.Add(key, item))
                    NinoThrow.DuplicateKey(concurrent.GetType());
            }

            local.CopyTo(ref reader);
            return concurrent;
        }

        /// <summary>
        /// Where the table can be read but not built: a pair at a time through TryAdd (a lock per pair), each key's
        /// bucket walked after it is added.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ConcurrentDictionary<TKey, TValue> CreateConcurrentWalked(int length, ref Reader reader)
        {
            var concurrent = ConcurrentTables.Create<TKey, TValue>(length);
            Reader local = default;
            reader.CopyTo(ref local);
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref local);
                default(TValueFormatter).Deserialize(ref item, ref local);
                if (key == null)
                    NinoThrow.NullKey(concurrent);
                if (!concurrent.TryAdd(key, item))
                    NinoThrow.DuplicateKey(concurrent.GetType());
                ConcurrentTables.Walk(concurrent, key);
            }

            local.CopyTo(ref reader);
            return concurrent;
        }

        /// <summary>From its two arrays.</summary>
        private static void WriteSortedList(SortedList<TKey, TValue> list, ref Writer writer)
        {
            int count = CollectionFields.Size(list);
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if ((uint)count > (uint)keys.Length || (uint)count > (uint)values.Length)
                NinoThrow.ModifiedDuringWrite();
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
                if (key == null)
                    NinoThrow.NullKey(concurrent);
                if (!concurrent.TryAdd(key, item))
                    NinoThrow.DuplicateKey(concurrent.GetType());
                ConcurrentTables.Added(concurrent, key);
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
                    if (SortedSetTrees.Fill(set, pairs, length) == length)
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
}
