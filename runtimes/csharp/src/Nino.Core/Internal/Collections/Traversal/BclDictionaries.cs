using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <see cref="DictionaryLikeFormatter{TDictionary,TKey,TValue,TKeyFormatter,TValueFormatter}"/> for the runtime's
    /// own dictionaries (and types derived from them), as <see cref="BclCollections{T,TFormatter}"/> for collections.
    /// </summary>
    internal static class BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        /// <summary>Writes <paramref name="value"/> (not null) if it is one of these dictionaries.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryWrite(IDictionary<TKey, TValue> value, ref Writer writer)
        {
            switch (value)
            {
                case SortedDictionary<TKey, TValue> sorted:
                    writer = WriteSorted(sorted, writer);
                    return true;
                case SortedList<TKey, TValue> list when CollectionViews.SortedList:
                    WriteSortedList(list, ref writer);
                    return true;
                case ConcurrentDictionary<TKey, TValue> concurrent:
                    writer = WriteConcurrent(concurrent, writer);
                    return true;
                default:
                    return false;
            }
        }


        /// <summary>
        /// Walked out of its tree a chunk at a time, as a SortedSet is (BclCollections), else through its enumerator; out
        /// of line with the writer by value, as there.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteSorted(SortedDictionary<TKey, TValue> sorted, Writer writer)
        {
            object root = SortedSetTrees.RootOf(sorted);
            return root != null
                ? NinoGather.Write<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    SortedSetNodes<KeyValuePair<TKey, TValue>>.Walk>(SortedSetNodes<KeyValuePair<TKey, TValue>>.Items(root), sorted.Count, writer)
                : NinoGather.Write<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    NinoGather.Enumerated<KeyValuePair<TKey, TValue>, SortedDictionary<TKey, TValue>.Enumerator>>(
                    new(sorted.GetEnumerator()), sorted.Count, writer);
        }

        /// <summary>
        /// Reads <paramref name="length"/> pairs into <paramref name="dictionary"/> (empty) if it is one of these
        /// dictionaries. A key that is null or there twice is reported as the general path reports it.
        /// </summary>
        public static bool TryRead(IDictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            switch (dictionary)
            {
                case SortedDictionary<TKey, TValue> sorted:
                    ReadSorted(sorted, length, ref reader);
                    return true;
                case SortedList<TKey, TValue> list:
#if NET8_0_OR_GREATER
                    if (typeof(TKey) == typeof(int) && typeof(TValue) == typeof(int) && length >= 16 &&
                        TryReadSortedInt32Pairs(list, length, ref reader))
                        return true;
                    if (NinoSimd.CanSplitOrderedPairs(length) && typeof(TKey) == typeof(TValue) &&
                        typeof(TKey) != typeof(int) && PairKey.Code is >= TypeCode.SByte and <= TypeCode.UInt64 &&
                        TryReadMatchingOrderedPairs(list, length, ref reader))
                        return true;
#endif
                    ReadSortedList(list, length, ref reader);
                    return true;
                case ConcurrentDictionary<TKey, TValue> concurrent:
                    ReadConcurrent(concurrent, length, ref reader);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A new <typeparamref name="TDictionary"/> of the <paramref name="length"/> pairs that follow, if it is one the
        /// runtime builds faster from a collection than pair by pair; otherwise null, and nothing is read.
        /// </summary>
        public static TDictionary TryCreate<TDictionary>(int length, ref Reader reader) where TDictionary : class =>
            typeof(TDictionary) == typeof(ConcurrentDictionary<TKey, TValue>)
                ? (TDictionary)(object)CreateConcurrent(length, ref reader)
                : null;

        /// <summary>
        /// From the pairs read as an array: its constructor sizes the table for them and adds them without the locks,
        /// where TryAdd takes a lock per pair and grows the table as it fills.
        /// </summary>
        private static ConcurrentDictionary<TKey, TValue> CreateConcurrent(int length, ref Reader reader)
        {
            // Where its table is known, built here instead, each key's bucket counted as it is filled
            // (ConcurrentTables): the constructor gives no point between two pairs to count the keys in a bucket at.
            if (ConcurrentTables.Plan<TKey, TValue>.Builds)
                return CreateConcurrentBuilt(length, ref reader);
            if (ConcurrentTables.Plan<TKey, TValue>.Walks)
            {
                // Where the table can be read but not built: a pair at a time through TryAdd (a lock per pair), each
                // key's bucket walked after it is added.
                var walked = ConcurrentTables.Create<TKey, TValue>(length);
                ReadConcurrent(walked, length, ref reader);
                return walked;
            }

            var pairs = NinoCollections.ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(length,
                ref reader);
            try
            {
                return new ConcurrentDictionary<TKey, TValue>(
                    new ArraySegment<KeyValuePair<TKey, TValue>>(pairs, 0, length));
            }
            catch (ArgumentNullException)
            {
                NinoThrow.NullKey(typeof(ConcurrentDictionary<TKey, TValue>));
                throw;
            }
            catch (ArgumentException)
            {
                NinoThrow.DuplicateKey(typeof(ConcurrentDictionary<TKey, TValue>));
                throw;
            }
            finally
            {
                NinoCollections.ReturnRented(pairs);
            }
        }

        /// <summary>
        /// A new dictionary sized and configured as its constructor from the pairs makes it, its table filled as that
        /// constructor fills it, without locks (<see cref="ConcurrentTables.Builder{TKey,TValue}"/>), each key's
        /// bucket counted (hash flooding, <see cref="NinoSettings.MaxHashCollisions"/>). The first pair of the first
        /// dictionary of these types is added through its public API, to learn the type of its nodes.
        /// </summary>
        private static ConcurrentDictionary<TKey, TValue> CreateConcurrentBuilt(int length, ref Reader reader)
        {
            var builds = new Builds(ConcurrentTables.Create<TKey, TValue>(length));
            reader = NinoGather.ReadAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>, Builds>(ref builds,
                length, reader);
            return builds.Concurrent;
        }

        /// <summary>From its two arrays.</summary>
        private static void WriteSortedList(SortedList<TKey, TValue> list, ref Writer writer)
        {
            int count = CollectionFields.Size(list);
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if ((uint)count > (uint)keys.Length || (uint)count > (uint)values.Length)
                NinoThrow.ModifiedDuringWrite();
            if (NinoKind.Of<TKey, TKeyFormatter>() == NinoElementKind.Raw &&
                NinoKind.Of<TValue, TValueFormatter>() == NinoElementKind.Raw)
            {
                ref byte target = ref writer.ReserveCounted(count, Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>());
#if NET8_0_OR_GREATER
                if ((Unsafe.SizeOf<TKey>() == 2 || Unsafe.SizeOf<TKey>() == 8) &&
                    (NinoSimd.TryInterleave<short, NinoSimd.Zip16, TKey, TValue>(keys, values, ref target, count) ||
                     NinoSimd.TryInterleave<long, NinoSimd.Zip64, TKey, TValue>(keys, values, ref target, count)))
                    return;
#endif
                if (count != 0)
                    WriteSortedListRaw(keys, values, count, ref target);
                return;
            }

            // Its two arrays zipped into pairs a chunk at a time, written by the pairs' run loop; through a copy of the
            // writer, as the pair loop it replaces, so the caller's stays in registers on the raw path above (asm.py:
            // SortedList<int, int>'s write 27 to 40 stack accesses with the writer itself passed).
            Writer local = default;
            writer.CopyTo(ref local);
            local = NinoGather.Write<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>, Zip>(new Zip(keys, values, count),
                count, local);
            local.CopyTo(ref writer);
        }

        /// <summary>A <see cref="SortedList{TKey,TValue}"/>'s keys and values, zipped into its pairs.</summary>
        private struct Zip : INinoGatherSource<KeyValuePair<TKey, TValue>>
        {
            private readonly TKey[] _keys;
            private readonly TValue[] _values;
            private readonly int _count;
            private int _at;

            public Zip(TKey[] keys, TValue[] values, int count)
            {
                _keys = keys;
                _values = values;
                _count = count;
                _at = 0;
            }

            public int Fill(Span<KeyValuePair<TKey, TValue>> chunk)
            {
                int filled = Math.Min(_count - _at, chunk.Length);
                // count is at most the arrays' lengths (checked by the caller).
                ref TKey key = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_keys), _at);
                ref TValue value = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_values), _at);
                ref var pair = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(chunk);
                for (int i = 0; i < filled; i++)
                    Unsafe.Add(ref pair, i) = new KeyValuePair<TKey, TValue>(Unsafe.Add(ref key, i), Unsafe.Add(ref value, i));
                _at += filled;
                return filled;
            }

            public void End()
            {
            }
        }

        // The two arrays and the single destination reservation were checked by the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteSortedListRaw(TKey[] keys, TValue[] values, int count, ref byte target)
        {
            int keySize = Unsafe.SizeOf<TKey>();
            int pairSize = keySize + Unsafe.SizeOf<TValue>();
            ref TKey key = ref NinoUnsafe.GetArrayDataReference(keys);
            ref TValue value = ref NinoUnsafe.GetArrayDataReference(values);
#if NET8_0_OR_GREATER
            if (NinoSimd.IsPackedLane32<TKey>() && NinoSimd.IsPackedLane32<TValue>())
            {
                if (count == 1)
                {
                    Unsafe.WriteUnaligned(ref target, Unsafe.As<TKey, int>(ref key));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, 4), Unsafe.As<TValue, int>(ref value));
                    return;
                }

                if (NinoSimd.CanInterleave<int>(count))
                {
                    NinoSimd.InterleaveBest<int, NinoSimd.Zip32>(ref Unsafe.As<TKey, int>(ref key),
                        ref Unsafe.As<TValue, int>(ref value), ref target, count);
                    return;
                }
            }
