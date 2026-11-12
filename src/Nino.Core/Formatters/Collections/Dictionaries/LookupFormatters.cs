using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>IGrouping&lt;TKey, TElement&gt;</c>: element count (-1 = null), the key, then the elements. Read into a
    /// grouping over an array.
    /// </summary>
    public struct GroupingFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>
        : INinoFormatter<IGrouping<TKey, TElement>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TElementFormatter : struct, INinoFormatter<TElement>
    {
        public void Serialize(IGrouping<TKey, TElement> value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            // A grouping that holds its elements (Nino's, and .NET's Lookup, whose groupings are lists) is written
            // from an array, as an array's elements are (one block copy for raw ones), out of line with the writer by
            // value: enumerating it took two interface calls per element.
            if (value is NinoGrouping<TKey, TElement> stored)
            {
                writer = WriteElements(stored.Key, stored.Elements, writer);
                return;
            }

            if (value is ICollection<TElement> collection)
            {
                writer = WriteCollection(value.Key, collection, writer);
                return;
            }

            // The count first, patched after the elements: a grouping need not know its count.
            int countPos = writer.ReserveInt32();
            default(TKeyFormatter).Serialize(value.Key, ref writer);
            int count = 0;
            foreach (var element in value)
            {
                default(TElementFormatter).Serialize(element, ref writer);
                count++;
            }

            writer.PatchInt32(countPos, count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteElements(TKey key, ReadOnlySpan<TElement> elements, Writer writer)
        {
            writer.WriteLength(elements.Length);
            default(TKeyFormatter).Serialize(key, ref writer);
            NinoElements.Write<TElement, TElementFormatter>(elements, ref writer);
            return writer;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteCollection(TKey key, ICollection<TElement> collection, Writer writer)
        {
            int count = collection.Count;
            var elements = ArrayPool<TElement>.Shared.Rent(count);
            try
            {
                collection.CopyTo(elements, 0);
                return WriteElements(key, new ReadOnlySpan<TElement>(elements, 0, count), writer);
            }
            finally
            {
                ArrayPool<TElement>.Shared.Return(elements, NinoUnsafe.IsReferenceOrContainsReferences<TElement>());
            }
        }

        public void Deserialize(ref Reader reader, ref IGrouping<TKey, TElement> value)
        {
            int count = reader.ReadLength(NinoElements.MinWireSize<TElement, TElementFormatter>());
            if (count < 0)
            {
                value = null;
                return;
            }

            value = Read(count, ref reader);
        }

        /// <summary>Reads a grouping of <paramref name="count"/> elements: the key, then the elements.</summary>
        internal static NinoGrouping<TKey, TElement> Read(int count, ref Reader reader)
        {
            TKey key = default;
            default(TKeyFormatter).Deserialize(ref reader, ref key);
            TElement[] elements;
            if (Unsafe.SizeOf<TElement>() <= NinoElements.Amplification ||
                !NinoElements.HeavyElements<TElement, TElementFormatter>.Value)
            {
                elements = NinoUnsafe.NewArray<TElement>(count,
                    NinoElements.CopiedWhole<TElement, TElementFormatter>());
                NinoElements.Read<TElement, TElementFormatter>(elements, ref reader);
            }
            else
            {
                // Out of line, grown as they are read where the data could not fill as many (see ArrayFormatter).
                elements = null;
                reader = NinoElements.ReadGrowing<TElement, TElementFormatter>(ref elements, count, reader);
            }

            return new NinoGrouping<TKey, TElement>(key, elements);
        }
    }

    /// <summary>
    /// <c>ILookup&lt;TKey, TElement&gt;</c>: grouping count (-1 = null), then each grouping as
    /// <see cref="GroupingFormatter{TKey,TElement,TKeyFormatter,TElementFormatter}"/> writes it. Read into a lookup
    /// that keeps the groupings in order and finds them with the default comparer, as <c>ToLookup</c> does.
    /// </summary>
    public struct LookupFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>
        : INinoFormatter<ILookup<TKey, TElement>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TElementFormatter : struct, INinoFormatter<TElement>
    {
        public void Serialize(ILookup<TKey, TElement> value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            writer.WriteLength(value.Count);
            foreach (var grouping in value)
                default(GroupingFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>).Serialize(grouping,
                    ref writer);
        }

        public void Deserialize(ref Reader reader, ref ILookup<TKey, TElement> value)
        {
            // Each grouping starts with its element count.
            int count = reader.ReadLength(NinoWire.LengthSize);
            if (count < 0)
            {
                value = null;
                return;
            }

            // The groupings by key in a dictionary sized for them, unless its entries would take far more memory
            // than the data (a large key): then it grows as they are added.
            var lookup = new NinoLookup<TKey, TElement>(count,
                Unsafe.SizeOf<KeyValuePair<TKey, object>>() + NinoElements.HashSlot >
                NinoElements.Amplification * NinoWire.LengthSize
                    ? NinoElements.Capacity(count, Unsafe.SizeOf<KeyValuePair<TKey, object>>() + NinoElements.HashSlot,
                        reader.Remaining)
                    : count);
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadLength(NinoElements.MinWireSize<TElement, TElementFormatter>());
                if (length < 0)
                    NinoThrow.InvalidLength(length);
                lookup.Add(GroupingFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>.Read(length, ref reader));
            }

            value = lookup;
        }
    }

    /// <summary>The grouping <see cref="GroupingFormatter{TKey,TElement,TKeyFormatter,TElementFormatter}"/> reads.</summary>
    internal sealed class NinoGrouping<TKey, TElement> : IGrouping<TKey, TElement>, IReadOnlyList<TElement>
    {
        private readonly TElement[] _elements;

        public NinoGrouping(TKey key, TElement[] elements)
        {
            Key = key;
            _elements = elements;
        }

        public TKey Key { get; }

        /// <summary>The elements, as read.</summary>
        internal TElement[] Elements => _elements;

        public int Count => _elements.Length;

        public TElement this[int index] => _elements[index];

        public IEnumerator<TElement> GetEnumerator() => ((IEnumerable<TElement>)_elements).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _elements.GetEnumerator();
    }

    /// <summary>The lookup <see cref="LookupFormatter{TKey,TElement,TKeyFormatter,TElementFormatter}"/> reads.</summary>
    internal sealed class NinoLookup<TKey, TElement> : ILookup<TKey, TElement>
    {
        private readonly NinoGrouping<TKey, TElement>[] _groupings;
        private readonly Dictionary<TKey, NinoGrouping<TKey, TElement>> _byKey;
        private NinoGrouping<TKey, TElement> _nullKey; // a Dictionary takes no null key; a lookup does
        private int _count;
        // Keys added by HashTables.Insert, which counts the collisions as it walks the bucket; else by the dictionary,
        // and the bucket walked after (HashTables.Added).
        private readonly bool _insert;

        public NinoLookup(int count, int capacity)
        {
            _groupings = new NinoGrouping<TKey, TElement>[count];
            _byKey = NinoLimits.Untrusted
                ? Internal.NinoSeededHash.NewDictionary<TKey, NinoGrouping<TKey, TElement>>(capacity)
                : new Dictionary<TKey, NinoGrouping<TKey, TElement>>(capacity);
            _insert = Internal.HashTables.CanInsert(_byKey, count);
        }

        public void Add(NinoGrouping<TKey, TElement> grouping)
        {
            bool added;
            if (grouping.Key == null)
            {
                added = _nullKey == null;
                _nullKey = grouping;
            }
            else if (_insert)
            {
                Internal.HashTables.Insert(_byKey, grouping.Key, grouping, typeof(ILookup<TKey, TElement>));
                added = true;
            }
            else
            {
                added = NinoCollections.TryAdd(_byKey, grouping.Key, grouping);
                if (added)
                    Internal.HashTables.Added(_byKey, grouping.Key);
            }

            if (!added)
                NinoThrow.DuplicateKey(typeof(ILookup<TKey, TElement>));
            _groupings[_count++] = grouping;
        }

        public int Count => _count;

        public IEnumerable<TElement> this[TKey key] =>
            Find(key) ?? (IEnumerable<TElement>)Array.Empty<TElement>();

        public bool Contains(TKey key) => Find(key) != null;

        private NinoGrouping<TKey, TElement> Find(TKey key) =>
            key == null ? _nullKey : _byKey.TryGetValue(key, out var grouping) ? grouping : null;

        public IEnumerator<IGrouping<TKey, TElement>> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
                yield return _groupings[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
