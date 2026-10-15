using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

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
        public static void Write(Dictionary<TKey, TValue> value, ref Writer writer)
        {
            if (RawFormatter<TKeyFormatter>.Is && RawFormatter<TValueFormatter>.Is)
            {
                // Raw keys and values: one reservation for the whole dictionary, no per-element bounds checks.
                int keySize = Unsafe.SizeOf<TKey>();
                int pairSize = keySize + Unsafe.SizeOf<TValue>();
                int count = value.Count;
                ref byte dst = ref writer.Reserve(4 + count * pairSize);
                NinoUnsafe.Write(ref dst, count);
                int offset = 4;
                int written = 0;
                foreach (var pair in value)
                {
                    if (++written > count)
                        NinoThrow.InvalidData("collection was modified during serialization");
                    NinoUnsafe.Write(ref dst, offset, pair.Key);
                    NinoUnsafe.Write(ref dst, offset + keySize, pair.Value);
                    NinoLayout<TKey>.ClearPadding(ref Unsafe.Add(ref dst, offset));
                    NinoLayout<TValue>.ClearPadding(ref Unsafe.Add(ref dst, offset + keySize));
                    offset += pairSize;
                }

                return;
            }

            writer.WriteLength(value.Count);
            foreach (var pair in value)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref writer);
                default(TValueFormatter).Serialize(pair.Value, ref writer);
            }
        }

        /// <summary>Reads the pair count, validated against the remaining bytes (-1 for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadLength(ref Reader reader) =>
            reader.ReadLength(RawFormatter<TKeyFormatter>.Is && RawFormatter<TValueFormatter>.Is
                ? Unsafe.SizeOf<TKey>() + Unsafe.SizeOf<TValue>()
                : 1);

        /// <summary>Recycles and clears an existing dictionary before it is refilled with <paramref name="length"/> pairs.</summary>
        public static void Reset(Dictionary<TKey, TValue> dictionary, int length)
        {
            NinoCollections.RecycleItems<TKey, TValue>(dictionary);
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

                if (matched != before)
                    RemoveStale(dictionary, seen, length);
            }
            finally
            {
                System.Buffers.ArrayPool<TKey>.Shared.Return(seen, !typeof(TKey).IsValueType);
            }
        }

        /// <summary>Removes (and recycles) the entries whose key is not among the <paramref name="count"/> read.</summary>
        private static void RemoveStale(Dictionary<TKey, TValue> dictionary, TKey[] keys, int count)
        {
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
            {
                dictionary.Remove(pair.Key);
                NinoRecycler<TKey>.Recycle(pair.Key);
                NinoRecycler<TValue>.Recycle(pair.Value);
            }
        }

        /// <summary>Reads <paramref name="length"/> pairs into <paramref name="dictionary"/>.</summary>
        public static void Read(Dictionary<TKey, TValue> dictionary, int length, ref Reader reader)
        {
            if (RawFormatter<TKeyFormatter>.Is && RawFormatter<TValueFormatter>.Is)
            {
                int keySize = Unsafe.SizeOf<TKey>();
                int pairSize = keySize + Unsafe.SizeOf<TValue>();
                ref byte src = ref reader.ReadBlock(length * pairSize);
                for (int i = 0, offset = 0; i < length; i++, offset += pairSize)
                    dictionary[NinoUnsafe.Read<TKey>(ref src, offset)] = NinoUnsafe.Read<TValue>(ref src, offset + keySize);
                return;
            }

            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref reader);
                default(TValueFormatter).Deserialize(ref item, ref reader);
                dictionary[key] = item;
            }
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
        public void Serialize(Dictionary<TKey, TValue> value, ref Writer writer)
        {
            if (value == null)
                writer.WriteNullLength();
            else
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(value, ref writer);
        }

        public void Deserialize(ref Dictionary<TKey, TValue> value, ref Reader reader)
        {
            int length = NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.ReadLength(ref reader);
            if (length < 0)
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
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Refill(value, length, ref reader);
            }
        }
    }

    /// <summary>
    /// A type derived from <c>Dictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then key/value pairs. An existing
    /// dictionary is refilled in place (merged by key), keeping its comparer.
    /// </summary>
    public struct DictionaryFormatter<TDictionary, TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<TDictionary>
        where TDictionary : Dictionary<TKey, TValue>, new()
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        public void Serialize(TDictionary value, ref Writer writer)
        {
            if (value == null)
                writer.WriteNullLength();
            else
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(value, ref writer);
        }

        public void Deserialize(ref TDictionary value, ref Reader reader)
        {
            int length = NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.ReadLength(ref reader);
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            value ??= new TDictionary();
            NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Refill(value, length, ref reader);
        }
    }

    /// <summary>
    /// Any <c>IDictionary&lt;TKey, TValue&gt;</c> with a parameterless constructor (<c>SortedDictionary</c>,
    /// <c>SortedList</c>, <c>ConcurrentDictionary</c>, user dictionaries...): count (-1 = null), then key/value pairs.
    /// </summary>
    public struct DictionaryLikeFormatter<TDictionary, TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<TDictionary>
        where TDictionary : class, IDictionary<TKey, TValue>, new()
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

            var dictionary = value;
            if (dictionary == null)
            {
                dictionary = new TDictionary();
                value = dictionary;
            }
            else
            {
                NinoCollections.RecycleItems<TKey, TValue>(dictionary);
                dictionary.Clear();
            }

            for (int i = 0; i < length; i++)
            {
                TKey key = default;
                TValue item = default;
                default(TKeyFormatter).Deserialize(ref key, ref reader);
                default(TValueFormatter).Deserialize(ref item, ref reader);
                dictionary[key] = item;
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
                default(DictionaryFormatter<Dictionary<TKey, TValue>, TKey, TValue, TKeyFormatter, TValueFormatter>)
                    .Serialize(dictionary, ref writer);
                return;
            }

            NinoCollections.WritePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(value, ref writer);
        }

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            var old = value;
            if (typeof(TInterface) == typeof(ReadOnlyDictionary<TKey, TValue>))
            {
                Dictionary<TKey, TValue> inner = null;
                default(DictionaryFormatter<Dictionary<TKey, TValue>, TKey, TValue, TKeyFormatter, TValueFormatter>)
                    .Deserialize(ref inner, ref reader);
                value = inner == null ? null : (TInterface)(object)new ReadOnlyDictionary<TKey, TValue>(inner);
            }
            else
            {
                var dictionary = value as Dictionary<TKey, TValue>;
                default(DictionaryFormatter<Dictionary<TKey, TValue>, TKey, TValue, TKeyFormatter, TValueFormatter>)
                    .Deserialize(ref dictionary, ref reader);
                value = (TInterface)(object)dictionary;
            }

            if (old != null && !ReferenceEquals(old, value))
                NinoRecycler<TInterface>.Recycle(old);
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

    public static partial class NinoCollections
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

        /// <summary>Recycles the keys and values of a dictionary that is about to be cleared.</summary>
        public static void RecycleItems<TKey, TValue>(IEnumerable<KeyValuePair<TKey, TValue>> pairs)
        {
            bool keys = NinoRecycler<TKey>.IsRegistered && !typeof(TKey).IsValueType;
            bool values = NinoRecycler<TValue>.IsRegistered && !typeof(TValue).IsValueType;
            if (!keys && !values)
                return;

            foreach (var pair in pairs)
            {
                if (keys)
                    NinoRecycler<TKey>.Recycle(pair.Key);
                if (values)
                    NinoRecycler<TValue>.Recycle(pair.Value);
            }
        }
    }
}