#else
            int pairs = NinoSimd.TryInterleave32(ref key, ref value, ref target, count);
            if (pairs != 0)
            {
                key = ref Unsafe.Add(ref key, pairs);
                value = ref Unsafe.Add(ref value, pairs);
                target = ref Unsafe.Add(ref target, pairs * 8);
                count -= pairs;
            }
#endif
            WriteSortedListScalar(ref key, ref value, count, ref target, keySize, pairSize);
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
#else
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static void WriteSortedListScalar(ref TKey key, ref TValue value, int count, ref byte target,
            int keySize, int pairSize)
        {
            for (int i = 0; i < count; i++)
            {
                NinoUnsafe.WriteValueIn(ref target, 0, in key);
                NinoUnsafe.WriteValueIn(ref target, keySize, in value);
                key = ref Unsafe.Add(ref key, 1);
                value = ref Unsafe.Add(ref value, 1);
                target = ref Unsafe.Add(ref target, pairSize);
            }
        }

        /// <summary>
        /// Through its own enumerator, typed here rather than left to the shared loop of
        /// <see cref="NinoCollections.WriteEnumerable{T,TFormatter}"/>, whose guess at the dictionary type is made
        /// once for every dictionary with these keys and values (4x slower when a Dictionary was seen first), and
        /// without its count (which takes every lock, and may not match the pairs enumerated after it).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteConcurrent(ConcurrentDictionary<TKey, TValue> concurrent, Writer writer) =>
            NinoGather.Write<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                NinoGather.Enumerated<KeyValuePair<TKey, TValue>, IEnumerator<KeyValuePair<TKey, TValue>>>>(new(concurrent.GetEnumerator()), -1, writer);

        /// <summary>
        /// The pairs of a new dictionary filled as its constructor from the pairs fills it, without locks
        /// (<see cref="ConcurrentTables.Builder{TKey,TValue}"/>), each key's bucket counted. The first pair of the first
        /// dictionary of these types is added through its public API, to learn the type of its nodes.
        /// </summary>
        private struct Builds : INinoGatherTarget<KeyValuePair<TKey, TValue>>
        {
            public readonly ConcurrentDictionary<TKey, TValue> Concurrent;
            private IntPtr _nodes;
            private ConcurrentTables.Builder<TKey, TValue> _builder;

            public Builds(ConcurrentDictionary<TKey, TValue> concurrent)
            {
                Concurrent = concurrent;
                _nodes = ConcurrentTables.Plan<TKey, TValue>.NodeType;
                _builder = _nodes != IntPtr.Zero ? new ConcurrentTables.Builder<TKey, TValue>(concurrent, _nodes) : default;
            }

            public void Add(Span<KeyValuePair<TKey, TValue>> chunk)
            {
                foreach (var pair in chunk)
                {
                    if (pair.Key == null)
                        NinoThrow.NullKey(Concurrent);
                    if (_nodes == IntPtr.Zero)
                    {
                        // Empty, so it cannot hold the key yet.
                        Concurrent.TryAdd(pair.Key, pair.Value);
                        _nodes = ConcurrentTables.TypeOfNodes(Concurrent, pair.Key);
                        ConcurrentTables.Plan<TKey, TValue>.NodeType = _nodes;
                        _builder = new ConcurrentTables.Builder<TKey, TValue>(Concurrent, _nodes);
                    }
                    else if (!_builder.Add(pair.Key, pair.Value))
                        NinoThrow.DuplicateKey(Concurrent.GetType());
                }
            }
        }

        /// <summary>A pair added through TryAdd, its key's bucket walked after (hash flooding).</summary>
        private struct TryAdd : INinoAdder<ConcurrentDictionary<TKey, TValue>, KeyValuePair<TKey, TValue>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(ConcurrentDictionary<TKey, TValue> target, KeyValuePair<TKey, TValue> pair)
            {
                if (pair.Key == null)
                    NinoThrow.NullKey(target);
                if (!target.TryAdd(pair.Key, pair.Value))
                    NinoThrow.DuplicateKey(target.GetType());
                ConcurrentTables.Added(target, pair.Key);
            }
        }

        /// <summary>
        /// Through TryAdd: its Count takes every lock, and the general path reads it per pair (27x slower).
        /// </summary>
        private static void ReadConcurrent(ConcurrentDictionary<TKey, TValue> concurrent, int length, ref Reader reader)
        {
            var adds = new NinoGather.Adds<ConcurrentDictionary<TKey, TValue>, KeyValuePair<TKey, TValue>, TryAdd>(
                concurrent);
            reader = NinoGather.ReadAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter,
                TValueFormatter>, NinoGather.Adds<ConcurrentDictionary<TKey, TValue>, KeyValuePair<TKey, TValue>,
                TryAdd>>(ref adds, length, reader);
        }

        /// <summary>
        /// Read as an array of pairs, then built in one pass where the tree can be moved in and the keys are not null
        /// or repeated, otherwise added one by one.
        /// </summary>
        private static void ReadSorted(SortedDictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            var pairs = NinoCollections.ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(length,
                ref reader);
            try
            {
                if (CollectionViews.SortedDictionary && !HasNullKey(pairs, length))
                {
                    var set = CollectionFields.Set(dictionary);
                    if (SortedSetTrees.Fill(set, pairs, length) == length)
                        return;
                    set.Clear();
                }

                AddEach(dictionary, pairs, length);
            }
            finally
            {
                NinoCollections.ReturnRented(pairs);
            }
        }

        /// <summary>
        /// Read a chunk at a time straight into its two arrays (<see cref="Split"/>), then published when the keys
        /// come in order under its comparer (as Nino writes them), whatever the keys and values are (#350); otherwise,
        /// or where its layout is not known or the arrays would take far more memory than the data, added one by one.
        /// </summary>
        private static void ReadSortedList(SortedList<TKey, TValue> list, int length, ref Reader reader)
        {
            if (!CollectionViews.SortedList || length == 0 ||
                !NinoElements.Fits(length, Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>(), reader.Remaining))
            {
                var adds = new NinoGather.Adds<SortedList<TKey, TValue>, KeyValuePair<TKey, TValue>, AddUnique>(list);
                reader = NinoGather.ReadAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    NinoGather.Adds<SortedList<TKey, TValue>, KeyValuePair<TKey, TValue>, AddUnique>>(ref adds, length, reader);
                return;
            }

            // Its own arrays when they are large enough (Clear cleared them), otherwise new ones; the list stays empty
            // until its size is published, so a read rejected half way leaves it empty.
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if (keys.Length < length)
                CollectionFields.Keys(list) = keys = new TKey[length];
            if (values.Length < length)
                CollectionFields.Values(list) = values = new TValue[length];
            var split = new Split(keys, values);
            reader = NinoGather.ReadAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>, Split>(ref split, length, reader);
            if (InOrder(keys, length, CollectionFields.Comparer(list)))
            {
                CollectionFields.Size(list) = length;
                CollectionFields.Version(list)++;
                return;
            }

            // Out of order under this comparer (or a null key): moved out of the arrays and added one by one.
            var pairs = new KeyValuePair<TKey, TValue>[length];
            for (int i = 0; i < length; i++)
                pairs[i] = new KeyValuePair<TKey, TValue>(keys[i], values[i]);
            Array.Clear(keys, 0, length);
            Array.Clear(values, 0, length);
            AddEach(list, pairs, length);
        }

        /// <summary>The pairs of each chunk split into a <see cref="SortedList{TKey,TValue}"/>'s keys and values.</summary>
        private struct Split : INinoGatherTarget<KeyValuePair<TKey, TValue>>
        {
            private readonly TKey[] _keys;
            private readonly TValue[] _values;
            private int _at;

            public Split(TKey[] keys, TValue[] values)
            {
                _keys = keys;
                _values = values;
                _at = 0;
            }

            public void Add(Span<KeyValuePair<TKey, TValue>> chunk)
            {
                // Every pair has its place: the arrays were sized for the count the reads stop at.
                ref TKey key = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_keys), _at);
                ref TValue value = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_values), _at);
                if ((uint)(_at + chunk.Length) > (uint)_keys.Length || (uint)(_at + chunk.Length) > (uint)_values.Length)
                    NinoThrow.EndOfData();
                _at += chunk.Length;
                ref var pair = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(chunk);
