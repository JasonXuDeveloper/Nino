using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if !NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// Hash flooding for <see cref="ConcurrentDictionary{TKey,TValue}"/>, as <see cref="HashTables"/> does it for
    /// <see cref="Dictionary{TKey,TValue}"/>: more than <see cref="NinoSettings.MaxHashCollisions"/> other keys in the
    /// bucket of a key read from the data are invalid data (keys built to collide, by hash code or by bucket, would make
    /// reading quadratic). The dictionary keeps its buckets in a private table, a linked list of nodes per bucket; where
    /// those are is found at startup, from mirror classes that declare the fields of each known implementation in its
    /// order (.NET 8 and later, Mono and Unity, .NET Framework), checked against a dictionary with known contents (its
    /// int fields first, then its comparer as an address, then its chains read through the layout). The bucket is the
    /// hash code modulo the bucket count, masked to 31 bits or not (found at startup too). Where no layout matches,
    /// nothing is counted, and a new dictionary is built by its constructor from the pairs, as before.
    /// <para>
    /// A new dictionary is built here (<see cref="Builder{TKey,TValue}"/>), on every runtime where the layout and
    /// <see cref="ObjectTypes"/> are found and a dictionary built this way passes a check through the public API at
    /// startup: made empty by its constructor from a collection that only reports the pairs' count (sized and
    /// configured as before), then its nodes made as <see cref="ObjectTypes"/> makes objects and put at the heads of
    /// their buckets as the constructor puts them, without locks, each bucket's chain searched for the key and counted
    /// first. No public path gives a point between two of the constructor's adds to count at, and <c>TryAdd</c> takes
    /// a lock per pair (twice as slow on .NET 8). Built here, it is faster than the constructor on every runtime (no
    /// enumerator, lock or resize check per pair; Mono's constructor starts at 31 buckets and grows). Where the layout
    /// is found but the check fails, pairs are added through <c>TryAdd</c> and each bucket is walked after its add, as
    /// for a dictionary read into.
    /// </para>
    /// <para>
    /// String keys are left to the runtimes that protect the table themselves (.NET and .NET Framework switch to a
    /// randomized comparer after 100 collisions), as <see cref="HashTables"/> leaves them.
    /// </para>
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    [SuppressMessage("ReSharper", "UnusedMember.Local")]
    internal static class ConcurrentTables
    {
#pragma warning disable CS0649, CS0169 // laid out only
        // ConcurrentDictionary<TKey, TValue>: .NET 10 and later, .NET 8 and 9, Mono and Unity (corefx's), .NET
        // Framework (the reference source's). Its own fields do not depend on TKey and TValue.
        private sealed class DictionaryCore10
        {
            public object _tables;
            public int _budget;
            public bool _growLockArray;
            public bool _comparerIsDefaultForClasses;
            public int _initialCapacity;
        }

        private sealed class DictionaryCore8
        {
            public object _tables;
            public int _budget;
            public bool _growLockArray;
            public bool _comparerIsDefaultForClasses;
        }

        private sealed class DictionaryMono
        {
            public object _tables;
            public object _comparer;
            public bool _growLockArray;
            public int _budget;
            public object _serializationArray;
            public int _serializationConcurrencyLevel;
            public int _serializationCapacity;
        }

        private sealed class DictionaryFramework
        {
            public object m_tables;
            public object m_comparer;
            public bool m_growLockArray;
            public int m_keyRehashCount;
            public int m_budget;
            public object m_serializationArray;
            public int m_serializationConcurrencyLevel;
            public int m_serializationCapacity;
        }

        // Its table: buckets (an array of nodes, or of structs holding one), locks, a count per lock, and on .NET and
        // .NET Framework the comparer.
        private sealed class TablesCore
        {
            public object _comparer;
            public object _buckets;
            public ulong _fastModBucketsMultiplier;
            public object _locks;
            public object _countPerLock;
        }

        private sealed class TablesMono
        {
            public object _buckets;
            public object _locks;
            public object _countPerLock;
        }

        private sealed class TablesFramework
        {
            public object m_buckets;
            public object m_locks;
            public object m_countPerLock;
            public object m_comparer;
        }
#pragma warning restore CS0649, CS0169

        /// <summary>
        /// A node of a bucket's chain, in the order of every known implementation (<c>_key</c>, <c>_value</c>,
        /// <c>_next</c>, <c>_hashcode</c>; .NET Framework's <c>m_</c> names): laid out as the real one, for the same
        /// <typeparamref name="TKey"/> and <typeparamref name="TValue"/>.
        /// </summary>
        private sealed class Node<TKey, TValue>
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public TKey _key;
            public TValue _value;
            public object _next;
            public int _hashcode;
#pragma warning restore CS0649
        }

        /// <summary>Where the fields are: offsets in the dictionary and its table; -1 for a comparer not there.</summary>
        private readonly struct Offsets
        {
            public readonly int Tables, DictionaryComparer, GrowLockArray, Buckets, Locks, CountPerLock, TablesComparer;

            public Offsets(int tables, int dictionaryComparer, int growLockArray, int buckets, int locks,
                int countPerLock, int tablesComparer)
            {
                Tables = tables;
                GrowLockArray = growLockArray;
                DictionaryComparer = dictionaryComparer;
                Buckets = buckets;
                Locks = locks;
                CountPerLock = countPerLock;
                TablesComparer = tablesComparer;
            }
        }

        // First, in textual order: the checks below use it.
        /// <summary>The comparer of every probe dictionary, found in the layouts by its address.</summary>
        private static readonly ProbeComparer Probe = new ProbeComparer();

        private static readonly Offsets? Layout = Find();
        private static readonly int TablesAt = Layout?.Tables ?? 0;
        private static readonly int DictionaryComparerAt = Layout?.DictionaryComparer ?? -1;
        private static readonly int BucketsAt = Layout?.Buckets ?? 0;
        private static readonly int LocksAt = Layout?.Locks ?? 0;
        private static readonly int CountPerLockAt = Layout?.CountPerLock ?? 0;
        private static readonly int TablesComparerAt = Layout?.TablesComparer ?? -1;

        // Hash codes as they are (.NET, -1) or masked to 31 bits (Mono, .NET Framework); 0: not found.
        private static readonly int Mask = Layout == null ? 0 : FindMask();

        /// <summary>Whether the chains of value-type nodes can be walked on this runtime.</summary>
        private static readonly bool Chains = Mask != 0;

        /// <summary>Whether the chains of nodes with references can be walked (checked separately, as their layout is).</summary>
        private static readonly bool ReferenceChains = Chains && Check(CheckReferenceChains);

        // String keys of tables the runtime protects itself: .NET's and .NET Framework's ConcurrentDictionary switch to
        // a randomized comparer after 100 collisions. Mono and IL2CPP are not counted on.
