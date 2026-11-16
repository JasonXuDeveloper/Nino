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

        public void Deserialize(ref Reader reader, ref TInterface value)
        {
            // A Dictionary is handed to its formatter (which reuses or drops it); anything else is recycled here. A
            // ReadOnlyDictionary is never a Dictionary, so it is always recycled and re-created around a new one.
            var dictionary = NinoRecycler.Reuse<TInterface, Dictionary<TKey, TValue>>(ref value);
            default(DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>).Deserialize(ref reader, ref dictionary);
            if (dictionary == null)
                value = null;
            else if (typeof(TInterface) == typeof(ReadOnlyDictionary<TKey, TValue>))
                value = (TInterface)(object)new ReadOnlyDictionary<TKey, TValue>(dictionary);
            else
                value = (TInterface)(object)dictionary;
        }
    }
}
