using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
#pragma warning disable CS0649, CS0169 // laid out only
    // The candidate layouts: mirror classes per runtime, and the fields by name on net10.0.
    internal static partial class CollectionViews
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

        private sealed class LinkedListCore
        {
            public object head;
            public int count;
            public int version;
            public object _siInfo;
        }

        private sealed class LinkedListFramework
        {
            public object head;
            public int count;
            public int version;
            public object _syncRoot;
            public object _siInfo;
        }

        private sealed class SortedDictionaryAny
        {
            public object _keys;
            public object _values;
            public object _set;
        }

        // HashSet<T> and Dictionary<TKey, TValue> on .NET 5+ (64-bit and 32-bit, which has no _fastModMultiplier);
        // on .NET Core 3.1, .NET Framework, Mono and Unity (the "old" ones) their entries are m_slots / entries, and
        // their bound is m_lastIndex / count.
        private sealed class HashSetCore64
        {
            public object _buckets;
            public object _entries;
            public ulong _fastModMultiplier;
            public int _count;
            public int _freeList;
            public int _freeCount;
            public int _version;
            public object _comparer;
        }

        private sealed class HashSetCore32
        {
            public object _buckets;
            public object _entries;
            public int _count;
            public int _freeList;
            public int _freeCount;
            public int _version;
            public object _comparer;
        }

        private sealed class HashSetOld
        {
            public object m_buckets;
            public object m_slots;
            public int m_count;
            public int m_lastIndex;
            public int m_freeList;
            public object m_comparer;
            public int m_version;
            public object m_siInfo;
        }

        private sealed class DictionaryCore64
        {
            public object _buckets;
            public object _entries;
            public ulong _fastModMultiplier;
            public int _count;
            public int _freeList;
            public int _freeCount;
            public int _version;
            public object _comparer;
            public object _keys;
            public object _values;
        }

        private sealed class DictionaryCore32
        {
            public object _buckets;
            public object _entries;
            public int _count;
            public int _freeList;
            public int _freeCount;
            public int _version;
            public object _comparer;
            public object _keys;
            public object _values;
        }

        private sealed class DictionaryOld
        {
            public object buckets;
            public object entries;
            public int count;
            public int version;
            public int freeList;
            public int freeCount;
            public object comparer;
            public object keys;
            public object values;
            public object _syncRoot;
        }
#pragma warning restore CS0649, CS0169

#if NET9_0_OR_GREATER
        /// <summary>
        /// The fields themselves, by name (UnsafeAccessor), in the net10.0 build of Nino.Core: the first candidate of
        /// the layouts whose fields all have types Nino can name, which holds however a later .NET orders or adds
        /// fields; a field renamed throws (MissingFieldException) and the mirror classes above are tried next. The
        /// offsets go through the same checks. Not in the net8.0 build: .NET 8 has accessors for closed generic types
        /// only (List&lt;int&gt;), and .NET 9 and later, which run that build too, match a field typed by the class's
        /// type parameter only through an open one, so those would throw on every start there. Not for
        /// <see cref="SortedSet{T}"/>, <see cref="SortedDictionary{TKey,TValue}"/>, <see cref="HashSet{T}"/> and
        /// <see cref="Dictionary{TKey,TValue}"/>, whose root, set or entries are of a private type. Every other
        /// runtime (the netstandard2.1 build: Mono, Unity, IL2CPP) uses the mirrors alone.
        /// </summary>
        private static class Fields<T>
        {
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
            public static extern ref T[] Items(List<T> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
            public static extern ref int Size(List<T> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
            public static extern ref int Version(List<T> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_array")]
            public static extern ref T[] Array(Stack<T> stack);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
            public static extern ref int Size(Stack<T> stack);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
            public static extern ref int Version(Stack<T> stack);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_array")]
            public static extern ref T[] Array(Queue<T> queue);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_head")]
            public static extern ref int Head(Queue<T> queue);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_tail")]
            public static extern ref int Tail(Queue<T> queue);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
            public static extern ref int Size(Queue<T> queue);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
            public static extern ref int Version(Queue<T> queue);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "head")]
            public static extern ref LinkedListNode<T> Head(LinkedList<T> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "count")]
            public static extern ref int Count(LinkedList<T> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "version")]
            public static extern ref int Version(LinkedList<T> list);
        }

        /// <summary><see cref="Fields{T}"/> of <see cref="SortedList{TKey,TValue}"/>.</summary>
        private static class Fields<TKey, TValue>
        {
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "keys")]
            public static extern ref TKey[] Keys(SortedList<TKey, TValue> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "values")]
            public static extern ref TValue[] Values(SortedList<TKey, TValue> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
            public static extern ref int Size(SortedList<TKey, TValue> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "version")]
            public static extern ref int Version(SortedList<TKey, TValue> list);

            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "comparer")]
            public static extern ref IComparer<TKey> Comparer(SortedList<TKey, TValue> list);
        }

        /// <summary>The layouts <see cref="Fields{T}"/> gives, the first candidates (net10.0 build).</summary>
        private static class ByName
        {
            public static readonly Func<ListOffsets> List = () =>
            {
                var l = new List<int>();
                return new ListOffsets(At(l, ref Fields<int>.Items(l)), At(l, ref Fields<int>.Size(l)),
                    At(l, ref Fields<int>.Version(l)));
            };

            public static readonly Func<ListOffsets> Stack = () =>
            {
                var s = new Stack<int>();
                return new ListOffsets(At(s, ref Fields<int>.Array(s)), At(s, ref Fields<int>.Size(s)),
                    At(s, ref Fields<int>.Version(s)));
            };

            public static readonly Func<QueueOffsets> Queue = () =>
            {
                var q = new Queue<int>();
                return new QueueOffsets(At(q, ref Fields<int>.Array(q)), At(q, ref Fields<int>.Head(q)),
                    At(q, ref Fields<int>.Tail(q)), At(q, ref Fields<int>.Size(q)), At(q, ref Fields<int>.Version(q)));
            };

            public static readonly Func<SortedListOffsets> SortedList = () =>
            {
                var l = new SortedList<int, int>();
                return new SortedListOffsets(At(l, ref Fields<int, int>.Keys(l)), At(l, ref Fields<int, int>.Values(l)),
                    At(l, ref Fields<int, int>.Size(l)), At(l, ref Fields<int, int>.Version(l)),
                    At(l, ref Fields<int, int>.Comparer(l)));
            };

            public static readonly Func<LinkedListOffsets> LinkedList = () =>
            {
                var l = new LinkedList<int>();
                return new LinkedListOffsets(At(l, ref Fields<int>.Head(l)), At(l, ref Fields<int>.Count(l)),
                    At(l, ref Fields<int>.Version(l)));
            };
        }

        /// <summary>
        /// For the tests: whether every layout <see cref="ByName"/> gives passes its check and equals the one the
        /// mirrors give, so the read and write paths are the same with either.
        /// </summary>
        internal static bool ByNameMatchesMirrors() =>
            Same(CheckList, ListMirrors, ByName.List) && Same(CheckStack, ListMirrors, ByName.Stack) &&
            Same(CheckQueue, QueueMirrors, ByName.Queue) &&
            Same(CheckSortedList, SortedListMirrors, ByName.SortedList) &&
            Same(CheckLinkedList, LinkedListMirrors, ByName.LinkedList);

        private static bool Same<TOffsets>(Func<TOffsets, bool> check, Func<TOffsets>[] mirrors, Func<TOffsets> byName)
            where TOffsets : struct =>
            Try(check, byName) is { } named && Find(check, mirrors) is { } mirrored && named.Equals(mirrored);
#endif
    }
}
