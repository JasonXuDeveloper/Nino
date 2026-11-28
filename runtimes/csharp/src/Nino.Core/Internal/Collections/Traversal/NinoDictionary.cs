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
    /// The top layer of <c>Dictionary&lt;TKey, TValue&gt;</c>, generic over the key and value types only: with value-type
    /// keys and values this is fully specialized code, whereas a dictionary type parameter (a reference type)
    /// would make every call shared generic code with run-time lookups. It reaches the entries, picks their element,
    /// then writes them, or fills them and links them, as a set's are (<see cref="NinoRuns.Write{T,TFormatter}(NinoRun,
    /// int, ref Writer)"/>, <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>), or merges pairs one at a time into an
    /// existing dictionary, the only code that reads a pair's key and value apart. Its element is its entry for every
    /// key and value (#355): a storage element of the key and the value at their offsets in it
    /// (<see cref="NinoMapEntryFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>), a block where both are raw
    /// or blocks, which the block kernels move skipping the hash code and the link.
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
        /// Writes the count and the pairs of a dictionary (not null): its entries as a run of them
        /// (<see cref="NinoRuns.Write{T,TFormatter}(NinoRun, int, ref Writer)"/>), or through its enumerator where this runtime's
        /// layout of them is not known. Entries holding references out of line (<see cref="WriteEntries"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(Dictionary<TKey, TValue> value, ref Writer writer)
        {
            int count = value.Count;
            // An empty dictionary skips the run and the copies of the writer in and out (as NinoRuns.Read).
            if (count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            // Entries without references (raw or block items) and pairs holding strings: the free space to one call
            // and the bytes written back, in registers, as main's pair loops had it; through a copy of the writer (below) each step of the cursor went through memory, the
            // caller's stores loaded back in the call and its stores loaded back here (a top-level
            // Dictionary<int, string> of one pair written in 1.4 times main's time). What does not fit there takes the
            // writer's path, which grows the buffer.
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<CollectionFields.MapEntry<TKey, TValue>>() ||
                CursorPairs)
            {
                int written = WriteFree(value, count, ref writer.FreeSpace(out int available), available);
                if (written >= 0)
                {
                    writer.Commit(written);
                    return;
                }
            }

            // One call: reaching the entries, the run's kind tests and its block path inlined here took the budget and
            // the registers of the generated body around it (SimpleClass's top-level write 112 to 159 stack accesses, a
            // class-init helper call; 1.2 times main's time). Through a copy of the writer, field by field: a writer
            // passed and returned by value is copied with loads wider than the stores that built it (store forwarding
            // fails).
            Writer copy = default;
            // Entries of raw values and strings open no class body, defer nothing and track no reference: the cursor
            // alone (a JIT-time constant), as NinoRuns' strings.
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<CollectionFields.MapEntry<TKey, TValue>>() ||
                NinoItems.Pair<TKey, TValue, TKeyFormatter, TValueFormatter>())
            {
                writer.CopyCursorTo(ref copy);
                WriteEntries(value, count, ref copy);
                copy.CopyCursorTo(ref writer);
            }
            else
            {
                writer.CopyTo(ref copy);
                WriteEntries(value, count, ref copy);
                copy.CopyTo(ref writer);
            }
        }

        /// <summary>
        /// Writes the count and the pairs of a dictionary's entries, out of line, on the caller's copy of the writer:
        /// entries without references as one run of blocks or values (<see cref="NinoRuns.Write{T,TFormatter}(NinoRun,
        /// int, ref Writer)"/>); entries holding references by the loop chosen from what the key and value formatters
        /// say, with the offsets of the key and value read from the entry itself; pairs holding strings through the
        /// string run kernels (<see cref="NinoItems.Write{TA,TB,TFormatterA,TFormatterB}"/>), one call not generic over
        /// the pair, where through an element formatter each pair was indirect calls in code shared over a class (each
        /// of the entry formatter's answers there is a lookup and each call of its field accessors a call: asm.py, a
        /// <c>Dictionary&lt;string, int&gt;</c>'s 889 to 1,670 bytes, 23 to 54 stack accesses). One call per
        /// dictionary: inlined, its tests and the run's used up the budget of a generated body holding two dictionaries
        /// (asm.py: a Lookup's top-level write, 107 to 189 stack accesses).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteEntries(Dictionary<TKey, TValue> value, int count, ref Writer writer)
        {
            if (!CollectionFields.Run(value, count, out var run))
                writer = WriteEnumerated(value, count, writer);
            else if (!RuntimeHelpers.IsReferenceOrContainsReferences<CollectionFields.MapEntry<TKey, TValue>>())
                NinoRuns.Write<CollectionFields.MapEntry<TKey, TValue>, NinoMapEntryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(run,
                    count, ref writer);
            else if (NinoItems.RunLoop<TKey, TValue, TKeyFormatter, TValueFormatter>(KeyOffset, ValueOffset))
            {
                // The value's run loop.
                writer.WriteLength(count);
                NinoMapEntryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>.WriteRun(run, ref writer);
            }
            else if (!CursorPairs)
                writer = NinoRuns.WriteEach<CollectionFields.MapEntry<TKey, TValue>, NinoMapEntryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(run,
                    count, writer);
            else
            {
                writer.WriteLength(count);
                NinoItems.Write<TKey, TValue, TKeyFormatter, TValueFormatter>(run, KeyOffset, ValueOffset, ref writer);
            }
        }

        /// <summary>
        /// Writes the count and the pairs of a dictionary's entries into the <paramref name="available"/> bytes at
        /// <paramref name="destination"/>, the kernels <see cref="WriteEntries"/> takes: entries without references as
        /// one block (none free), pairs holding strings (<see cref="CursorPairs"/>) through the string run kernels.
        /// Returns the bytes written, or -1 when they did not fit, an entry is free in a block or the entries are not
        /// known (nothing then counts as written: the writer's path writes them all, <see cref="WriteEntries"/>). Out
        /// of line, one call per dictionary.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteFree(Dictionary<TKey, TValue> value, int count, ref byte destination, int available)
        {
            if (available < sizeof(int) || !CollectionFields.Run(value, count, out var run))
                return -1;
            if (!RuntimeHelpers.IsReferenceOrContainsReferences<CollectionFields.MapEntry<TKey, TValue>>())
            {
                // NinoRuns.Write's block, its one capacity check against the free space.
                int block = NinoKind.Stride<CollectionFields.MapEntry<TKey, TValue>, NinoMapEntryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>();
                if (block <= 0 || run.InUse != NinoRun.AllInUse || (long)count * block > available - sizeof(int))
                    return -1;
                NinoUnsafe.Write(ref destination, count);
                NinoElements.WriteBlock<CollectionFields.MapEntry<TKey, TValue>, NinoMapEntryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(
                    ref Unsafe.Add(ref destination, sizeof(int)),
                    ref Unsafe.As<byte, CollectionFields.MapEntry<TKey, TValue>>(ref run.First), count);
                return sizeof(int) + count * block;
            }

            NinoUnsafe.Write(ref destination, count);
            ulong left = NinoItems.WriteFree<TKey, TValue, TKeyFormatter, TValueFormatter>(run, KeyOffset, ValueOffset,
                ref Unsafe.Add(ref destination, sizeof(int)), available - sizeof(int));
            return left >> 32 != 0 ? -1 : available - (int)left;
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
                default(KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>).Serialize(pair, ref writer);
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

        /// <summary>
        /// Whether a dropped pair has a key or a value to recycle (<see cref="NinoRecycler.MayRecycle{T,TFormatter}"/>
        /// of each, the items asked directly: in code shared over a class, the pair's formatter
        /// would be asked through lookups and an indirect call).
        /// </summary>
        private static bool PairsMayRecycle
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoRecycler.MayRecycle<TKey, TKeyFormatter>() || NinoRecycler.MayRecycle<TValue, TValueFormatter>();
        }

        /// <summary>Recycles and clears an existing dictionary before it is refilled with <paramref name="length"/>
        /// pairs.</summary>
        public static void Reset(Dictionary<TKey, TValue> dictionary, int length, int remaining)
        {
            if (PairsMayRecycle)
                NinoRecycler.DiscardAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(dictionary);
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
                    // The merge in the cursor form, each key kept in seen and each value read over the one it maps to.
                    ulong read = MergeCursors(dictionary, seen, length, ref reader.Peek(), reader.Remaining);
                    reader.UnsafeAdvance((int)read);
                    matched = (int)(read >> 32);
                }
                else
                for (int i = 0; i < length; i++)
                {
                    TKey key = default;
                    default(TKeyFormatter).Deserialize(ref reader, ref key);
                    if (NinoUnsafe.IsNull(key))
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
                !PairsMayRecycle)
            {
                if (count == 1)
                    RemoveExcept(dictionary, keys[0]);
                else
                    RemoveExcept(dictionary, keys[0], keys[1]);
                return;
            }
            if (count is 3 or 4 && dictionary.Count <= 4 &&
                !PairsMayRecycle)
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
            if (!PairsMayRecycle)
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
            NinoRecycler.DiscardAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(stale);
        }

        /// <summary>
        /// Reads <paramref name="length"/> pairs into <paramref name="dictionary"/>, new or emptied: its entries filled
        /// and linked, or each added (<see cref="NinoEntries{TEntry,TFormatter,TTable}"/>, as a set's).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(Dictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            // Pairs holding strings: the string run kernels, the data and the bytes left to the call. Asked behind the
            // intrinsic: entries without references are blocks or values of their own, which read no field of the kind.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<CollectionFields.MapEntry<TKey, TValue>>() && CursorPairs)
            {
                if (length != 0)
                    reader.UnsafeAdvance(ReadStrings(dictionary, length, ref reader.Peek(), reader.Remaining));
                return;
            }

            NinoEntries<CollectionFields.MapEntry<TKey, TValue>, NinoMapEntryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>, MapTable<TKey, TValue>>
                .Read(new MapTable<TKey, TValue>(dictionary), length, ref reader);
        }

        /// <summary>
        /// Reads <paramref name="length"/> pairs holding strings (not 0, <see cref="CursorPairs"/>) into
        /// <paramref name="dictionary"/>, new or emptied, from the data at <paramref name="data"/>
        /// (<paramref name="end"/> bytes), the steps of <see cref="NinoEntries{TEntry,TFormatter,TTable}"/> through the
        /// string run kernels (<see cref="NinoItems.Read{TA,TB,TFormatterA,TFormatterB,TStore}"/>): into its
        /// entries and linked where it takes them (from <see cref="HashTables.FillFrom"/> on), else each read into one
        /// entry and added by a store generic over the side that is not a string (<see
        /// cref="NinoStringRuns.KeyAdds{TValue}"/>), so that no call per pair is indirect. Returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadStrings(Dictionary<TKey, TValue> dictionary, int length, ref byte data, int end)
        {
            bool insert = HashTables.CanInsert(dictionary, length);
            if (insert && length >= HashTables.FillFrom)
                return FillStrings(dictionary, length, ref data, end);

            CollectionFields.MapEntry<TKey, TValue> entry = default;
            var run = new NinoRun(ref Unsafe.As<CollectionFields.MapEntry<TKey, TValue>, byte>(ref entry), length, 0,
                NinoRun.NoKey, NinoRun.AllInUse);
            bool walk = HashTables.Plan<TKey, TValue>.Walks;
            // Keyed by strings where the dictionary guards its own buckets (.NET's randomized string hashing): each
            // pair added inline through the dictionary (GuardedPairAdds).
            if (!insert && !walk && NinoKind<TKeyFormatter>.Strings)
            {
                if (NinoKind<TValueFormatter>.Strings)
                    return NinoItems.Read<TKey, TValue, TKeyFormatter, TValueFormatter, NinoStringRuns.GuardedPairAdds>(
                        run, KeyOffset, ValueOffset, ref data, end,
                        new NinoStringRuns.GuardedPairAdds(Unsafe.As<Dictionary<string, string>>(dictionary)));
                return NinoItems.Read<TKey, TValue, TKeyFormatter, TValueFormatter,
                    NinoStringRuns.GuardedKeyAdds<TValue>>(run, KeyOffset, ValueOffset, ref data, end,
                    new NinoStringRuns.GuardedKeyAdds<TValue>(Unsafe.As<Dictionary<string, TValue>>(dictionary)));
            }

            // Told by the formatters' types: typeof(TKey) of a class is no constant in code shared over it.
            if (NinoKind<TKeyFormatter>.Strings && NinoKind<TValueFormatter>.Strings)
                return NinoItems.Read<TKey, TValue, TKeyFormatter, TValueFormatter, NinoStringRuns.PairAdds>(
                    run, KeyOffset, ValueOffset, ref data, end,
                    new NinoStringRuns.PairAdds(Unsafe.As<Dictionary<string, string>>(dictionary), insert, walk));
            if (NinoKind<TKeyFormatter>.Strings)
                return NinoItems.Read<TKey, TValue, TKeyFormatter, TValueFormatter,
                    NinoStringRuns.KeyAdds<TValue>>(run, KeyOffset, ValueOffset, ref data, end,
                    new NinoStringRuns.KeyAdds<TValue>(Unsafe.As<Dictionary<string, TValue>>(dictionary), insert, walk));
            return NinoItems.Read<TKey, TValue, TKeyFormatter, TValueFormatter,
                NinoStringRuns.ValueAdds<TKey>>(run, KeyOffset, ValueOffset, ref data, end,
                new NinoStringRuns.ValueAdds<TKey>(Unsafe.As<Dictionary<TKey, string>>(dictionary), insert, walk));
        }

        /// <summary><see cref="ReadStrings"/>' fill, the dictionary left empty if the data is rejected part way: an
        /// exception region in a method of its own (NinoEntries' FillEach).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FillStrings(Dictionary<TKey, TValue> dictionary, int length, ref byte data, int end)
        {
            try
            {
                int read = NinoItems.Read<TKey, TValue, TKeyFormatter, TValueFormatter,
                    NinoStringRuns.InPlace>(CollectionFields.Run(HashTables.Entries(dictionary), length), KeyOffset,
                    ValueOffset, ref data, end, default);
                HashTables.Link(dictionary, length);
                return read;
            }
            catch
            {
                HashTables.Unlink(dictionary);
                throw;
            }
        }

        private static int KeyOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => CollectionFields.MapEntry<TKey, TValue>.KeyOffset;
        }

        private static int ValueOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => CollectionFields.MapEntry<TKey, TValue>.ValueOffset;
        }

        /// <summary>
        /// Whether the pairs are moved by the string run kernels (<see cref="NinoItems.Pair{TA,TB,TFormatterA,
        /// TFormatterB}"/>): strings in either encoding with strings, raw values or blocks. A JIT-time constant.
        /// </summary>
        private static bool CursorPairs
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoItems.Pair<TKey, TValue, TKeyFormatter, TValueFormatter>();
        }

        /// <summary>
        /// <see cref="Refill"/>'s merge in the cursor form: <paramref name="length"/> pairs (not 0) from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes), each key kept in <paramref name="seen"/> and each
        /// value read over the one it maps to (kept when the data holds the same, a string's chars), a key not there
        /// before added with its bucket walked. Its key is read before its value's place is known, so the merge reads
        /// the two items itself (<see cref="NinoCursor.ReadAt{T,TFormatter}"/>), the dictionary's own layer. Returns
        /// the bytes read (low half) and the keys already there (high half).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong MergeCursors(Dictionary<TKey, TValue> dictionary, TKey[] seen, int length, ref byte data,
            int end)
        {
            int pos = 0, matched = 0;
            ref TKey keys = ref NinoUnsafe.GetArrayDataReference(seen);
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                pos += NinoCursor.ReadAt<TKey, TKeyFormatter>(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos,
                    ref key);
                if (NinoUnsafe.IsNull(key))
                    NinoThrow.NullKey(dictionary);
                Unsafe.Add(ref keys, i) = key;
#if NET8_0_OR_GREATER
                // Reading the value never touches this dictionary, so the reference stays valid.
                ref TValue slot = ref CollectionsMarshal.GetValueRefOrAddDefault(dictionary, key, out bool exists);
                if (exists)
                    matched++;
                else
                    Walked(dictionary, key);
                pos += NinoCursor.ReadAt<TValue, TValueFormatter>(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos,
                    ref slot);
#else
                bool exists = dictionary.TryGetValue(key, out TValue item);
                if (exists)
                    matched++;
                pos += NinoCursor.ReadAt<TValue, TValueFormatter>(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos,
                    ref item);
                dictionary[key] = item;
                if (!exists)
                    Walked(dictionary, key);
#endif
            }

            return ((ulong)(uint)matched << 32) | (uint)pos;
        }
    }
}
