using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
#if !NETSTANDARD2_0
using System.Runtime.CompilerServices;
#endif
using System.Runtime.InteropServices;
using Nino.Core;
using Nino.Immutable;

// Registered like Nino.Core's own BCL formatters: generated code and the runtime resolver both read these.
[assembly: NinoFormatter(typeof(ImmutableArrayFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableListFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableHashSetFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableSortedSetFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableQueueFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableStackFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableDictionaryFormatter<,,,>))]
[assembly: NinoFormatter(typeof(ImmutableSortedDictionaryFormatter<,,,>))]

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableArray&lt;T&gt;</c>: length (-1 = default), then the elements, as <c>T[]</c> writes them. Its array
    /// is written and read as a <c>T[]</c> (raw elements as one block) and wrapped without a copy
    /// (<see cref="ImmutableCollectionsMarshal"/>); the array of the old value is never reused, since others may
    /// hold it.
    /// </summary>
    public struct ImmutableArrayFormatter<T, TFormatter> : INinoFormatter<ImmutableArray<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableArray<T> value, ref Writer writer)
        {
            // References (strings, classes) element by element: the array formatter's loop is slower here, reached
            // out of line with the writer behind a ref (100 strings: 290 ns against 264). Values as an array, raw
            // ones as one block.
            if (!typeof(T).IsValueType)
            {
                if (value.IsDefault)
                {
                    writer.WriteNullLength();
                    return;
                }

                writer.WriteLength(value.Length);
                foreach (var item in value)
                    default(TFormatter).Serialize(item, ref writer);
                return;
            }

            default(ArrayFormatter<T, TFormatter>).Serialize(ImmutableCollectionsMarshal.AsArray(value), ref writer);
        }

        public void Deserialize(ref ImmutableArray<T> value, ref Reader reader)
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            value = ImmutableCollectionsMarshal.AsImmutableArray(array);
        }
    }

    /// <summary><c>ImmutableList&lt;T&gt;</c>: count (-1 = null), then the elements.</summary>
    public struct ImmutableListFormatter<T, TFormatter> : INinoFormatter<ImmutableList<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableList<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Copied out by the tree's own recursive walk, then written as a T[] (raw elements as one block): its
            // enumerator rents a stack and walks it node by node.
            var array = ArrayPool<T>.Shared.Rent(value.Count);
            value.CopyTo(array);
            ImmutableCollections.WriteRented<T, TFormatter>(array, value.Count, ref writer);
        }

        public void Deserialize(ref ImmutableList<T> value, ref Reader reader)
        {
            // Read as a T[] (raw elements as one block) and built from it in one pass: a builder would insert and
            // rebalance its tree for every element.
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            value = array == null ? null : ImmutableList.CreateRange(array);
        }
    }

    /// <summary>
    /// <c>ImmutableHashSet&lt;T&gt;</c>: count (-1 = null), then the elements, as a <c>HashSet&lt;T&gt;</c> writes them.
    /// Read with the default comparer.
    /// </summary>
    public struct ImmutableHashSetFormatter<T, TFormatter> : INinoFormatter<ImmutableHashSet<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableHashSet<T> value, ref Writer writer) =>
            ImmutableCollections.Write<T, TFormatter>(value, value?.Count ?? 0, ref writer);

        public void Deserialize(ref ImmutableHashSet<T> value, ref Reader reader)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableCollections.Distinct(ImmutableHashSet.CreateRange(array), array.Length);
        }
    }

    /// <summary>
    /// <c>ImmutableSortedSet&lt;T&gt;</c>: count (-1 = null), then the elements in order, as a <c>SortedSet&lt;T&gt;</c>
    /// writes them. Read with the default comparer.
    /// </summary>
    public struct ImmutableSortedSetFormatter<T, TFormatter> : INinoFormatter<ImmutableSortedSet<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableSortedSet<T> value, ref Writer writer) =>
            ImmutableCollections.Write<T, TFormatter>(value, value?.Count ?? 0, ref writer);

        public void Deserialize(ref ImmutableSortedSet<T> value, ref Reader reader)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableCollections.Distinct(ImmutableSortedSet.CreateRange(array), array.Length);
        }
    }

    /// <summary><c>ImmutableQueue&lt;T&gt;</c>: count (-1 = null), then the elements front to back, as a <c>Queue&lt;T&gt;</c>.</summary>
    public struct ImmutableQueueFormatter<T, TFormatter> : INinoFormatter<ImmutableQueue<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableQueue<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // No count: patched after the elements.
            int countPos = writer.ReserveInt32();
            int count = 0;
            foreach (var item in value)
            {
                default(TFormatter).Serialize(item, ref writer);
                count++;
            }

            writer.PatchInt32(countPos, count);
        }

        public void Deserialize(ref ImmutableQueue<T> value, ref Reader reader)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableQueue.CreateRange(array);
        }
    }

    /// <summary>
    /// <c>ImmutableStack&lt;T&gt;</c>: count (-1 = null), then the elements bottom to top, as a <c>Stack&lt;T&gt;</c>, so
    /// pushing them in order rebuilds it.
    /// </summary>
    public struct ImmutableStackFormatter<T, TFormatter> : INinoFormatter<ImmutableStack<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableStack<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Enumerated top to bottom: reversed into a T[] first (raw elements then go as one block).
            var items = new List<T>(value);
            items.Reverse();
            var array = ArrayPool<T>.Shared.Rent(items.Count);
            items.CopyTo(array);
            ImmutableCollections.WriteRented<T, TFormatter>(array, items.Count, ref writer);
        }

        public void Deserialize(ref ImmutableStack<T> value, ref Reader reader)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableStack.CreateRange(array);
        }
    }

    /// <summary>
    /// <c>ImmutableDictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then each key and value, as a
    /// <c>Dictionary&lt;TKey, TValue&gt;</c> writes them. Read with the default comparers.
    /// </summary>
    public struct ImmutableDictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<ImmutableDictionary<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        public void Serialize(ImmutableDictionary<TKey, TValue> value, ref Writer writer) =>
            ImmutableCollections.Write<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(value, value?.Count ?? 0, ref writer);

        public void Deserialize(ref ImmutableDictionary<TKey, TValue> value, ref Reader reader)
        {
            var pairs = ImmutableCollections.Read<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(ref reader);
            value = pairs == null
                ? null
                : ImmutableCollections.Distinct(ImmutableCollections.Build(pairs, ImmutableDictionary.CreateRange),
                    pairs.Length);
        }
    }

    /// <summary>
    /// <c>ImmutableSortedDictionary&lt;TKey, TValue&gt;</c>: count (-1 = null), then each key and value in key order, as
    /// a <c>SortedDictionary&lt;TKey, TValue&gt;</c> writes them. Read with the default comparers.
    /// </summary>
    public struct ImmutableSortedDictionaryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<ImmutableSortedDictionary<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        public void Serialize(ImmutableSortedDictionary<TKey, TValue> value, ref Writer writer) =>
            ImmutableCollections.Write<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(value, value?.Count ?? 0, ref writer);

        public void Deserialize(ref ImmutableSortedDictionary<TKey, TValue> value, ref Reader reader)
        {
            var pairs = ImmutableCollections.Read<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(ref reader);
            value = pairs == null
                ? null
                : ImmutableCollections.Distinct(ImmutableCollections.Build(pairs, ImmutableSortedDictionary.CreateRange),
                    pairs.Length);
        }
    }

    internal static class ImmutableCollections
    {
        /// <summary>Writes a collection as a <c>T[]</c> of its elements (null for null).</summary>
        public static void Write<T, TFormatter>(IEnumerable<T> items, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (items == null)
            {
                writer.WriteNullLength();
                return;
            }

            var array = ArrayPool<T>.Shared.Rent(count);
            int i = 0;
            foreach (var item in items)
                array[i++] = item;
            WriteRented<T, TFormatter>(array, count, ref writer);
        }

        /// <summary>Writes the first <paramref name="count"/> elements of a rented array as a <c>T[]</c>, then returns it.</summary>
        public static void WriteRented<T, TFormatter>(T[] array, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            default(ArraySegmentFormatter<T, TFormatter>).Serialize(new ArraySegment<T>(array, 0, count), ref writer);
#if NETSTANDARD2_0
            ArrayPool<T>.Shared.Return(array, clearArray: true);
#else
            ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
#endif
        }

        /// <summary>Reads a new <c>T[]</c> (null for a null length).</summary>
        public static T[] Read<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            return array;
        }

        /// <summary>Builds a dictionary, reporting a key that differs in value from an earlier one as the data's fault.</summary>
        public static TDictionary Build<TKey, TValue, TDictionary>(KeyValuePair<TKey, TValue>[] pairs,
            Func<IEnumerable<KeyValuePair<TKey, TValue>>, TDictionary> create)
        {
            try
            {
                return create(pairs);
            }
            catch (ArgumentException e) when (!(e is ArgumentNullException))
            {
                DuplicateKey(typeof(TDictionary));
                return default;
            }
        }

        /// <summary>
        /// Returns <paramref name="collection"/>, or throws if it has fewer elements than were read: the data held an
        /// element or key twice, which a <c>HashSet&lt;T&gt;</c> or <c>Dictionary</c> member would not accept either.
        /// </summary>
        public static TCollection Distinct<TCollection>(TCollection collection, int length)
            where TCollection : System.Collections.ICollection
        {
            if (collection.Count != length)
                DuplicateKey(typeof(TCollection));
            return collection;
        }

        private static void DuplicateKey(Type type) =>
            throw new InvalidOperationException(
                $"Nino: the data holds the same key (or set element) twice for {type.FullName}, under its comparer.");
    }
}
