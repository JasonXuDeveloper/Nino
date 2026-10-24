using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        public static bool TryWrite(ICollection<T> value, ref Writer writer)
        {
            switch (value)
            {
                case SortedSet<T> sorted:
                    // Copied out of its tree: its enumerator keeps a stack of nodes and checks the version per
                    // element.
                    NinoCollections.WriteCopied<T, TFormatter>(sorted, SortedTrees.RootOf(sorted), ref writer);
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
                    // Copied out of its tree, then written as an array of pairs (raw ones as one block): its
                    // enumerator keeps a stack of nodes and checks the version per pair.
                    NinoCollections.WriteCopiedPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(sorted,
                        SortedTrees.RootOf(sorted), ref writer);
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
            if (SortedSetNodes.Layout != 0 && count != 0 && Increasing(items, count, set.Comparer))
            {
                // In order and none twice (as Nino writes them): the tree is built from them as they are.
                CollectionFields.Root(set) = SortedSetNodes<T>.Build(items, count);
                CollectionFields.Count(set) = count;
                CollectionFields.Version(set)++;
                return count;
            }

            var built = new SortedSet<T>(new ArraySegment<T>(items, 0, count), set.Comparer);
            CollectionFields.Root(set) = CollectionFields.Root(built);
            CollectionFields.Count(set) = built.Count;
            CollectionFields.Version(set)++;
            return built.Count;
        }

        /// <summary>
        /// The root of <paramref name="set"/>'s tree, to copy its items out of (<see cref="SortedSetNodes{T}.CopyTo"/>),
        /// or null to copy them with the set's own <c>CopyTo</c> (an unknown layout, or a view of another set). Out of
        /// line, as the copy is: inlined, its checks keep the writer in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object RootOf<T>(SortedSet<T> set) =>
            SortedSetNodes.Layout != 0 && CanFill(set) ? CollectionFields.Root(set) : null;

        /// <summary><see cref="RootOf{T}(SortedSet{T})"/> of the set of <paramref name="dictionary"/>'s pairs.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object RootOf<TKey, TValue>(SortedDictionary<TKey, TValue> dictionary) =>
            SortedSetNodes.Layout != 0 && CollectionViews.SortedDictionary
                ? CollectionFields.Root(CollectionFields.Set(dictionary))
                : null;

        private static bool Increasing<T>(T[] items, int count, IComparer<T> comparer)
        {
            for (int i = 1; i < count; i++)
                if (comparer.Compare(items[i - 1], items[i]) >= 0)
                    return false;
            return true;
        }
    }

    /// <summary>
    /// The nodes of <see cref="SortedSet{T}"/> (an internal class) made as <see cref="Nino.Core.Internal.ObjectTypes"/>
    /// makes objects, so a set of items in order is built as its tree directly: the public constructor copies them,
    /// sorts the copy and drops duplicates first. Each layout below declares the node's fields in the order of a known
    /// implementation (.NET, Mono and Unity: Item, Left, Right, a byte color; .NET Framework: IsRed, Item, Left,
    /// Right); the first one a set with known contents reads as built is used, and the public constructor otherwise.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
