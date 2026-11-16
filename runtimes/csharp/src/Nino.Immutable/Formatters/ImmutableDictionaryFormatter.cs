using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableDictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then each key and value, as a
    /// <c>Dictionary&lt;TKey, TValue&gt;</c> writes them. Read with the default comparers.
    /// </summary>
    public struct ImmutableDictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<ImmutableDictionary<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        public void Serialize(ImmutableDictionary<TKey, TValue> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // As ImmutableHashSet<T>'s: copied out of its tree, else key by key with its own enumerator.
            int count = value.Count;
            if (count != 0 && HashTrees.Usable && DictionaryTree<TKey, TValue>.Copy(value, count) is { } pairs)
            {
                ImmutableCollections.WriteRented<KeyValuePair<TKey, TValue>,
                    KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(pairs, count, ref writer);
                return;
            }

            writer = WritePairs(value, count, writer);
        }

        // Out of line with the count, the writer by value, as ImmutableHashSet<T>'s.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WritePairs(ImmutableDictionary<TKey, TValue> value, int count, Writer writer)
        {
            writer.WriteLength(count);
            foreach (var pair in value)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref writer);
                default(TValueFormatter).Serialize(pair.Value, ref writer);
            }

            return writer;
        }

        public void Deserialize(ref Reader reader, ref ImmutableDictionary<TKey, TValue> value)
        {
            var pairs = ImmutableCollections.Read<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(ref reader);
            if (pairs == null)
            {
                value = null;
                return;
            }

            value = Build(pairs);
        }
        // As ImmutableHashSet<T>'s, out of line like WritePairs.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImmutableDictionary<TKey, TValue> Build(KeyValuePair<TKey, TValue>[] pairs)
        {
            if (pairs.Length == 0)
                return ImmutableDictionary<TKey, TValue>.Empty;
            if (HashTrees.Usable && DictionaryTree<TKey, TValue>.Create(pairs) is { } dictionary)
                return dictionary;

            HashTrees.CheckCollisions(pairs, typeof(ImmutableDictionary<TKey, TValue>));
            var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();
            ImmutableCollections.AddAll(builder, pairs, typeof(ImmutableDictionary<TKey, TValue>));
            return builder.ToImmutable();
        }
    }
}
