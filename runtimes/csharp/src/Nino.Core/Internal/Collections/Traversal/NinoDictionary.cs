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
        /// <summary>The kind of the keys (<see cref="NinoKind"/>): a JIT-time constant.</summary>
        private static NinoElementKind KeyKind
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoKind.Of<TKey, TKeyFormatter>();
        }

        /// <summary>The kind of the values (<see cref="NinoKind"/>): a JIT-time constant.</summary>
        private static NinoElementKind ValueKind
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoKind.Of<TValue, TValueFormatter>();
        }

        /// <summary>
        /// Writes the count and the pairs of a dictionary (not null): its entries as a run
        /// (<see cref="NinoRuns.WritePairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/>), or through its enumerator
        /// where this runtime's layout of them is not known.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(Dictionary<TKey, TValue> value, ref Writer writer)
        {
            // Asked first, so NinoKind is initialized when Tier1 compiles this: a polymorphic value's generated loop
            // writes the run with its keys where Nino.Core says how (NinoRuns.KeyOf).
            int key = !typeof(TValue).IsValueType && NinoKind<TValueFormatter>.Runs
                ? NinoRuns.KeyOf<TKey, TKeyFormatter>()
                : NinoRun.NoKey;
            int count = value.Count;
            // An empty dictionary skips the run and the copies of the writer in and out (as NinoRuns.Read).
            if (count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            if (CollectionFields.Run(value, count, key, out var run))
                NinoRuns.WritePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(run, count, ref writer);
            else
                writer = WriteEnumerated(value, count, writer);
        }

        /// <summary>The count and the pairs through the enumerator, out of line, the writer by value and
        /// returned.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEnumerated(Dictionary<TKey, TValue> value, int count, Writer writer)
        {
            writer.WriteLength(count);
            int written = 0;
            foreach (var pair in value)
            {
                if (++written > count)
                    NinoThrow.ModifiedDuringWrite();
                default(TKeyFormatter).Serialize(pair.Key, ref writer);
                default(TValueFormatter).Serialize(pair.Value, ref writer);
            }

            return writer;
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
            if (before == 0 || (KeyKind == NinoElementKind.Raw && ValueKind == NinoElementKind.Raw) ||
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
                if (CursorPairs)
                {
                    // The pair loop, each key kept in seen and each value read over the one it maps to.
                    ulong read = MergeCursors(dictionary, seen, length, ref reader.Peek(), reader.Remaining);
                    reader.UnsafeAdvance((int)read);
                    matched = (int)(read >> 32);
                }
                else
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

        /// <summary>
        /// Whether the pairs are read as one block and scattered into the entries
        /// (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/> for the keys and the values): a JIT-time constant.
        /// </summary>
        private static bool ScattersPairs
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoRuns.ScattersItems<TKey, TKeyFormatter>() && NinoRuns.ScattersItems<TValue, TValueFormatter>();
        }

        /// <summary>Reads <paramref name="length"/> pairs into <paramref name="dictionary"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(Dictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            if (ScattersPairs)
            {
                // Pairs read as one block (no key can be null): the block is taken here and the pairs filled or added
                // out of line, so the reader's address never leaves the caller, which stays small enough for the JIT
                // to inline the rest of its path.
                ref byte src = ref NinoRuns.TakePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(ref reader, length);
                if (length != 0)
                    AddBlock(dictionary, ref src, length);
                return;
            }

            // Out of line, through a copy of the reader (see above); an empty dictionary skips both.
            if (length == 0)
                return;
            if (CursorPairs)
            {
                // Keys and values with the cursor form (strings among them): the pair loop, the data and the bytes
                // left to the call, not the reader, which stays in the caller's registers.
                reader.UnsafeAdvance(ReadCursors(dictionary, length, ref reader.Peek(), reader.Remaining));
                return;
            }

            Reader copy = reader;
            // Only a reference-type key can be null (a JIT-time constant).
            if (default(TKey) == null)
                ReadPairsChecked(dictionary, length, ref copy);
            else
                ReadPairs(dictionary, length, ref copy);
            reader = copy;
        }

        /// <summary>
        /// Whether the pairs take the pair loop (<see cref="NinoRuns.CursorPairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/>,
        /// <see cref="NinoRuns.ReadCursors{TKey,TValue,TKeyFormatter,TValueFormatter,TStore}"/>): strings in either
        /// encoding with strings, raw values or blocks. A JIT-time constant.
        /// </summary>
        private static bool CursorPairs
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoRuns.CursorPairs<TKey, TValue, TKeyFormatter, TValueFormatter>();
        }

        /// <summary>
        /// Reads <paramref name="length"/> pairs (not 0) through the pair loop from the data at <paramref name="data"/>
        /// (<paramref name="end"/> bytes): into the entries of a dictionary
        /// <see cref="HashTables.CanInsert{TKey,TValue}"/> accepts, then linked (from <see cref="HashTables.FillFrom"/>
        /// pairs on), the dictionary left empty if the data is rejected part way (as
        /// <see cref="FillGuarded(Dictionary{TKey,TValue}, int, Reader)"/>); else each added (<see cref="NinoCursor.Adds"/>).
        /// Returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadCursors(Dictionary<TKey, TValue> dictionary, int length, ref byte data, int end)
        {
            bool insert = HashTables.Plan<TKey, TValue>.Inserts && HashTables.CanInsert(dictionary, length);
            if (insert && length >= HashTables.FillFrom)
                return FillCursors(dictionary, length, ref data, end);

            // Each pair read into one pair of locals, then added.
            NinoCursor.Pair<TKey, TValue> pair = default;
            var adds = new NinoCursor.Adds
            {
                Key = NinoCursor.Pair<TKey, TValue>.KeyOf(ref pair), Dictionary = dictionary, Insert = insert,
                Walk = HashTables.Plan<TKey, TValue>.Walks,
            };
            return (int)NinoRuns.ReadCursors<TKey, TValue, TKeyFormatter, TValueFormatter, NinoCursor.Adds>(
                NinoCursor.Pair<TKey, TValue>.Run(ref pair, length), ref data, end, adds);
        }

        /// <summary>
        /// <see cref="Refill"/>'s pair loop: <paramref name="length"/> pairs (not 0) from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes), each key kept in <paramref name="seen"/> and each
        /// value read over the one it maps to (<see cref="NinoCursor.Merges"/>). Returns the bytes read (low half) and
        /// the keys already there (high half). Out of line, so the pair of locals the loop reads into stays out of the
        /// caller's frame.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong MergeCursors(Dictionary<TKey, TValue> dictionary, TKey[] seen, int length, ref byte data,
            int end)
        {
            NinoCursor.Pair<TKey, TValue> pair = default;
            var merge = new NinoCursor.Merges
            {
                Key = NinoCursor.Pair<TKey, TValue>.KeyOf(ref pair), Dictionary = dictionary, Seen = seen,
            };
            return NinoRuns.ReadCursors<TKey, TValue, TKeyFormatter, TValueFormatter, NinoCursor.Merges>(
                NinoCursor.Pair<TKey, TValue>.Run(ref pair, length), ref data, end, merge);
        }

        /// <summary>
        /// <see cref="ReadCursors"/>' fill: the pairs read into the entries
        /// (<see cref="NinoCursor.Entries{TKey,TValue}"/>), then linked (null and repeated keys, hash flooding), the
        /// dictionary left empty, holding no reference the read stored, if the data is rejected part way. A method of
        /// its own, as <see cref="FillGuarded(Dictionary{TKey,TValue}, int, Reader)"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FillCursors(Dictionary<TKey, TValue> dictionary, int length, ref byte data, int end)
        {
            try
            {
                var entries = new NinoCursor.Entries { Key = NinoRuns.MapKey<TKey, TValue>() };
                int read = (int)NinoRuns.ReadCursors<TKey, TValue, TKeyFormatter, TValueFormatter, NinoCursor.Entries>(
                    CollectionFields.Run(HashTables.Entries(dictionary), length), ref data, end, entries);
                HashTables.Link(dictionary, length);
                return read;
            }
            catch
            {
                HashTables.Unlink(dictionary);
                throw;
            }
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
            // Where the dictionary is as expected (new, sized for the pairs), the pairs are read into its entries and
            // its buckets built after them (Fill, from HashTables.FillFrom pairs on),
            // or, for a few pairs, each key added here with its bucket walked as it is; else added by the dictionary,
            // and its bucket walked after (HashTables: hash flooding). The plan read once, into locals
            // (HashTables.Plan).
            bool insert = HashTables.Plan<TKey, TValue>.Inserts && HashTables.CanInsert(dictionary, length);
            if (insert && length >= HashTables.FillFrom)
            {
                source = FillGuarded(dictionary, length, source);
                return;
            }

            // A local reader, so its fields stay in registers inside the loop.
            Reader reader = source;
            bool walk = HashTables.Plan<TKey, TValue>.Walks;
            // One loop (the formatters inlined once), the choice made per pair: predicted, and cheaper than a second
            // copy of the loop, which kept more of its state on the stack.
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

        /// <summary>
        /// The <paramref name="length"/> pairs of the block at <paramref name="src"/>
        /// (<see cref="NinoRuns.TakePairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/>): scattered into the entries of
        /// a dictionary <see cref="HashTables.CanInsert{TKey,TValue}"/> accepts and linked, else each read from the
        /// block, added by the dictionary and its bucket walked after (HashTables.Plan).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddBlock(Dictionary<TKey, TValue> dictionary, ref byte src, int length)
        {
            if (HashTables.Plan<TKey, TValue>.Inserts && HashTables.CanInsert(dictionary, length))
            {
                FillGuarded(dictionary, ref src, length);
                return;
            }

            bool walk = HashTables.Plan<TKey, TValue>.Walks;
            int keySize = NinoKind.Stride<TKey, TKeyFormatter>();
            int pairSize = keySize + NinoKind.Stride<TValue, TValueFormatter>();
            for (int i = 0; i < length; i++, src = ref Unsafe.Add(ref src, pairSize))
            {
                TKey key = NinoRuns.ReadItem<TKey, TKeyFormatter>(ref src);
                NinoCollections.AddUnique(dictionary, key,
                    NinoRuns.ReadItem<TValue, TValueFormatter>(ref Unsafe.Add(ref src, keySize)));
                if (walk)
                    HashTables.Walk(dictionary, key);
            }
        }

        /// <summary>
        /// <see cref="AddBlock"/>'s fill: the block scattered into the entries
        /// (<see cref="NinoRuns.ScatterPairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/>), then linked, the dictionary
        /// left empty if the data is rejected part way (<see cref="HashTables.Unlink{TKey,TValue}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FillGuarded(Dictionary<TKey, TValue> dictionary, ref byte src, int length)
        {
            try
            {
                NinoRuns.ScatterPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(
                    CollectionFields.Run(HashTables.Entries(dictionary), length), ref src);
                HashTables.Link(dictionary, length);
            }
            catch
            {
                HashTables.Unlink(dictionary);
                throw;
            }
        }

        /// <summary>
        /// <see cref="Fill"/>, with the dictionary left empty if the data is rejected part way, holding no reference the
        /// read stored (<see cref="HashTables.Unlink{TKey,TValue}"/>). A method of its own: an exception region in
        /// <see cref="ReadGuarded"/> slowed its loop for a few pairs too (one int-keyed pair 5 to 7 % slower on
        /// Neoverse-N2, #320, run 44246428900), and one in the read loop would keep the reader out of registers there.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader FillGuarded(Dictionary<TKey, TValue> dictionary, int length, Reader reader)
        {
            try
            {
                return Fill(dictionary, length, reader);
            }
            catch
            {
                HashTables.Unlink(dictionary);
                throw;
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> pairs straight into the entries of a dictionary
        /// <see cref="HashTables.CanInsert{TKey,TValue}"/> accepted, through their run
        /// (<see cref="NinoRuns.ReadPairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/>), then builds the buckets in
        /// one pass (<see cref="HashTables.Link{TKey,TValue}(Dictionary{TKey,TValue}, int)"/>: null and repeated keys,
        /// hash flooding).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader Fill(Dictionary<TKey, TValue> dictionary, int length, Reader reader)
        {
            NinoRuns.ReadPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(
                CollectionFields.Run(HashTables.Entries(dictionary), length), ref reader);
            HashTables.Link(dictionary, length);
            return reader;
        }
    }
}
