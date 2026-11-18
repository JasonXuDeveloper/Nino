using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    // The startup checks behind HashTables' flags (static fields in HashTables.cs).
    internal static partial class HashTables
    {
        // Hash codes that fill several buckets twice, negative ones (masked on the old implementations) and longs whose
        // halves cancel out.
        private static long[] CheckValues()
        {
            var values = new long[64];
            for (int i = 0; i < 60; i++)
                values[i] = i * 37L - 900;
            values[60] = long.MinValue;
            values[61] = long.MaxValue;
            values[62] = 1L << 33;
            values[63] = (1L << 32) + 1;
            return values;
        }

        private static byte[] Bytes(long[] values, int pairSize)
        {
            var bytes = new byte[values.Length * pairSize];
            for (int i = 0; i < values.Length; i++)
            {
                NinoUnsafe.Write(ref bytes[i * pairSize], values[i]);
                if (pairSize > 8)
                    NinoUnsafe.Write(ref bytes[i * pairSize + 8], i);
            }

            return bytes;
        }

        private static bool CheckSet(int mask, int bias)
        {
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            var values = CheckValues();
            var set = new HashSet<long>();
            set.EnsureCapacity(values.Length);
            if (ObjectFields.At<object>(set, SetBuckets) is not int[] ||
                !Fill(set, ref Bytes(values, 8)[0], values.Length, 8, mask, bias) || set.Count != values.Length)
                return false;
            int index = 0;
            foreach (long value in set)
                if (value != values[index++])
                    return false;
            foreach (long value in values)
                if (!set.Contains(value))
                    return false;
            if (set.Contains(5) || !set.SetEquals(new HashSet<long>(values)) || !set.Add(5) || set.Add(values[3]) ||
                !set.Remove(values[0]) || set.Contains(values[0]) || !set.Contains(5) ||
                set.Count != values.Length)
                return false;
            return true;
#else
            // No EnsureCapacity to size a new set with: added element by element.
            return false;
#endif
        }

        private static bool CheckMap(int mask, int bias)
        {
            var keys = CheckValues();
            var dictionary = new Dictionary<long, int>(keys.Length);
            if (ObjectFields.At<object>(dictionary, MapBuckets) is not int[] ||
                !Fill(dictionary, ref Bytes(keys, 12)[0], keys.Length, 8, 12, mask, bias) ||
                dictionary.Count != keys.Length)
                return false;
            int index = 0;
            foreach (var pair in dictionary)
            {
                if (pair.Key != keys[index] || pair.Value != index)
                    return false;
                index++;
            }

            for (int i = 0; i < keys.Length; i++)
                if (!dictionary.TryGetValue(keys[i], out int value) || value != i)
                    return false;
            if (dictionary.ContainsKey(5) || !dictionary.Remove(keys[0]) || dictionary.ContainsKey(keys[0]))
                return false;
            dictionary.Add(5, 50);
            dictionary[keys[3]] = 30;
            return dictionary.Count == keys.Length && dictionary[5] == 50 && dictionary[keys[3]] == 30;
        }

        // Keys of both kinds of hashing (boxed: hashed by their value): strings, longs that fill buckets twice, and
        // longs that all hash to 0 (their halves cancel out), sharing one chain.
        private static object[] ReferenceKeys()
        {
            var keys = new object[40];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = (i % 3) switch
                {
                    0 => "k" + i,
                    1 => i * 37L - 700,
                    _ => (long)i << 32 | (uint)i,
                };
            return keys;
        }

        private static bool CheckMapReferences() => CheckMapOf(ReferenceKeys(), "x", "y");

        private static bool CheckMapStrings() => CheckMapOf(StringKeys(), "x", "y");

        private static bool CheckMapOf<TKey>(TKey[] keys, TKey absent, TKey added)
        {
            var dictionary = new Dictionary<TKey, string>(keys.Length);
            if (!Empty(dictionary, keys.Length))
                return false;
            for (int i = 0; i < keys.Length; i++)
                Insert(dictionary, keys[i], "v" + i);
            return CheckMapFilled(dictionary, keys, i => "v" + i, absent, added);
        }

        private static string[] StringKeys()
        {
            var keys = new string[40];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = "s" + i;
            return keys;
        }

        // Every key found with its value, in order, through the public API, which then removes and adds as usual.
        private static bool CheckMapFilled<TKey>(Dictionary<TKey, string> dictionary, TKey[] keys,
            Func<int, string> value, TKey absent, TKey added)
        {
            if (dictionary.Count != keys.Length)
                return false;
            int index = 0;
            foreach (var pair in dictionary)
            {
                if (!Equals(pair.Key, keys[index]) || pair.Value != value(index))
                    return false;
                index++;
            }

            for (int i = 0; i < keys.Length; i++)
                if (!dictionary.TryGetValue(keys[i], out string found) || found != value(i))
                    return false;
            if (dictionary.ContainsKey(absent) || !dictionary.Remove(keys[0]) || dictionary.ContainsKey(keys[0]))
                return false;
            dictionary.Add(added, "a");
            dictionary[keys[3]] = "b";
            return dictionary.Count == keys.Length && dictionary[added] == "a" && dictionary[keys[3]] == "b";
        }

        private static bool CheckSetReferences() => CheckSetOf(ReferenceKeys(), "x", "y");

        private static bool CheckSetStrings() => CheckSetOf(StringKeys(), "x", "y");

        private static bool CheckSetOf<T>(T[] items, T absent, T added)
        {
            // A null item too: a set holds one (a dictionary holds no null key).
            items[5] = default;
            var set = new HashSet<T>();
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            set.EnsureCapacity(items.Length);
#endif
            if (!Empty(set, items.Length))
                return false;
            foreach (var item in items)
                Insert(set, item);
            return CheckSetFilled(set, items, absent, added);
        }

        private static bool CheckSetFilled<T>(HashSet<T> set, T[] items, T absent, T added)
        {
            if (set.Count != items.Length)
                return false;
            int index = 0;
            foreach (var item in set)
                if (!Equals(item, items[index++]))
                    return false;
            foreach (var item in items)
                if (!set.Contains(item))
                    return false;
            return !set.Contains(absent) && set.SetEquals(new HashSet<T>(items)) && set.Add(added) &&
                   !set.Add(items[3]) && set.Remove(items[0]) && !set.Contains(items[0]) && set.Contains(added) &&
                   set.Count == items.Length;
        }

        // Filled by the runtime: each entry's chain read back as the runtime would walk it, which reaches every
        // entry in exactly one bucket.
        private static bool CheckMapChains()
        {
            var keys = ReferenceKeys();
            var values = new string[keys.Length];
            var dictionary = new Dictionary<object, string>();
            for (int i = 0; i < keys.Length; i++)
                dictionary.Add(keys[i], values[i] = "v" + i);
            if (ObjectFields.At<int>(dictionary, MapBound) != keys.Length ||
                !(ObjectFields.At<object>(dictionary, MapEntries) is Array { Length: var length }) || length < keys.Length)
                return false;
            var entries = Unsafe.As<CollectionFields.MapEntry<object, string>[]>(
                ObjectFields.At<object>(dictionary, MapEntries));
            var next = new int[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                if (!ReferenceEquals(entries[i].Key, keys[i]) || !ReferenceEquals(entries[i].Value, values[i]))
                    return false;
                next[i] = entries[i].Next;
            }

            return Chains(next, i => dictionary.Comparer.GetHashCode(keys[i]));
        }

        private static bool CheckSetChains()
        {
            var items = ReferenceKeys();
            var set = new HashSet<object>();
            foreach (var item in items)
                set.Add(item);
            if (ObjectFields.At<int>(set, SetBound) != items.Length ||
                !(ObjectFields.At<object>(set, SetEntries) is Array { Length: var length }) || length < items.Length)
                return false;
            var entries = Unsafe.As<CollectionFields.SetEntry<object>[]>(ObjectFields.At<object>(set, SetEntries));
            var next = new int[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                if (!ReferenceEquals(entries[i].Value, items[i]))
                    return false;
                next[i] = entries[i].Next;
            }

            return Chains(next, i => set.Comparer.GetHashCode(items[i]));
        }

        // Each entry's next is an earlier entry (each was added to the front of its bucket) or -1, and the next of
        // no other entry: so every chain ends, and is walked as the table walks it. Keys of the same hash code share
        // a chain: the later one reaches the earlier.
        private static bool Chains(int[] next, Func<int, int> hash)
        {
            var taken = new bool[next.Length];
            for (int i = 0; i < next.Length; i++)
            {
                int at = next[i];
                if (at == -1)
                    continue;
                if (at < 0 || at >= i || taken[at])
                    return false;
                taken[at] = true;
            }

            for (int i = 0; i < next.Length; i++)
            for (int j = 0; j < i; j++)
            {
                if (hash(i) != hash(j))
                    continue;
                int at = next[i];
                while (at > j)
                    at = next[at];
                if (at != j)
                    return false;
            }

            return true;
        }
    }
}