#pragma warning disable CS0649 // laid out only
    internal static class SortedSetNodes
    {
        internal sealed class NodeCore<T>
        {
            public T Item;
            public NodeCore<T> Left;
            public NodeCore<T> Right;
            public byte Color; // red: 1
        }

        internal sealed class NodeFramework<T>
        {
            public bool IsRed;
            public T Item;
            public NodeFramework<T> Left;
            public NodeFramework<T> Right;
        }
#pragma warning restore CS0649

        /// <summary>1 for <see cref="NodeCore{T}"/>, 2 for <see cref="NodeFramework{T}"/>, 0 for neither.</summary>
        public static readonly int Layout = ObjectTypes.Usable && CollectionViews.SortedSet ? Find() : 0;

        /// <summary>
        /// Where the fields of a node are: its item, left and right children, and color (red: 1). A class, not a
        /// struct: <see cref="Make{T}"/> passes it down its recursion as one reference.
        /// </summary>
        internal sealed class NodeOffsets
        {
            public readonly int Item, Left, Right, Color;

            public NodeOffsets(int item, int left, int right, int color)
            {
                Item = item;
                Left = left;
                Right = right;
                Color = color;
            }
        }

        /// <summary>The offsets of <typeparamref name="T"/>'s node in a layout (1 or 2).</summary>
        public static NodeOffsets Offsets<T>(int layout)
        {
            if (layout == 1)
            {
                var m = new NodeCore<T>();
                return new NodeOffsets(ObjectFields.OffsetOf(m, ref m.Item), ObjectFields.OffsetOf(m, ref m.Left),
                    ObjectFields.OffsetOf(m, ref m.Right), ObjectFields.OffsetOf(m, ref m.Color));
            }

            var f = new NodeFramework<T>();
            return new NodeOffsets(ObjectFields.OffsetOf(f, ref f.Item), ObjectFields.OffsetOf(f, ref f.Left),
                ObjectFields.OffsetOf(f, ref f.Right), ObjectFields.OffsetOf(f, ref f.IsRed));
        }

        private static int Find()
        {
            for (int layout = 1; layout <= 2; layout++)
            {
                try
                {
                    if (Check(layout))
                        return layout;
                }
                catch
                {
                    // Not this layout.
                }
            }

            return 0;
        }

        private static bool Check(int layout)
        {
            // Added in this order, the tree is 2 (black) over 1 and 3 (red) on every implementation. Int fields first
            // (a reference may sit where another layout has an int, never read as one).
            var at = Offsets<int>(layout);
            var root = CollectionFields.Root(new SortedSet<int> { 1, 2, 3 });
            if (ObjectFields.At<int>(root, at.Item) != 2 || ObjectFields.At<byte>(root, at.Color) != 0)
                return false;
            object left = ObjectFields.At<object>(root, at.Left), right = ObjectFields.At<object>(root, at.Right);
            if (left == null || right == null || ObjectFields.At<int>(left, at.Item) != 1 ||
                ObjectFields.At<byte>(left, at.Color) != 1 || ObjectFields.At<int>(right, at.Item) != 3 ||
                ObjectFields.At<byte>(right, at.Color) != 1 || ObjectFields.At<object>(left, at.Left) != null)
                return false;
            var strings = Offsets<string>(layout);
            var stringRoot = CollectionFields.Root(new SortedSet<string>(StringComparer.Ordinal) { "a", "b", "c" });
            if (ObjectFields.At<byte>(stringRoot, strings.Color) != 0 ||
                ObjectFields.At<byte>(ObjectFields.At<object>(stringRoot, strings.Left), strings.Color) != 1 ||
                ObjectFields.At<string>(stringRoot, strings.Item) != "b")
                return false;
            return CheckBuilt(layout);
        }

        /// <summary>The type of <typeparamref name="T"/>'s nodes: the root of a set of one item (added without a comparison).</summary>
        public static IntPtr TypeOfNodes<T>()
        {
            var set = new SortedSet<T>();
            set.Add(default);
            return ObjectTypes.TypeOf(CollectionFields.Root(set));
        }

        /// <summary>
        /// The root of the red-black tree of <c>items[0..count)</c> (in order, none twice, count not 0), its nodes in
        /// <paramref name="layout"/> at the offsets <paramref name="at"/> (<see cref="Offsets{T}"/>), of the type
        /// <paramref name="type"/> (<see cref="TypeOfNodes{T}"/>).
        /// </summary>
        public static object Build<T>(T[] items, int count, int layout, NodeOffsets at, IntPtr type)
        {
            // Halves that differ by at most one item: every leaf is on the last level or the one above it. The last
            // level is red, the others black: every path from the root to a missing child passes as many black nodes,
            // and no red node has a red child. A single node, the root, stays black.
            int last = 0;
            for (int n = count; n > 1; n >>= 1)
                last++;
            return Make(items, 0, count - 1, 0, last == 0 ? -1 : last, layout, at, type);
        }

        private static object Make<T>(T[] items, int low, int high, int depth, int red, int layout, NodeOffsets at,
            IntPtr type)
        {
            if (low > high)
                return null;
            int middle = (low + high) >> 1;
            object node = layout == 1 ? new NodeCore<T>() : new NodeFramework<T>();
            ObjectTypes.Retype(node, type);
            ObjectFields.At<T>(node, at.Item) = items[middle];
            ObjectFields.At<object>(node, at.Left) = Make(items, low, middle - 1, depth + 1, red, layout, at, type);
            ObjectFields.At<object>(node, at.Right) = Make(items, middle + 1, high, depth + 1, red, layout, at, type);
            if (depth == red)
                ObjectFields.At<byte>(node, at.Color) = 1;
            return node;
        }

        /// <summary>Built as Nino builds it, a set works through its public API: lookups, order, adds and removes.</summary>
        private static bool CheckBuilt(int layout)
        {
            var items = new string[40];
            for (int i = 0; i < items.Length; i++)
                items[i] = "s" + (i + 10).ToString(CultureInfo.InvariantCulture);
            var set = new SortedSet<string>(StringComparer.Ordinal);
            CollectionFields.Root(set) = Build(items, items.Length, layout, Offsets<string>(layout),
                TypeOfNodes<string>());
            CollectionFields.Count(set) = items.Length;
            CollectionFields.Version(set)++;
            int at = 0;
            foreach (var item in set)
                if (item != items[at++])
                    return false;
            if (at != 40 || set.Min != "s10" || set.Max != "s49" || !set.Contains("s33") || set.Contains("s50") ||
                set.GetViewBetween("s20", "s29").Count != 10)
                return false;
            for (int i = 0; i < 40; i += 2)
                set.Remove(items[i]);
            for (int i = 50; i < 70; i++)
                set.Add("s" + i.ToString(CultureInfo.InvariantCulture));
            at = 0;
            string previous = null;
            foreach (var item in set)
            {
                if (previous != null && string.CompareOrdinal(previous, item) >= 0)
                    return false;
                previous = item;
                at++;
            }

            return at == 40 && set.Count == 40 && set.Min == "s11" && set.Max == "s69" && !set.Contains("s10") &&
                   set.Contains("s49");
        }
    }

    /// <summary>
    /// The nodes of <see cref="SortedSet{T}"/> for one <typeparamref name="T"/> in the layout
    /// <see cref="SortedSetNodes"/> found. Read only once that is known: the check builds sets of its own through
    /// <see cref="SortedSetNodes.Build{T}"/>, which keeps nothing.
    /// </summary>
    internal static class SortedSetNodes<T>
    {
        private static readonly SortedSetNodes.NodeOffsets At =
            SortedSetNodes.Layout != 0 ? SortedSetNodes.Offsets<T>(SortedSetNodes.Layout) : null;
        private static readonly IntPtr Type = SortedSetNodes.Layout != 0 ? SortedSetNodes.TypeOfNodes<T>() : IntPtr.Zero;

        /// <summary>The root of the red-black tree of <c>items[0..count)</c> (in order, none twice, count not 0).</summary>
        public static object Build(T[] items, int count) =>
            SortedSetNodes.Build(items, count, SortedSetNodes.Layout, At, Type);

        /// <summary>Copies the items under <paramref name="root"/> into <paramref name="array"/> in order.</summary>
        public static void CopyTo(object root, T[] array)
        {
            int at = 0;
            CopyTo(root, array, ref at, At.Item, At.Left, At.Right);
        }

        private static void CopyTo(object node, T[] array, ref int at, int item, int left, int right)
        {
            for (; node != null; node = ObjectFields.At<object>(node, right))
            {
                CopyTo(ObjectFields.At<object>(node, left), array, ref at, item, left, right);
                array[at++] = ObjectFields.At<T>(node, item);
            }
        }
    }

    internal static partial class NinoCollections
    {
        /// <summary>
        /// Writes the count of <paramref name="source"/>, then its elements in the order
        /// <see cref="ICollection{T}.CopyTo"/> puts them, as an array is written (raw elements as one block): copied
        /// out of the <see cref="SortedSet{T}"/> tree under <paramref name="root"/> when it is not null.
        /// </summary>
        public static void WriteCopied<T, TFormatter>(ICollection<T> source, object root, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = source.Count;
            var array = ArrayPool<T>.Shared.Rent(count);
            try
            {
                CopyOut(source, root, array);
                WriteSpan<T, TFormatter>(new ReadOnlySpan<T>(array, 0, count), ref writer);
            }
            finally
            {
                ReturnRented(array);
            }
        }

        // Out of line: in the caller, the branch keeps the writer in memory.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CopyOut<T>(ICollection<T> source, object root, T[] array)
        {
            if (root != null)
                SortedSetNodes<T>.CopyTo(root, array);
            else
                source.CopyTo(array, 0);
        }

        /// <summary>
        /// Reads <paramref name="length"/> elements as an array is read, into a rented array (give it back with
        /// <see cref="ReturnRented{T}"/>). Out of line, as it was with the catch it had (a method with a catch is
        /// never inlined; one with a finally may be, which made every caller larger).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T[] ReadRented<T, TFormatter>(int length, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Elements far larger in memory than on the wire (NinoElements.HeavyElements): out of line.
            if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
            {
                reader = ReadRentedHeavy<T, TFormatter>(out var rented, length, reader);
                return rented;
            }

            var array = ArrayPool<T>.Shared.Rent(length);
            var span = new Span<T>(array, 0, length);
            // Over default values: a rented array holds old ones (unless every element is overwritten).
            if (!NinoElements.ReadOverwrites<T, TFormatter>())
                span.Clear();
            // A finally, not a catch that rethrows: a rethrow is a new throw from the catch, on top of the stack of
            // the first, so a failure deep in data that nests through here (a collection holding itself) would
            // need stack at every level on its way out.
            bool read = false;
            try
            {
                NinoElements.Read<T, TFormatter>(span, ref reader);
                read = true;
            }
            finally
            {
                if (!read)
                    ReturnRented(array);
            }

            return array;
        }

        /// <summary>
        /// <see cref="ReadRented{T,TFormatter}"/> of elements far larger in memory than on the wire: where the data
        /// could not fill as many (<see cref="NinoElements.Fits"/>), read into an array grown as they are
        /// (<see cref="NinoElements.ReadGrowing{T,TFormatter}"/>), then copied into the rented one.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadRentedHeavy<T, TFormatter>(out T[] rented, int length, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            T[] grown = null;
            reader = NinoElements.ReadGrowing<T, TFormatter>(ref grown, length, reader);
            rented = ArrayPool<T>.Shared.Rent(length);
            Array.Copy(grown, rented, length);
            return reader;
        }

        /// <summary>
        /// <see cref="WriteCopied{T,TFormatter}"/> for pairs, as
        /// <see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/> writes them.
        /// </summary>
        public static void WriteCopiedPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(
            ICollection<KeyValuePair<TKey, TValue>> source, object root, ref Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            WriteCopied<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(
                source, root, ref writer);

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
