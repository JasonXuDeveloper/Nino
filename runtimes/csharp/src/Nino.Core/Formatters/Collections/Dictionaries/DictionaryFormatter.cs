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
    /// <c>Dictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then key/value pairs. An existing dictionary is
    /// refilled in place (merged by key), keeping its comparer.
    /// </summary>
    public struct DictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<Dictionary<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(Dictionary<TKey, TValue> value, ref Writer writer) =>
            default(DictionaryFormatter<Dictionary<TKey, TValue>, TKey, TValue, TKeyFormatter, TValueFormatter>)
                .Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref Dictionary<TKey, TValue> value) =>
            default(DictionaryFormatter<Dictionary<TKey, TValue>, TKey, TValue, TKeyFormatter, TValueFormatter>)
                .Deserialize(ref reader, ref value);
    }

    /// <summary>
    /// A type derived from <c>Dictionary&lt;TKey, TValue&gt;</c> (and <c>Dictionary&lt;TKey, TValue&gt;</c> itself):
    /// count (-1 = null), then key/value pairs. An existing dictionary is refilled in place (merged by key), keeping
    /// its comparer.
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
            if (!writer.TryWriteNullLength(value))
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Write(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref TDictionary value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value,
                    NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.MinPairSize, out int length))
                return;

            if (value == null && typeof(TDictionary) == typeof(Dictionary<TKey, TValue>))
            {
                // A new plain dictionary, sized for the pairs: filled without the merge of Refill.
                int capacity = NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Capacity(length, reader.Remaining);
                // NinoSettings.Untrusted (a constant): seeded comparers, out of line.
                var created = NinoLimits.Untrusted
                    ? NinoSeededHash.NewDictionary<TKey, TValue>(capacity)
                    : new Dictionary<TKey, TValue>(capacity);
                value = Unsafe.As<TDictionary>(created);
                NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Read(created, length, ref reader);
                return;
            }

            value ??= NinoActivator<TDictionary>.Create();
            // Through a copy: the reader's own address must not reach a call that is not inlined (see
            // NinoObject.ReadBody), or none of its fields stay in registers in the caller.
            Reader copy = reader;
            NinoDictionary<TKey, TValue, TKeyFormatter, TValueFormatter>.Refill(value, length, ref copy);
            reader = copy;
        }
    }
}
