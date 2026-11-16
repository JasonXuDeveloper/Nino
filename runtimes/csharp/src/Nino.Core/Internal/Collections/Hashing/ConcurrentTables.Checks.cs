using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    // The startup checks behind ConcurrentTables' layout and flags (static fields in ConcurrentTables.cs).
    internal static partial class ConcurrentTables
    {
        private static int At<TField>(object instance, ref TField field) => ObjectFields.OffsetOf(instance, ref field);

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
            Offsets? found = null;
            foreach (var candidate in candidates)
                if (ObjectTypes.Passes(() => (found = candidate(ProbeDictionary())) != null))
                    return found;
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
            if (ObjectFields.At<object>(tables, locksAt) is not object[] locks || locks.Length != 1 ||
                ObjectFields.At<object>(tables, countPerLockAt) is not int[] counts || counts.Length != 1 ||
                counts[0] != d.Count ||
                ObjectFields.At<object>(tables, bucketsAt) is not Array buckets || buckets.Length != budget)
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
                if (ObjectTypes.Passes(() =>
                        CheckChains<long, long>(ProbeDictionary(), mask, key => key, key => key * 3)))
                    return mask;
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
            object first = new(), second = new();
            var array = new[] { first, second };
            int at = ObjectFields.OffsetOf(array, ref array[0]);
            return at > 0 && ObjectTypes.Passes(() => ReferenceEquals(ObjectFields.At<object>(array, at), first) &&
                ReferenceEquals(Unsafe.Add(ref ObjectFields.At<object>(array, at), 1), second))
                ? at
                : 0;
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
