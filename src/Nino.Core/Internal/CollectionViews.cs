using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The fields of an object at byte offsets: what the runtime's own collections hold, where they hold it on this
    /// runtime (<see cref="CollectionViews"/>).
    /// </summary>
    internal static class ObjectFields
    {
        /// <summary>A class whose one field is the first byte after the object header of this runtime.</summary>
        private sealed class RawObject
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public byte Data;
#pragma warning restore CS0649
        }

        /// <summary>The field of type <typeparamref name="TField"/> at <paramref name="offset"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TField At<TField>(object instance, int offset) =>
            ref Unsafe.As<byte, TField>(ref Unsafe.AddByteOffset(ref Unsafe.As<RawObject>(instance).Data,
                (IntPtr)offset));

        /// <summary>The offset of <paramref name="field"/>, a field of <paramref name="instance"/>.</summary>
        public static int OffsetOf<TField>(object instance, ref TField field) =>
            (int)Unsafe.ByteOffset(ref Unsafe.As<RawObject>(instance).Data, ref Unsafe.As<TField, byte>(ref field));

        /// <summary>
        /// Whether the field at <paramref name="offset"/> holds <paramref name="target"/>. Compared as addresses, so
        /// a field that holds no reference is never read as one; retried in case a GC moved the target between the
        /// two reads.
        /// </summary>
        public static bool Holds(object instance, int offset, object target)
        {
            for (int attempt = 0; attempt < 3; attempt++)
                if (At<IntPtr>(instance, offset) == Unsafe.As<object, IntPtr>(ref target))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Where the fields of <see cref="List{T}"/>, <see cref="Stack{T}"/>, <see cref="Queue{T}"/>,
    /// <see cref="SortedList{TKey,TValue}"/>, <see cref="SortedSet{T}"/> and
    /// <see cref="SortedDictionary{TKey,TValue}"/> are on this runtime, for <see cref="CollectionFields"/>. Each
    /// layout is one of the classes below, which declare the fields of a known implementation in its order (.NET; .NET
    /// Framework, Mono and Unity, which add a <c>_syncRoot</c>) and so are laid out as it is: the runtime places
    /// references first, in declaration order. The first one whose offsets hold what an instance with known contents
    /// holds is used: its int fields compared first, then its references as addresses, then read through the
    /// layout; otherwise the formatters use the public API. Static readonly: constants in Tier1 code.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
#pragma warning disable CS0649, CS0169 // laid out only
    internal static class CollectionViews
    {
        private sealed class ListCore
        {
            public object _items;
            public int _size;
            public int _version;
        }

        private sealed class ListFramework
        {
            public object _items;
            public int _size;
            public int _version;
            public object _syncRoot;
        }

        private sealed class QueueCore
        {
            public object _array;
            public int _head;
            public int _tail;
            public int _size;
            public int _version;
        }

        private sealed class QueueFramework
        {
            public object _array;
            public int _head;
            public int _tail;
            public int _size;
            public int _version;
            public object _syncRoot;
        }

        private sealed class SortedListCore
        {
            public object keys;
            public object values;
            public int _size;
            public int version;
            public object comparer;
            public object keyList;
            public object valueList;
        }

        private sealed class SortedListFramework
        {
            public object keys;
            public object values;
            public int _size;
            public int version;
            public object comparer;
            public object keyList;
            public object valueList;
            public object _syncRoot;
        }

        private sealed class SortedSetCore
        {
            public object root;
            public object comparer;
            public int count;
            public int version;
            public object siInfo;
        }

        private sealed class SortedSetFramework
        {
            public object root;
            public object comparer;
            public int count;
            public int version;
            public object _syncRoot;
            public object siInfo;
        }

        private sealed class SortedDictionaryAny
        {
            public object _keys;
            public object _values;
            public object _set;
        }
#pragma warning restore CS0649, CS0169

        // List<T> (Stack<T> has the same fields): items, size, version.
        private static readonly int[] ListLayout = Find(CheckList,
            () => { var m = new ListCore(); return Offsets(m, ref m._items, ref m._size, ref m._version); },
            () => { var m = new ListFramework(); return Offsets(m, ref m._items, ref m._size, ref m._version); });

        public static readonly bool List = ListLayout != null;
        public static readonly int ListItems = List ? ListLayout[0] : 0;
        public static readonly int ListSize = List ? ListLayout[1] : 0;

        // Stack<T>: array, size, version.
        private static readonly int[] StackLayout = Find(CheckStack,
            () => { var m = new ListCore(); return Offsets(m, ref m._items, ref m._size, ref m._version); },
            () => { var m = new ListFramework(); return Offsets(m, ref m._items, ref m._size, ref m._version); });

        public static readonly bool Stack = StackLayout != null;
        public static readonly int StackArray = Stack ? StackLayout[0] : 0;
        public static readonly int StackSize = Stack ? StackLayout[1] : 0;
        public static readonly int StackVersion = Stack ? StackLayout[2] : 0;

        // Queue<T>: array, head, tail, size, version.
        private static readonly int[] QueueLayout = Find(CheckQueue,
            () =>
            {
                var m = new QueueCore();
                return Offsets(m, ref m._array, ref m._head, ref m._tail, ref m._size, ref m._version);
            },
            () =>
            {
                var m = new QueueFramework();
                return Offsets(m, ref m._array, ref m._head, ref m._tail, ref m._size, ref m._version);
            });

        public static readonly bool Queue = QueueLayout != null;
        public static readonly int QueueArray = Queue ? QueueLayout[0] : 0;
        public static readonly int QueueHead = Queue ? QueueLayout[1] : 0;
        public static readonly int QueueTail = Queue ? QueueLayout[2] : 0;
        public static readonly int QueueSize = Queue ? QueueLayout[3] : 0;
        public static readonly int QueueVersion = Queue ? QueueLayout[4] : 0;

        // SortedList<TKey, TValue>: keys, values, size, version, comparer.
        private static readonly int[] SortedListLayout = Find(CheckSortedList,
            () =>
            {
                var m = new SortedListCore();
                return Offsets(m, ref m.keys, ref m.values, ref m._size, ref m.version, ref m.comparer);
            },
            () =>
            {
                var m = new SortedListFramework();
                return Offsets(m, ref m.keys, ref m.values, ref m._size, ref m.version, ref m.comparer);
            });

        public static readonly bool SortedList = SortedListLayout != null;
        public static readonly int SortedListKeys = SortedList ? SortedListLayout[0] : 0;
        public static readonly int SortedListValues = SortedList ? SortedListLayout[1] : 0;
        public static readonly int SortedListSize = SortedList ? SortedListLayout[2] : 0;
        public static readonly int SortedListVersion = SortedList ? SortedListLayout[3] : 0;
        public static readonly int SortedListComparer = SortedList ? SortedListLayout[4] : 0;

        // SortedSet<T>: root, count, version, comparer.
        private static readonly int[] SortedSetLayout = Find(CheckSortedSet,
            () =>
            {
                var m = new SortedSetCore();
                return Offsets(m, ref m.root, ref m.count, ref m.version, ref m.comparer);
            },
            () =>
            {
                var m = new SortedSetFramework();
                return Offsets(m, ref m.root, ref m.count, ref m.version, ref m.comparer);
            });

        public static readonly bool SortedSet = SortedSetLayout != null;
        public static readonly int SortedSetRoot = SortedSet ? SortedSetLayout[0] : 0;
        public static readonly int SortedSetCount = SortedSet ? SortedSetLayout[1] : 0;
        public static readonly int SortedSetVersion = SortedSet ? SortedSetLayout[2] : 0;

        // SortedDictionary<TKey, TValue>: the set of its pairs (every field a reference: no int to compare first).
        private static readonly int[] SortedDictionaryLayout = SortedSet
            ? Find(CheckSortedDictionary, () =>
            {
                var m = new SortedDictionaryAny();
                return Offsets(m, ref m._set, ref m._keys, ref m._values);
            })
            : null;

        public static readonly bool SortedDictionary = SortedDictionaryLayout != null;
        public static readonly int SortedDictionarySet = SortedDictionary ? SortedDictionaryLayout[0] : 0;

        private static int[] Offsets<T0, T1, T2>(object mirror, ref T0 f0, ref T1 f1, ref T2 f2) => new[]
        {
            ObjectFields.OffsetOf(mirror, ref f0), ObjectFields.OffsetOf(mirror, ref f1),
            ObjectFields.OffsetOf(mirror, ref f2)
        };

        private static int[] Offsets<T0, T1, T2, T3>(object mirror, ref T0 f0, ref T1 f1, ref T2 f2, ref T3 f3) =>
            new[]
            {
                ObjectFields.OffsetOf(mirror, ref f0), ObjectFields.OffsetOf(mirror, ref f1),
                ObjectFields.OffsetOf(mirror, ref f2), ObjectFields.OffsetOf(mirror, ref f3)
            };

        private static int[] Offsets<T0, T1, T2, T3, T4>(object mirror, ref T0 f0, ref T1 f1, ref T2 f2, ref T3 f3,
            ref T4 f4) => new[]
        {
            ObjectFields.OffsetOf(mirror, ref f0), ObjectFields.OffsetOf(mirror, ref f1),
            ObjectFields.OffsetOf(mirror, ref f2), ObjectFields.OffsetOf(mirror, ref f3),
            ObjectFields.OffsetOf(mirror, ref f4)
        };

        /// <summary>The offsets of the first candidate layout that <paramref name="check"/> accepts, or null.</summary>
        private static int[] Find(Func<int[], bool> check, params Func<int[]>[] candidates)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var offsets = candidate();
                    if (check(offsets))
                        return offsets;
                }
                catch
                {
                    // Not this layout.
                }
            }

            return null;
        }

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

        private static bool CheckList(int[] at)
        {
            var source = new Source(37);
            var list = new List<int>(source);
            for (int i = 0; i < 5; i++)
                list[i] = 100 + i; // version 5
            if (ObjectFields.At<int>(list, at[1]) != 37 || ObjectFields.At<int>(list, at[2]) != 5 ||
                !ObjectFields.Holds(list, at[0], source.Target))
                return false;
            var items = ObjectFields.At<int[]>(list, at[0]);
            ObjectFields.At<int>(list, at[1]) = 30;
            return items[4] == 104 && items[36] == 36 && list.Count == 30 && list[29] == 29;
        }

        private static bool CheckStack(int[] at)
        {
            var source = new Source(37);
            var stack = new Stack<int>(source);
            stack.Pop();
            stack.Push(99); // version 2 on .NET, 2 on .NET Framework and Mono
            if (ObjectFields.At<int>(stack, at[1]) != 37 || ObjectFields.At<int>(stack, at[2]) != 2 ||
                !ObjectFields.Holds(stack, at[0], source.Target))
                return false;
            var array = ObjectFields.At<int[]>(stack, at[0]);
            ObjectFields.At<int>(stack, at[1]) = 30;
            return array[36] == 99 && array[0] == 0 && stack.Count == 30 && stack.Peek() == 29;
        }

        private static bool CheckQueue(int[] at)
        {
            // Head 3, tail 6, size 3, version 9 (each Enqueue and Dequeue bumps it), in an array of 8.
            var queue = new Queue<int>(8);
            for (int i = 0; i < 6; i++)
                queue.Enqueue(10 + i);
            for (int i = 0; i < 3; i++)
                queue.Dequeue();
            if (ObjectFields.At<int>(queue, at[1]) != 3 || ObjectFields.At<int>(queue, at[2]) != 6 ||
                ObjectFields.At<int>(queue, at[3]) != 3 || ObjectFields.At<int>(queue, at[4]) != 9)
                return false;
            var array = ObjectFields.At<int[]>(queue, at[0]);
            if (array is not { Length: 8 } || array[3] != 13 || array[5] != 15)
                return false;
            ObjectFields.At<int>(queue, at[3]) = 2;
            return queue.Count == 2 && queue.Peek() == 13;
        }

        private static bool CheckSortedList(int[] at)
        {
            var comparer = Comparer<int>.Create((a, b) => a.CompareTo(b));
            var list = new SortedList<int, int>(4, comparer) { { 9, 90 }, { 7, 70 } }; // version 2
            if (ObjectFields.At<int>(list, at[2]) != 2 || ObjectFields.At<int>(list, at[3]) != 2 ||
                !ObjectFields.Holds(list, at[4], comparer))
                return false;
            var keys = ObjectFields.At<int[]>(list, at[0]);
            var values = ObjectFields.At<int[]>(list, at[1]);
            if (keys is not { Length: 4 } || keys[0] != 7 || keys[1] != 9 || values is not { Length: 4 } ||
                values[0] != 70 || values[1] != 90)
                return false;
            ObjectFields.At<int>(list, at[2]) = 1;
            return list.Count == 1 && list.Keys[0] == 7 && list.Values[0] == 70;
        }

        private static bool CheckSortedSet(int[] at)
        {
            var comparer = Comparer<int>.Create((a, b) => a.CompareTo(b));
            var source = new SortedSet<int>(comparer) { 5, 1, 3 }; // version 3
            if (ObjectFields.At<int>(source, at[1]) != 3 || ObjectFields.At<int>(source, at[2]) != 3 ||
                !ObjectFields.Holds(source, at[3], comparer))
                return false;
            // A tree moved into an empty set is that set's: its nodes do not point back to the set.
            var target = new SortedSet<int>(comparer);
            ObjectFields.At<object>(target, at[0]) = ObjectFields.At<object>(source, at[0]);
            ObjectFields.At<int>(target, at[1]) = 3;
            ObjectFields.At<int>(target, at[2])++;
            using var e = target.GetEnumerator();
            return e.MoveNext() && e.Current == 1 && e.MoveNext() && e.Current == 3 && e.MoveNext() &&
                   e.Current == 5 && !e.MoveNext() && target.Contains(3) && !target.Contains(2) && target.Max == 5 &&
                   target.Count == 3;
        }

        private static bool CheckSortedDictionary(int[] at)
        {
            var dictionary = new SortedDictionary<int, int> { { 9, 90 }, { 7, 70 } };
            // Every field a reference: a wrong guess reads null (keys and values are created on first use) or
            // another of its objects.
            return ObjectFields.At<object>(dictionary, at[1]) == null &&
                   ObjectFields.At<object>(dictionary, at[2]) == null &&
                   ObjectFields.At<object>(dictionary, at[0]) is SortedSet<KeyValuePair<int, int>> { Count: 2 } set &&
                   set.Min.Key == 7 && set.Max.Value == 90;
        }
    }

    /// <summary>
    /// The fields of the runtime's collections at the offsets <see cref="CollectionViews"/> found; use each only when
    /// its flag there is set.
    /// </summary>
    internal static class CollectionFields
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Items<T>(List<T> list) => ref ObjectFields.At<T[]>(list, CollectionViews.ListItems);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(List<T> list) => ref ObjectFields.At<int>(list, CollectionViews.ListSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Array<T>(Stack<T> stack) =>
            ref ObjectFields.At<T[]>(stack, CollectionViews.StackArray);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(Stack<T> stack) => ref ObjectFields.At<int>(stack, CollectionViews.StackSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(Stack<T> stack) =>
            ref ObjectFields.At<int>(stack, CollectionViews.StackVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T[] Array<T>(Queue<T> queue) =>
            ref ObjectFields.At<T[]>(queue, CollectionViews.QueueArray);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Head<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueHead);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Tail<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueTail);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<T>(Queue<T> queue) => ref ObjectFields.At<int>(queue, CollectionViews.QueueSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(Queue<T> queue) =>
            ref ObjectFields.At<int>(queue, CollectionViews.QueueVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TKey[] Keys<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<TKey[]>(list, CollectionViews.SortedListKeys);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TValue[] Values<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<TValue[]>(list, CollectionViews.SortedListValues);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Size<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.SortedListSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<int>(list, CollectionViews.SortedListVersion);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref IComparer<TKey> Comparer<TKey, TValue>(SortedList<TKey, TValue> list) =>
            ref ObjectFields.At<IComparer<TKey>>(list, CollectionViews.SortedListComparer);

        /// <summary>The root node of the set's red-black tree (an internal type).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref object Root<T>(SortedSet<T> set) => ref ObjectFields.At<object>(set, CollectionViews.SortedSetRoot);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Count<T>(SortedSet<T> set) => ref ObjectFields.At<int>(set, CollectionViews.SortedSetCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref int Version<T>(SortedSet<T> set) =>
            ref ObjectFields.At<int>(set, CollectionViews.SortedSetVersion);

        /// <summary>The set of the dictionary's pairs, ordered by key.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SortedSet<KeyValuePair<TKey, TValue>> Set<TKey, TValue>(
            SortedDictionary<TKey, TValue> dictionary) =>
            ObjectFields.At<SortedSet<KeyValuePair<TKey, TValue>>>(dictionary, CollectionViews.SortedDictionarySet);
    }
}
