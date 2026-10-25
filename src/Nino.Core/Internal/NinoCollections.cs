using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>Shared helpers for collection formatters.</summary>
    internal static class NinoCollections
    {
        /// <summary>
        /// Reads <paramref name="length"/> elements into a list, as <see cref="ListFormatter{TList,T,TFormatter}"/>
        /// does, for <see cref="NinoElements.HeavyElements{T,TFormatter}"/> elements: where they do not
        /// <see cref="NinoElements.Fits"/>, the list's count starts at what fits the bound (or the elements it has)
        /// and doubles as they are read, as
        /// <see cref="NinoElements.ReadGrowing{T,TFormatter}"/> does for an array. Elements it has are reused.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Reader ReadGrowing<TList, T, TFormatter>(scoped ref TList value, int length, Reader reader)
            where TList : List<T>
            where TFormatter : struct, INinoFormatter<T>
        {
            var list = value ??= ListAccess.Create<TList, T>(0);
            if (list.Count > length)
                NinoRecycler.DiscardAll(ListAccess.AsSpan<T>(list).Slice(length));
            int count = NinoElements.Fits(length, Unsafe.SizeOf<T>(), reader.Remaining)
                ? length
                : Math.Min(length, Math.Max(NinoElements.Clamped(Unsafe.SizeOf<T>(), reader.Remaining), list.Count));
            int done = 0;
            while (true)
            {
                var span = ListAccess.Open(list, count, overwritten: false, out var buffer);
                NinoElements.Read<T, TFormatter>(span.Slice(done), ref reader);
                ListAccess.Close(list, count, buffer);
                if (count == length)
                    return reader;
                done = count;
                count = (int)Math.Min(length, 2L * count);
            }
        }

        /// <summary>Writes the count of <paramref name="values"/> (patched afterwards if unknown), then the elements.</summary>
        public static void WriteEnumerable<T, TFormatter>(IEnumerable<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count;
            if (values is ICollection<T> c)
                count = c.Count;
            else if (values is IReadOnlyCollection<T> rc)
                count = rc.Count;
            else
                count = -1;

            // As HashSetFormatter's: an empty collection skips the call, and elements with references go through the
            // writer parameter.
            if (count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            if (NinoElements.WritesOnLocal<T>())
                writer = WriteEnumerated<T, TFormatter>(values, count, writer);
            else
                WriteEnumerated<T, TFormatter>(values, count, ref writer);
        }

        // The writer by value and returned, a local whose cursor stays in registers across the enumerator's calls (as
        // HashSetFormatter's).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEnumerated<T, TFormatter>(IEnumerable<T> values, int count, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            WriteEnumerated<T, TFormatter>(values, count, ref writer);
            return writer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteEnumerated<T, TFormatter>(IEnumerable<T> values, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int countPos = writer.ReserveInt32();
            int written = 0;
            foreach (var item in values)
            {
                default(TFormatter).Serialize(item, ref writer);
                written++;
            }

            if (count >= 0 && count != written)
                NinoThrow.ModifiedDuringWrite();
            writer.PatchInt32(countPos, written);
        }

        /// <summary>
        /// Writes a collection declared as an interface: count (-1 = null), then the elements. Lists and arrays
        /// take their own (block-copying) paths.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInterface<T, TFormatter>(IEnumerable<T> value, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (value is List<T> list)
            {
                default(ListFormatter<List<T>, T, TFormatter>).Serialize(list, ref writer);
                return;
            }

            if (value is T[] array)
            {
                default(ArrayFormatter<T, TFormatter>).Serialize(array, ref writer);
                return;
            }

            WriteEnumerable<T, TFormatter>(value, ref writer);
        }

        /// <summary><c>Dictionary.TryAdd</c>, which netstandard2.0 lacks.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryAdd<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
#if NETSTANDARD2_0
            int before = dictionary.Count;
            dictionary[key] = value;
            return dictionary.Count != before;
#else
            return dictionary.TryAdd(key, value);
#endif
        }

        /// <summary>Adds a pair; a key the data repeats (under the dictionary's comparer) is invalid data.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddUnique<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (!TryAdd(dictionary, key, value))
                NinoThrow.DuplicateKey(dictionary.GetType());
        }

        /// <summary>
        /// Adds a pair to a dictionary that held <paramref name="count"/> pairs, through its indexer (which throws
        /// for a null key); a key the data repeats is invalid data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddUnique<TKey, TValue>(IDictionary<TKey, TValue> dictionary, TKey key, TValue value,
            int count)
        {
            if (key == null)
                AddNullKey(dictionary, value);
            else
                dictionary[key] = value;
            if (dictionary.Count != count + 1)
                NinoThrow.DuplicateKey(dictionary.GetType());
        }

        /// <summary>
        /// A null key, which a dictionary of its own may take; the runtime's throw ArgumentNullException, reported as
        /// invalid data.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddNullKey<TKey, TValue>(IDictionary<TKey, TValue> dictionary, TValue value)
        {
            try
            {
                dictionary[default] = value;
            }
            catch (ArgumentNullException)
            {
                NinoThrow.NullKey(dictionary.GetType());
            }
        }

        /// <summary>
        /// Writes the count of <paramref name="source"/>, then its elements in the order
        /// <see cref="ICollection{T}.CopyTo"/> puts them, as an array is written (raw elements as one block): copied
        /// out of the <see cref="SortedSet{T}"/> tree under <paramref name="root"/> when it is not null.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteCopied<T, TFormatter>(ICollection<T> source, object root, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = source.Count;
            var array = ArrayPool<T>.Shared.Rent(count);
            try
            {
                CopyOut(source, root, array);
                WriteSpan<T, TFormatter>(new ReadOnlySpan<T>(array, 0, count), ref writer);
            }
            finally
            {
                ReturnRented(array);
            }
        }

        // Out of line: in the caller, the branch keeps the writer in memory.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CopyOut<T>(ICollection<T> source, object root, T[] array)
        {
            if (root != null)
                SortedSetNodes<T>.CopyTo(root, array);
            else
                source.CopyTo(array, 0);
        }

        /// <summary>
        /// Reads <paramref name="length"/> elements as an array is read, into a rented array (give it back with
        /// <see cref="ReturnRented{T}"/>). Out of line, as it was with the catch it had (a method with a catch is
        /// never inlined; one with a finally may be, which made every caller larger).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T[] ReadRented<T, TFormatter>(int length, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Elements far larger in memory than on the wire (NinoElements.HeavyElements): out of line.
            if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
            {
                reader = ReadRentedHeavy<T, TFormatter>(out var rented, length, reader);
                return rented;
            }

            var array = ArrayPool<T>.Shared.Rent(length);
            var span = new Span<T>(array, 0, length);
            // Over default values: a rented array holds old ones (unless every element is overwritten).
            if (!NinoElements.ReadOverwrites<T, TFormatter>())
                span.Clear();
            // A finally, not a catch that rethrows: a rethrow is a new throw from the catch, on top of the stack of
            // the first, so a failure deep in data that nests through here (a collection holding itself) would
            // need stack at every level on its way out.
            bool read = false;
            try
            {
                NinoElements.Read<T, TFormatter>(span, ref reader);
                read = true;
            }
            finally
            {
                if (!read)
                    ReturnRented(array);
            }

            return array;
        }

        /// <summary>
        /// <see cref="ReadRented{T,TFormatter}"/> of elements far larger in memory than on the wire: where the data
        /// could not fill as many (<see cref="NinoElements.Fits"/>), read into an array grown as they are
        /// (<see cref="NinoElements.ReadGrowing{T,TFormatter}"/>), then copied into the rented one.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadRentedHeavy<T, TFormatter>(out T[] rented, int length, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            T[] grown = null;
            reader = NinoElements.ReadGrowing<T, TFormatter>(ref grown, length, reader);
            rented = ArrayPool<T>.Shared.Rent(length);
            Array.Copy(grown, rented, length);
            return reader;
        }

        /// <summary>
        /// <see cref="WriteCopied{T,TFormatter}"/> for pairs, as
        /// <see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/> writes them.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteCopiedPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(
            ICollection<KeyValuePair<TKey, TValue>> source, object root, ref Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            WriteCopied<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(
                source, root, ref writer);

        /// <summary><see cref="ReadRented{T,TFormatter}"/> for pairs.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static KeyValuePair<TKey, TValue>[] ReadPairsRented<TKey, TValue, TKeyFormatter, TValueFormatter>(
            int length, ref Reader reader)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            ReadRented<KeyValuePair<TKey, TValue>,
                KeyValuePairFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>>(
                length, ref reader);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReturnRented<T>(T[] array) =>
            ArrayPool<T>.Shared.Return(array, NinoUnsafe.IsReferenceOrContainsReferences<T>());

        /// <summary>Writes a span as a <c>T[]</c> of its elements is written: length, then the elements.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSpan<T, TFormatter>(ReadOnlySpan<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Elements of a fixed size (BlockSize, a constant): the length and the block in one reservation, one
            // capacity check where the length and the block would each take one.
            int block = NinoElements.BlockSize<T, TFormatter>();
            if (block > 0)
            {
                ref byte elements = ref writer.ReserveCounted(values.Length, block);
                if (!values.IsEmpty)
                    NinoElements.WriteBlock<T, TFormatter>(ref elements, values);
                return;
            }

            writer.WriteLength(values.Length);
            NinoElements.Write<T, TFormatter>(values, ref writer);
        }

        /// <summary>Reads a new <c>T[]</c> (null for a null length).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] ReadArray<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            return array;
        }
    }
}