#if NET8_0_OR_GREATER
                // Pairs of two 4-byte values whose memory is their wire form: one vector pass.
                if (Unsafe.SizeOf<TKey>() == 4 && Unsafe.SizeOf<TValue>() == 4 && NinoSimd.CanInterleave<int>(chunk.Length) &&
                    NinoBlockPlan<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>.IsMemory)
                {
                    NinoSimd.DeinterleaveInt32(ref Unsafe.As<KeyValuePair<TKey, TValue>, byte>(ref pair), ref Unsafe.As<TKey, int>(ref key),
                        ref Unsafe.As<TValue, int>(ref value), chunk.Length);
                    return;
                }
#endif
                for (int n = chunk.Length; n > 0; n--)
                {
                    key = pair.Key;
                    value = pair.Value;
                    pair = ref Unsafe.Add(ref pair, 1);
                    key = ref Unsafe.Add(ref key, 1);
                    value = ref Unsafe.Add(ref value, 1);
                }
            }
        }

        private struct AddUnique : INinoAdder<SortedList<TKey, TValue>, KeyValuePair<TKey, TValue>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(SortedList<TKey, TValue> target, KeyValuePair<TKey, TValue> item) =>
                NinoCollections.AddUnique(target, item.Key, item.Value, target.Count);
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool CanReadRawSortedPairs(SortedList<TKey, TValue> list) =>
            CollectionViews.SortedList && NinoKind.Of<TKey, TKeyFormatter>() == NinoElementKind.Raw &&
            NinoKind.Of<TValue, TValueFormatter>() == NinoElementKind.Raw &&
            ReferenceEquals(CollectionFields.Comparer(list), Comparer<TKey>.Default);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadSortedInt32Pairs(SortedList<TKey, TValue> list, int length, ref Reader reader)
        {
            if (!CanReadRawSortedPairs(list))
                return false;

            ref byte source = ref reader.ReadElements(length, 8);
            ReadSortedInt32Pairs(Unsafe.As<SortedList<int, int>>(list), ref source, length);
            return true;
        }

        private static class PairKey
        {
            // Type.GetTypeCode also reports the underlying integer type of an enum.
            internal static readonly TypeCode Code = Type.GetTypeCode(typeof(TKey));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadMatchingOrderedPairs(SortedList<TKey, TValue> list, int length, ref Reader reader)
        {
            if (!CanReadRawSortedPairs(list))
                return false;

            return PairKey.Code switch
            {
                TypeCode.SByte => TrySplitOrderedPairs<sbyte>(list, length, ref reader),
                TypeCode.Byte => TrySplitOrderedPairs<byte>(list, length, ref reader),
                TypeCode.Int16 => TrySplitOrderedPairs<short>(list, length, ref reader),
                TypeCode.UInt16 => TrySplitOrderedPairs<ushort>(list, length, ref reader),
                TypeCode.Int32 => TrySplitOrderedPairs<int>(list, length, ref reader),
                TypeCode.UInt32 => TrySplitOrderedPairs<uint>(list, length, ref reader),
                TypeCode.Int64 => TrySplitOrderedPairs<long>(list, length, ref reader),
                TypeCode.UInt64 => TrySplitOrderedPairs<ulong>(list, length, ref reader),
                _ => false,
            };
        }

        private static bool TrySplitOrderedPairs<T>(SortedList<TKey, TValue> list, int length, ref Reader reader)
            where T : unmanaged, IComparable<T>
        {
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if (keys.Length < length || values.Length < length)
                return false;

            ref byte source = ref reader.ReadElements(length, 2 * Unsafe.SizeOf<T>());
            bool ordered = NinoSimd.SplitOrderedPairs(ref source,
                ref Unsafe.As<TKey, T>(ref NinoUnsafe.GetArrayDataReference(keys)),
                ref Unsafe.As<TValue, T>(ref NinoUnsafe.GetArrayDataReference(values)), length);
            FinishSortedPairs(list, ref source, length, ordered);
            return true;
        }

        private static void ReadSortedInt32Pairs(SortedList<int, int> list, ref byte source, int length)
        {
            int[] keys = CollectionFields.Keys(list), values = CollectionFields.Values(list);
            bool ordered;
            if (NinoSimd.CanSplitOrderedPairs(length) && keys.Length >= length && values.Length >= length)
            {
                ordered = NinoSimd.SplitOrderedPairs(ref source, ref NinoUnsafe.GetArrayDataReference(keys),
                    ref NinoUnsafe.GetArrayDataReference(values), length);
            }
            else
            {
                ordered = NinoSimd.AreStrictlyIncreasingInt32(ref source, length);
                if (ordered)
                {
                    if (keys.Length < length)
                        CollectionFields.Keys(list) = keys = new int[length];
                    if (values.Length < length)
                        CollectionFields.Values(list) = values = new int[length];
                    NinoSimd.DeinterleaveInt32(ref source, ref NinoUnsafe.GetArrayDataReference(keys),
                        ref NinoUnsafe.GetArrayDataReference(values), length);
                }
            }

            FinishSortedPairs(list, ref source, length, ordered);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FinishSortedPairs<TReadKey, TReadValue>(SortedList<TReadKey, TReadValue> list, ref byte source, int length, bool ordered)
        {
            if (ordered)
            {
                CollectionFields.Size(list) = length;
                CollectionFields.Version(list)++;
                return;
            }

            // The list is still empty. The comparer fallback reads only its published entries.
            int keySize = Unsafe.SizeOf<TReadKey>();
            int pairSize = keySize + Unsafe.SizeOf<TReadValue>();
            for (int j = 0; j < length; j++)
            {
                ref byte pair = ref Unsafe.Add(ref source, j * pairSize);
                NinoCollections.AddUnique(list, NinoUnsafe.Read<TReadKey>(ref pair),
                    NinoUnsafe.Read<TReadValue>(ref Unsafe.Add(ref pair, keySize)), j);
            }
        }
#endif

        /// <summary>Whether the keys are not null and strictly ascending under <paramref name="comparer"/>.</summary>
        /// <remarks><paramref name="count"/> is not 0 and at most the array's length.</remarks>
        private static bool InOrder(TKey[] keys, int count, IComparer<TKey> comparer)
        {
            // Indexed from the first key, without a bounds check per key. An index, not a cursor: a cursor bumped
            // around the comparer's call was kept on the stack.
            ref TKey first = ref NinoUnsafe.GetArrayDataReference(keys);
            if (!typeof(TKey).IsValueType)
            {
                for (int i = 0; i < count; i++)
                    if (Unsafe.Add(ref first, i) == null)
                        return false;
            }

            if (typeof(TKey).IsValueType && ReferenceEquals(comparer, Comparer<TKey>.Default))
            {
                // Comparer<TKey>.Default devirtualized and inlined for value types.
                for (int i = 1; i < count; i++)
                    if (Comparer<TKey>.Default.Compare(Unsafe.Add(ref first, i - 1), Unsafe.Add(ref first, i)) >= 0)
                        return false;
                return true;
            }

            for (int i = 1; i < count; i++)
                if (comparer.Compare(Unsafe.Add(ref first, i - 1), Unsafe.Add(ref first, i)) >= 0)
                    return false;
            return true;
        }

        private static bool HasNullKey(KeyValuePair<TKey, TValue>[] pairs, int count)
        {
            if (typeof(TKey).IsValueType)
                return false;
            // count is at most the array's length (it is rented for them).
            ref var at = ref NinoUnsafe.GetArrayDataReference(pairs);
            for (int n = count; n > 0; n--)
            {
                if (at.Key == null)
                    return true;
                at = ref Unsafe.Add(ref at, 1);
            }

            return false;
        }

        /// <summary>
        /// Pair by pair through the indexer, which throws for a null key; a repeated key is reported.
        /// </summary>
        private static void AddEach(IDictionary<TKey, TValue> dictionary, KeyValuePair<TKey, TValue>[] pairs,
            int length)
        {
            for (int i = 0; i < length; i++)
                NinoCollections.AddUnique(dictionary, pairs[i].Key, pairs[i].Value, i);
        }
    }
}
