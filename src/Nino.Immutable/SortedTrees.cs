using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableSortedDictionary&lt;TKey, TValue&gt;</c> and <c>ImmutableSortedSet&lt;T&gt;</c> built directly as their
    /// AVL tree from items in order, as <see cref="HashTrees"/> builds the hash collections: Nino writes them in order,
    /// so the tree is built in one pass, without a sort (<c>ImmutableSortedSet.CreateRange</c>) or a tree in between
    /// (a <c>SortedDictionary</c>). Built only with the default comparers, when every item is greater than the one
    /// before (otherwise the data is out of order, or holds an item twice) and none is null; the builder handles
    /// everything else, and every collection when <see cref="Usable"/> is not set.
    /// </summary>
    internal static class SortedTrees
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
                var map = SortedDictionaryTree<int, int>.ViewOf(ImmutableSortedDictionary<int, int>.Empty.Add(7, 70));
                if (map._count != 1 || map._root._key != 7 || map._root._value != 70 || map._root._height != 1 ||
                    !map._root._frozen || map._root._left._height != 0)
                    return false;
                var set = SortedSetTree<int>.ViewOf(ImmutableSortedSet<int>.Empty.Add(7).Add(9));
                if (set._root._count != 2 || set._root._height != 2 || set._root._key != 7 ||
                    set._root._right._key != 9 || set._root._right._count != 1)
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
                pairs[i] = new KeyValuePair<string, int>("k" + (i + 10).ToString(CultureInfo.InvariantCulture), i);
            var built = SortedDictionaryTree<string, int>.Create(pairs);
            if (built == null || built.Count != 40 || built.ContainsKey("k50"))
                return false;
            int at = 0;
            foreach (var pair in built)
                if (pair.Value != at++ || built[pair.Key] != pair.Value)
                    return false;
            var changed = built.Add("k50", 50).Remove("k13").SetItem("k15", 50);
            return changed.Count == 40 && changed["k50"] == 50 && !changed.ContainsKey("k13") && changed["k15"] == 50 &&
                   built.Count == 40 && built["k15"] == 5 && built.ContainsKey("k13");
        }

        private static bool CheckSet()
        {
            var items = new string[40];
            for (int i = 0; i < items.Length; i++)
                items[i] = "s" + (i + 10).ToString(CultureInfo.InvariantCulture);
            var built = SortedSetTree<string>.Create(items);
            if (built == null || built.Count != 40 || built.Contains("s50") || built.Min != "s10" || built.Max != "s49")
                return false;
            for (int i = 0; i < 40; i++)
                if (built[i] != items[i] || built.IndexOf(items[i]) != i)
                    return false;
            var changed = built.Add("s50").Remove("s13");
            return changed.Count == 40 && changed.Contains("s50") && !changed.Contains("s13") && changed[3] == "s14" &&
                   built.Count == 40 && built.Contains("s13") && built.SetEquals(items);
        }
    }

    /// <summary><c>ImmutableSortedDictionary&lt;TKey, TValue&gt;</c> as a tree (<see cref="SortedTrees"/>).</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class SortedDictionaryTree<TKey, TValue>
    {
#pragma warning disable CS0649, CS0169 // laid out only
        internal sealed class View
        {
            public Node _root;
            public int _count;
            public object _keyComparer;
            public object _valueComparer;
        }

        internal sealed class Node
        {
            public TKey _key;
            public TValue _value;
            public bool _frozen;
            public byte _height;
            public Node _left;
            public Node _right;
        }
#pragma warning restore CS0649, CS0169

        public static View ViewOf(ImmutableSortedDictionary<TKey, TValue> dictionary) => Unsafe.As<View>(dictionary);

        /// <summary>The dictionary of <paramref name="pairs"/>, or null to build it the usual way.</summary>
        public static ImmutableSortedDictionary<TKey, TValue> Create(KeyValuePair<TKey, TValue>[] pairs)
        {
            var comparer = Comparer<TKey>.Default;
            for (int i = 0; i < pairs.Length; i++)
            {
                var key = pairs[i].Key;
                if (key == null || i != 0 && comparer.Compare(pairs[i - 1].Key, key) >= 0)
                    return null;
            }

            var empty = ViewOf(ImmutableSortedDictionary<TKey, TValue>.Empty);
            var view = new View();
            ObjectTypes.Retype(view, ObjectTypes.TypeOf(ImmutableSortedDictionary<TKey, TValue>.Empty));
            view._root = Make(pairs, 0, pairs.Length - 1, empty._root, ObjectTypes.TypeOf(empty._root));
            view._count = pairs.Length;
            view._keyComparer = empty._keyComparer;
            view._valueComparer = empty._valueComparer;
            return Unsafe.As<ImmutableSortedDictionary<TKey, TValue>>(view);
        }

        private static Node Make(KeyValuePair<TKey, TValue>[] pairs, int low, int high, Node empty, IntPtr type)
        {
            if (low > high)
                return empty;
            int middle = (low + high) >> 1;
            var node = new Node();
            ObjectTypes.Retype(node, type);
            node._key = pairs[middle].Key;
            node._value = pairs[middle].Value;
            node._frozen = true;
            node._left = Make(pairs, low, middle - 1, empty, type);
            node._right = Make(pairs, middle + 1, high, empty, type);
            // Halves that differ by at most one item: heights that differ by at most one, as an AVL tree needs.
            node._height = (byte)(1 + Math.Max(node._left._height, node._right._height));
            return node;
        }
    }

    /// <summary><c>ImmutableSortedSet&lt;T&gt;</c> as a tree (<see cref="SortedTrees"/>).</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class SortedSetTree<T>
    {
#pragma warning disable CS0649, CS0169 // laid out only
        internal sealed class View
        {
            public Node _root;
            public object _comparer;
        }

        internal sealed class Node
        {
            public T _key;
            public bool _frozen;
            public byte _height;
            public int _count;
            public Node _left;
            public Node _right;
        }
#pragma warning restore CS0649, CS0169

        public static View ViewOf(ImmutableSortedSet<T> set) => Unsafe.As<View>(set);

        /// <summary>The set of <paramref name="items"/>, or null to build it the usual way.</summary>
        public static ImmutableSortedSet<T> Create(T[] items)
        {
            var comparer = Comparer<T>.Default;
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item == null || i != 0 && comparer.Compare(items[i - 1], item) >= 0)
                    return null;
            }

            var empty = ViewOf(ImmutableSortedSet<T>.Empty);
            var view = new View();
            ObjectTypes.Retype(view, ObjectTypes.TypeOf(ImmutableSortedSet<T>.Empty));
            view._root = Make(items, 0, items.Length - 1, empty._root, ObjectTypes.TypeOf(empty._root));
            view._comparer = empty._comparer;
            return Unsafe.As<ImmutableSortedSet<T>>(view);
        }

        private static Node Make(T[] items, int low, int high, Node empty, IntPtr type)
        {
            if (low > high)
                return empty;
            int middle = (low + high) >> 1;
            var node = new Node();
            ObjectTypes.Retype(node, type);
            node._key = items[middle];
            node._frozen = true;
            node._left = Make(items, low, middle - 1, empty, type);
            node._right = Make(items, middle + 1, high, empty, type);
            // As SortedDictionaryTree's; each node also counts the items under it (for the indexer).
            node._height = (byte)(1 + Math.Max(node._left._height, node._right._height));
            node._count = high - low + 1;
            return node;
        }
    }
}
