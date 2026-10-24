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
                WriteRaw(value, ref writer.ReserveCounted(count, Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>()),
                    count);
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

        /// <summary>Writes the <paramref name="count"/> raw pairs at <paramref name="pairs"/> (after the count).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteRaw(Dictionary<TKey, TValue> value, ref byte pairs, int count)
        {
            int keySize = Unsafe.SizeOf<TKey>();
            int pairSize = keySize + Unsafe.SizeOf<TValue>();
            // The dictionary's own entries when none is free (as HashSetFormatter's); the enumerator out of line.
            var entries = CollectionViews.Dictionary ? CollectionFields.Entries(value, count) : null;
            if (entries == null || count > entries.Length)
            {
                if (count != 0)
                    WriteEnumerated(value, ref pairs, count);
                return;
            }

            if (count == 0)
                return;
            ref var entry = ref entries[0];
            int left = count;
            do
            {
                NinoUnsafe.Write(ref pairs, 0, entry.Key);
                NinoUnsafe.Write(ref pairs, keySize, entry.Value);
                NinoLayout<TKey>.ClearPadding(ref pairs);
                NinoLayout<TValue>.ClearPadding(ref Unsafe.Add(ref pairs, keySize));
                entry = ref Unsafe.Add(ref entry, 1);
                pairs = ref Unsafe.Add(ref pairs, pairSize);
            } while (--left != 0);
        }

        /// <summary>The <paramref name="count"/> raw pairs of a dictionary with free entries, in enumeration order.</summary>
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
            get => NinoElements.MinWireSize<TKey, TKeyFormatter>() + NinoElements.MinWireSize<TValue, TValueFormatter>();
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

        /// <summary>Recycles and clears an existing dictionary before it is refilled with <paramref name="length"/> pairs.</summary>
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
            // A new key's bucket walked after the dictionary adds it (HashTables.Plan, read once).
            bool walk = HashTables.Plan<TKey, TValue>.Walks;
            try
            {
                int matched = 0;
                for (int i = 0; i < length; i++)
                {
                    TKey key = default;
                    default(TKeyFormatter).Deserialize(ref key, ref reader);
                    if (key == null)
                        NinoThrow.NullKey(dictionary);
                    seen[i] = key;
#if NET8_0_OR_GREATER
                    // Reading the value never touches this dictionary, so the reference stays valid.
                    ref TValue slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                        dictionary, key, out bool exists);
                    if (exists)
                        matched++;
                    else if (walk)
                        HashTables.Walk(dictionary, key);
                    default(TValueFormatter).Deserialize(ref slot, ref reader);
#else
                    bool exists = dictionary.TryGetValue(key, out TValue item);
                    if (exists)
                        matched++;
                    default(TValueFormatter).Deserialize(ref item, ref reader);
                    dictionary[key] = item;
                    if (!exists && walk)
                        HashTables.Walk(dictionary, key);
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

        /// <summary>Removes (and recycles) the entries whose key is not among the <paramref name="count"/> read.</summary>
        private static void RemoveStale(Dictionary<TKey, TValue> dictionary, TKey[] keys, int count)
        {
            // A key read twice is one key (the last value won).
            var read = new HashSet<TKey>(dictionary.Comparer);
            for (int i = 0; i < count; i++)
                read.Add(keys[i]);

            var stale = new List<KeyValuePair<TKey, TValue>>(dictionary.Count - read.Count);
            foreach (var pair in dictionary)
            {
                if (!read.Contains(pair.Key))
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

            // Out of line, through a copy of the reader (see AddRaw); an empty dictionary skips both.
            if (length == 0)
                return;
            Reader copy = default;
            reader.CopyTo(ref copy);
            // Only a reference-type key can be null (a JIT-time constant).
            if (default(TKey) == null)
                ReadPairsChecked(dictionary, length, ref copy);
            else
                ReadPairs(dictionary, length, ref copy);
            copy.CopyTo(ref reader);
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
            Reader reader = default;
            source.CopyTo(ref reader);
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref reader);
                default(TValueFormatter).Deserialize(ref item, ref reader);
                NinoCollections.AddUnique(dictionary, key, item);
            }

            reader.CopyTo(ref source);
        }

        /// <summary><see cref="ReadPairs"/>, each key inserted or its bucket walked (hash flooding).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadGuarded(Dictionary<TKey, TValue> dictionary, int length, ref Reader source)
        {
            // A local reader, so its fields stay in registers inside the loop.
            Reader reader = default;
            source.CopyTo(ref reader);
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
                default(TKeyFormatter).Deserialize(ref key, ref reader);
                default(TValueFormatter).Deserialize(ref item, ref reader);
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

            reader.CopyTo(ref source);
        }
    }

    /// <summary>
    /// <c>Dictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then key/value pairs. An existing dictionary is
    /// refilled in place (merged by key), keeping its comparer.
    /// </summary>
    public struct DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<Dictionary<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(Dictionary<TKey, TValue> value, ref Writer writer) =>
            default(DictionaryFormatter<Dictionary<TKey, TValue>, TKey, TValue, TKeyFormatter, TValueFormatter>)
                .Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Dictionary<TKey, TValue> value, ref Reader reader) =>
            default(DictionaryFormatter<Dictionary<TKey, TValue>, TKey, TValue, TKeyFormatter, TValueFormatter>)
                .Deserialize(ref value, ref reader);
    }

    /// <summary>
    /// A type derived from <c>Dictionary&lt;TKey, TValue&gt;</c> (and <c>Dictionary&lt;TKey, TValue&gt;</c> itself):
    /// count (-1 = null), then key/value pairs. An existing dictionary is refilled in place (merged by key), keeping
    /// its comparer.
    /// </summary>
    public struct DictionaryFormatter<[NinoCreated] TDictionary, TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<TDictionary>
        where TDictionary : Dictionary<TKey, TValue>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TDictionary value, ref Writer writer)
        {
            if (!writer.TryWriteNullLength(value))
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(value, ref writer);
        }

        public void Deserialize(ref TDictionary value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value,
                    NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.MinPairSize, out int length))
                return;

            if (value == null && typeof(TDictionary) == typeof(Dictionary<TKey, TValue>))
            {
                // A new plain dictionary, sized for the pairs: filled without the merge of Refill.
                int capacity = NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Capacity(length, reader.Remaining);
                // NinoSettings.Untrusted (a constant): seeded comparers, out of line.
                var created = NinoLimits.Untrusted
                    ? NinoSeededHash.NewDictionary<TKey, TValue>(capacity)
                    : new Dictionary<TKey, TValue>(capacity);
                value = Unsafe.As<TDictionary>(created);
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Read(created, length, ref reader);
                return;
            }

            value ??= NinoActivator<TDictionary>.Create();
            // Through a copy: the reader's own address must not reach a call that is not inlined (see
            // NinoObject.ReadBody), or none of its fields stay in registers in the caller.
            Reader copy = default;
            reader.CopyTo(ref copy);
            NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Refill(value, length, ref copy);
            copy.CopyTo(ref reader);
        }
    }

    /// <summary>
    /// Any <c>IDictionary&lt;TKey, TValue&gt;</c> Nino can create (<c>SortedDictionary</c>,
    /// <c>SortedList</c>, <c>ConcurrentDictionary</c>, user dictionaries...): count (-1 = null), then key/value pairs.
    /// </summary>
    public struct DictionaryLikeFormatter<[NinoCreated] TDictionary, TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<TDictionary>
        where TDictionary : class, IDictionary<TKey, TValue>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        public void Serialize(TDictionary value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>.TryWrite(value, ref writer))
                return;

            NinoCollections.WriteEnumerable<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(value, ref writer);
        }

        public void Deserialize(ref TDictionary value, ref Reader reader)
        {
            // 1 byte a pair: what reads them into an array first (sorted and concurrent dictionaries,
            // NinoCollections.ReadRented) bounds it by the pairs' own fewest bytes (KeyValuePairFormatter's), which
            // this is for pairs larger in memory than on the wire.
            if (!reader.TryReadLengthOrDiscard(ref value, 1, out int length))
                return;

            if (value == null)
            {
                value = BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>.TryCreate<TDictionary>(length,
                    ref reader);
                if (value != null)
                    return;
            }

            var dictionary = value ??= NinoActivator<TDictionary>.Create();
            if (dictionary.IsReadOnly)
                NinoThrow.ReadOnlyCollection(dictionary.GetType());
            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            if (dictionary.Count != 0)
            {
                NinoRecycler.DiscardPairs<TKey, TValue>(dictionary);
                dictionary.Clear();
            }

            if (BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>.TryRead(dictionary, length, ref reader))
                return;

            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref reader);
                default(TValueFormatter).Deserialize(ref item, ref reader);
                NinoCollections.AddUnique(dictionary, key, item, i);
            }
        }
    }

    /// <summary>
    /// Dictionary interfaces (<c>IDictionary&lt;TKey, TValue&gt;</c>, <c>IReadOnlyDictionary&lt;TKey, TValue&gt;</c>)
    /// and <c>ReadOnlyDictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then key/value pairs.
    /// Read into a <c>Dictionary&lt;TKey, TValue&gt;</c>.
    /// </summary>
    public struct InterfaceDictionaryFormatter<TInterface, TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<TInterface>
        where TInterface : class, IEnumerable<KeyValuePair<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        public void Serialize(TInterface value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (value is Dictionary<TKey, TValue> dictionary)
            {
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(dictionary, ref writer);
                return;
            }

            // The runtime's other dictionaries through their own paths (a ConcurrentDictionary without reading its
            // count, which takes every lock and may not match the pairs enumerated after it).
            if (value is IDictionary<TKey, TValue> other &&
                BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>.TryWrite(other, ref writer))
                return;

            NinoCollections.WriteEnumerable<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(value, ref writer);
        }

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            // A Dictionary is handed to its formatter (which reuses or drops it); anything else is recycled here. A
            // ReadOnlyDictionary is never a Dictionary, so it is always recycled and re-created around a new one.
            var dictionary = NinoRecycler.Reuse<TInterface, Dictionary<TKey, TValue>>(ref value);
            default(DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>).Deserialize(ref dictionary, ref reader);
            if (dictionary == null)
                value = null;
            else if (typeof(TInterface) == typeof(ReadOnlyDictionary<TKey, TValue>))
                value = (TInterface)(object)new ReadOnlyDictionary<TKey, TValue>(dictionary);
            else
                value = (TInterface)(object)dictionary;
        }
    }

    /// <summary><c>KeyValuePair&lt;TKey, TValue&gt;</c>: key, then value.</summary>
    public struct KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<KeyValuePair<TKey, TValue>>, INinoBlockFormatter<KeyValuePair<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        private static bool Raw
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => RawFormatter.Is<TKey, TKeyFormatter>() && RawFormatter.Is<TValue, TValueFormatter>();
        }

        int INinoBlockFormatter<KeyValuePair<TKey, TValue>>.BlockSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Raw ? NinoLayout<TKey>.Size + NinoLayout<TValue>.Size : -1;
        }

        // Its fields are private: found by marking each item's bytes.
        NinoBlockMap INinoBlockFormatter<KeyValuePair<TKey, TValue>>.BlockMap()
        {
            if (!Raw)
                return null;
            int key = NinoBlocks.Find(NinoLayout<TKey>.Size,
                () => new KeyValuePair<TKey, TValue>(NinoBlocks.Marked<TKey>(), default));
            int value = NinoBlocks.Find(NinoLayout<TValue>.Size,
                () => new KeyValuePair<TKey, TValue>(default, NinoBlocks.Marked<TValue>()));
            return new NinoBlockMap().Raw<TKey>(key, 0).Raw<TValue>(value, NinoLayout<TKey>.Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoBlockFormatter<KeyValuePair<TKey, TValue>>.WriteBlock(ref byte destination,
            in KeyValuePair<TKey, TValue> value) => WriteRaw(ref destination, in value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoBlockFormatter<KeyValuePair<TKey, TValue>>.ReadBlock(ref byte source,
            ref KeyValuePair<TKey, TValue> value) => value = ReadRaw(ref source);

        /// <summary>The key and the value of a pair of raw items, at <paramref name="destination"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRaw(ref byte destination, in KeyValuePair<TKey, TValue> value)
        {
            NinoLayout<TKey>.Write(ref destination, value.Key);
            NinoLayout<TValue>.Write(ref Unsafe.Add(ref destination, NinoLayout<TKey>.Size), value.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static KeyValuePair<TKey, TValue> ReadRaw(ref byte source) =>
            new KeyValuePair<TKey, TValue>(NinoLayout<TKey>.Read(ref source),
                NinoLayout<TValue>.Read(ref Unsafe.Add(ref source, NinoLayout<TKey>.Size)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(KeyValuePair<TKey, TValue> value, ref Writer writer)
        {
            // Raw items (a constant check): one capacity check for the pair, as for a tuple of them.
            if (Raw)
            {
                WriteRaw(ref writer.Reserve(NinoLayout<TKey>.Size + NinoLayout<TValue>.Size), in value);
                return;
            }

            default(TKeyFormatter).Serialize(value.Key, ref writer);
            default(TValueFormatter).Serialize(value.Value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref KeyValuePair<TKey, TValue> value, ref Reader reader)
        {
            if (Raw)
            {
                value = ReadRaw(ref reader.ReadBlock(NinoLayout<TKey>.Size + NinoLayout<TValue>.Size));
                return;
            }

            TKey key = value.Key;
            TValue item = value.Value;
            default(TKeyFormatter).Deserialize(ref key, ref reader);
            default(TValueFormatter).Deserialize(ref item, ref reader);
            value = new KeyValuePair<TKey, TValue>(key, item);
        }
    }

    internal static partial class NinoCollections
    {
        /// <summary><c>Dictionary.TryAdd</c>, which netstandard2.0 lacks.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
#if NETSTANDARD2_0
            int before = dictionary.Count;
            dictionary[key] = value;
            return dictionary.Count != before;
#else
            return dictionary.TryAdd(key, value);
#endif
        }

        /// <summary>Adds a pair; a key the data repeats (under the dictionary's comparer) is invalid data.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddUnique<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (!TryAdd(dictionary, key, value))
                NinoThrow.DuplicateKey(dictionary.GetType());
        }

        /// <summary>
        /// Adds a pair to a dictionary that held <paramref name="count"/> pairs, through its indexer (which throws
        /// for a null key); a key the data repeats is invalid data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddUnique<TKey, TValue>(IDictionary<TKey, TValue> dictionary, TKey key, TValue value,
            int count)
        {
            if (key == null)
                AddNullKey(dictionary, value);
            else
                dictionary[key] = value;
            if (dictionary.Count != count + 1)
                NinoThrow.DuplicateKey(dictionary.GetType());
        }

        /// <summary>
        /// A null key, which a dictionary of its own may take; the runtime's throw ArgumentNullException, reported as
        /// invalid data.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddNullKey<TKey, TValue>(IDictionary<TKey, TValue> dictionary, TValue value)
        {
            try
            {
                dictionary[default] = value;
            }
            catch (ArgumentNullException)
            {
                NinoThrow.NullKey(dictionary.GetType());
            }
        }
    }
}
