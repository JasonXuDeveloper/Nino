using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

[assembly: InternalsVisibleTo("Nino.UnitTests")]

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableDictionary&lt;TKey, TValue&gt;</c> and <c>ImmutableHashSet&lt;T&gt;</c> built directly as their
    /// tree, instead of one builder <c>Add</c> at a time (each a walk down the tree and a rebalance): the classes below
    /// declare the fields of the collection and its nodes in their order, and so are laid out as they are, and are
    /// read and written through <see cref="Unsafe.As{T}(object)"/>. The tree is an AVL tree of hash buckets keyed by
    /// the item's hash: sorted by hash, the items are its nodes in order, each bucket holding one item. Built only with
    /// the default comparers, when no two hashes are equal (otherwise a bucket holds more than one item, or the data
    /// holds a key twice) and no key is null; the builder handles everything else, and every collection when
    /// <see cref="HashTrees.Usable"/> found another layout.
    /// </summary>
    internal static class HashTrees
    {
        /// <summary>
        /// Whether the layouts below are the ones of this runtime's System.Collections.Immutable: checked against
        /// collections with known contents, built both ways, through the public API.
        /// </summary>
        public static readonly bool Usable = ObjectTypes.Usable && ObjectTypes.Passes(Check);

        /// <summary>
        /// Whether the sorted <paramref name="hashes"/> are all different; throws <see cref="NinoDataException"/> when
        /// one of them is shared by more than <see cref="NinoSettings.MaxHashCollisions"/> others. The collections
        /// keep the items of one hash in a list, which adding to costs a scan of it: keys built to share a hash (hash
        /// flooding) would make the read quadratic.
        /// </summary>
        public static bool CheckCollisions(int[] hashes, Type collection)
        {
            // One pass, as the check for equal hashes was: a run of equal ones is measured where it ends. A key may
            // find MaxHashCollisions others with its hash, as in a Dictionary bucket.
            bool distinct = true;
            int start = 0;
            for (int i = 1; i < hashes.Length; i++)
            {
                if (hashes[i] == hashes[i - 1])
                    continue;
                if (i - start > 1)
                {
                    distinct = false;
                    if (i - start > NinoEffectiveSettings.MaxHashCollisions + 1)
                        Flooded(collection);
                }

                start = i;
            }

            if (hashes.Length - start > 1)
            {
                distinct = false;
                if (hashes.Length - start > NinoEffectiveSettings.MaxHashCollisions + 1)
                    Flooded(collection);
            }

            return distinct;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Flooded(Type collection) =>
            throw new NinoDataException(
                $"Nino: more than {NinoEffectiveSettings.MaxHashCollisions} keys of the data share a hash " +
                $"code in {collection.FullName} (NinoSettings.MaxHashCollisions): keys built to collide, " +
                "or keys whose hash code is poor.");

        /// <summary>
        /// <see cref="CheckCollisions(int[], Type)"/> for items about to be added to a builder one by one, where they
        /// were not built as a tree (the default comparer's hash codes, null's 0).
        /// </summary>
        public static void CheckCollisions<T>(T[] items, Type collection)
        {
            var hashes = new int[items.Length];
            for (int i = 0; i < hashes.Length; i++)
                hashes[i] = items[i] == null ? 0 : EqualityComparer<T>.Default.GetHashCode(items[i]);
            Array.Sort(hashes);
            CheckCollisions(hashes, collection);
        }

        /// <summary>As <see cref="CheckCollisions{T}(T[], Type)"/>, for the keys of pairs.</summary>
        public static void CheckCollisions<TKey, TValue>(KeyValuePair<TKey, TValue>[] pairs, Type collection)
        {
            var hashes = new int[pairs.Length];
            for (int i = 0; i < hashes.Length; i++)
                hashes[i] = pairs[i].Key == null ? 0 : EqualityComparer<TKey>.Default.GetHashCode(pairs[i].Key);
            Array.Sort(hashes);
            CheckCollisions(hashes, collection);
        }

        private static bool Check()
        {
            // Int fields first (a reference may sit where another layout has an int, never read as one).
            var one = ImmutableDictionary<int, string>.Empty.Add(7, "seven");
            var view = DictionaryTree<int, string>.ViewOf(one);
            if (DictionaryTree<int, string>.ViewOf(ImmutableDictionary<int, string>.Empty)._count != 0 ||
                view._count != 1 || view._root._key != 7 || view._root._height != 1 || !view._root._frozen ||
                view._root._left._height != 0 || view._root._value._firstValue.Key != 7)
                return false;
            var set = SetTree<int>.ViewOf(ImmutableHashSet<int>.Empty.Add(7));
            if (set._count != 1 || set._root._key != 7 || set._root._height != 1 || set._root._value._firstValue != 7)
                return false;
            return CheckDictionary() && CheckSet() && CheckBuckets();
        }

        private static bool CheckBuckets()
        {
            // Hashes that collide (a long's is its halves XORed): buckets with further items, walked in the
            // enumerator's order.
            // Built from an enumerable, never a params call: the compiler binds those to the ReadOnlySpan overloads of
            // newer versions, which older ones (Mono's) lack.
            var set = ImmutableHashSet.CreateRange(Enumerable.Range(0, 4).Select(i => (long)i | (long)i << 32).Append(5L).Append(7L));
            var dictionary = ImmutableDictionary<long, int>.Empty.AddRange(set.Select(item => new KeyValuePair<long, int>(item, (int)item + 1)));
            return Walks(SetTree<long>.ItemsOf(set), set, set.Count) &&
                   Walks(DictionaryTree<long, int>.PairsOf(dictionary), dictionary, dictionary.Count);
        }

        private static bool Walks<T, TSource>(TSource source, IEnumerable<T> collection, int count)
            where TSource : struct, INinoGatherSource<T>
        {
            var walked = new T[count + 1];
            int filled = source.Fill(walked);
            source.Finish();
            int at = 0;
            foreach (var item in collection)
                if (at >= filled || !EqualityComparer<T>.Default.Equals(walked[at++], item))
                    return false;
            return filled == count && at == count;
        }

        private static bool CheckDictionary()
        {
            var pairs = new KeyValuePair<string, int>[40];
            for (int i = 0; i < pairs.Length; i++)
                pairs[i] = new KeyValuePair<string, int>("k" + i, i);
            var built = DictionaryTree<string, int>.Create(pairs);
            if (built == null || built.Count != 40 || built.ContainsKey("k40"))
                return false;
            for (int i = 0; i < 40; i++)
                if (!built.TryGetValue("k" + i, out int value) || value != i)
                    return false;
            int seen = 0;
            foreach (var pair in built)
                seen += pair.Key == "k" + pair.Value ? 1 : 0;
            var changed = built.Add("k40", 40).Remove("k3").SetItem("k5", 50);
            return seen == 40 && changed.Count == 40 && changed["k40"] == 40 && !changed.ContainsKey("k3") &&
                   changed["k5"] == 50 && built.Count == 40 && built["k5"] == 5 && built.ContainsKey("k3");
        }

        private static bool CheckSet()
        {
            var items = new string[40];
            for (int i = 0; i < items.Length; i++)
                items[i] = "s" + i;
            var built = SetTree<string>.Create(items);
            if (built == null || built.Count != 40 || built.Contains("s40"))
                return false;
            for (int i = 0; i < 40; i++)
                if (!built.Contains("s" + i))
                    return false;
            int seen = 0;
            foreach (var item in built)
                seen += item[0] == 's' ? 1 : 0;
            var changed = built.Add("s40").Remove("s3");
            return seen == 40 && changed.Count == 40 && changed.Contains("s40") && !changed.Contains("s3") &&
                   built.Count == 40 && built.Contains("s3") && built.SetEquals(items);
        }
    }

    /// <summary>The tree both collections keep: <c>SortedInt32KeyNode&lt;HashBucket&gt;</c>.</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
#pragma warning disable CS0649 // laid out only
    internal static class HashTree<TItem>
    {
        /// <summary><c>SortedInt32KeyNode&lt;HashBucket&gt;</c>.</summary>
        internal sealed class Node
        {
            public int _key;
            public Bucket _value;
            public bool _frozen;
            public byte _height;
            public Node _left;
            public Node _right;
        }

        /// <summary><c>HashBucket</c>: the first item, then an <c>ImmutableList&lt;TItem&gt;.Node</c> of any others.</summary>
        internal struct Bucket
        {
            public TItem _firstValue;
            public object _additionalElements;
        }

#pragma warning restore CS0649

        private static readonly Node Sample = new();
        private static readonly int LeftAt = NinoTreeWalk.OffsetOf(Sample, ref Sample._left);
        private static readonly int RightAt = NinoTreeWalk.OffsetOf(Sample, ref Sample._right);

        /// <summary>The further items of a bucket that holds one: the empty node of an <c>ImmutableList</c>.</summary>
        private static readonly ListNode<TItem> NoMore = ListTree<TItem>.End;

        /// <summary>
        /// The tree of <paramref name="items"/> under <paramref name="empty"/> (the collection's empty node, of the
        /// type of all its nodes), or null when two of their <paramref name="hashes"/> are equal. Sorts both. More
        /// than <c>NinoSettings.MaxHashCollisions</c> items with one hash are invalid data
        /// (<see cref="HashTrees.CheckCollisions"/>).
        /// </summary>
        public static Node Build(TItem[] items, int[] hashes, Node empty, Type collection)
        {
            if (hashes.Length != items.Length)
                return null;
            Array.Sort(hashes, items);
            if (!HashTrees.CheckCollisions(hashes, collection))
                return null;
            // hashes is as long as items (checked above): every middle below is in both, read without bounds checks.
            return Make(ref NinoUnsafe.GetArrayDataReference(items), ref NinoUnsafe.GetArrayDataReference(hashes), 0,
                items.Length - 1, empty, ObjectTypes.TypeOf(empty));
        }

        /// <summary>
        /// The items under <paramref name="root"/> (subtrees ending at <paramref name="empty"/>) in the order the
        /// collection's enumerator yields them, a chunk at a time: the buckets in order of their hash
        /// (<see cref="NinoTreeWalk"/>), each bucket's first item, then its further ones (an <c>ImmutableList</c>'s tree,
        /// walked the same way), where the enumerator keeps a pooled stack and checks it per item.
        /// </summary>
        public static Items ItemsOf(Node root, Node empty) => new(new NinoTreeWalk(root, empty, LeftAt, RightAt));

        internal struct Items : INinoGatherSource<TItem>
        {
            private NinoTreeWalk _buckets;
            private NinoTreeItems<TItem> _more;
            private bool _inBucket;

            public Items(NinoTreeWalk buckets)
            {
                _buckets = buckets;
                _more = default;
                _inBucket = false;
            }

            public int Fill(Span<TItem> chunk)
            {
                int filled = 0;
                while (true)
                {
                    if (_inBucket)
                    {
                        // A bucket's further items: keys whose hashes are equal, rare.
                        filled += _more.Fill(chunk.Slice(filled));
                        if (filled == chunk.Length)
                            return filled;
                        _more.Finish();
                        _inBucket = false;
                    }

                    var buckets = new Buckets();
                    filled += _buckets.Fill(chunk.Slice(filled), ref buckets);
                    if (buckets.Held == null)
                        return filled;
                    _more = ListNode<TItem>.Items(Unsafe.As<ListNode<TItem>>(buckets.Held._value._additionalElements),
                        NoMore);
                    _inBucket = true;
                }
            }

            public void Finish()
            {
                if (_inBucket)
                    _more.Finish();
                _buckets.Finish();
            }
        }

        /// <summary>A bucket's first item; one with further items ends the fill, which then walks them.</summary>
        private struct Buckets : INinoTreeNodes<TItem>
        {
            public Node Held;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Take(object node, ref TItem item)
            {
                var bucket = Unsafe.As<Node>(node);
                item = bucket._value._firstValue;
                if (bucket._value._additionalElements == NoMore)
                    return true;
                Held = bucket;
                return false;
            }
        }

        private static Node Make(ref TItem items, ref int hashes, int low, int high, Node empty, IntPtr type)
        {
            if (low > high)
                return empty;
            int middle = (low + high) >> 1;
            var node = new Node();
            ObjectTypes.Retype(node, type);
            node._key = Unsafe.Add(ref hashes, middle);
            node._value._firstValue = Unsafe.Add(ref items, middle);
            node._value._additionalElements = NoMore;
            node._frozen = true;
            node._left = Make(ref items, ref hashes, low, middle - 1, empty, type);
            node._right = Make(ref items, ref hashes, middle + 1, high, empty, type);
            // Halves that differ by at most one item: heights that differ by at most one, as an AVL tree needs.
            node._height = (byte)(1 + Math.Max(node._left._height, node._right._height));
            return node;
        }
    }

    /// <summary><c>ImmutableDictionary&lt;TKey, TValue&gt;</c> as a tree (<see cref="HashTrees"/>).</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class DictionaryTree<TKey, TValue>
    {
#pragma warning disable CS0649, CS0169 // laid out only
        internal sealed class View
        {
            public int _count;
            public HashTree<KeyValuePair<TKey, TValue>>.Node _root;
            public object _comparers;
        }
#pragma warning restore CS0649, CS0169

        public static View ViewOf(ImmutableDictionary<TKey, TValue> dictionary) => Unsafe.As<View>(dictionary);

        private static readonly HashTree<KeyValuePair<TKey, TValue>>.Node End =
            ViewOf(ImmutableDictionary<TKey, TValue>.Empty)._root;

        /// <summary>The pairs of <paramref name="dictionary"/> in its enumerator's order, a chunk at a time.</summary>
        public static HashTree<KeyValuePair<TKey, TValue>>.Items PairsOf(ImmutableDictionary<TKey, TValue> dictionary) =>
            HashTree<KeyValuePair<TKey, TValue>>.ItemsOf(ViewOf(dictionary)._root, End);

        /// <summary>The dictionary of <paramref name="pairs"/> (sorted by hash), or null to build it the usual way.</summary>
        public static ImmutableDictionary<TKey, TValue> Create(KeyValuePair<TKey, TValue>[] pairs)
        {
            var comparer = EqualityComparer<TKey>.Default;
            var hashes = new int[pairs.Length];
            // As long as pairs: written through a cursor, without a bounds check per pair.
            ref int hash = ref NinoUnsafe.GetArrayDataReference(hashes);
            for (int i = 0; i < pairs.Length; i++)
            {
                var key = pairs[i].Key;
                if (key == null)
                    return null;
                Unsafe.Add(ref hash, i) = comparer.GetHashCode(key);
            }

            var empty = ViewOf(ImmutableDictionary<TKey, TValue>.Empty);
            var root = HashTree<KeyValuePair<TKey, TValue>>.Build(pairs, hashes, empty._root,
                typeof(ImmutableDictionary<TKey, TValue>));
            if (root == null)
                return null;
            var view = new View();
            ObjectTypes.Retype(view, ObjectTypes.TypeOf(ImmutableDictionary<TKey, TValue>.Empty));
            view._count = pairs.Length;
            view._root = root;
            view._comparers = empty._comparers;
            return Unsafe.As<ImmutableDictionary<TKey, TValue>>(view);
        }
    }

    /// <summary><c>ImmutableHashSet&lt;T&gt;</c> as a tree (<see cref="HashTrees"/>).</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class SetTree<T>
    {
#pragma warning disable CS0649, CS0169 // laid out only
        internal sealed class View
        {
            public object _equalityComparer;
            public int _count;
            public HashTree<T>.Node _root;
            public object _hashBucketEqualityComparer;
        }
#pragma warning restore CS0649, CS0169

        public static View ViewOf(ImmutableHashSet<T> set) => Unsafe.As<View>(set);

        private static readonly HashTree<T>.Node End = ViewOf(ImmutableHashSet<T>.Empty)._root;

        /// <summary>The items of <paramref name="set"/> in its enumerator's order, a chunk at a time.</summary>
        public static HashTree<T>.Items ItemsOf(ImmutableHashSet<T> set) => HashTree<T>.ItemsOf(ViewOf(set)._root, End);

        /// <summary>The set of <paramref name="items"/> (sorted by hash), or null to build it the usual way.</summary>
        public static ImmutableHashSet<T> Create(T[] items)
        {
            var comparer = EqualityComparer<T>.Default;
            var hashes = new int[items.Length];
            // As long as items: written through a cursor, without a bounds check per item.
            ref int hash = ref NinoUnsafe.GetArrayDataReference(hashes);
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null)
                    return null;
                Unsafe.Add(ref hash, i) = comparer.GetHashCode(item);
            }

            var empty = ViewOf(ImmutableHashSet<T>.Empty);
            var root = HashTree<T>.Build(items, hashes, empty._root, typeof(ImmutableHashSet<T>));
            if (root == null)
                return null;
            var view = new View();
            ObjectTypes.Retype(view, ObjectTypes.TypeOf(ImmutableHashSet<T>.Empty));
            view._equalityComparer = empty._equalityComparer;
            view._count = items.Length;
            view._root = root;
            view._hashBucketEqualityComparer = empty._hashBucketEqualityComparer;
            return Unsafe.As<ImmutableHashSet<T>>(view);
        }
    }
}
