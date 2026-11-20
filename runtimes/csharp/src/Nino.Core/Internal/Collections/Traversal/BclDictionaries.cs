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
                    reader = ReadSortedList(list, length, reader);
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

        /// <summary>
        /// Its two arrays zipped into its pairs (<see cref="Zip"/>) and written through the gather, the one write of a
        /// run of pairs whatever the keys and values are (#350): pairs whose memory is their wire form zipped straight
        /// into the writer's buffer by the interleave kernels, the others a chunk at a time through the run loops. Out
        /// of line with the caller's writer by reference: that is a reference already (DictionaryLikeFormatter's), and
        /// by value its copies in and out cost the caller 5 stack accesses (asm.py: SortedList&lt;int, int&gt;).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteSortedList(SortedList<TKey, TValue> list, ref Writer writer)
        {
            int count = CollectionFields.Size(list);
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if ((uint)count > (uint)keys.Length || (uint)count > (uint)values.Length)
                NinoThrow.ModifiedDuringWrite();
            writer = NinoGather.Write<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>, Zip>(new Zip(keys, values, count),
                count, writer);
        }

        /// <summary>
        /// A <see cref="SortedList{TKey,TValue}"/>'s keys and values, zipped into its pairs: where the pairs' memory is
        /// their wire form (<see cref="NinoGather.InPlace{T,TFormatter}"/>), the one fill of them all, straight into the
        /// writer's buffer, takes the interleave kernels of their sizes (a layer over the scalar zip below).
        /// </summary>
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
                int count = Math.Min(_count - _at, chunk.Length), filled = count;
                // count is at most the arrays' lengths (checked by the caller).
                ref TKey key = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_keys), _at);
                ref TValue value = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_values), _at);
                ref var pair = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(chunk);
                _at += count;
                if (NinoGather.InPlace<KeyValuePair<TKey, TValue>,
                        KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>())
                {
                    int zipped = Interleave(ref key, ref value, ref Unsafe.As<KeyValuePair<TKey, TValue>, byte>(ref pair),
                        filled);
                    key = ref Unsafe.Add(ref key, zipped);
                    value = ref Unsafe.Add(ref value, zipped);
                    pair = ref Unsafe.Add(ref pair, zipped);
                    filled -= zipped;
                }

                for (int i = 0; i < filled; i++)
                    Unsafe.Add(ref pair, i) = new KeyValuePair<TKey, TValue>(Unsafe.Add(ref key, i), Unsafe.Add(ref value, i));
                return count;
            }

            public void End()
            {
            }
        }

        /// <summary>
        /// Zips the first pairs of <paramref name="count"/> raw keys and values without padding into the block at
        /// <paramref name="target"/> with the interleave kernels (two-, four- and eight-byte keys and values of one
        /// size; the netstandard2.1 build's Burst and WebGL kernel for four bytes); returns how many, the rest the
        /// caller's.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Interleave(ref TKey key, ref TValue value, ref byte target, int count)
        {
#if NET8_0_OR_GREATER
            if (NinoSimd.IsPackedLane32<TKey>() && NinoSimd.IsPackedLane32<TValue>() && NinoSimd.CanInterleave<int>(count))
            {
                NinoSimd.InterleaveBest<int, NinoSimd.Zip32>(ref Unsafe.As<TKey, int>(ref key),
                    ref Unsafe.As<TValue, int>(ref value), ref target, count);
                return count;
            }

            if (typeof(TKey) == typeof(short) && typeof(TValue) == typeof(short) && NinoSimd.CanInterleave<short>(count))
            {
                NinoSimd.InterleaveBest<short, NinoSimd.Zip16>(ref Unsafe.As<TKey, short>(ref key),
                    ref Unsafe.As<TValue, short>(ref value), ref target, count);
                return count;
            }

            if (typeof(TKey) == typeof(long) && typeof(TValue) == typeof(long) && NinoSimd.CanInterleave<long>(count))
            {
                NinoSimd.InterleaveBest<long, NinoSimd.Zip64>(ref Unsafe.As<TKey, long>(ref key),
                    ref Unsafe.As<TValue, long>(ref value), ref target, count);
                return count;
            }

            return 0;
#else
            return NinoSimd.TryInterleave32(ref key, ref value, ref target, count);
#endif
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
        /// The one read of a <see cref="SortedList{TKey,TValue}"/> (#350): its pairs split into its two arrays
        /// (<see cref="Split"/>), a chunk at a time through the gather, or straight out of the data where their memory
        /// is their wire form (<see cref="SplitInPlace"/>), then published when the keys come in order under its
        /// comparer (as Nino writes them), whatever the keys and values are; otherwise, or where its layout is not
        /// known or the arrays would take far more memory than the data, added one by one.
        /// </summary>
        /// <remarks>Out of line with the reader by value and returned: inlined into a top-level read, its paths
        /// doubled the caller (asm.py: SortedList&lt;int, int&gt;'s 598 to 1,166 bytes, 17 to 34 stack accesses).</remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadSortedList(SortedList<TKey, TValue> list, int length, Reader reader)
        {
            if (!CollectionViews.SortedList || length == 0 ||
                !NinoElements.Fits(length, Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>(), reader.Remaining))
            {
                var adds = new NinoGather.Adds<SortedList<TKey, TValue>, KeyValuePair<TKey, TValue>, AddUnique>(list);
                return NinoGather.ReadAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    NinoGather.Adds<SortedList<TKey, TValue>, KeyValuePair<TKey, TValue>, AddUnique>>(ref adds, length, reader);
            }

            // Its own arrays when they are large enough (Clear cleared them), otherwise new ones; the list stays empty
            // until its size is published, so a read rejected half way leaves it empty.
            TKey[] keys = CollectionFields.Keys(list);
            TValue[] values = CollectionFields.Values(list);
            if (keys.Length < length)
                CollectionFields.Keys(list) = keys = new TKey[length];
            if (values.Length < length)
                CollectionFields.Values(list) = values = new TValue[length];
            bool ordered;
            if (SplitsInPlace)
                ordered = SplitInPlace(keys, values, ref reader.ReadElements(length, Unsafe.SizeOf<KeyValuePair<TKey, TValue>>()),
                    length, CollectionFields.Comparer(list));
            else
            {
                var split = new Split(keys, values);
                reader = NinoGather.ReadAll<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>, Split>(ref split, length, reader);
                ordered = InOrder(keys, length, CollectionFields.Comparer(list));
            }

            if (ordered)
            {
                CollectionFields.Size(list) = length;
                CollectionFields.Version(list)++;
                return reader;
            }

            // Out of order under this comparer (or a null key): moved out of the arrays and added one by one.
            var pairs = new KeyValuePair<TKey, TValue>[length];
            for (int i = 0; i < length; i++)
                pairs[i] = new KeyValuePair<TKey, TValue>(keys[i], values[i]);
            Array.Clear(keys, 0, length);
            Array.Clear(values, 0, length);
            AddEach(list, pairs, length);
            return reader;
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
                // Pairs of two 4-byte values whose memory is their wire form (raw, or a block that is its memory): one
                // vector pass.
                if (Unsafe.SizeOf<TKey>() == 4 && Unsafe.SizeOf<TValue>() == 4 && NinoSimd.CanInterleave<int>(chunk.Length) &&
                    NinoGather.InPlace<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>())
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

        /// <summary>
        /// Whether a read splits the pairs straight out of the data (<see cref="SplitInPlace"/>): pairs whose memory is
        /// their wire form (<see cref="NinoGather.InPlace{T,TFormatter}"/>: raw, or a block that is its memory, as a
        /// <c>KeyValuePair</c> of raw items is), read as they lie (no bool to check, no Untrusted check of their
        /// fields). A JIT-time constant.
        /// </summary>
        private static bool SplitsInPlace
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoGather.InPlace<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>() &&
                   (NinoKind.Of<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>() == NinoElementKind.Raw
                       ? NinoLayout<KeyValuePair<TKey, TValue>>.CopiesAsIs
                       : NinoBlockPlan<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>.ReadSize > 0 &&
                         !NinoBlockPlan<KeyValuePair<TKey, TValue>, KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>.Rejects);
        }

        /// <summary>
        /// Splits the <paramref name="length"/> pairs of the block at <paramref name="source"/> (checked by the caller)
        /// into <paramref name="keys"/> and <paramref name="values"/> (large enough) and tells whether the keys are in
        /// order under <paramref name="comparer"/>: the read of raw pairs without a chunk between the data and the
        /// arrays. Layers over the split and the order check of any pair, under the default comparer of a value type:
        /// on Arm64 one pass that splits integer keys and values of one type and checks the keys' order
        /// (<see cref="NinoSimd.SplitOrderedPairs{T}"/>), and the vector order check of <c>int</c> keys beside
        /// four-byte values.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SplitInPlace(TKey[] keys, TValue[] values, ref byte source, int length,
            IComparer<TKey> comparer)
        {
#if NET8_0_OR_GREATER
            bool byDefault = typeof(TKey).IsValueType && ReferenceEquals(comparer, Comparer<TKey>.Default);
            if (byDefault && typeof(TKey) == typeof(TValue) && NinoSimd.CanSplitOrderedPairs(length))
            {
                int split = SplitOrdered(keys, values, ref source, length);
                if (split > 0)
                    return true;
                if (split == 0)
                {
                    // Out of order: the arrays filled whole for the adds that follow.
                    new Split(keys, values).Add(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                        ref Unsafe.As<byte, KeyValuePair<TKey, TValue>>(ref source), length));
                    return false;
                }
            }
