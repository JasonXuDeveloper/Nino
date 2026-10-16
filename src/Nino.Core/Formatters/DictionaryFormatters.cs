using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
            if (RawFormatter<TKeyFormatter>.Is && RawFormatter<TValueFormatter>.Is)
            {
                // Raw keys and values: one reservation for the whole dictionary, no per-element bounds checks.
                int count = value.Count;
                WriteRaw(value, ref writer.Reserve(checked(NinoWire.LengthSize +
                                                           count * (Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>()))),
                    count);
                return;
            }

            if (RawFormatter<TKeyFormatter>.Is && typeof(TValueFormatter) == typeof(StringFormatter))
            {
                // Raw keys and UTF-16 strings: written into the free part of the buffer by value; only when they
                // do not fit does the buffer grow, through the general loop.
                int written = WriteStringPairs(Unsafe.As<Dictionary<TKey, string>>(value),
                    ref writer.FreeSpace(out int available), available);
                if (written >= 0)
                {
                    writer.Commit(written);
                    return;
                }
            }

            var copy = writer;
            WritePairs(value, ref copy);
            writer = copy;
        }

        /// <summary>
        /// The bytes of <see cref="WritePairs"/> for raw keys and UTF-16 strings, written into <paramref name="dst"/>
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
                    NinoThrow.InvalidData("collection was modified during serialization");
                string s = pair.Value;
                int length = s == null ? NinoWire.NullLength : s.Length;
                nuint chars = s == null ? 0 : (nuint)(uint)length * sizeof(char);
                nuint size = (nuint)(uint)(keySize + NinoWire.LengthSize) + chars;
                if (size > end - pos)
                    return -1;
                ref byte entry = ref Unsafe.Add(ref dst, (nint)pos);
                NinoUnsafe.Write(ref entry, 0, pair.Key);
                NinoLayout<TKey>.ClearPadding(ref entry);
                NinoUnsafe.Write(ref entry, keySize, length);
                if (chars != 0)
                    Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref entry, keySize + NinoWire.LengthSize),
                        ref Unsafe.As<char, byte>(ref MemoryMarshal.GetReference(s.AsSpan())), (uint)chars);
                pos += size;
            }

            return (int)pos;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteRaw(Dictionary<TKey, TValue> value, ref byte dst, int count)
        {
            int keySize = Unsafe.SizeOf<TKey>();
            int pairSize = keySize + Unsafe.SizeOf<TValue>();
            NinoUnsafe.Write(ref dst, count);
            ref byte pairs = ref Unsafe.Add(ref dst, NinoWire.LengthSize);
            int written = 0;
            foreach (var pair in value)
            {
                if (++written > count)
                    NinoThrow.InvalidData("collection was modified during serialization");
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
            var writer = target;
            writer.WriteLength(value.Count);
            foreach (var pair in value)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref writer);
                default(TValueFormatter).Serialize(pair.Value, ref writer);
            }

            target = writer;
        }

        /// <summary>Reads the pair count, validated against the remaining bytes; false for null.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryReadLength(ref Reader reader, out int length) =>
            reader.TryReadLength(RawFormatter<TKeyFormatter>.Is && RawFormatter<TValueFormatter>.Is
                ? Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>()
                : 1, out length);

        /// <summary>Recycles and clears an existing dictionary before it is refilled with <paramref name="length"/> pairs.</summary>
        public static void Reset(Dictionary<TKey, TValue> dictionary, int length)
        {
            NinoRecycler.DiscardPairs<TKey, TValue>(dictionary);
            dictionary.Clear();
#if NET8_0_OR_GREATER || NETSTANDARD2_1
            dictionary.EnsureCapacity(length);
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
            if (before == 0 || (RawFormatter<TKeyFormatter>.Is && RawFormatter<TValueFormatter>.Is))
            {
                Reset(dictionary, length);
                Read(dictionary, length, ref reader);
                return;
            }

            var seen = System.Buffers.ArrayPool<TKey>.Shared.Rent(length == 0 ? 1 : length);
            try
            {
                int matched = 0;
                for (int i = 0; i < length; i++)
                {
                    TKey key = default;
                    default(TKeyFormatter).Deserialize(ref key, ref reader);
                    seen[i] = key;
#if NET8_0_OR_GREATER
                    // Reading the value never touches this dictionary, so the reference stays valid.
                    ref TValue slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
                        dictionary, key, out bool exists);
                    if (exists)
                        matched++;
                    default(TValueFormatter).Deserialize(ref slot, ref reader);
#else
                    if (dictionary.TryGetValue(key, out TValue item))
                        matched++;
                    default(TValueFormatter).Deserialize(ref item, ref reader);
                    dictionary[key] = item;
#endif
                }

                // Every key that was there before was read again, unless the data repeats a key: a repeated key
                // counts twice, so equal counts are only trusted when the keys come back in the same order.
                if (matched != before || !SameKeys(dictionary, seen, length))
                    RemoveStale(dictionary, seen, length);
            }
            finally
            {
                System.Buffers.ArrayPool<TKey>.Shared.Return(seen, !typeof(TKey).IsValueType);
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

        /// <summary>Adds a pair; a key the data repeats (under the dictionary's comparer) is invalid data.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Add(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
#if NETSTANDARD2_0
            int before = dictionary.Count;
            dictionary[key] = value;
            if (dictionary.Count == before)
                NinoThrow.DuplicateKey(dictionary.GetType());
#else
            if (!dictionary.TryAdd(key, value))
                NinoThrow.DuplicateKey(dictionary.GetType());
#endif
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddRaw(Dictionary<TKey, TValue> dictionary, ref byte src, int length)
        {
            // A cursor rather than an int offset, which every access would sign-extend.
            int keySize = Unsafe.SizeOf<TKey>();
            int pairSize = keySize + Unsafe.SizeOf<TValue>();
            for (int i = 0; i < length; i++)
            {
                Add(dictionary, NinoUnsafe.Read<TKey>(ref src), NinoUnsafe.Read<TValue>(ref src, keySize));
                src = ref Unsafe.Add(ref src, pairSize);
            }
        }

        /// <summary>Reads <paramref name="length"/> pairs into <paramref name="dictionary"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(Dictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            if (RawFormatter<TKeyFormatter>.Is && RawFormatter<TValueFormatter>.Is)
            {
                // The block is taken here and the pairs added out of line: the reader's address never leaves the
                // caller, and the caller stays small enough for the JIT to inline the rest of its path.
                ref byte src = ref reader.ReadBlock(length * (Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>()));
                AddRaw(dictionary, ref src, length);
                return;
            }

            if (RawFormatter<TKeyFormatter>.Is && typeof(TValueFormatter) == typeof(StringFormatter))
            {
                // Raw keys and UTF-16 strings: one loop with the state in registers (see Reader.ReadStrings).
                reader.ReadStringPairs(Unsafe.As<Dictionary<TKey, string>>(dictionary), length);
                return;
            }

            // Out of line, through a copy of the reader (see AddRaw).
            var copy = reader;
            ReadPairs(dictionary, length, ref copy);
            reader = copy;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadPairs(Dictionary<TKey, TValue> dictionary, int length, ref Reader source)
        {
            // A local reader, so its fields stay in registers inside the loop.
            var reader = source;
            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref reader);
                default(TValueFormatter).Deserialize(ref item, ref reader);
                Add(dictionary, key, item);
            }

            source = reader;
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
        public void Serialize(Dictionary<TKey, TValue> value, ref Writer writer)
        {
            if (value == null)
                writer.WriteNullLength();
            else
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(value, ref writer);
        }

        public void Deserialize(ref Dictionary<TKey, TValue> value, ref Reader reader)
        {
            if (!NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.TryReadLength(ref reader, out int length))
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            if (value == null)
            {
                value = new Dictionary<TKey, TValue>(length);
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Read(value, length, ref reader);
            }
            else
            {
                // Through a copy: the reader's own address must not reach a call that is not inlined (see
                // NinoObject.ReadBody), or none of its fields stay in registers in the caller.
                var copy = reader;
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Refill(value, length, ref copy);
                reader = copy;
            }
        }
    }

    /// <summary>
    /// A type derived from <c>Dictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then key/value pairs. An existing
    /// dictionary is refilled in place (merged by key), keeping its comparer.
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
            if (value == null)
                writer.WriteNullLength();
            else
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(value, ref writer);
        }

        public void Deserialize(ref TDictionary value, ref Reader reader)
        {
            if (!NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.TryReadLength(ref reader, out int length))
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            value ??= NinoActivator<TDictionary>.Create();
            var copy = reader;
            NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Refill(value, length, ref copy);
            reader = copy;
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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            NinoCollections.WritePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(value, ref writer);
        }

        public void Deserialize(ref TDictionary value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
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

            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref reader);
                default(TValueFormatter).Deserialize(ref item, ref reader);
                dictionary[key] = item;
                if (dictionary.Count != i + 1)
                    NinoThrow.DuplicateKey(dictionary.GetType());
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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            if (value is Dictionary<TKey, TValue> dictionary)
            {
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(dictionary, ref writer);
                return;
            }

            NinoCollections.WritePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(value, ref writer);
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
        : INinoFormatter<KeyValuePair<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(KeyValuePair<TKey, TValue> value, ref Writer writer)
        {
            default(TKeyFormatter).Serialize(value.Key, ref writer);
            default(TValueFormatter).Serialize(value.Value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref KeyValuePair<TKey, TValue> value, ref Reader reader)
        {
            TKey key = value.Key;
            TValue item = value.Value;
            default(TKeyFormatter).Deserialize(ref key, ref reader);
            default(TValueFormatter).Deserialize(ref item, ref reader);
            value = new KeyValuePair<TKey, TValue>(key, item);
        }
    }

    internal static partial class NinoCollections
    {
        /// <summary>Writes the count of <paramref name="pairs"/>, then each key and value.</summary>
        public static void WritePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(
            IEnumerable<KeyValuePair<TKey, TValue>> pairs, ref Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            int countPos = writer.ReserveInt32();
            int written = 0;
            foreach (var pair in pairs)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref writer);
                default(TValueFormatter).Serialize(pair.Value, ref writer);
                written++;
            }

            writer.PatchInt32(countPos, written);
        }
    }
}
