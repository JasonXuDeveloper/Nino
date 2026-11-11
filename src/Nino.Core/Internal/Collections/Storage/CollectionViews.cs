using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nino.Core.Internal
{
    /// <summary>
    /// Where the fields of <see cref="List{T}"/>, <see cref="Stack{T}"/>, <see cref="Queue{T}"/>,
    /// <see cref="LinkedList{T}"/>, <see cref="HashSet{T}"/>, <see cref="Dictionary{TKey,TValue}"/>,
    /// <see cref="SortedList{TKey,TValue}"/>, <see cref="SortedSet{T}"/> and
    /// <see cref="SortedDictionary{TKey,TValue}"/> are on this runtime, for <see cref="CollectionFields"/>. Each
    /// layout is one of the mirror classes (CollectionViews.Mirrors.cs), which declare the fields of a known
    /// implementation in its order (.NET; .NET Framework, Mono and Unity, which add a <c>_syncRoot</c>) and so are laid
    /// out as it is: the runtime places references first, in declaration order. The first one whose offsets hold what
    /// an instance with known contents holds is used: its int fields compared first, then its references as
    /// addresses, then read through the layout (CollectionViews.Checks.cs); otherwise the formatters use the public
    /// API. Static readonly: constants in Tier1 code.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    internal static partial class CollectionViews
    {
        /// <summary>Where <see cref="List{T}"/>'s and <see cref="Stack{T}"/>'s fields are (the same three).</summary>
        internal readonly struct ListOffsets
        {
            public readonly int Items, Size, Version;

            public ListOffsets(int items, int size, int version)
            {
                Items = items;
                Size = size;
                Version = version;
            }
        }

        /// <summary>Where <see cref="Queue{T}"/>'s fields are.</summary>
        internal readonly struct QueueOffsets
        {
            public readonly int Array, Head, Tail, Size, Version;

            public QueueOffsets(int array, int head, int tail, int size, int version)
            {
                Array = array;
                Head = head;
                Tail = tail;
                Size = size;
                Version = version;
            }
        }

        /// <summary>Where <see cref="SortedList{TKey,TValue}"/>'s fields are.</summary>
        internal readonly struct SortedListOffsets
        {
            public readonly int Keys, Values, Size, Version, Comparer;

            public SortedListOffsets(int keys, int values, int size, int version, int comparer)
            {
                Keys = keys;
                Values = values;
                Size = size;
                Version = version;
                Comparer = comparer;
            }
        }

        /// <summary>Where <see cref="SortedSet{T}"/>'s fields are.</summary>
        internal readonly struct SortedSetOffsets
        {
            public readonly int Root, Count, Version, Comparer;

            public SortedSetOffsets(int root, int count, int version, int comparer)
            {
                Root = root;
                Count = count;
                Version = version;
                Comparer = comparer;
            }
        }

        /// <summary>Where <see cref="SortedDictionary{TKey,TValue}"/>'s fields are (every one a reference).</summary>
        internal readonly struct SortedDictionaryOffsets
        {
            public readonly int Set, Keys, Values;

            public SortedDictionaryOffsets(int set, int keys, int values)
            {
                Set = set;
                Keys = keys;
                Values = values;
            }
        }

        /// <summary>Where <see cref="LinkedList{T}"/>'s fields are.</summary>
        internal readonly struct LinkedListOffsets
        {
            public readonly int Head, Count, Version;

            public LinkedListOffsets(int head, int count, int version)
            {
                Head = head;
                Count = count;
                Version = version;
            }
        }

        /// <summary>
        /// Where the fields of <see cref="HashSet{T}"/> and <see cref="Dictionary{TKey,TValue}"/> are: the entries, the
        /// bound (the entries in use, live or free) and the version; for <see cref="HashTables"/>, the buckets, the
        /// fast-mod multiplier (-1: none, the bucket is the hash modulo the bucket count) and the live count.
        /// </summary>
        internal readonly struct HashOffsets
        {
            public readonly int Entries, Bound, Version, Buckets, FastMod, Count;

            public HashOffsets(int entries, int bound, int version, int buckets, int fastMod, int count)
            {
                Entries = entries;
                Bound = bound;
                Version = version;
                Buckets = buckets;
                FastMod = fastMod;
                Count = count;
            }
        }

        // The mirror layouts of List<T> and Stack<T>, which have the same fields: items, size, version.
        private static readonly Func<ListOffsets>[] ListMirrors =
        {
            () =>
            {
                var m = new ListCore();
                return new ListOffsets(At(m, ref m._items), At(m, ref m._size), At(m, ref m._version));
            },
            () =>
            {
                var m = new ListFramework();
                return new ListOffsets(At(m, ref m._items), At(m, ref m._size), At(m, ref m._version));
            }
        };

        private static readonly ListOffsets? ListLayout = Find(CheckList, ListMirrors
#if NET9_0_OR_GREATER
            , ByName.List
#endif
        );

        public static readonly bool List = ListLayout.HasValue;
        public static readonly int ListItems = ListLayout?.Items ?? 0;
        public static readonly int ListSize = ListLayout?.Size ?? 0;

        // Stack<T>: array, size, version.
        private static readonly ListOffsets? StackLayout = Find(CheckStack, ListMirrors
#if NET9_0_OR_GREATER
            , ByName.Stack
#endif
        );

        public static readonly bool Stack = StackLayout.HasValue;
        public static readonly int StackArray = StackLayout?.Items ?? 0;
        public static readonly int StackSize = StackLayout?.Size ?? 0;
        public static readonly int StackVersion = StackLayout?.Version ?? 0;

        private static readonly Func<QueueOffsets>[] QueueMirrors =
        {
            () =>
            {
                var m = new QueueCore();
                return new QueueOffsets(At(m, ref m._array), At(m, ref m._head), At(m, ref m._tail), At(m, ref m._size),
                    At(m, ref m._version));
            },
            () =>
            {
                var m = new QueueFramework();
                return new QueueOffsets(At(m, ref m._array), At(m, ref m._head), At(m, ref m._tail), At(m, ref m._size),
                    At(m, ref m._version));
            }
        };

        private static readonly QueueOffsets? QueueLayout = Find(CheckQueue, QueueMirrors
#if NET9_0_OR_GREATER
            , ByName.Queue
#endif
        );

        public static readonly bool Queue = QueueLayout.HasValue;
        public static readonly int QueueArray = QueueLayout?.Array ?? 0;
        public static readonly int QueueHead = QueueLayout?.Head ?? 0;
        public static readonly int QueueTail = QueueLayout?.Tail ?? 0;
        public static readonly int QueueSize = QueueLayout?.Size ?? 0;
        public static readonly int QueueVersion = QueueLayout?.Version ?? 0;

        private static readonly Func<SortedListOffsets>[] SortedListMirrors =
        {
            () =>
            {
                var m = new SortedListCore();
                return new SortedListOffsets(At(m, ref m.keys), At(m, ref m.values), At(m, ref m._size),
                    At(m, ref m.version), At(m, ref m.comparer));
            },
            () =>
            {
                var m = new SortedListFramework();
                return new SortedListOffsets(At(m, ref m.keys), At(m, ref m.values), At(m, ref m._size),
                    At(m, ref m.version), At(m, ref m.comparer));
            }
        };

        private static readonly SortedListOffsets? SortedListLayout = Find(CheckSortedList, SortedListMirrors
#if NET9_0_OR_GREATER
            , ByName.SortedList
#endif
        );

        public static readonly bool SortedList = SortedListLayout.HasValue;
        public static readonly int SortedListKeys = SortedListLayout?.Keys ?? 0;
        public static readonly int SortedListValues = SortedListLayout?.Values ?? 0;
        public static readonly int SortedListSize = SortedListLayout?.Size ?? 0;
        public static readonly int SortedListVersion = SortedListLayout?.Version ?? 0;
        public static readonly int SortedListComparer = SortedListLayout?.Comparer ?? 0;

        private static readonly SortedSetOffsets? SortedSetLayout = Find(CheckSortedSet, new Func<SortedSetOffsets>[]
        {
            () =>
            {
                var m = new SortedSetCore();
                return new SortedSetOffsets(At(m, ref m.root), At(m, ref m.count), At(m, ref m.version),
                    At(m, ref m.comparer));
            },
            () =>
            {
                var m = new SortedSetFramework();
                return new SortedSetOffsets(At(m, ref m.root), At(m, ref m.count), At(m, ref m.version),
                    At(m, ref m.comparer));
            }
        });

        public static readonly bool SortedSet = SortedSetLayout.HasValue;
        public static readonly int SortedSetRoot = SortedSetLayout?.Root ?? 0;
        public static readonly int SortedSetCount = SortedSetLayout?.Count ?? 0;
        public static readonly int SortedSetVersion = SortedSetLayout?.Version ?? 0;

        // SortedDictionary<TKey, TValue>: the set of its pairs (every field a reference: no int to compare first).
        private static readonly SortedDictionaryOffsets? SortedDictionaryLayout = SortedSet
            ? Find(CheckSortedDictionary, new Func<SortedDictionaryOffsets>[]
            {
                () =>
                {
                    var m = new SortedDictionaryAny();
                    return new SortedDictionaryOffsets(At(m, ref m._set), At(m, ref m._keys), At(m, ref m._values));
                }
            })
            : null;

        public static readonly bool SortedDictionary = SortedDictionaryLayout.HasValue;
        public static readonly int SortedDictionarySet = SortedDictionaryLayout?.Set ?? 0;

        // LinkedList<T>; its nodes: LinkedNode<T> (list, next, prev, item, the same everywhere).
        private static readonly Func<LinkedListOffsets>[] LinkedListMirrors =
        {
            () =>
            {
                var m = new LinkedListCore();
                return new LinkedListOffsets(At(m, ref m.head), At(m, ref m.count), At(m, ref m.version));
            },
            () =>
            {
                var m = new LinkedListFramework();
                return new LinkedListOffsets(At(m, ref m.head), At(m, ref m.count), At(m, ref m.version));
            }
        };

        private static readonly LinkedListOffsets? LinkedListLayout = Find(CheckLinkedList, LinkedListMirrors
#if NET9_0_OR_GREATER
            , ByName.LinkedList
#endif
        );

        public static readonly bool LinkedList = LinkedListLayout.HasValue;
        public static readonly int LinkedListHead = LinkedListLayout?.Head ?? 0;
        public static readonly int LinkedListCount = LinkedListLayout?.Count ?? 0;
        public static readonly int LinkedListVersion = LinkedListLayout?.Version ?? 0;

        private static readonly HashOffsets? HashSetLayout = Find(CheckHashSet, new Func<HashOffsets>[]
        {
            () =>
            {
                var m = new HashSetCore64();
                return new HashOffsets(At(m, ref m._entries), At(m, ref m._count), At(m, ref m._version),
                    At(m, ref m._buckets), At(m, ref m._fastModMultiplier), At(m, ref m._count));
            },
            () =>
            {
                var m = new HashSetCore32();
                return new HashOffsets(At(m, ref m._entries), At(m, ref m._count), At(m, ref m._version),
                    At(m, ref m._buckets), -1, At(m, ref m._count));
            },
            () =>
            {
                var m = new HashSetOld();
                return new HashOffsets(At(m, ref m.m_slots), At(m, ref m.m_lastIndex), At(m, ref m.m_version),
                    At(m, ref m.m_buckets), -1, At(m, ref m.m_count));
            }
        });

        public static readonly bool HashSet = HashSetLayout.HasValue;
        public static readonly int HashSetEntries = HashSetLayout?.Entries ?? 0;
        public static readonly int HashSetBound = HashSetLayout?.Bound ?? 0;

        /// <summary>The offsets above, or null: read once by <see cref="HashTables"/>.</summary>
        internal static HashOffsets? HashSetOffsets => HashSetLayout;

        // Dictionary<TKey, TValue>: as HashSet<T>'s.
        private static readonly HashOffsets? DictionaryLayout = Find(CheckDictionary, new Func<HashOffsets>[]
        {
            () =>
            {
                var m = new DictionaryCore64();
                return new HashOffsets(At(m, ref m._entries), At(m, ref m._count), At(m, ref m._version),
                    At(m, ref m._buckets), At(m, ref m._fastModMultiplier), At(m, ref m._count));
            },
            () =>
            {
                var m = new DictionaryCore32();
                return new HashOffsets(At(m, ref m._entries), At(m, ref m._count), At(m, ref m._version),
                    At(m, ref m._buckets), -1, At(m, ref m._count));
            },
            () =>
            {
                var m = new DictionaryOld();
                return new HashOffsets(At(m, ref m.entries), At(m, ref m.count), At(m, ref m.version),
                    At(m, ref m.buckets), -1, At(m, ref m.count));
            }
        });

        public static readonly bool Dictionary = DictionaryLayout.HasValue;
        public static readonly int DictionaryEntries = DictionaryLayout?.Entries ?? 0;
        public static readonly int DictionaryBound = DictionaryLayout?.Bound ?? 0;
        internal static HashOffsets? DictionaryOffsets => DictionaryLayout;

        private static int At<TField>(object instance, ref TField field) => ObjectFields.OffsetOf(instance, ref field);

        /// <summary>The offsets of the first candidate layout that <paramref name="check"/> accepts, or null.</summary>
        /// <remarks><paramref name="byName"/> (<see cref="ByName"/>), when given, is tried first.</remarks>
        private static TOffsets? Find<TOffsets>(Func<TOffsets, bool> check, Func<TOffsets>[] mirrors,
            Func<TOffsets> byName = null)
            where TOffsets : struct
        {
            if (byName != null && Try(check, byName) is { } named)
                return named;
            foreach (var mirror in mirrors)
            {
                if (Try(check, mirror) is { } offsets)
                    return offsets;
            }

            return null;
        }

        private static TOffsets? Try<TOffsets>(Func<TOffsets, bool> check, Func<TOffsets> candidate)
            where TOffsets : struct
        {
            TOffsets offsets = default;
            return ObjectTypes.Passes(() => check(offsets = candidate())) ? offsets : null;
        }
    }
}
