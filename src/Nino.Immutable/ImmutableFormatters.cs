using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
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

        public void Deserialize(ref ImmutableArray<T> value, ref Reader reader) =>
            value = ImmutableCollectionsMarshal.AsImmutableArray(ImmutableCollections.Read<T, TFormatter>(ref reader));
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
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
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
        public void Serialize(ImmutableHashSet<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Copied out of its tree (HashTrees) in the order its enumerator yields them, and written as an array
            // (raw elements as one block): the enumerator keeps a pooled stack of nodes and checks it per element.
            int count = value.Count;
            if (count != 0 && HashTrees.Usable && SetTree<T>.Copy(value, count) is { } array)
            {
                ImmutableCollections.WriteRented<T, TFormatter>(array, count, ref writer);
                return;
            }

            // Element by element with its own (struct) enumerator: through IEnumerable<T>, each step is an
            // interface call.
            writer = WriteItems(value, count, writer);
        }

        // Out of line, the count too: inlined into the caller, the large enumerator is kept in memory instead of
        // registers, and this path (empty sets, runtimes whose trees HashTrees cannot read) takes the inlining
        // budget of the common one. The writer by value and returned: one passed by reference would keep the
        // caller's in memory.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteItems(ImmutableHashSet<T> value, int count, Writer writer)
        {
            writer.WriteLength(count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
            return writer;
        }

        public void Deserialize(ref ImmutableHashSet<T> value, ref Reader reader)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            if (array == null)
            {
                value = null;
                return;
            }

            value = Build(array);
        }

        // Built as its tree where it can be (HashTrees), else added one by one to a builder (CreateRange enumerates
        // through IEnumerable<T>); out of line like WriteItems.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImmutableHashSet<T> Build(T[] items)
        {
            // Empty: the shared instance, which a builder would return too, without allocating one.
            if (items.Length == 0)
                return ImmutableHashSet<T>.Empty;
            if (HashTrees.Usable && SetTree<T>.Create(items) is { } set)
                return set;

            var builder = ImmutableHashSet.CreateBuilder<T>();
            foreach (var item in items)
            {
                if (!builder.Add(item))
                    ImmutableCollections.DuplicateKey(typeof(ImmutableHashSet<T>));
            }

            return builder.ToImmutable();
        }
    }

    /// <summary>
    /// <c>ImmutableSortedSet&lt;T&gt;</c>: count (-1 = null), then the elements in order, as a <c>SortedSet&lt;T&gt;</c>
    /// writes them. Read with the default comparer.
    /// </summary>
    public struct ImmutableSortedSetFormatter<T, TFormatter> : INinoFormatter<ImmutableSortedSet<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableSortedSet<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Copied out of its tree in order (SortedTrees), else with its enumerator, then written as an array.
            int count = value.Count;
            ImmutableCollections.WriteRented<T, TFormatter>(Copy(value, count), count, ref writer);
        }

        // Out of line: renting inlines a large path into the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T[] Copy(ImmutableSortedSet<T> value, int count)
        {
            var array = ArrayPool<T>.Shared.Rent(count);
            if (SortedTrees.Usable)
            {
                SortedSetTree<T>.CopyTo(value, array);
                return array;
            }

            // By reference: the set enumerates exactly Count elements, which the JIT cannot tell.
            ref T slot = ref MemoryMarshal.GetReference(array.AsSpan());
            foreach (var item in value)
            {
                slot = item;
                slot = ref Unsafe.Add(ref slot, 1);
            }

            return array;
        }

        public void Deserialize(ref ImmutableSortedSet<T> value, ref Reader reader)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : Build(array);
        }

        // Built as its tree where it can be (SortedTrees), else by CreateRange, which sorts the elements.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImmutableSortedSet<T> Build(T[] items) =>
            items.Length == 0 ? ImmutableSortedSet<T>.Empty
            : SortedTrees.Usable && SortedSetTree<T>.Create(items) is { } set
                ? set
                : ImmutableCollections.Distinct(ImmutableSortedSet.CreateRange(items), items.Length);
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

            // Enumerated top to bottom: counted, then copied into a T[] from its end (raw elements then go as one
            // block), without a list to grow and reverse.
            int count = 0;
            for (var rest = value; !rest.IsEmpty; rest = rest.Pop())
                count++;
            var array = ArrayPool<T>.Shared.Rent(count);
            ref T slot = ref Unsafe.Add(ref MemoryMarshal.GetReference(array.AsSpan()), count);
            foreach (var item in value)
            {
                slot = ref Unsafe.Subtract(ref slot, 1);
                slot = item;
            }
            ImmutableCollections.WriteRented<T, TFormatter>(array, count, ref writer);
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

        public void Deserialize(ref ImmutableDictionary<TKey, TValue> value, ref Reader reader)
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

            var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>();
            ImmutableCollections.AddAll(builder, pairs, typeof(ImmutableDictionary<TKey, TValue>));
            return builder.ToImmutable();
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
        public void Serialize(ImmutableSortedDictionary<TKey, TValue> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // As ImmutableSortedSet<T>'s.
            int count = value.Count;
            ImmutableCollections.WriteRented<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(Copy(value, count), count, ref writer);
        }

        // Out of line, as ImmutableSortedSet<T>'s.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static KeyValuePair<TKey, TValue>[] Copy(ImmutableSortedDictionary<TKey, TValue> value, int count)
        {
            var pairs = ArrayPool<KeyValuePair<TKey, TValue>>.Shared.Rent(count);
            if (SortedTrees.Usable)
            {
                SortedDictionaryTree<TKey, TValue>.CopyTo(value, pairs);
                return pairs;
            }

            ref var slot = ref MemoryMarshal.GetReference(pairs.AsSpan());
            foreach (var pair in value)
            {
                slot = pair;
                slot = ref Unsafe.Add(ref slot, 1);
            }

            return pairs;
        }

        public void Deserialize(ref ImmutableSortedDictionary<TKey, TValue> value, ref Reader reader)
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

    internal static class ImmutableCollections
    {
        /// <summary>Writes the first <paramref name="count"/> elements of a rented array as a <c>T[]</c>, then returns it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRented<T, TFormatter>(T[] array, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            default(ArraySegmentFormatter<T, TFormatter>).Serialize(new ArraySegment<T>(array, 0, count), ref writer);
            Return(array);
        }

        /// <summary>Returns a rented array, cleared if it may hold references (as Nino.Core returns its own).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Return<T>(T[] array) =>
            ArrayPool<T>.Shared.Return(array, NinoUnsafe.IsReferenceOrContainsReferences<T>());

        /// <summary>Reads a new <c>T[]</c> (null for a null length).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] Read<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            return array;
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

        /// <summary>
        /// Adds <paramref name="pairs"/> to <paramref name="builder"/>, empty, or throws if the data holds a key twice: a
        /// key already there throws (another value) or leaves the count as it was (the same value).
        /// </summary>
        public static void AddAll<TKey, TValue>(IDictionary<TKey, TValue> builder, KeyValuePair<TKey, TValue>[] pairs,
            Type type)
        {
            try
            {
                foreach (var pair in pairs)
                {
                    int count = builder.Count;
                    builder.Add(pair.Key, pair.Value);
                    if (builder.Count == count)
                        DuplicateKey(type);
                }
            }
            catch (ArgumentNullException)
            {
                throw new InvalidOperationException(
                    $"Nino: the data holds a null key for {type.FullName}, which takes none.");
            }
            catch (ArgumentException)
            {
                DuplicateKey(type);
            }
        }

        public static void DuplicateKey(Type type) =>
            throw new InvalidOperationException(
                $"Nino: the data holds the same key (or set element) twice for {type.FullName}, under its comparer.");
    }
}
