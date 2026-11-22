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
    /// <c>KeyValuePair&lt;TKey, TValue&gt;</c>: key, then value. A block where both items are raw or blocks
    /// (<see cref="NinoBlocks.ItemSize{TItem, TFormatter}"/>), as a value tuple of them.
    /// </summary>
    public struct KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<KeyValuePair<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        // The items' wire bytes (-1: of no fixed size), and the pair's: their sum, or -1.
        private static readonly int KeySize = NinoBlocks.ItemSize<TKey, TKeyFormatter>();
        private static readonly int ValueSize = NinoBlocks.ItemSize<TValue, TValueFormatter>();
        private static readonly int Size = KeySize < 0 || ValueSize < 0 ? -1 : KeySize + ValueSize;

        // Where the pair keeps its key and value (-1: not known on this runtime).
        private static int KeyAt => CollectionFields.PairLayout<TKey, TValue>.Key;
        private static int ValueAt => CollectionFields.PairLayout<TKey, TValue>.Value;

        /// <summary>
        /// Whether every pair is a block of <see cref="Size"/> bytes: a JIT-time constant, false without reading the
        /// fields where an item holds references (code shared over a class, where each read would be a lookup).
        /// </summary>
        private static bool Fixed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !NinoUnsafe.IsReferenceOrContainsReferences<KeyValuePair<TKey, TValue>>() && Size > 0;
        }

        // A pair whose key or value dispatches subtypes dispatches them too (its items' bits).
        int INinoFormatter.BodyTraits =>
            NinoBodyTraits.Nests | NinoBlocks.Dispatches<TKeyFormatter>() | NinoBlocks.Dispatches<TValueFormatter>();

        int INinoFormatter<KeyValuePair<TKey, TValue>>.FixedSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Fixed ? Size : -1;
        }

        // Its fields are private: found by marking each item's bytes.
        NinoBlockMap INinoFormatter<KeyValuePair<TKey, TValue>>.BlockMap() => Fixed
            ? new NinoBlockMap()
                .Item<TKey, TKeyFormatter>(NinoBlocks.Find(Unsafe.SizeOf<TKey>(),
                    () => new KeyValuePair<TKey, TValue>(NinoBlocks.Marked<TKey>(), default)))
                .Item<TValue, TValueFormatter>(NinoBlocks.Find(Unsafe.SizeOf<TValue>(),
                    () => new KeyValuePair<TKey, TValue>(default, NinoBlocks.Marked<TValue>())))
            : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<KeyValuePair<TKey, TValue>>.WriteFixed(in KeyValuePair<TKey, TValue> value,
            ref byte destination) => WriteFixed(ref destination, in value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<KeyValuePair<TKey, TValue>>.ReadBlock(ref KeyValuePair<TKey, TValue> value,
            ref byte source) => ReadBlock(ref source, ref value);

        /// <summary>The key and the value at their offsets in a block, at <paramref name="destination"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFixed(ref byte destination, in KeyValuePair<TKey, TValue> value)
        {
            NinoBlocks.WriteItem<TKey, TKeyFormatter>(value.Key, ref destination);
            NinoBlocks.WriteItem<TValue, TValueFormatter>(value.Value, ref Unsafe.Add(ref destination, KeySize));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadBlock(ref byte source, ref KeyValuePair<TKey, TValue> value)
        {
            // Every byte of both items is read: nothing of the old pair is kept.
            TKey key = default;
            TValue item = default;
            NinoBlocks.ReadItem<TKey, TKeyFormatter>(ref key, ref source);
            NinoBlocks.ReadItem<TValue, TValueFormatter>(ref item, ref Unsafe.Add(ref source, KeySize));
            value = new KeyValuePair<TKey, TValue>(key, item);
        }

        // What the pair does on a run, composed from its items (NinoItems), each at its offset in the pair where this
        // runtime's pair is known (CollectionFields.PairLayout): a run with a string among the items through the string
        // run kernels (a list's, an array's, a sorted or concurrent dictionary's gathered), one call per run; a
        // polymorphic value's run loop, the keys written by it; a dropped pair's items recycled.
        bool INinoFormatter.HasCursor => NinoItems.Cursor<TKey, TValue, TKeyFormatter, TValueFormatter>(KeyAt, ValueAt);

        void INinoFormatter.WriteStrings(scoped in NinoRun run, ref Writer writer) =>
            NinoItems.Write<TKey, TValue, TKeyFormatter, TValueFormatter>(run, KeyAt, ValueAt, ref writer);

        int INinoFormatter.ReadStrings(scoped NinoRun run, ref byte data, int end) =>
            NinoItems.Read<TKey, TValue, TKeyFormatter, TValueFormatter>(run, KeyAt, ValueAt, ref data, end);

        bool INinoFormatter.HasRunLoop => NinoItems.RunLoop<TKey, TValue, TKeyFormatter, TValueFormatter>(KeyAt, ValueAt);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter.WriteRun(NinoRun run, ref Writer writer) => NinoItems.WriteRun<TKey, TValue, TKeyFormatter, TValueFormatter>(run, KeyAt, ValueAt, ref writer);

        bool INinoFormatter.RecyclesItems => true;

        bool INinoFormatter<KeyValuePair<TKey, TValue>>.ItemsMayRecycle => 
            NinoRecycler.MayRecycle<TKey, TKeyFormatter>() || NinoRecycler.MayRecycle<TValue, TValueFormatter>();

        void INinoFormatter<KeyValuePair<TKey, TValue>>.RecycleItems(in KeyValuePair<TKey, TValue> value)
        {
            NinoRecycler.Recycle<TKey, TKeyFormatter>(value.Key);
            NinoRecycler.Recycle<TValue, TValueFormatter>(value.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(KeyValuePair<TKey, TValue> value, ref Writer writer)
        {
            // A block (a constant check): one capacity check for the pair.
            if (Fixed)
            {
                WriteFixed(ref writer.Reserve(Size), in value);
                return;
            }

            default(TKeyFormatter).Serialize(value.Key, ref writer);
            default(TValueFormatter).Serialize(value.Value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref KeyValuePair<TKey, TValue> value)
        {
            if (Fixed)
            {
                ReadBlock(ref reader.ReadBlock(Size), ref value);
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
