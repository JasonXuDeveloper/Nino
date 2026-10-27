using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// <see cref="SortedSet{T}"/> trees built in one pass: the set's constructor sorts the elements (found in order,
    /// as Nino writes them), drops duplicates and builds a balanced tree from the array, where Add would insert and
    /// rebalance per element. Its nodes do not point back to the set, so the tree is moved into the set being read
    /// (<see cref="CollectionFields"/>; also the set under a <see cref="SortedDictionary{TKey,TValue}"/>).
    /// </summary>
    internal static class SortedSetTrees
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

        /// <remarks><paramref name="count"/> is at most the array's length (Fill's callers rent it for them).</remarks>
        private static bool Increasing<T>(T[] items, int count, IComparer<T> comparer)
        {
            // Indexed from the first item, without a bounds check per item. An index, not a cursor: a cursor bumped
            // around the comparer's call was kept on the stack.
            ref T first = ref NinoUnsafe.GetArrayDataReference(items);
            for (int i = 1; i < count; i++)
                if (comparer.Compare(Unsafe.Add(ref first, i - 1), Unsafe.Add(ref first, i)) >= 0)
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
            // count is at most the array's length: every middle below is in it, read without a bounds check.
            return Make(ref NinoUnsafe.GetArrayDataReference(items), 0, count - 1, 0, last == 0 ? -1 : last, layout,
                at, type);
        }

        private static object Make<T>(ref T items, int low, int high, int depth, int red, int layout, NodeOffsets at,
            IntPtr type)
        {
            if (low > high)
                return null;
            int middle = (low + high) >> 1;
            object node = layout == 1 ? new NodeCore<T>() : new NodeFramework<T>();
            ObjectTypes.Retype(node, type);
            ObjectFields.At<T>(node, at.Item) = Unsafe.Add(ref items, middle);
            ObjectFields.At<object>(node, at.Left) =
                Make(ref items, low, middle - 1, depth + 1, red, layout, at, type);
            ObjectFields.At<object>(node, at.Right) =
                Make(ref items, middle + 1, high, depth + 1, red, layout, at, type);
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
}
