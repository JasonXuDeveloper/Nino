using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Pair loops of <c>Dictionary&lt;TKey, TValue&gt;</c>, generic over the key and value types only: with value-type
    /// keys and values this is fully specialized code, whereas a dictionary type parameter (a reference type)
    /// would make every call shared generic code with run-time lookups.
    /// </summary>
    internal static class NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        /// <summary>Writes the count and the pairs of a dictionary (not null).</summary>
        // Inlined, with the loops out of line: the writer's address never reaches a call that is not inlined (raw
        // pairs are written into one reservation taken here; other pairs go through a copy of the writer), so the
        // caller keeps the writer's fields in registers.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(Dictionary<TKey, TValue> value, ref Writer writer)
        {
            if (RawFormatter.Is<TKey, TKeyFormatter>() && RawFormatter.Is<TValue, TValueFormatter>())
            {
                // Raw keys and values: one reservation for the whole dictionary, no per-element bounds checks.
                int count = value.Count;
                ref byte pairs = ref writer.ReserveCounted(count, Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>());
                if (count != 0)
                    WriteRaw(value, ref pairs, count);
                return;
            }

            if (RawFormatter.Is<TKey, TKeyFormatter>() && typeof(TValueFormatter) == typeof(StringFormatter))
            {
                // Raw keys and strings: written into the free part of the buffer by value; only when they
                // do not fit does the buffer grow, through the general loop.
                int written = WriteStringPairs(Unsafe.As<Dictionary<TKey, string>>(value),
                    ref writer.FreeSpace(out int available), available);
                if (written >= 0)
                {
                    writer.Commit(written);
                    return;
                }
            }

            // An empty dictionary skips the call and the copies of the writer in and out (as NinoElements.Read).
            if (value.Count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            Writer copy = default;
            writer.CopyTo(ref copy);
            WritePairs(value, ref copy);
            copy.CopyTo(ref writer);
        }

        /// <summary>
        /// The bytes of <see cref="WritePairs"/> for raw keys and strings, written into <paramref name="dst"/>
        /// (<paramref name="available"/> bytes). Returns the number of bytes written, or -1 when they do not fit.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteStringPairs(Dictionary<TKey, string> value, ref byte dst, int available)
        {
            int keySize = Unsafe.SizeOf<TKey>();
            int count = value.Count;
            if (available < NinoWire.LengthSize)
                return -1;
            NinoUnsafe.Write(ref dst, count);
            nuint pos = NinoWire.LengthSize;
            nuint end = (nuint)(uint)available;
            int written = 0;
            foreach (var pair in value)
            {
                if (++written > count)
                    NinoThrow.ModifiedDuringWrite();
                string s = pair.Value;
                // Room for the longest form of the string; only what it takes is kept.
                nuint size = (nuint)(uint)(keySize + NinoStrings.MaxSize(s));
                if (size > end - pos)
                    return -1;
                ref byte entry = ref Unsafe.Add(ref dst, (nint)pos);
                NinoUnsafe.Write(ref entry, 0, pair.Key);
                NinoLayout<TKey>.ClearPadding(ref entry);
                pos += (nuint)(uint)(keySize + NinoStrings.Write(ref Unsafe.Add(ref entry, keySize), s));
            }

            return (int)pos;
        }

        /// <summary>Writes the <paramref name="count"/> raw pairs at <paramref name="pairs"/> (after the
        /// count).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteRaw(Dictionary<TKey, TValue> value, ref byte pairs, int count)
        {
            int keySize = Unsafe.SizeOf<TKey>();
            int pairSize = keySize + Unsafe.SizeOf<TValue>();
            // The dictionary's own entries when none is free (as HashSetFormatter's); the enumerator out of line.
            var entries = CollectionViews.Dictionary ? CollectionFields.Entries(value, count) : null;
            if (entries == null || count > entries.Length)
            {
                WriteEnumerated(value, ref pairs, count);
                return;
            }

            // Several entries at a time where the CPU permutes vectors, each key and value packed out of its entry.
            NinoBlockCollections.WriteAll<CollectionFields.MapEntry<TKey, TValue>, CollectionFields.MapEntryBlock<TKey, TValue>>(
                ref pairs, ref entries[0], count, pairSize);
        }

        /// <summary>The <paramref name="count"/> raw pairs of a dictionary with free entries, in enumeration
        /// order.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteEnumerated(Dictionary<TKey, TValue> value, ref byte pairs, int count)
        {
            int keySize = Unsafe.SizeOf<TKey>();
            int pairSize = keySize + Unsafe.SizeOf<TValue>();
            int written = 0;
            foreach (var pair in value)
            {
                if (++written > count)
                    NinoThrow.ModifiedDuringWrite();
                NinoUnsafe.Write(ref pairs, 0, pair.Key);
                NinoUnsafe.Write(ref pairs, keySize, pair.Value);
                NinoLayout<TKey>.ClearPadding(ref pairs);
                NinoLayout<TValue>.ClearPadding(ref Unsafe.Add(ref pairs, keySize));
                pairs = ref Unsafe.Add(ref pairs, pairSize);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WritePairs(Dictionary<TKey, TValue> value, ref Writer target)
        {
            // A local writer, so its fields stay in registers inside the loop.
            Writer writer = default;
            target.CopyTo(ref writer);
            writer.WriteLength(value.Count);
            foreach (var pair in value)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref writer);
                default(TValueFormatter).Serialize(pair.Value, ref writer);
            }

            writer.CopyTo(ref target);
        }

        /// <summary>
        /// The fewest bytes a pair takes, which the pair count is validated with: the key's and the value's
        /// (<see cref="NinoElements.MinWireSize{T,TFormatter}"/>; their sizes for raw ones). A JIT-time constant.
        /// </summary>
        public static int MinPairSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoElements.MinSingleSize<TKey, TKeyFormatter>() +
                   NinoElements.MinSingleSize<TValue, TValueFormatter>();
        }

        /// <summary>
        /// The capacity to give a dictionary for <paramref name="length"/> pairs: the length, unless its entries would
        /// take far more memory than the data (<see cref="NinoElements.Heavy(int, int)"/>; large keys
        /// or values): then what fits the bound, and it grows as the pairs are added.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Capacity(int length, int remaining) =>
            // A pair takes 2 bytes at least: entries of up to 32 bytes never are, without the field.
            Unsafe.SizeOf<KeyValuePair<TKey, TValue>>() + NinoElements.HashSlot > 2 * NinoElements.Amplification &&
            HeavyEntries
                ? NinoElements.Capacity(length, Unsafe.SizeOf<KeyValuePair<TKey, TValue>>() + NinoElements.HashSlot,
                    remaining)
                : length;

        /// <summary>
        /// Whether a dictionary's entries (<see cref="NinoElements.HashSlot"/> bytes beyond the pair), or the keys
        /// alone, are <see cref="NinoElements.Heavy(int, int)"/>: fields, which the JIT folds where it imports the
        /// test (a call's result it does not).
        /// </summary>
        private static readonly bool HeavyEntries = NinoElements.Heavy(
            Unsafe.SizeOf<KeyValuePair<TKey, TValue>>() + NinoElements.HashSlot, MinPairSize);

        private static readonly bool HeavyKeys = NinoElements.Heavy(Unsafe.SizeOf<TKey>(), MinPairSize);

        /// <summary>Recycles and clears an existing dictionary before it is refilled with <paramref name="length"/>
        /// pairs.</summary>
        public static void Reset(Dictionary<TKey, TValue> dictionary, int length, int remaining)
        {
            NinoRecycler.DiscardPairs<TKey, TValue>(dictionary);
            dictionary.Clear();
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            dictionary.EnsureCapacity(Capacity(length, remaining));
#endif
        }

        /// <summary>
        /// Refills an existing dictionary with <paramref name="length"/> pairs. Raw pairs are cleared and rewritten;
        /// otherwise the data is merged by key, each value read into the one the key already maps to, so nested
        /// objects, collections and unchanged strings are reused. Keys missing from the data are removed (and their
        /// values recycled).
        /// </summary>
        public static void Refill(Dictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            int before = dictionary.Count;
            // Also when the keys read (into an array of the length) would take far more memory than the data.
            if (before == 0 || (RawFormatter.Is<TKey, TKeyFormatter>() && RawFormatter.Is<TValue, TValueFormatter>()) ||
                Unsafe.SizeOf<TKey>() > 2 * NinoElements.Amplification && HeavyKeys &&
                !NinoElements.Fits(length, Unsafe.SizeOf<TKey>(), reader.Remaining))
            {
                Reset(dictionary, length, reader.Remaining);
                Read(dictionary, length, ref reader);
                return;
            }

            var seen = System.Buffers.ArrayPool<TKey>.Shared.Rent(length);
            try
            {
                int matched = 0;
#if NET8_0_OR_GREATER
                if (RawFormatter.Is<TKey, TKeyFormatter>() && typeof(TValueFormatter) == typeof(StringFormatter))
                    // Raw keys and strings: one loop with the state in registers (see Reader.ReadStringPairs).
                    matched = reader.RefillStringPairs(Unsafe.As<Dictionary<TKey, string>>(dictionary), seen,
                        length);
                else
#endif
                for (int i = 0; i < length; i++)
                {
                    TKey key = default;
                    default(TKeyFormatter).Deserialize(ref reader, ref key);
                    if (key == null)
                        NinoThrow.NullKey(dictionary);
                    seen[i] = key;
#if NET8_0_OR_GREATER
                    // Reading the value never touches this dictionary, so the reference stays valid.
                    ref TValue slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                        dictionary, key, out bool exists);
                    if (exists)
                        matched++;
                    else
                        Walked(dictionary, key);
                    default(TValueFormatter).Deserialize(ref reader, ref slot);
#else
                    bool exists = dictionary.TryGetValue(key, out TValue item);
                    if (exists)
                        matched++;
                    default(TValueFormatter).Deserialize(ref reader, ref item);
                    dictionary[key] = item;
                    if (!exists)
                        Walked(dictionary, key);
#endif
                }

                // Every key that was there before was read again, unless the data repeats a key: a repeated key
                // counts twice, so equal counts are only trusted when the keys come back in the same order.
                if (matched != before || !SameKeys(dictionary, seen, length))
                    RemoveStale(dictionary, seen, length);
            }
            finally
            {
                NinoCollections.ReturnRented(seen);
            }
        }

        // A key Refill added (not in the dictionary before): its bucket walked (HashTables.Plan). Out of line, with the
        // plan's lookup (a run-time lookup in shared code): read once per Refill, it cost every read into an existing
        // dictionary, where keys are rarely new.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Walked(Dictionary<TKey, TValue> dictionary, TKey key)
        {
            if (HashTables.Plan<TKey, TValue>.Walks)
                HashTables.Walk(dictionary, key);
        }

        /// <summary>
        /// Whether the dictionary holds exactly the <paramref name="count"/> keys read, in that order: true when it
        /// was filled from data of the same shape, without hashing anything.
        /// </summary>
        private static bool SameKeys(Dictionary<TKey, TValue> dictionary, TKey[] keys, int count)
        {
            if (dictionary.Count != count)
                return false;
            var comparer = dictionary.Comparer;
            int i = 0;
            foreach (var pair in dictionary)
            {
                if (!comparer.Equals(pair.Key, keys[i++]))
                    return false;
            }

            return true;
        }

        /// <summary>Removes (and recycles) the entries whose key is not among the <paramref name="count"/>
        /// read.</summary>
        private static void RemoveStale(Dictionary<TKey, TValue> dictionary, TKey[] keys, int count)
        {
            if (count == 0)
            {
                RemoveUnread(dictionary, null);
                return;
            }

#if NET8_0_OR_GREATER
            if (count <= 2 && dictionary.Count <= (count == 1 ? 16 : 4) &&
                !NinoRecycler<TKey>.MayRecycle && !NinoRecycler<TValue>.MayRecycle)
            {
                if (count == 1)
                    RemoveExcept(dictionary, keys[0]);
                else
                    RemoveExcept(dictionary, keys[0], keys[1]);
                return;
            }
            if (count is 3 or 4 && dictionary.Count <= 4 &&
                !NinoRecycler<TKey>.MayRecycle && !NinoRecycler<TValue>.MayRecycle)
            {
                RemoveExcept(dictionary, keys, count);
                return;
            }
#endif

            // A key read twice is one key (the last value won).
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            // Every distinct key read is now in the dictionary, and there can be at most one per pair.
            var read = new HashSet<TKey>(Math.Min(count, dictionary.Count), dictionary.Comparer);
#else
            var read = new HashSet<TKey>(dictionary.Comparer);
#endif
            for (int i = 0; i < count; i++)
                read.Add(keys[i]);

            if (dictionary.Count == read.Count)
                return;

            RemoveUnread(dictionary, read);
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RemoveExcept(Dictionary<TKey, TValue> dictionary, TKey keep)
        {
            var comparer = dictionary.Comparer;
            foreach (var pair in dictionary)
                if (!comparer.Equals(keep, pair.Key))
                    dictionary.Remove(pair.Key);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RemoveExcept(Dictionary<TKey, TValue> dictionary, TKey first, TKey second)
        {
            var comparer = dictionary.Comparer;
            foreach (var pair in dictionary)
                if (!comparer.Equals(first, pair.Key) && !comparer.Equals(second, pair.Key))
                    dictionary.Remove(pair.Key);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RemoveExcept(Dictionary<TKey, TValue> dictionary, TKey[] keys, int count)
        {
            var comparer = dictionary.Comparer;
            ref TKey first = ref NinoUnsafe.GetArrayDataReference(keys);
            KeyHashes hashes = default;
            bool distinct = dictionary.Count == count;
            for (int i = 0; i < count; i++)
            {
                TKey key = Unsafe.Add(ref first, i);
                int hash = comparer.GetHashCode(key);
                if (distinct)
                    for (int j = 0; j < i; j++)
                        if (hash == hashes[j] && comparer.Equals(key, Unsafe.Add(ref first, j)))
                        {
                            distinct = false;
                            break;
                        }
                hashes[i] = hash;
            }

            // Every read key is already present, so distinct keys can cover the whole dictionary.
            if (distinct)
                return;
            foreach (var pair in dictionary)
            {
                int hash = comparer.GetHashCode(pair.Key);
                int i = 0;
                for (; i < count; i++)
                    if (hash == hashes[i] && comparer.Equals(Unsafe.Add(ref first, i), pair.Key))
                        break;
                if (i == count)
                    dictionary.Remove(pair.Key);
            }
        }

        [InlineArray(4)]
        private struct KeyHashes
        {
            private int _element;
        }

#endif

        private static void RemoveUnread(Dictionary<TKey, TValue> dictionary, HashSet<TKey> read)
        {
#if NET8_0_OR_GREATER
            // Remove keeps a .NET Core dictionary enumerator valid. Only keep the dropped pairs when they
            // must be recycled, after every stale entry has been removed.
            if (!NinoRecycler<TKey>.MayRecycle && !NinoRecycler<TValue>.MayRecycle)
            {
                foreach (var pair in dictionary)
                    if (read == null || !read.Contains(pair.Key))
                        dictionary.Remove(pair.Key);
                return;
            }
#endif

            RemoveAndRecycleStale(dictionary, read);
        }

#if NET8_0_OR_GREATER
        // Keep the list and recycling walk out of the method that usually removes keys directly.
        [MethodImpl(MethodImplOptions.NoInlining)]
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static void RemoveAndRecycleStale(Dictionary<TKey, TValue> dictionary, HashSet<TKey> read)
        {
            var stale = new List<KeyValuePair<TKey, TValue>>(dictionary.Count - (read?.Count ?? 0));
            foreach (var pair in dictionary)
            {
                if (read == null || !read.Contains(pair.Key))
                    stale.Add(pair);
            }

            foreach (var pair in stale)
                dictionary.Remove(pair.Key);
            NinoRecycler.DiscardPairs<TKey, TValue>(stale);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddRaw(Dictionary<TKey, TValue> dictionary, ref byte src, int length)
        {
            // A cursor rather than an int offset, which every access would sign-extend.
            int keySize = Unsafe.SizeOf<TKey>();
            int pairSize = keySize + Unsafe.SizeOf<TValue>();
            // A bool other than 0 or 1, and in Untrusted mode other values no constructor makes, are invalid data.
            if (!NinoLayout<TKey>.CopiesAsIs)
                NinoChecks.Values<TKey>(ref src, length, pairSize);
            if (!NinoLayout<TValue>.CopiesAsIs)
                NinoChecks.Values<TValue>(ref Unsafe.Add(ref src, keySize), length, pairSize);
            // The dictionary is new and sized for the pairs (Read's only caller): filled directly where it can be.
            if (HashTables.Map && HashTables.TryFill(dictionary, ref src, length, keySize, pairSize))
                return;
            for (int i = 0; i < length; i++)
            {
                TKey key = NinoUnsafe.Read<TKey>(ref src);
                NinoCollections.AddUnique(dictionary, key, NinoUnsafe.Read<TValue>(ref src, keySize));
                HashTables.Added(dictionary, key);
                src = ref Unsafe.Add(ref src, pairSize);
            }
        }

        /// <summary>Reads <paramref name="length"/> pairs into <paramref name="dictionary"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(Dictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            if (RawFormatter.Is<TKey, TKeyFormatter>() && RawFormatter.Is<TValue, TValueFormatter>())
            {
                // The block is taken here and the pairs added out of line: the reader's address never leaves the
                // caller, and the caller stays small enough for the JIT to inline the rest of its path.
                ref byte src = ref reader.ReadElements(length, Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>());
                if (length != 0)
                    AddRaw(dictionary, ref src, length);
                return;
            }

            if (RawFormatter.Is<TKey, TKeyFormatter>() && typeof(TValueFormatter) == typeof(StringFormatter))
            {
                // Raw keys and strings: one loop with the state in registers (see Reader.ReadStrings).
                reader.ReadStringPairs(Unsafe.As<Dictionary<TKey, string>>(dictionary), length);
                return;
            }

            if (typeof(TKeyFormatter) == typeof(StringFormatter) && typeof(TValueFormatter) == typeof(StringFormatter))
            {
                // Strings for both: the same, a null key invalid data.
                if (length != 0)
                    reader.ReadStringPairs(Unsafe.As<Dictionary<string, string>>(dictionary), length);
                return;
            }

            // Out of line, through a copy of the reader (see AddRaw); an empty dictionary skips both.
            if (length == 0)
                return;
            Reader copy = reader;
            // Only a reference-type key can be null (a JIT-time constant).
            if (default(TKey) == null)
                ReadPairsChecked(dictionary, length, ref copy);
            else
                ReadPairs(dictionary, length, ref copy);
            reader = copy;
        }

        /// <summary>
        /// <see cref="ReadPairs"/>, with a null key in the data (TryAdd's ArgumentNullException) reported as invalid
        /// data: caught here, around the loop, rather than checked for every key in it, which cost the loop registers
        /// (a <c>Dictionary&lt;string, int&gt;</c> read kept more of its state on the stack).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadPairsChecked(Dictionary<TKey, TValue> dictionary, int length, ref Reader source)
        {
            try
            {
                ReadPairs(dictionary, length, ref source);
            }
            catch (ArgumentNullException e) when (e.ParamName == "key")
            {
                NinoThrow.NullKey(dictionary);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadPairs(Dictionary<TKey, TValue> dictionary, int length, ref Reader source)
        {
            // Keys whose buckets the dictionary guards itself (strings on .NET) are added here, by the dictionary,
            // in a loop with nothing else in it; others in ReadGuarded (HashTables.Plan). The calls a guarded loop
            // makes, even never taken, kept more of this loop's state on the stack.
            if (HashTables.Plan<TKey, TValue>.Inserts || HashTables.Plan<TKey, TValue>.Walks)
            {
                ReadGuarded(dictionary, length, ref source);
                return;
            }

            // A local reader, so its fields stay in registers inside the loop.
            Reader reader = source;
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref reader, ref key);
                default(TValueFormatter).Deserialize(ref reader, ref item);
                NinoCollections.AddUnique(dictionary, key, item);
            }

            source = reader;
        }

        /// <summary><see cref="ReadPairs"/>, each key inserted or its bucket walked (hash flooding).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadGuarded(Dictionary<TKey, TValue> dictionary, int length, ref Reader source)
        {
            // A local reader, so its fields stay in registers inside the loop.
            Reader reader = source;
            // Each key added here, its bucket walked as it is, where the dictionary is as expected (new, sized for the
            // pairs); else added by the dictionary, and its bucket walked after (HashTables: hash flooding).
            // One loop (the formatters inlined once), the choice made per pair: predicted, and cheaper than a second
            // copy of the loop, which kept more of its state on the stack.
            // The plan read once, into locals (HashTables.Plan).
            bool insert = HashTables.Plan<TKey, TValue>.Inserts && HashTables.CanInsert(dictionary, length);
            bool walk = HashTables.Plan<TKey, TValue>.Walks;
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref reader, ref key);
                default(TValueFormatter).Deserialize(ref reader, ref item);
                if (insert)
                {
                    HashTables.Insert(dictionary, key, item);
                }
                else
                {
                    NinoCollections.AddUnique(dictionary, key, item);
                    if (walk)
                        HashTables.Walk(dictionary, key);
                }
            }

            source = reader;
        }
    }
}
