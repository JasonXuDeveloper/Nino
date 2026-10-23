using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// Fills a new <see cref="HashSet{T}"/> or <see cref="Dictionary{TKey,TValue}"/> of raw elements by writing its
    /// entries and buckets directly, as its Add would, without the checks Add repeats per element (the comparer, the
    /// free list, the capacity, the version). The collection is created and sized through its public API first (its
    /// constructor or EnsureCapacity allocate the buckets and entries and set the fast-mod multiplier); only the
    /// entries, the buckets and the count are written here, at the offsets <see cref="CollectionViews"/> found. The
    /// hashing differs between implementations, and not along the lines of their fields (Mono's dictionary has .NET
    /// Core's fields and buckets but masks hash codes to 31 bits as .NET Framework's does): the bucket is .NET's fast
    /// modulo where the table keeps a multiplier (64-bit .NET 5+), else the remainder; whether hash codes are masked
    /// and what a bucket holds for its first entry (its index + 1, empty 0; or its index, empty -1) are found at
    /// startup, as the first of the four that a collection filled here passes the public API's checks with. None:
    /// the formatters add element by element.
    /// </summary>
    internal static class HashTables
    {
        private static readonly CollectionViews.HashOffsets? SetAt = CollectionViews.HashSetOffsets;
        private static readonly int SetEntries = SetAt?.Entries ?? 0;
        private static readonly int SetBound = SetAt?.Bound ?? 0;
        private static readonly int SetBuckets = SetAt?.Buckets ?? 0;
        private static readonly int SetFastMod = SetAt?.FastMod ?? -1;
        private static readonly int SetCount = SetAt?.Count ?? 0;

        private static readonly CollectionViews.HashOffsets? MapAt = CollectionViews.DictionaryOffsets;
        private static readonly int MapEntries = MapAt?.Entries ?? 0;
        private static readonly int MapBound = MapAt?.Bound ?? 0;
        private static readonly int MapBuckets = MapAt?.Buckets ?? 0;
        private static readonly int MapFastMod = MapAt?.FastMod ?? -1;
        private static readonly int MapCount = MapAt?.Count ?? 0;

        // The hashing (mask and bias) each passed its check with, -1 for none.
        private static readonly int SetHashing = SetAt == null ? -1 : FindHashing(CheckSet);
        private static readonly int SetMask = Mask(SetHashing), SetBias = Bias(SetHashing);
        private static readonly int MapHashing = MapAt == null ? -1 : FindHashing(CheckMap);
        private static readonly int MapMask = Mask(MapHashing), MapBias = Bias(MapHashing);

        /// <summary>Whether <see cref="TryFill{T}"/> fills a new <see cref="HashSet{T}"/> on this runtime.</summary>
        public static readonly bool Set = SetHashing >= 0;

        /// <summary>Whether <see cref="TryFill{TKey,TValue}"/> fills a new dictionary on this runtime.</summary>
        public static readonly bool Map = MapHashing >= 0;

        // Hashing 0 to 3: hash codes as they are or masked to 31 bits; a bucket holds its first entry's index + 1 or
        // its index.
        private static int Mask(int hashing) => (hashing & 1) == 0 ? -1 : int.MaxValue;
        private static int Bias(int hashing) => hashing < 2 ? 1 : 0;

        private static int FindHashing(Func<int, int, bool> check)
        {
            for (int hashing = 0; hashing < 4; hashing++)
                if (check(Mask(hashing), Bias(hashing)))
                    return hashing;
            return -1;
        }

        /// <summary>
        /// Adds the <paramref name="count"/> raw values at <paramref name="source"/> to <paramref name="set"/>, new
        /// (default comparer) and sized for them; false, with the set untouched, when it is not as expected. A value
        /// the data holds twice throws, as the element by element read does.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryFill<T>(HashSet<T> set, ref byte source, int count, int size) =>
            Fill(set, ref source, count, size, SetMask, SetBias);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Fill<T>(HashSet<T> set, ref byte source, int count, int size, int mask, int bias)
        {
            var buckets = ObjectFields.At<int[]>(set, SetBuckets);
            var entries = Unsafe.As<CollectionFields.SetEntry<T>[]>(ObjectFields.At<object>(set, SetEntries));
            if (buckets == null || buckets.Length == 0 || entries == null || entries.Length < count ||
                ObjectFields.At<int>(set, SetBound) != 0)
                return false;

            ulong multiplier = SetFastMod >= 0 ? ObjectFields.At<ulong>(set, SetFastMod) : 0;
            uint length = (uint)buckets.Length;
            ref int firstBucket = ref buckets[0];
            ref var first = ref entries[0];
            ref var entry = ref first;
            for (int i = 0; i < count; i++)
            {
                T value = NinoUnsafe.Read<T>(ref source);
                source = ref Unsafe.Add(ref source, size);
                int hash = EqualityComparer<T>.Default.GetHashCode(value) & mask;
                ref int bucket = ref Unsafe.Add(ref firstBucket, (IntPtr)Bucket((uint)hash, length, multiplier));
                for (int at = bucket - bias; at >= 0;)
                {
                    ref var other = ref Unsafe.Add(ref first, at);
                    if (other.HashCode == hash && EqualityComparer<T>.Default.Equals(other.Value, value))
                        NinoThrow.DuplicateKey(typeof(HashSet<T>));
                    at = other.Next;
                }

                entry.HashCode = hash;
                entry.Next = bucket - bias;
                entry.Value = value;
                bucket = i + bias;
                entry = ref Unsafe.Add(ref entry, 1);
            }

            ObjectFields.At<int>(set, SetBound) = count;
            ObjectFields.At<int>(set, SetCount) = count;
            return true;
        }

        /// <summary>
        /// As <see cref="TryFill{T}"/>, for the raw pairs of a new dictionary: a key, then its value, each pair
        /// <paramref name="pairSize"/> bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryFill<TKey, TValue>(Dictionary<TKey, TValue> dictionary, ref byte source, int count,
            int keySize, int pairSize) =>
            Fill(dictionary, ref source, count, keySize, pairSize, MapMask, MapBias);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Fill<TKey, TValue>(Dictionary<TKey, TValue> dictionary, ref byte source, int count,
            int keySize, int pairSize, int mask, int bias)
        {
            var buckets = ObjectFields.At<int[]>(dictionary, MapBuckets);
            var entries = Unsafe.As<CollectionFields.MapEntry<TKey, TValue>[]>(
                ObjectFields.At<object>(dictionary, MapEntries));
            if (buckets == null || buckets.Length == 0 || entries == null || entries.Length < count ||
                ObjectFields.At<int>(dictionary, MapBound) != 0)
                return false;

            ulong multiplier = MapFastMod >= 0 ? ObjectFields.At<ulong>(dictionary, MapFastMod) : 0;
            uint length = (uint)buckets.Length;
            ref int firstBucket = ref buckets[0];
            ref var first = ref entries[0];
            ref var entry = ref first;
            for (int i = 0; i < count; i++)
            {
                TKey key = NinoUnsafe.Read<TKey>(ref source);
                TValue value = NinoUnsafe.Read<TValue>(ref source, keySize);
                source = ref Unsafe.Add(ref source, pairSize);
                int hash = EqualityComparer<TKey>.Default.GetHashCode(key) & mask;
                ref int bucket = ref Unsafe.Add(ref firstBucket, (IntPtr)Bucket((uint)hash, length, multiplier));
                for (int at = bucket - bias; at >= 0;)
                {
                    ref var other = ref Unsafe.Add(ref first, at);
                    if (other.HashCode == hash && EqualityComparer<TKey>.Default.Equals(other.Key, key))
                        NinoThrow.DuplicateKey(typeof(Dictionary<TKey, TValue>));
                    at = other.Next;
                }

                entry.HashCode = hash;
                entry.Next = bucket - bias;
                entry.Key = key;
                entry.Value = value;
                bucket = i + bias;
                entry = ref Unsafe.Add(ref entry, 1);
            }

            ObjectFields.At<int>(dictionary, MapBound) = count;
            ObjectFields.At<int>(dictionary, MapCount) = count;
            return true;
        }

        /// <summary>
        /// The bucket of a hash code: .NET's fast modulo where the table keeps a multiplier (64-bit .NET 5+), else the
        /// remainder.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Bucket(uint hash, uint length, ulong multiplier) =>
            multiplier != 0 ? (uint)(((((multiplier * hash) >> 32) + 1) * length) >> 32) : hash % length;

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
            try
            {
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
            }
            catch
            {
                return false;
            }
#else
            // No EnsureCapacity to size a new set with: added element by element.
            return false;
#endif
        }

        private static bool CheckMap(int mask, int bias)
        {
            try
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
            catch
            {
                return false;
            }
        }
    }
}
