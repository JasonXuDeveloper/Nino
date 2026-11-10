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
    /// <summary><c>KeyValuePair&lt;TKey, TValue&gt;</c>: key, then value.</summary>
    public struct KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<KeyValuePair<TKey, TValue>>, INinoBlockFormatter<KeyValuePair<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        private static bool Raw
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => RawFormatter.Is<TKey, TKeyFormatter>() && RawFormatter.Is<TValue, TValueFormatter>();
        }

        int INinoFixedSizeFormatter<KeyValuePair<TKey, TValue>>.FixedSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Raw ? NinoLayout<TKey>.Size + NinoLayout<TValue>.Size : -1;
        }

        // Its fields are private: found by marking each item's bytes.
        NinoBlockMap INinoBlockFormatter<KeyValuePair<TKey, TValue>>.BlockMap()
        {
            if (!Raw)
                return null;
            int key = NinoBlocks.Find(NinoLayout<TKey>.Size,
                () => new KeyValuePair<TKey, TValue>(NinoBlocks.Marked<TKey>(), default));
            int value = NinoBlocks.Find(NinoLayout<TValue>.Size,
                () => new KeyValuePair<TKey, TValue>(default, NinoBlocks.Marked<TValue>()));
            return new NinoBlockMap().Raw<TKey>(key, 0).Raw<TValue>(value, NinoLayout<TKey>.Size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFixedSizeFormatter<KeyValuePair<TKey, TValue>>.WriteFixed(in KeyValuePair<TKey, TValue> value,
            ref byte destination) => WriteRaw(ref destination, in value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoBlockFormatter<KeyValuePair<TKey, TValue>>.ReadBlock(ref KeyValuePair<TKey, TValue> value,
            ref byte source) => value = ReadRaw(ref source);

        /// <summary>The key and the value of a pair of raw items, at <paramref name="destination"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRaw(ref byte destination, in KeyValuePair<TKey, TValue> value)
        {
            NinoLayout<TKey>.Write(ref destination, value.Key);
            NinoLayout<TValue>.Write(ref Unsafe.Add(ref destination, NinoLayout<TKey>.Size), value.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static KeyValuePair<TKey, TValue> ReadRaw(ref byte source) =>
            new KeyValuePair<TKey, TValue>(NinoLayout<TKey>.Read(ref source),
                NinoLayout<TValue>.Read(ref Unsafe.Add(ref source, NinoLayout<TKey>.Size)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(KeyValuePair<TKey, TValue> value, ref Writer writer)
        {
            // Raw items (a constant check): one capacity check for the pair, as for a tuple of them.
            if (Raw)
            {
                WriteRaw(ref writer.Reserve(NinoLayout<TKey>.Size + NinoLayout<TValue>.Size), in value);
                return;
            }

            default(TKeyFormatter).Serialize(value.Key, ref writer);
            default(TValueFormatter).Serialize(value.Value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref KeyValuePair<TKey, TValue> value)
        {
            if (Raw)
            {
                value = ReadRaw(ref reader.ReadBlock(NinoLayout<TKey>.Size + NinoLayout<TValue>.Size));
                return;
            }

            TKey key = value.Key;
            TValue item = value.Value;
            default(TKeyFormatter).Deserialize(ref reader, ref key);
            default(TValueFormatter).Deserialize(ref reader, ref item);
            value = new KeyValuePair<TKey, TValue>(key, item);
        }
    }
}
