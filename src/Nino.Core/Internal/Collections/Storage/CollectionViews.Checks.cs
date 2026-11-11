using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    // The startup checks of each candidate layout against an instance with known contents.
    internal static partial class CollectionViews
    {
        /// <summary>
        /// An <see cref="ICollection{T}"/> of <c>0, 1, 2...</c> that keeps the array a collection's constructor copies
        /// it into: the collection's own backing array on every known implementation.
        /// </summary>
        private sealed class Source : ICollection<int>
        {
            private readonly int _count;
            public int[] Target;

            public Source(int count) => _count = count;
            public int Count => _count;
            public bool IsReadOnly => true;

            public void CopyTo(int[] array, int arrayIndex)
            {
                Target = array;
                for (int i = 0; i < _count; i++)
                    array[arrayIndex + i] = i;
            }

            public IEnumerator<int> GetEnumerator()
            {
                for (int i = 0; i < _count; i++)
                    yield return i;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Add(int item) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(int item) => item >= 0 && item < _count;
            public bool Remove(int item) => throw new NotSupportedException();
        }

        // Each check reads int fields first (a reference may sit where another layout has an int, never read as
        // one), then compares references as addresses, and only then reads through the layout.

        private static bool CheckList(ListOffsets at)
        {
            var source = new Source(37);
            var list = new List<int>(source);
            for (int i = 0; i < 5; i++)
                list[i] = 100 + i; // version 5
            if (ObjectFields.At<int>(list, at.Size) != 37 || ObjectFields.At<int>(list, at.Version) != 5 ||
                !ObjectFields.Holds(list, at.Items, source.Target))
                return false;
            var items = ObjectFields.At<int[]>(list, at.Items);
            ObjectFields.At<int>(list, at.Size) = 30;
            return items[4] == 104 && items[36] == 36 && list.Count == 30 && list[29] == 29;
        }

        private static bool CheckStack(ListOffsets at)
        {
            var source = new Source(37);
            var stack = new Stack<int>(source);
            stack.Pop();
            stack.Push(99); // version 2 on .NET, 2 on .NET Framework and Mono
            if (ObjectFields.At<int>(stack, at.Size) != 37 || ObjectFields.At<int>(stack, at.Version) != 2 ||
                !ObjectFields.Holds(stack, at.Items, source.Target))
                return false;
            var array = ObjectFields.At<int[]>(stack, at.Items);
            ObjectFields.At<int>(stack, at.Size) = 30;
            return array[36] == 99 && array[0] == 0 && stack.Count == 30 && stack.Peek() == 29;
        }

        private static bool CheckQueue(QueueOffsets at)
        {
            // Head 3, tail 6, size 3, version 9 (each Enqueue and Dequeue bumps it), in an array of 8.
            var queue = new Queue<int>(8);
            for (int i = 0; i < 6; i++)
                queue.Enqueue(10 + i);
            for (int i = 0; i < 3; i++)
                queue.Dequeue();
            if (ObjectFields.At<int>(queue, at.Head) != 3 || ObjectFields.At<int>(queue, at.Tail) != 6 ||
                ObjectFields.At<int>(queue, at.Size) != 3 || ObjectFields.At<int>(queue, at.Version) != 9)
                return false;
            var array = ObjectFields.At<int[]>(queue, at.Array);
            if (array is not { Length: 8 } || array[3] != 13 || array[5] != 15)
                return false;
            ObjectFields.At<int>(queue, at.Size) = 2;
            return queue.Count == 2 && queue.Peek() == 13;
        }

        private static bool CheckSortedList(SortedListOffsets at)
        {
            var comparer = Comparer<int>.Create((a, b) => a.CompareTo(b));
            var list = new SortedList<int, int>(4, comparer) { { 9, 90 }, { 7, 70 } }; // version 2
            if (ObjectFields.At<int>(list, at.Size) != 2 || ObjectFields.At<int>(list, at.Version) != 2 ||
                !ObjectFields.Holds(list, at.Comparer, comparer))
                return false;
            var keys = ObjectFields.At<int[]>(list, at.Keys);
            var values = ObjectFields.At<int[]>(list, at.Values);
            if (keys is not { Length: 4 } || keys[0] != 7 || keys[1] != 9 || values is not { Length: 4 } ||
                values[0] != 70 || values[1] != 90)
                return false;
            ObjectFields.At<int>(list, at.Size) = 1;
            return list.Count == 1 && list.Keys[0] == 7 && list.Values[0] == 70;
        }

        private static bool CheckSortedSet(SortedSetOffsets at)
        {
            var comparer = Comparer<int>.Create((a, b) => a.CompareTo(b));
            var source = new SortedSet<int>(comparer) { 5, 1, 3 }; // version 3
            if (ObjectFields.At<int>(source, at.Count) != 3 || ObjectFields.At<int>(source, at.Version) != 3 ||
                !ObjectFields.Holds(source, at.Comparer, comparer))
                return false;
            // A tree moved into an empty set is that set's: its nodes do not point back to the set.
            var target = new SortedSet<int>(comparer);
            ObjectFields.At<object>(target, at.Root) = ObjectFields.At<object>(source, at.Root);
            ObjectFields.At<int>(target, at.Count) = 3;
            ObjectFields.At<int>(target, at.Version)++;
            using var e = target.GetEnumerator();
            return e.MoveNext() && e.Current == 1 && e.MoveNext() && e.Current == 3 && e.MoveNext() &&
                   e.Current == 5 && !e.MoveNext() && target.Contains(3) && !target.Contains(2) && target.Max == 5 &&
                   target.Count == 3;
        }

        private static bool CheckLinkedList(LinkedListOffsets at)
        {
            var list = new LinkedList<int>();
            for (int i = 0; i < 4; i++)
                list.AddLast(i); // version 4
            if (ObjectFields.At<int>(list, at.Count) != 4 || ObjectFields.At<int>(list, at.Version) != 4 ||
                !ObjectFields.Holds(list, at.Head, list.First) || !CheckLinkedNode(list.First, list, 0) ||
                !CheckLinkedNode(list.Last, list, 3))
                return false;
            var strings = new LinkedList<string>();
            strings.AddLast("a");
            strings.AddLast("b");
            if (!CheckLinkedNode(strings.First, strings, "a"))
                return false;

            // Built as Nino builds it: the list works through its public API.
            var built = new LinkedList<string>();
            LinkedListNode<string> first = null, last = null;
            for (int i = 0; i < 40; i++)
            {
                var node = new LinkedListNode<string>("n" + i);
                CollectionFields.Append(built, node, last);
                first ??= node;
                last = node;
            }
            ObjectFields.At<LinkedListNode<string>>(built, at.Head) = first;
            ObjectFields.At<int>(built, at.Count) = 40;
            ObjectFields.At<int>(built, at.Version)++;
            CollectionFields.Close(first, last);
            int seen = 0;
            foreach (var item in built)
                if (item != "n" + seen++)
                    return false;
            built.Remove("n5");
            built.AddLast("n40");
            built.AddFirst("n41");
            return seen == 40 && built.Count == 41 && built.First.Value == "n41" && built.Last.Value == "n40" &&
                   built.Last.Previous.Value == "n39" && built.Find("n6").Previous.Value == "n4" &&
                   built.Find("n0").List == built && built.First.Previous == null && built.Last.Next == null;
        }

        /// <summary>Whether <paramref name="node"/>, read as a <see cref="CollectionFields.LinkedNode{T}"/>, holds what it holds.</summary>
        private static bool CheckLinkedNode<T>(LinkedListNode<T> node, LinkedList<T> list, T item)
        {
            var m = new CollectionFields.LinkedNode<T>();
            // References compared as addresses first: an item that is not one is never read as one.
            if (!ObjectFields.Holds(node, ObjectFields.OffsetOf(m, ref m.list), list) ||
                !ObjectFields.Holds(node, ObjectFields.OffsetOf(m, ref m.next), node.Next ?? list.First) ||
                !ObjectFields.Holds(node, ObjectFields.OffsetOf(m, ref m.prev), node.Previous ?? list.Last))
                return false;
            return EqualityComparer<T>.Default.Equals(ObjectFields.At<T>(node, ObjectFields.OffsetOf(m, ref m.item)),
                item);
        }

        private static bool CheckHashSet(HashOffsets at)
        {
            // Four entries in use, one of them free: bound 4, count 3, version 4 or 5 (Remove bumps it on some).
            var set = new HashSet<long> { 10, 11, 12, 13 };
            set.Remove(11);
            int version = ObjectFields.At<int>(set, at.Version);
            if (ObjectFields.At<int>(set, at.Bound) != 4 || version != 4 && version != 5)
                return false;
            if (ObjectFields.At<object>(set, at.Entries) is not Array { Length: >= 4 } array ||
                array.GetType().GetElementType() is not { IsValueType: true, IsGenericType: true } ||
                Unsafe.As<CollectionFields.SetEntry<long>[]>(array) is not { } entries ||
                entries[0].Value != 10 || entries[2].Value != 12 || entries[3].Value != 13)
                return false;
            set.Add(14); // into the free entry: bound 4, count 4
            return ObjectFields.At<int>(set, at.Bound) == 4 && entries[1].Value == 14 && set.Count == 4;
        }

        private static bool CheckDictionary(HashOffsets at)
        {
            // As CheckHashSet's.
            var dictionary = new Dictionary<long, short> { { 10, 1 }, { 11, 2 }, { 12, 3 }, { 13, 4 } };
            dictionary.Remove(11);
            int version = ObjectFields.At<int>(dictionary, at.Version);
            if (ObjectFields.At<int>(dictionary, at.Bound) != 4 || version != 4 && version != 5)
                return false;
            if (ObjectFields.At<object>(dictionary, at.Entries) is not Array { Length: >= 4 } array ||
                array.GetType().GetElementType() is not { IsValueType: true, IsGenericType: true } ||
                Unsafe.As<CollectionFields.MapEntry<long, short>[]>(array) is not { } entries ||
                entries[0].Key != 10 || entries[2].Value != 3 || entries[3].Key != 13 || entries[3].Value != 4)
                return false;
            dictionary.Add(14, 5); // into the free entry: bound 4, count 4
            return ObjectFields.At<int>(dictionary, at.Bound) == 4 && entries[1].Key == 14 && entries[1].Value == 5 &&
                   dictionary.Count == 4;
        }

        private static bool CheckSortedDictionary(SortedDictionaryOffsets at)
        {
            var dictionary = new SortedDictionary<int, int> { { 9, 90 }, { 7, 70 } };
            // Every field a reference: a wrong guess reads null (keys and values are created on first use) or
            // another of its objects.
            return ObjectFields.At<object>(dictionary, at.Keys) == null &&
                   ObjectFields.At<object>(dictionary, at.Values) == null &&
                   ObjectFields.At<object>(dictionary, at.Set) is SortedSet<KeyValuePair<int, int>> { Count: 2 } set &&
                   set.Min.Key == 7 && set.Max.Value == 90;
        }
    }
}
