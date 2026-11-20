using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Immutable
{
    /// <summary>
    /// The AVL trees of <c>ImmutableSortedDictionary&lt;TKey, TValue&gt;</c>, <c>ImmutableSortedSet&lt;T&gt;</c> and
    /// <c>ImmutableList&lt;T&gt;</c>: walked in order for a write by Nino.Core's one walk of every tree
    /// (<see cref="NinoTreeWalk"/>, a chunk at a time through the gather), where their enumerators rent a stack of nodes
    /// and check it per element; and the sorted ones built directly as their tree from items in order, as <see cref="HashTrees"/> builds the hash collections: Nino writes them in order,
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
        public static readonly bool Usable = ObjectTypes.Usable && ObjectTypes.Passes(Check);

        private static bool Check()
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
            var list = ListTree<int>.ViewOf(ImmutableList.Create(7));
            if (list._root._key != 7 || list._root._count != 1 || list._root._height != 1 || !list._root._frozen ||
                list._root._left._height != 0 || ListTree<int>.ViewOf(ImmutableList<int>.Empty)._root._left != null)
                return false;
            return CheckDictionary() && CheckSet() && CheckList();
        }

        private static bool CheckList()
        {
            // Built, then grown at both ends and in the middle: walked through the mirror in the enumerator's order.
            var list = ImmutableList.CreateRange(Enumerable.Range(0, 3).Select(i => "s" + i.ToString(CultureInfo.InvariantCulture))).Insert(0, "z").Add("y").Insert(2, "x");
            for (int i = 0; i < 40; i++)
                list = list.Add("n" + i.ToString(CultureInfo.InvariantCulture));
            var walked = new List<string>();
            Walk(ListTree<string>.ViewOf(list)._root, walked);
            if (walked.Count != list.Count || ListTree<string>.ViewOf(list)._root._count != list.Count)
                return false;
            int at = 0;
            foreach (var item in list)
                if (!ReferenceEquals(item, walked[at++]))
                    return false;
            return true;
        }

        private static void Walk(ListNode<string> node, List<string> items)
        {
            for (; node._left != null; node = node._right)
            {
                Walk(node._left, items);
                items.Add(node._key);
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
            // Every middle below is in the array: read without a bounds check.
            view._root = Make(ref NinoUnsafe.GetArrayDataReference(pairs), 0, pairs.Length - 1, empty._root,
                ObjectTypes.TypeOf(empty._root));
            view._count = pairs.Length;
            view._keyComparer = empty._keyComparer;
            view._valueComparer = empty._valueComparer;
            return Unsafe.As<ImmutableSortedDictionary<TKey, TValue>>(view);
        }

        private static readonly Node Sample = new();
        private static readonly int LeftAt = NinoTreeWalk.OffsetOf(Sample, ref Sample._left);
        private static readonly int RightAt = NinoTreeWalk.OffsetOf(Sample, ref Sample._right);
        private static readonly Node End = ViewOf(ImmutableSortedDictionary<TKey, TValue>.Empty)._root;

        /// <summary>The pairs of <paramref name="dictionary"/> in order, a chunk at a time (<see cref="NinoTreeWalk"/>).</summary>
        public static Pairs PairsOf(ImmutableSortedDictionary<TKey, TValue> dictionary) =>
            new(new NinoTreeWalk(ViewOf(dictionary)._root, End, LeftAt, RightAt));

        /// <summary>A node's key and value, which it keeps as two fields, made into its pair.</summary>
        internal struct Pairs : INinoGatherSource<KeyValuePair<TKey, TValue>>
        {
            private NinoTreeWalk _nodes;

            public Pairs(NinoTreeWalk nodes) => _nodes = nodes;

            public int Fill(Span<KeyValuePair<TKey, TValue>> chunk)
            {
                // The walk in a local for the loop, written back once.
                var nodes = _nodes;
                ref var at = ref MemoryMarshal.GetReference(chunk);
                int filled = 0;
                for (; filled < chunk.Length; filled++)
                {
                    var node = Unsafe.As<Node>(nodes.Next());
                    if (node == null)
                        break;
                    Unsafe.Add(ref at, filled) = new KeyValuePair<TKey, TValue>(node._key, node._value);
                }

                _nodes = nodes;
                return filled;
            }

            public void Finish() => _nodes.Finish();
        }

        private static Node Make(ref KeyValuePair<TKey, TValue> pairs, int low, int high, Node empty, IntPtr type)
        {
            if (low > high)
                return empty;
            int middle = (low + high) >> 1;
            var node = new Node();
            ObjectTypes.Retype(node, type);
            ref var pair = ref Unsafe.Add(ref pairs, middle);
            node._key = pair.Key;
            node._value = pair.Value;
            node._frozen = true;
            node._left = Make(ref pairs, low, middle - 1, empty, type);
            node._right = Make(ref pairs, middle + 1, high, empty, type);
            // Halves that differ by at most one item: heights that differ by at most one, as an AVL tree needs.
            node._height = (byte)(1 + Math.Max(node._left._height, node._right._height));
            return node;
        }
    }

    /// <summary>
    /// The node of <c>ImmutableList&lt;T&gt;</c> and of <c>ImmutableSortedSet&lt;T&gt;</c>, which declare the same fields
    /// in the same order (<c>ImmutableList&lt;T&gt;.Node</c>, <c>ImmutableSortedSet&lt;T&gt;.Node</c>), so one mirror
    /// is laid out as both; also the further items of a hash bucket (<see cref="HashTree{TItem}"/>), an
    /// <c>ImmutableList</c>'s node.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal sealed class ListNode<T>
    {
#pragma warning disable CS0649, CS0169 // laid out only
        public T _key;
        public bool _frozen;
        public byte _height;
        public int _count;
        public ListNode<T> _left;
        public ListNode<T> _right;
#pragma warning restore CS0649, CS0169

        private static readonly ListNode<T> Sample = new();
        public static readonly int KeyAt = NinoTreeWalk.OffsetOf(Sample, ref Sample._key);
        public static readonly int LeftAt = NinoTreeWalk.OffsetOf(Sample, ref Sample._left);
        public static readonly int RightAt = NinoTreeWalk.OffsetOf(Sample, ref Sample._right);

        /// <summary>
        /// The items under <paramref name="root"/> in order, a chunk at a time (<see cref="NinoTreeItems{T}"/>), its
        /// subtrees ending at <paramref name="end"/>, the collection's empty node.
        /// </summary>
        public static NinoTreeItems<T> Items(ListNode<T> root, ListNode<T> end) =>
            new(root, end, LeftAt, RightAt, KeyAt);
    }

    /// <summary><c>ImmutableList&lt;T&gt;</c>: its tree (<see cref="ListNode{T}"/>), walked in order.</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class ListTree<T>
    {
#pragma warning disable CS0649, CS0169 // laid out only
        internal sealed class View
        {
            public ListNode<T> _root;
        }
#pragma warning restore CS0649, CS0169

        public static View ViewOf(ImmutableList<T> list) => Unsafe.As<View>(list);

        /// <summary>The empty node under every leaf.</summary>
        public static readonly ListNode<T> End = ViewOf(ImmutableList<T>.Empty)._root;

        /// <summary>The items of <paramref name="list"/> in order.</summary>
        public static NinoTreeItems<T> Items(ImmutableList<T> list) => ListNode<T>.Items(ViewOf(list)._root, End);
    }

    /// <summary><c>ImmutableSortedSet&lt;T&gt;</c> as a tree (<see cref="SortedTrees"/>).</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal static class SortedSetTree<T>
    {
#pragma warning disable CS0649, CS0169 // laid out only
        internal sealed class View
        {
            public ListNode<T> _root;
            public object _comparer;
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
            // Every middle below is in the array: read without a bounds check.
            view._root = Make(ref NinoUnsafe.GetArrayDataReference(items), 0, items.Length - 1, empty._root,
                ObjectTypes.TypeOf(empty._root));
            view._comparer = empty._comparer;
            return Unsafe.As<ImmutableSortedSet<T>>(view);
        }

        private static readonly ListNode<T> End = ViewOf(ImmutableSortedSet<T>.Empty)._root;

        /// <summary>The items of <paramref name="set"/> in order.</summary>
        public static NinoTreeItems<T> Items(ImmutableSortedSet<T> set) => ListNode<T>.Items(ViewOf(set)._root, End);

        private static ListNode<T> Make(ref T items, int low, int high, ListNode<T> empty, IntPtr type)
        {
            if (low > high)
                return empty;
            int middle = (low + high) >> 1;
            var node = new ListNode<T>();
            ObjectTypes.Retype(node, type);
            node._key = Unsafe.Add(ref items, middle);
            node._frozen = true;
            node._left = Make(ref items, low, middle - 1, empty, type);
            node._right = Make(ref items, middle + 1, high, empty, type);
            // As SortedDictionaryTree's; each node also counts the items under it (for the indexer).
            node._height = (byte)(1 + Math.Max(node._left._height, node._right._height));
            node._count = high - low + 1;
            return node;
        }
    }
}
