using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

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
        public static readonly bool Usable = FindHeader() is var header && (Header = header) != 0 && Check();

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

        // What type an object is: the word before its first field and any other header word, which CoreCLR (the method
        // table, one word) and Mono and IL2CPP (the vtable or class, then the monitor: two words) keep at the address a
        // reference holds. Found from two classes of one byte field: the word that differs between them and not
        // between two instances of one.
        private sealed class Probe
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public byte Data;
#pragma warning restore CS0649
        }

        private sealed class OtherProbe
        {
#pragma warning disable CS0649, CS0169 // laid out only
            public byte Data;
#pragma warning restore CS0649, CS0169
        }

        // Set before Usable is.
        private static readonly int Header;

        private static int FindHeader()
        {
            foreach (int header in new[] { IntPtr.Size, 2 * IntPtr.Size })
            {
                IntPtr type = TypeWord(new Probe(), header), other = TypeWord(new OtherProbe(), header);
                if (type == IntPtr.Zero || type != TypeWord(new Probe(), header) || type == other)
                    continue;
                object instance = new OtherProbe();
                TypeWord(instance, header) = type;
                if (TypeOfInstance(instance) == typeof(Probe))
                    return header;
            }

            return 0;
        }

        // Out of line: inlined, the JIT knows the type it allocated and takes that for GetType's (Arm32).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Type TypeOfInstance(object instance) => instance.GetType();

        private static ref IntPtr TypeWord(object instance, int header) =>
            ref Unsafe.As<byte, IntPtr>(ref Unsafe.Subtract(ref Unsafe.As<Probe>(instance).Data, header));

        /// <summary>The word that says what type <paramref name="instance"/> is.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr TypeOf(object instance) => TypeWord(instance, Header);

        /// <summary>
        /// Makes <paramref name="instance"/>, a new instance of a class laid out as the type <paramref name="type"/>
        /// says (the same fields in the same order: the same size and the same references where the GC looks for
        /// them), an instance of that type: a plain allocation instead of an uninitialized object made by type
        /// (RuntimeHelpers.GetUninitializedObject: 13 ns on .NET 10 but 77 on .NET 8, and FormatterServices on Mono:
        /// 59), and nothing a runtime without them (IL2CPP) could lack.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Retype(object instance, IntPtr type) => TypeWord(instance, Header) = type;

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
            return Make(items, hashes, 0, items.Length - 1, empty, HashTrees.TypeOf(empty));
        }

        private static Node Make(TItem[] items, int[] hashes, int low, int high, Node empty, IntPtr type)
        {
            if (low > high)
                return empty;
            int middle = (low + high) >> 1;
            var node = new Node();
            HashTrees.Retype(node, type);
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
            HashTrees.Retype(view, HashTrees.TypeOf(ImmutableDictionary<TKey, TValue>.Empty));
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
            HashTrees.Retype(view, HashTrees.TypeOf(ImmutableHashSet<T>.Empty));
            view._equalityComparer = empty._equalityComparer;
            view._count = items.Length;
            view._root = root;
            view._hashBucketEqualityComparer = empty._hashBucketEqualityComparer;
            return Unsafe.As<ImmutableHashSet<T>>(view);
        }
    }
}