#if NET8_0_OR_GREATER
        private const bool GuardsStrings = true;
#else
        private static readonly bool GuardsStrings =
            RuntimeInformation.FrameworkDescription.StartsWith(".NET", StringComparison.Ordinal);
#endif

        /// <summary>Where an array's first element is (as <see cref="ObjectFields.At{TField}"/> counts).</summary>
        private static readonly int ElementsAt = FindElements();

        // How the constructor from a collection configures a new dictionary: its lock count, whether it adds locks as
        // the table grows, and whether it sizes the buckets for the collection's count (.NET) or starts at 31 and
        // grows as it adds (Mono and Unity, .NET Framework).
        private static readonly ConcurrentDictionary<long, long> Configured =
            Layout == null ? null : new ConcurrentDictionary<long, long>(new Empty<long, long>(1000));
        private static readonly int DefaultLocks = Configured == null ? 0 : LocksOf(Configured);
        private static readonly bool GrowsLocks =
            Configured != null && ObjectFields.At<bool>(Configured, Layout.Value.GrowLockArray);
        private static readonly bool SizedByCount = Configured != null && BucketsOf(Configured).Length >= 1000;

        /// <summary>
        /// Whether a new dictionary is built here (<see cref="Builder{TKey,TValue}"/>): its nodes made as
        /// <see cref="ObjectTypes"/> makes objects, checked at startup through the public API.
        /// </summary>
        private static readonly bool Builds = Mask != 0 && ElementsAt != 0 && ObjectTypes.Usable && Check(CheckBuilt);

        /// <summary>What is decided from the key and value types alone, once per pair of types.</summary>
        public static class Plan<TKey, TValue>
        {
            /// <summary>Whether a key the dictionary added has its bucket walked (<see cref="Walk{TKey,TValue}"/>).</summary>
            public static readonly bool Walks = !(typeof(TKey) == typeof(string) && GuardsStrings) &&
                (NinoUnsafe.IsReferenceOrContainsReferences<TKey>() ||
                 NinoUnsafe.IsReferenceOrContainsReferences<TValue>()
                    ? ReferenceChains
                    : Chains);

            /// <summary>Whether a new dictionary is built here (<see cref="Builder{TKey,TValue}"/>).</summary>
            public static readonly bool Builds = Walks && ConcurrentTables.Builds;

            /// <summary>The type word of the nodes, taken from the first node a dictionary added itself.</summary>
            internal static IntPtr NodeType;
        }

        /// <summary>
        /// A new, empty dictionary configured as its constructor from <paramref name="count"/> pairs configures it,
        /// sized for them: where that constructor starts at 31 buckets and grows as it adds (Mono, .NET Framework),
        /// made with as many buckets as pairs, as many locks as it takes and its lock growth set as it sets it.
        /// </summary>
        public static ConcurrentDictionary<TKey, TValue> Create<TKey, TValue>(int count)
        {
            if (SizedByCount || count <= 31)
                return new ConcurrentDictionary<TKey, TValue>(new Empty<TKey, TValue>(count));
            var dictionary = new ConcurrentDictionary<TKey, TValue>(DefaultLocks, count | 1);
            ObjectFields.At<bool>(dictionary, Layout.GetValueOrDefault().GrowLockArray) = GrowsLocks;
            return dictionary;
        }

        /// <summary>A collection that reports a count but holds nothing (<see cref="Create{TKey,TValue}"/>).</summary>
        private sealed class Empty<TKey, TValue> : ICollection<KeyValuePair<TKey, TValue>>
        {
            public Empty(int count) => Count = count;
            public int Count { get; }
            public bool IsReadOnly => true;

            public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
            {
                yield break;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) { }
            public void Add(KeyValuePair<TKey, TValue> item) => throw new NotSupportedException();
            public void Clear() => throw new NotSupportedException();
            public bool Contains(KeyValuePair<TKey, TValue> item) => false;
            public bool Remove(KeyValuePair<TKey, TValue> item) => throw new NotSupportedException();
        }

        private static object TablesOf(object dictionary) => ObjectFields.At<object>(dictionary, TablesAt);

        internal static Array BucketsOf(object dictionary) =>
            Unsafe.As<Array>(ObjectFields.At<object>(TablesOf(dictionary), BucketsAt));

        private static int LocksOf(object dictionary) =>
            Unsafe.As<object[]>(ObjectFields.At<object>(TablesOf(dictionary), LocksAt)).Length;

        /// <summary>
        /// Fills a new dictionary (<see cref="Create{TKey,TValue}"/>), not seen by any other thread, as its constructor
        /// from a collection fills it, without locks: each pair's node put at the head of its bucket and counted for its
        /// lock, after the bucket's chain is searched for the key and counted (more than
        /// <see cref="NinoSettings.MaxHashCollisions"/> other keys: <see cref="NinoDataException"/>). The table is not
        /// grown: it has a bucket per pair; a lock over its budget makes the next add grow it, as after the constructor.
        /// </summary>
        public readonly struct Builder<TKey, TValue>
        {
            private readonly object _buckets;
            private readonly int[] _counts;
            private readonly IEqualityComparer<TKey> _comparer;
            private readonly IntPtr _nodeType;
            private readonly uint _length;

            public Builder(ConcurrentDictionary<TKey, TValue> dictionary, IntPtr nodeType)
            {
                object tables = TablesOf(dictionary);
                _buckets = ObjectFields.At<object>(tables, BucketsAt);
                _counts = Unsafe.As<int[]>(ObjectFields.At<object>(tables, CountPerLockAt));
                _comparer = Comparer<TKey>(dictionary);
                _nodeType = nodeType;
                _length = (uint)Unsafe.As<Array>(_buckets).Length;
            }

            /// <summary>Adds the pair; false when the dictionary holds the key already.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Add(TKey key, TValue value)
            {
                var comparer = _comparer;
                int hash = comparer == null ? EqualityComparer<TKey>.Default.GetHashCode(key) : comparer.GetHashCode(key);
                uint bucket = (uint)(hash & Mask) % _length;
                ref object head = ref Unsafe.Add(ref ObjectFields.At<object>(_buckets, ElementsAt), (int)bucket);
                int count = 0;
                for (object node = head; node != null; node = Unsafe.As<Node<TKey, TValue>>(node)._next)
                {
                    var other = Unsafe.As<Node<TKey, TValue>>(node);
                    if (other._hashcode == hash && (comparer == null
                            ? EqualityComparer<TKey>.Default.Equals(other._key, key)
                            : comparer.Equals(other._key, key)))
                        return false;
                    count++;
                }

                if (count > NinoLimits.MaxHashCollisions)
                    NinoThrow.HashFlood(typeof(ConcurrentDictionary<TKey, TValue>));
                var made = new Node<TKey, TValue> { _key = key, _value = value, _next = head, _hashcode = hash };
                ObjectTypes.Retype(made, _nodeType);
                head = made;
                _counts[(int)(bucket % (uint)_counts.Length)]++;
                return true;
            }
        }

        /// <summary>
        /// The type word of the nodes of <paramref name="dictionary"/>, which holds only <paramref name="key"/>,
        /// added through its public API.
        /// </summary>
        public static IntPtr TypeOfNodes<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary, TKey key) =>
            ObjectTypes.TypeOf(Head(dictionary, key));

        /// <summary>
        /// After <paramref name="dictionary"/> added <paramref name="key"/>, read from the data: throws when more than
        /// <see cref="NinoSettings.MaxHashCollisions"/> other keys share its bucket (<see cref="Plan{TKey,TValue}.Walks"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Added<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary, TKey key)
        {
            if (Plan<TKey, TValue>.Walks)
                Walk(dictionary, key);
        }

        /// <summary>Walks the bucket of <paramref name="key"/>, which the dictionary added.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Walk<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary, TKey key)
        {
            object node = Head(dictionary, key);
            // The key's own node included.
            int left = NinoLimits.MaxHashCollisions + 1;
            for (; node != null; node = Unsafe.As<Node<TKey, TValue>>(node)._next)
                if (--left < 0)
                    NinoThrow.HashFlood(dictionary.GetType());
        }

        /// <summary>The dictionary's comparer (null: <see cref="EqualityComparer{T}.Default"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IEqualityComparer<TKey> Comparer<TKey>(object dictionary) =>
            TablesComparerAt >= 0
                ? ObjectFields.At<IEqualityComparer<TKey>>(TablesOf(dictionary), TablesComparerAt)
                : ObjectFields.At<IEqualityComparer<TKey>>(dictionary, DictionaryComparerAt);

        /// <summary>The first node of the bucket <paramref name="key"/> is in.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object Head<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary, TKey key)
        {
            var comparer = Comparer<TKey>(dictionary);
            int hash = comparer == null ? EqualityComparer<TKey>.Default.GetHashCode(key) : comparer.GetHashCode(key);
            // An array of nodes, or of structs holding one (VolatileNode): either way one reference per bucket.
            var buckets = Unsafe.As<object[]>(ObjectFields.At<object>(TablesOf(dictionary), BucketsAt));
            return buckets[(int)((uint)(hash & Mask) % (uint)buckets.Length)];
        }

        // ----- startup checks --------------------------------------------------------------------------------------

        private static int At<TField>(object instance, ref TField field) => ObjectFields.OffsetOf(instance, ref field);

        private static bool Check(Func<bool> check)
        {
            try
            {
                return check();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Hash codes the probe's keys are given: key / 8 (with a negative one), so that chains form.</summary>
        private sealed class ProbeComparer : IEqualityComparer<long>, IEqualityComparer<object>
        {
            public bool Equals(long x, long y) => x == y;
            public int GetHashCode(long value) => (int)(value >> 3);
            public new bool Equals(object x, object y) => ReferenceEquals(x, y) || x is long a && y is long b && a == b;
            public int GetHashCode(object value) => value is long l ? GetHashCode(l) : 0;
        }

        private const int ProbeCapacity = 50;

        // Keys 0 to 39 and -16 to -9: chains of 8 keys each (hash codes 0 to 4, and -2 and -1).
        private static long[] ProbeKeys()
        {
            var keys = new long[48];
            for (int i = 0; i < 40; i++)
                keys[i] = i;
            for (int i = 0; i < 8; i++)
                keys[40 + i] = -16 + i;
            return keys;
        }

        /// <summary>
        /// A dictionary of one lock (so that its budget is its bucket count) with the probe's comparer and keys, which
        /// the candidate layouts are checked against.
        /// </summary>
        private static ConcurrentDictionary<long, long> ProbeDictionary()
        {
            var dictionary = new ConcurrentDictionary<long, long>(1, ProbeCapacity, Probe);
            foreach (long key in ProbeKeys())
                dictionary.TryAdd(key, key * 3);
            return dictionary;
        }

        private static Offsets? Find()
        {
            var candidates = new Func<ConcurrentDictionary<long, long>, Offsets?>[]
            {
                d =>
                {
                    var m = new DictionaryCore10();
                    var t = new TablesCore();
                    // Ints first: one lock, so the budget is the bucket count, which is also the initial capacity.
                    int budget = ObjectFields.At<int>(d, At(m, ref m._budget));
                    if (budget < ProbeCapacity || budget != ObjectFields.At<int>(d, At(m, ref m._initialCapacity)) ||
                        ObjectFields.At<bool>(d, At(m, ref m._growLockArray)))
                        return null;
                    return Tables(d, At(m, ref m._tables), -1, At(m, ref m._growLockArray), budget,
                        At(t, ref t._buckets), At(t, ref t._locks),
                        At(t, ref t._countPerLock), At(t, ref t._comparer));
                },
                d =>
                {
                    var m = new DictionaryCore8();
                    var t = new TablesCore();
                    int budget = ObjectFields.At<int>(d, At(m, ref m._budget));
                    if (budget < ProbeCapacity || budget > 4 * ProbeCapacity ||
                        ObjectFields.At<bool>(d, At(m, ref m._growLockArray)))
                        return null;
                    return Tables(d, At(m, ref m._tables), -1, At(m, ref m._growLockArray), budget,
                        At(t, ref t._buckets), At(t, ref t._locks),
                        At(t, ref t._countPerLock), At(t, ref t._comparer));
                },
                d =>
                {
                    var m = new DictionaryMono();
                    var t = new TablesMono();
                    int budget = ObjectFields.At<int>(d, At(m, ref m._budget));
                    if (budget < ProbeCapacity || budget > 4 * ProbeCapacity ||
                        ObjectFields.At<bool>(d, At(m, ref m._growLockArray)) ||
                        ObjectFields.At<int>(d, At(m, ref m._serializationCapacity)) != 0 ||
                        !ObjectFields.Holds(d, At(m, ref m._comparer), Probe))
                        return null;
                    return Tables(d, At(m, ref m._tables), At(m, ref m._comparer), At(m, ref m._growLockArray),
                        budget, At(t, ref t._buckets),
                        At(t, ref t._locks), At(t, ref t._countPerLock), -1);
                },
                d =>
                {
                    var m = new DictionaryFramework();
                    var t = new TablesFramework();
                    int budget = ObjectFields.At<int>(d, At(m, ref m.m_budget));
                    if (budget < ProbeCapacity || budget > 4 * ProbeCapacity ||
                        ObjectFields.At<bool>(d, At(m, ref m.m_growLockArray)) ||
                        ObjectFields.At<int>(d, At(m, ref m.m_serializationCapacity)) != 0)
                        return null;
                    // The comparer is kept in the table (and, before .NET Framework 4.5, in the dictionary as well).
                    return Tables(d, At(m, ref m.m_tables), -1, At(m, ref m.m_growLockArray), budget,
                        At(t, ref t.m_buckets), At(t, ref t.m_locks),
                        At(t, ref t.m_countPerLock), At(t, ref t.m_comparer));
                },
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    if (candidate(ProbeDictionary()) is { } offsets)
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
        /// The rest of a candidate whose int fields matched: its table, found where the comparer is (compared as an
        /// address), and the table's arrays: one lock, a count of every key, and as many buckets as the budget.
        /// </summary>
        private static Offsets? Tables(ConcurrentDictionary<long, long> d, int tablesAt, int dictionaryComparerAt,
            int growLockArrayAt, int budget, int bucketsAt, int locksAt, int countPerLockAt, int tablesComparerAt)
        {
            object tables = ObjectFields.At<object>(d, tablesAt);
            if (tables == null || tables.GetType().DeclaringType != typeof(ConcurrentDictionary<,>))
                return null;
            if (tablesComparerAt >= 0 && !ObjectFields.Holds(tables, tablesComparerAt, Probe))
                return null;
            if (!(ObjectFields.At<object>(tables, locksAt) is object[] locks) || locks.Length != 1 ||
                !(ObjectFields.At<object>(tables, countPerLockAt) is int[] counts) || counts.Length != 1 ||
                counts[0] != d.Count ||
                !(ObjectFields.At<object>(tables, bucketsAt) is Array buckets) || buckets.Length != budget)
                return null;
            var offsets = new Offsets(tablesAt, dictionaryComparerAt, growLockArrayAt, bucketsAt, locksAt, countPerLockAt,
                tablesComparerAt);
            // The comparer read through the layout is the dictionary's.
            var read = tablesComparerAt >= 0
                ? ObjectFields.At<object>(tables, tablesComparerAt)
                : ObjectFields.At<object>(d, dictionaryComparerAt);
            return read is ProbeComparer ? offsets : (Offsets?)null;
        }

        /// <summary>The mask the probe's chains are found with (its negative hash codes tell the two apart), or 0.</summary>
        private static int FindMask()
        {
            foreach (int mask in new[] { -1, int.MaxValue })
            {
                try
                {
                    if (CheckChains<long, long>(ProbeDictionary(), mask, key => key, key => key * 3))
                        return mask;
                }
                catch
                {
                    // Not this hashing.
                }
            }

            return 0;
        }

        /// <summary>The chains of a dictionary with references in its nodes, read through <see cref="Node{TKey,TValue}"/>.</summary>
        private static bool CheckReferenceChains()
        {
            var dictionary = new ConcurrentDictionary<object, object>(1, ProbeCapacity, Probe);
            var keys = new Dictionary<long, object>();
            foreach (long key in ProbeKeys())
            {
                object boxed = key;
                keys[key] = boxed;
                dictionary.TryAdd(boxed, key * 3);
            }

            return CheckChains<object, object>(dictionary, Mask, key => keys[key], key => key * 3);
        }

        /// <summary>
        /// Whether every probe key's bucket, found by <see cref="Head{TKey,TValue}"/> with <paramref name="mask"/>,
        /// holds exactly the keys with its hash code (8 each, the key among them), with their values and hash codes.
        /// </summary>
        private static bool CheckChains<TKey, TValue>(ConcurrentDictionary<TKey, TValue> dictionary, int mask,
            Func<long, TKey> key, Func<long, TValue> value)
        {
            var comparer = Probe;
            foreach (long probe in ProbeKeys())
            {
                object tables = ObjectFields.At<object>(dictionary, TablesAt);
                var buckets = Unsafe.As<object[]>(ObjectFields.At<object>(tables, BucketsAt));
                int hash = comparer.GetHashCode(probe);
                object node = buckets[(int)((uint)(hash & mask) % (uint)buckets.Length)];
                int found = 0, mine = 0;
                for (; node != null && found <= 8; node = Unsafe.As<Node<TKey, TValue>>(node)._next)
                {
                    var n = Unsafe.As<Node<TKey, TValue>>(node);
                    long id = (object)n._key is long l ? l : long.MinValue; // a long key, or one boxed
                    if (id == long.MinValue || n._hashcode != hash || comparer.GetHashCode(id) != hash ||
                        !EqualityComparer<TValue>.Default.Equals(n._value, value(id)))
                        return false;
                    found++;
                    if (EqualityComparer<TKey>.Default.Equals(n._key, key(probe)))
                        mine++;
                }

                if (found != 8 || mine != 1)
                    return false;
            }

            return true;
        }

        /// <summary>The offset of an array's first element, or 0 (not found).</summary>
        private static int FindElements()
        {
            try
            {
                object first = new object(), second = new object();
                var array = new[] { first, second };
                int at = ObjectFields.OffsetOf(array, ref array[0]);
                return at > 0 && ReferenceEquals(ObjectFields.At<object>(array, at), first) &&
                       ReferenceEquals(Unsafe.Add(ref ObjectFields.At<object>(array, at), 1), second)
                    ? at
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Dictionaries built by <see cref="Builder{TKey,TValue}"/>, without and with references in their nodes, work
        /// through the public API: lookups, enumeration, removes, and adds that grow the table.
        /// </summary>
        private static bool CheckBuilt() =>
            CheckBuilt(key => key, key => key * 3) &&
            CheckBuilt(key => (object)key, key => (key * 3).ToString(CultureInfo.InvariantCulture));

        private static bool CheckBuilt<TKey, TValue>(Func<long, TKey> key, Func<long, TValue> value)
        {
            const int count = 300;
            var values = EqualityComparer<TValue>.Default;
            var dictionary = Create<TKey, TValue>(count);
            if (!dictionary.TryAdd(key(0), value(0)))
                return false;
            var builder = new Builder<TKey, TValue>(dictionary, TypeOfNodes(dictionary, key(0)));
            for (long i = 1; i < count; i++)
                if (!builder.Add(key(i), value(i)))
                    return false;
            if (builder.Add(key(7), value(7)) || dictionary.Count != count)
                return false;
            int seen = 0;
            foreach (var pair in dictionary)
                if (values.Equals(pair.Value, value(Convert.ToInt64(pair.Key, CultureInfo.InvariantCulture))))
                    seen++;
            if (seen != count)
                return false;
            for (long i = 0; i < count; i += 3)
                if (!dictionary.TryRemove(key(i), out var removed) || !values.Equals(removed, value(i)))
                    return false;
            for (long i = count; i < 4 * count; i++)
                if (!dictionary.TryAdd(key(i), value(i)))
                    return false;
            for (long i = 0; i < 4 * count; i++)
            {
                bool has = dictionary.TryGetValue(key(i), out var found);
                if (has != (i >= count || i % 3 != 0) || has && !values.Equals(found, value(i)))
                    return false;
            }

            return dictionary.Count == 4 * count - (count + 2) / 3 && BucketsOf(dictionary).Length > count;
        }
    }
}