#endif
            new Split(keys, values).Add(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(
                ref Unsafe.As<byte, KeyValuePair<TKey, TValue>>(ref source), length));
#if NET8_0_OR_GREATER
            if (byDefault && typeof(TKey) == typeof(int) && Unsafe.SizeOf<TValue>() == 4)
                return NinoSimd.AreStrictlyIncreasingInt32(ref source, length);
#endif
            return InOrder(keys, length, comparer);
        }

#if NET8_0_OR_GREATER
        private static class PairKey
        {
            // Type.GetTypeCode also reports the underlying integer type of an enum.
            internal static readonly TypeCode Code = Type.GetTypeCode(typeof(TKey));
        }

        /// <summary>
        /// <see cref="NinoSimd.SplitOrderedPairs{T}"/> of integer keys and values of one type: 1 when the keys are
        /// strictly ascending, 0 when not (the arrays then hold part of the pairs), -1 for keys of another type.
        /// </summary>
        private static int SplitOrdered(TKey[] keys, TValue[] values, ref byte source, int length) =>
            PairKey.Code switch
            {
                TypeCode.SByte => SplitOrdered<sbyte>(keys, values, ref source, length),
                TypeCode.Byte => SplitOrdered<byte>(keys, values, ref source, length),
                TypeCode.Int16 => SplitOrdered<short>(keys, values, ref source, length),
                TypeCode.UInt16 => SplitOrdered<ushort>(keys, values, ref source, length),
                TypeCode.Int32 => SplitOrdered<int>(keys, values, ref source, length),
                TypeCode.UInt32 => SplitOrdered<uint>(keys, values, ref source, length),
                TypeCode.Int64 => SplitOrdered<long>(keys, values, ref source, length),
                TypeCode.UInt64 => SplitOrdered<ulong>(keys, values, ref source, length),
                _ => -1,
            };

        private static int SplitOrdered<T>(TKey[] keys, TValue[] values, ref byte source, int length)
            where T : unmanaged, IComparable<T> =>
            NinoSimd.SplitOrderedPairs(ref source, ref Unsafe.As<TKey, T>(ref NinoUnsafe.GetArrayDataReference(keys)),
                ref Unsafe.As<TValue, T>(ref NinoUnsafe.GetArrayDataReference(values)), length) ? 1 : 0;
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
