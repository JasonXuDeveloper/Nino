using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>How a collection takes one element read for it (<see cref="NinoCollections.AddAll"/>).</summary>
    internal interface INinoAdder<TTarget, T>
    {
        void Add(TTarget target, T item);
    }

    /// <summary>Shared helpers for collection formatters.</summary>
    internal static class NinoCollections
    {

        /// <summary>
        /// Reads <paramref name="length"/> elements into a list, as <see cref="ListFormatter{TList,T,TFormatter}"/>
        /// does, for <see cref="NinoKind{T,TFormatter}.Heavy"/> elements: where they do not
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
                NinoRecycler.DiscardAll<T, TFormatter>(ListAccess.AsSpan<T>(list).Slice(length));
            int count = NinoElements.Fits(length, Unsafe.SizeOf<T>(), reader.Remaining)
                ? length
                : Math.Min(length, Math.Max(NinoElements.Clamped(Unsafe.SizeOf<T>(), reader.Remaining), list.Count));
            int done = 0;
            while (true)
            {
                var span = ListAccess.Open(list, count, overwritten: false, out var buffer);
                NinoRuns.Read<T, TFormatter>(span.Slice(done), ref reader);
                ListAccess.Close(list, count, buffer);
                if (count == length)
                    return reader;
                done = count;
                count = (int)Math.Min(length, 2L * count);
            }
        }

        /// <summary>
        /// The fewest elements of a collection of elements written one by one that are copied out of it before they
        /// are written (<see cref="NinoGather.WriteCopied{T,TFormatter}"/>) rather than enumerated: strings through a ReadOnlyCollection,
        /// locally against the enumerator, 20% slower at 4 elements, 25% faster at 16, 40% at 1,000.
        /// </summary>
        internal const int CopiedFrom = 16;

        /// <summary>Writes the count of <paramref name="values"/> (patched afterwards if unknown), then the
        /// elements.</summary>
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

            // Gathered and written through the run loops (#355): a collection that fits one chunk copied into it by
            // its CopyTo, one call where an enumerator makes two interface calls per element (ReadOnlyCollection<T>,
            // Collection<T>, ...), for elements of a fixed size (NinoKind.Stride, a constant: one block) and from
            // CopiedFrom elements on, where the copy costs more than it saves below; else, or larger, through its
            // enumerator a chunk at a time.
            if (values is ICollection<T> collection && count <= NinoGather<T>.Length &&
                (NinoKind.Stride<T, TFormatter>() > 0 || count >= CopiedFrom))
                NinoGather.WriteCopied<T, TFormatter>(collection, count, ref writer);
            else
                writer = WriteEnumerated<T, TFormatter>(values, count, writer);
        }

        // Out of line with the writer by value, the enumerator taken there: one call in the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEnumerated<T, TFormatter>(IEnumerable<T> values, int count, Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoGather.Write<T, TFormatter, NinoGathered.Enumerated<T, IEnumerator<T>>>(new(values.GetEnumerator()),
                count, writer);

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

        /// <summary>Adds a pair; a key the data repeats (under the dictionary's comparer) is invalid data.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void AddUnique<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (!dictionary.TryAdd(key, value))
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
        /// The elements of <paramref name="source"/> in a rented array (give it back with <see cref="ReturnRented{T}"/>),
        /// in the order <see cref="ICollection{T}.CopyTo"/> puts them.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] RentCopied<T>(ICollection<T> source, out int count)
        {
            count = source.Count;
            var array = ArrayPool<T>.Shared.Rent(count);
            source.CopyTo(array, 0);
            return array;
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
            // On CoreLib, rent directly for heavy elements if the pool's buffer fits the allocation budget.
            if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoKind<T, TFormatter>.Heavy
#if NET8_0_OR_GREATER
                && !NinoElements.FitsRented(length, Unsafe.SizeOf<T>(), reader.Remaining)
#endif
            )
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
                NinoRuns.Read<T, TFormatter>(span, ref reader);
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
        /// Reads <paramref name="length"/> elements and gives each to <typeparamref name="TAdder"/>: what a collection
        /// that takes its elements one at a time reads through. Raw values, slots, blocks and strings are read a chunk
        /// at a time by the run's read loop (<see cref="NinoGather.ReadAll{T,TFormatter,TTarget}"/>), others one at a
        /// time. A slot read alone went through the run's kernel once an element (1,000 <c>int?</c> into a
        /// <c>Collection</c>: 10,486 ns, against 4,552 read as one run; a <c>HashSet</c> 12,174 against 4,842, local).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Reader AddAll<TTarget, T, TFormatter, TAdder>(TTarget target, int length, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
            where TAdder : struct, INinoAdder<TTarget, T>
        {
            var adds = new NinoGather.Adds<TTarget, T, TAdder>(target);
            return NinoGather.ReadAll<T, TFormatter, NinoGather.Adds<TTarget, T, TAdder>>(ref adds, length, reader);
        }

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
            // Elements of a fixed size (NinoKind.Stride, a constant): the length and the block in one reservation, one
            // capacity check where the length and the block would each take one.
            int block = NinoKind.Stride<T, TFormatter>();
            if (block > 0)
            {
                ref byte elements = ref writer.ReserveCounted(values.Length, block);
                if (!values.IsEmpty)
                    NinoElements.WriteBlock<T, TFormatter>(ref elements, values);
                return;
            }

            writer.WriteLength(values.Length);
            NinoRuns.Write<T, TFormatter>(values, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryWriteRawExternal<T, TFormatter>(ReadOnlyMemory<T> value, IBufferWriter<byte> buffer)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw &&
            !MemoryMarshal.TryGetMemoryManager<T, MemoryManager<T>>(value, out _) &&
            MemoryMarshal.TryGetArray(value, out var segment) &&
            TryWriteCanonicalExternal(segment, buffer, nullSegment: false);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryWriteRawExternal<T, TFormatter>(ArraySegment<T> value, IBufferWriter<byte> buffer)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw &&
            TryWriteCanonicalExternal(value, buffer, nullSegment: true);

        private static bool TryWriteCanonicalExternal<T>(ArraySegment<T> value, IBufferWriter<byte> buffer, bool nullSegment)
        {
            long size = (long)value.Count * Unsafe.SizeOf<T>() + NinoWire.LengthSize;
            if (!BitConverter.IsLittleEndian || size > int.MaxValue)
                return false;

            // The destination can call user code. Borrow the array, without a MemoryManager that can release it.
            var values = value.AsSpan();
            var source = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), (int)size - NinoWire.LengthSize);
            int count = nullSegment && value.Array == null ? NinoWire.NullLength : value.Count;
            NinoLayout<T>.WriteRawExternal(buffer, source, count, (int)size);
            return true;
        }

        /// <summary>Reads a new <c>T[]</c> (null for a null length).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] ReadArray<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref reader, ref array);
            return array;
        }
    }
}
