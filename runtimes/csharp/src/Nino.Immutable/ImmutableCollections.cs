using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core;
using Nino.Core.CompilerServices;

namespace Nino.Immutable
{
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

        /// <summary>
        /// Writes <paramref name="items"/> (not null) as Nino.Core writes a collection it can only enumerate
        /// (<see cref="InterfaceListFormatter{TInterface,T,TFormatter}"/>): the count, then each element as a collection
        /// of them writes it, so a raw <c>T?</c> is a slot of a fixed size, never its formatter's single value, without
        /// a guess at which elements are slots. Out of line, the writer by value and returned: one passed by reference
        /// would keep the caller's in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Writer WriteEnumerated<T, TFormatter>(IEnumerable<T> items, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            default(InterfaceListFormatter<IEnumerable<T>, T, TFormatter>).Serialize(items, ref writer);
            return writer;
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
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref reader, ref array);
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
                throw new Nino.Core.NinoDataException(
                    $"Nino: the data holds a null key for {type.FullName}, which takes none.");
            }
            catch (ArgumentException)
            {
                DuplicateKey(type);
            }
        }

        public static void DuplicateKey(Type type) =>
            throw new Nino.Core.NinoDataException(
                $"Nino: the data holds the same key (or set element) twice for {type.FullName}, under its comparer.");
    }
}
