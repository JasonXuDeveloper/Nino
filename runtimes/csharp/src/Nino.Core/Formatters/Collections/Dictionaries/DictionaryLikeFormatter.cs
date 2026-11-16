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
    /// Any <c>IDictionary&lt;TKey, TValue&gt;</c> Nino can create (<c>SortedDictionary</c>,
    /// <c>SortedList</c>, <c>ConcurrentDictionary</c>, user dictionaries...): count (-1 = null), then key/value pairs.
    /// </summary>
    public struct DictionaryLikeFormatter<[NinoCreated] TDictionary, TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<TDictionary>
        where TDictionary : class, IDictionary<TKey, TValue>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TDictionary value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (BclDictionaries<TKey, TValue, TKeyFormatter, TValueFormatter>.TryWrite(value, ref writer))
                return;

            NinoCollections.WriteEnumerable<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(value, ref writer);
        }

        public void Deserialize(ref Reader reader, ref TDictionary value)
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
                default(TKeyFormatter).Deserialize(ref reader, ref key);
                default(TValueFormatter).Deserialize(ref reader, ref item);
                NinoCollections.AddUnique(dictionary, key, item, i);
            }
        }
    }
}
