using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Nino.Core;
using Nino.Core.CompilerServices;

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

            // As ImmutableHashSet<T>'s, its pairs the elements: each chunk of pairs written by Nino.Core's run loops
            // (pairs of raw items as one block, the string kernels, a polymorphic value's run loop), where key by key
            // each was its formatter's Serialize.
            if (HashTrees.Usable)
                NinoGathered.Write<KeyValuePair<TKey, TValue>,
                    KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    HashTree<KeyValuePair<TKey, TValue>>.Items>(DictionaryTree<TKey, TValue>.PairsOf(value),
                    value.Count, ref writer);
            else
                NinoGathered.Write<KeyValuePair<TKey, TValue>,
                    KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    NinoGathered.Enumerated<KeyValuePair<TKey, TValue>, ImmutableDictionary<TKey, TValue>.Enumerator>>(
                    new(value.GetEnumerator()), value.Count, ref writer);
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
        // As ImmutableHashSet<T>'s, out of line.
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
