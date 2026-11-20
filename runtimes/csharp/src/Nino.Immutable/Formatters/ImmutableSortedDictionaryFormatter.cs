using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System;
using Nino.Core;
using Nino.Core.CompilerServices;

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

            // As ImmutableSortedSet<T>'s: its tree walked in order, a node's key and value made into its pair, each
            // chunk of pairs written by Nino.Core's run loops (pairs of raw items as one block, the string kernels, a
            // polymorphic value's run loop).
            if (SortedTrees.Usable)
                NinoGathered.Write<KeyValuePair<TKey, TValue>,
                    KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    SortedDictionaryTree<TKey, TValue>.Pairs>(SortedDictionaryTree<TKey, TValue>.PairsOf(value),
                    value.Count, ref writer);
            else
                NinoGathered.Write<KeyValuePair<TKey, TValue>,
                    KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>,
                    NinoGathered.Enumerated<KeyValuePair<TKey, TValue>,
                        ImmutableSortedDictionary<TKey, TValue>.Enumerator>>(new(value.GetEnumerator()), value.Count,
                    ref writer);
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
