using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
        public static readonly bool Usable = ObjectTypes.Usable && Check();

        private static bool Check()
        {
            try
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
                return CheckDictionary() && CheckSet();
            }
            catch
            {
                return false;
            }
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

        /// <summary><c>ImmutableList&lt;TItem&gt;</c>: its empty one holds the empty node every bucket of one item holds.</summary>
        private sealed class ListView
        {
            public object _root;
        }
#pragma warning restore CS0649

        private static readonly object NoMore = Unsafe.As<ListView>(ImmutableList<TItem>.Empty)._root;

        /// <summary>
        /// The tree of <paramref name="items"/> under <paramref name="empty"/> (the collection's empty node, of the
        /// type of all its nodes), or null when two of their <paramref name="hashes"/> are equal. Sorts both.
        /// </summary>
        public static Node Build(TItem[] items, int[] hashes, Node empty)
        {
            Array.Sort(hashes, items);
            for (int i = 1; i < hashes.Length; i++)
                if (hashes[i] == hashes[i - 1])
                    return null;
            return Make(items, hashes, 0, items.Length - 1, empty, ObjectTypes.TypeOf(empty));
        }

        /// <summary>
        /// The <paramref name="count"/> items under <paramref name="root"/> in a rented array, in the order the
        /// collection's enumerator yields them (by hash); null when a bucket holds more than one item, which the
        /// enumerator is left to.
        /// </summary>
        public static TItem[] Copy(Node root, int count)
        {
            var array = ArrayPool<TItem>.Shared.Rent(count);
            int at = 0;
            if (CopyTo(root, array, ref at))
                return array;
            ImmutableCollections.Return(array);
            return null;
        }

        private static bool CopyTo(Node node, TItem[] array, ref int at)
        {
            // The empty node, under every leaf, has no children.
            for (; node._left != null; node = node._right)
            {
                if (!CopyTo(node._left, array, ref at) || node._value._additionalElements != NoMore)
                    return false;
                array[at++] = node._value._firstValue;
            }

            return true;
        }

        private static Node Make(TItem[] items, int[] hashes, int low, int high, Node empty, IntPtr type)
        {
            if (low > high)
                return empty;
            int middle = (low + high) >> 1;
            var node = new Node();
            ObjectTypes.Retype(node, type);
            node._key = hashes[middle];
            node._value._firstValue = items[middle];
            node._value._additionalElements = NoMore;
            node._frozen = true;
            node._left = Make(items, hashes, low, middle - 1, empty, type);
            node._right = Make(items, hashes, middle + 1, high, empty, type);
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

        /// <summary>
        /// The <paramref name="count"/> pairs of <paramref name="dictionary"/> in a rented array, as
        /// <see cref="HashTree{TItem}.Copy"/> copies them, or null.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static KeyValuePair<TKey, TValue>[] Copy(ImmutableDictionary<TKey, TValue> dictionary, int count) =>
            HashTree<KeyValuePair<TKey, TValue>>.Copy(ViewOf(dictionary)._root, count);

        /// <summary>The dictionary of <paramref name="pairs"/> (sorted by hash), or null to build it the usual way.</summary>
        public static ImmutableDictionary<TKey, TValue> Create(KeyValuePair<TKey, TValue>[] pairs)
        {
            var comparer = EqualityComparer<TKey>.Default;
            var hashes = new int[pairs.Length];
            for (int i = 0; i < pairs.Length; i++)
            {
                var key = pairs[i].Key;
                if (key == null)
                    return null;
                hashes[i] = comparer.GetHashCode(key);
            }

            var empty = ViewOf(ImmutableDictionary<TKey, TValue>.Empty);
            var root = HashTree<KeyValuePair<TKey, TValue>>.Build(pairs, hashes, empty._root);
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

        /// <summary>
        /// The <paramref name="count"/> items of <paramref name="set"/> in a rented array, as
        /// <see cref="HashTree{TItem}.Copy"/> copies them, or null.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T[] Copy(ImmutableHashSet<T> set, int count) => HashTree<T>.Copy(ViewOf(set)._root, count);

        /// <summary>The set of <paramref name="items"/> (sorted by hash), or null to build it the usual way.</summary>
        public static ImmutableHashSet<T> Create(T[] items)
        {
            var comparer = EqualityComparer<T>.Default;
            var hashes = new int[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null)
                    return null;
                hashes[i] = comparer.GetHashCode(item);
            }

            var empty = ViewOf(ImmutableHashSet<T>.Empty);
            var root = HashTree<T>.Build(items, hashes, empty._root);
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
