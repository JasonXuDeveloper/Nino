using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableSortedDictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then each key and value in key order, as
    /// a <c>SortedDictionary&lt;TKey, TValue&gt;</c> writes them. Read with the default comparers.
    /// </summary>
    public struct ImmutableSortedDictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<ImmutableSortedDictionary<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        public void Serialize(ImmutableSortedDictionary<TKey, TValue> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // As ImmutableSortedSet<T>'s.
            int count = value.Count;
            ImmutableCollections.WriteRented<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(Copy(value, count), count, ref writer);
        }

        // Out of line, as ImmutableSortedSet<T>'s.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static KeyValuePair<TKey, TValue>[] Copy(ImmutableSortedDictionary<TKey, TValue> value, int count)
        {
            var pairs = ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(count);
            if (SortedTrees.Usable)
            {
                SortedDictionaryTree<TKey, TValue>.CopyTo(value, pairs);
                return pairs;
            }

            ref var slot = ref MemoryMarshal.GetReference(pairs.AsSpan());
            foreach (var pair in value)
            {
                slot = pair;
                slot = ref Unsafe.Add(ref slot, 1);
            }

            return pairs;
        }

        public void Deserialize(ref Reader reader, ref ImmutableSortedDictionary<TKey, TValue> value)
        {
            var pairs = ImmutableCollections.Read<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(ref reader);
            value = pairs == null ? null : Build(pairs);
        }

        // Built as its tree where it can be (SortedTrees), else added one by one to a builder.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImmutableSortedDictionary<TKey, TValue> Build(KeyValuePair<TKey, TValue>[] pairs)
        {
            if (pairs.Length == 0)
                return ImmutableSortedDictionary<TKey, TValue>.Empty;
            if (SortedTrees.Usable && SortedDictionaryTree<TKey, TValue>.Create(pairs) is { } dictionary)
                return dictionary;

            var builder = ImmutableSortedDictionary.CreateBuilder<TKey, TValue>();
            ImmutableCollections.AddAll(builder, pairs, typeof(ImmutableSortedDictionary<TKey, TValue>));
            return builder.ToImmutable();
        }
    }
}
