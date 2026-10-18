using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Nino.Core
{
    /// <summary><c>BigInteger</c>: byte count, then its two's complement bytes, least significant first.</summary>
    public struct BigIntegerFormatter : INinoFormatter<BigInteger>
    {
        public void Serialize(BigInteger value, ref Writer writer)
        {
#if NETSTANDARD2_0
            var bytes = value.ToByteArray();
            writer.WriteLength(bytes.Length);
            writer.WriteRaw(bytes);
#else
            int count = value.GetByteCount();
            writer.WriteLength(count);
            value.TryWriteBytes(MemoryMarshal.CreateSpan(ref writer.Reserve(count), count), out _);
#endif
        }

        public void Deserialize(ref BigInteger value, ref Reader reader)
        {
            int count = reader.ReadLength();
            if (count < 0)
                NinoThrow.InvalidLength(count);
#if NETSTANDARD2_0
            value = new BigInteger(reader.ReadRaw(count).ToArray());
#else
            value = new BigInteger(reader.ReadRaw(count));
#endif
        }
    }

    /// <summary><c>Uri</c>: its original string (null for null), read back relative or absolute.</summary>
    public struct UriFormatter : INinoFormatter<Uri>
    {
        public void Serialize(Uri value, ref Writer writer) => writer.Write(value?.OriginalString);

        public void Deserialize(ref Uri value, ref Reader reader)
        {
            string text = reader.ReadString();
            value = text == null ? null : new Uri(text, UriKind.RelativeOrAbsolute);
        }
    }

    /// <summary>
    /// <c>Version</c>: major (-1 = null), minor, build and revision as int32s; an undefined build or revision is -1.
    /// </summary>
    public struct VersionFormatter : INinoFormatter<Version>
    {
        public void Serialize(Version value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.UnsafeWrite(value.Major);
            writer.UnsafeWrite(value.Minor);
            writer.UnsafeWrite(value.Build);
            writer.UnsafeWrite(value.Revision);
        }

        public void Deserialize(ref Version value, ref Reader reader)
        {
            int major = reader.UnsafeRead<int>();
            if (major == NinoWire.NullLength)
            {
                value = null;
                return;
            }

            int minor = reader.UnsafeRead<int>(), build = reader.UnsafeRead<int>(), revision = reader.UnsafeRead<int>();
            value = build < 0 ? new Version(major, minor)
                : revision < 0 ? new Version(major, minor, build)
                : new Version(major, minor, build, revision);
        }
    }

    /// <summary>
    /// <c>StringBuilder</c>: its text, as a string is written. An existing builder is cleared and refilled.
    /// </summary>
    public struct StringBuilderFormatter : INinoFormatter<StringBuilder>
    {
        public void Serialize(StringBuilder value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Length);
#if NET8_0_OR_GREATER
            // Chunk by chunk, without the string ToString would allocate.
            foreach (var chunk in value.GetChunks())
                writer.WriteUnmanagedRaw(chunk.Span);
#else
            writer.WriteUnmanagedRaw(value.ToString().AsSpan());
#endif
        }

        public void Deserialize(ref StringBuilder value, ref Reader reader)
        {
            int length = reader.ReadLength(sizeof(char));
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var builder = value ??= new StringBuilder(length);
            builder.Clear();
            var chars = MemoryMarshal.Cast<byte, char>(reader.ReadRaw(length * sizeof(char)));
#if NETSTANDARD2_0
            builder.Append(chars.ToArray());
#else
            builder.Append(chars);
#endif
        }
    }

    /// <summary><c>BitArray</c>: bit count (-1 = null), then the bits, eight to a byte, the first in the lowest bit.</summary>
    public struct BitArrayFormatter : INinoFormatter<BitArray>
    {
        public void Serialize(BitArray value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            int count = value.Length;
            var bytes = new byte[(count + 7) / 8];
            value.CopyTo(bytes, 0);
            writer.WriteLength(count);
            writer.WriteRaw(bytes);
        }

        public void Deserialize(ref BitArray value, ref Reader reader)
        {
            int count = reader.ReadLength(minElementSize: 0);
            if (count < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var bytes = reader.ReadRaw((int)(((uint)count + 7) / 8)).ToArray();
            // The last byte may hold bits past the count: Length cuts them off.
            value = new BitArray(bytes) { Length = count };
        }
    }

    /// <summary><c>CultureInfo</c>: its name (null for null; "" is the invariant culture), read back read-only.</summary>
    public struct CultureInfoFormatter : INinoFormatter<CultureInfo>
    {
        public void Serialize(CultureInfo value, ref Writer writer) => writer.Write(value?.Name);

        public void Deserialize(ref CultureInfo value, ref Reader reader)
        {
            string name = reader.ReadString();
            value = name == null ? null : CultureInfo.GetCultureInfo(name);
        }
    }

    /// <summary>
    /// <c>TimeZoneInfo</c>: its serialized form (<c>ToSerializedString</c>: id, offsets and adjustment rules), so a
    /// zone reads back the same on a machine whose time zone data lacks or differs from it.
    /// </summary>
    public struct TimeZoneInfoFormatter : INinoFormatter<TimeZoneInfo>
    {
        public void Serialize(TimeZoneInfo value, ref Writer writer) => writer.Write(value?.ToSerializedString());

        public void Deserialize(ref TimeZoneInfo value, ref Reader reader)
        {
            string text = reader.ReadString();
            value = text == null ? null : TimeZoneInfo.FromSerializedString(text);
        }
    }

    /// <summary>
    /// <c>Lazy&lt;T&gt;</c>: a has-value byte (0 = null), then the value, which serializing creates if it was not yet.
    /// Read back as an already created <c>Lazy&lt;T&gt;</c>.
    /// </summary>
    public struct LazyFormatter<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T, TFormatter> : INinoFormatter<Lazy<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(Lazy<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            writer.UnsafeWrite(NinoWire.NotNull);
            default(TFormatter).Serialize(value.Value, ref writer);
        }

        public void Deserialize(ref Lazy<T> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == NinoWire.Null)
            {
                value = null;
                return;
            }

            T item = default;
            default(TFormatter).Deserialize(ref item, ref reader);
#if NETSTANDARD2_0
            // No constructor takes the value: create it here, so the Lazy reads back created on every target.
            value = new Lazy<T>(() => item);
            _ = value.Value;
#else
            value = new Lazy<T>(item);
#endif
        }
    }

    /// <summary>
    /// <c>Memory&lt;T&gt;</c>: the bytes of a <c>T[]</c> of its elements (length, then the elements; raw ones as one
    /// block). Read into a new array; an array's null reads as empty memory.
    /// </summary>
    public struct MemoryFormatter<T, TFormatter> : INinoFormatter<Memory<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(Memory<T> value, ref Writer writer) =>
            NinoCollections.WriteSpan<T, TFormatter>(value.Span, ref writer);

        public void Deserialize(ref Memory<T> value, ref Reader reader) =>
            value = NinoCollections.ReadArray<T, TFormatter>(ref reader);
    }

    /// <summary><c>ReadOnlyMemory&lt;T&gt;</c>: as <see cref="MemoryFormatter{T,TFormatter}"/>.</summary>
    public struct ReadOnlyMemoryFormatter<T, TFormatter> : INinoFormatter<ReadOnlyMemory<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ReadOnlyMemory<T> value, ref Writer writer) =>
            NinoCollections.WriteSpan<T, TFormatter>(value.Span, ref writer);

        public void Deserialize(ref ReadOnlyMemory<T> value, ref Reader reader) =>
            value = NinoCollections.ReadArray<T, TFormatter>(ref reader);
    }

    /// <summary>
    /// <c>ReadOnlySequence&lt;T&gt;</c>: as <see cref="MemoryFormatter{T,TFormatter}"/>, its segments written one
    /// after another. Read back as one segment.
    /// </summary>
    public struct ReadOnlySequenceFormatter<T, TFormatter> : INinoFormatter<ReadOnlySequence<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ReadOnlySequence<T> value, ref Writer writer)
        {
            if (value.IsSingleSegment)
            {
                NinoCollections.WriteSpan<T, TFormatter>(value.First.Span, ref writer);
                return;
            }

            writer.WriteLength(checked((int)value.Length));
            foreach (var segment in value)
                NinoElements.Write<T, TFormatter>(segment.Span, ref writer);
        }

        public void Deserialize(ref ReadOnlySequence<T> value, ref Reader reader)
        {
            var array = NinoCollections.ReadArray<T, TFormatter>(ref reader);
            value = array == null ? default : new ReadOnlySequence<T>(array);
        }
    }

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
            if (value == null)
            {
                writer.WriteNullLength();
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

        public void Deserialize(ref IGrouping<TKey, TElement> value, ref Reader reader)
        {
            int count = reader.ReadLength();
            if (count < 0)
            {
                value = null;
                return;
            }

            TKey key = default;
            default(TKeyFormatter).Deserialize(ref key, ref reader);
            var elements = new TElement[count];
            NinoElements.Read<TElement, TElementFormatter>(elements, ref reader);
            value = new NinoGrouping<TKey, TElement>(key, elements);
        }
    }

    /// <summary>
    /// <c>ILookup&lt;TKey, TElement&gt;</c>: grouping count (-1 = null), then each grouping as
    /// <see cref="GroupingFormatter{TKey,TElement,TKeyFormatter,TElementFormatter}"/> writes it. Read back as
    /// <c>ToLookup</c> builds one, with the default comparer.
    /// </summary>
    public struct LookupFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>
        : INinoFormatter<ILookup<TKey, TElement>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TElementFormatter : struct, INinoFormatter<TElement>
    {
        public void Serialize(ILookup<TKey, TElement> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Count);
            foreach (var grouping in value)
                default(GroupingFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>).Serialize(grouping,
                    ref writer);
        }

        public void Deserialize(ref ILookup<TKey, TElement> value, ref Reader reader)
        {
            int count = reader.ReadLength();
            if (count < 0)
            {
                value = null;
                return;
            }

            var groupings = new IGrouping<TKey, TElement>[count];
            for (int i = 0; i < count; i++)
                default(GroupingFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>).Deserialize(
                    ref groupings[i], ref reader);
            value = groupings.SelectMany(g => g, (g, element) => new KeyValuePair<TKey, TElement>(g.Key, element))
                .ToLookup(pair => pair.Key, pair => pair.Value);
        }
    }

    /// <summary>
    /// <c>BlockingCollection&lt;T&gt;</c>: count (-1 = null), then a snapshot of its elements in the order it hands them
    /// out. Read into a new, unbounded collection over a <c>ConcurrentQueue&lt;T&gt;</c> (its default).
    /// </summary>
    public struct BlockingCollectionFormatter<T, TFormatter> : INinoFormatter<BlockingCollection<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(BlockingCollection<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            NinoCollections.WriteSpan<T, TFormatter>(value.ToArray(), ref writer);
        }

        public void Deserialize(ref BlockingCollection<T> value, ref Reader reader)
        {
            NinoRecycler.Discard(ref value);
            var items = NinoCollections.ReadArray<T, TFormatter>(ref reader);
            if (items == null)
                return;
            var collection = new BlockingCollection<T>(new ConcurrentQueue<T>(items));
            value = collection;
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

        public int Count => _elements.Length;

        public TElement this[int index] => _elements[index];

        public IEnumerator<TElement> GetEnumerator() => ((IEnumerable<TElement>)_elements).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _elements.GetEnumerator();
    }

    internal static partial class NinoCollections
    {
        /// <summary>Writes a span as a <c>T[]</c> of its elements is written: length, then the elements.</summary>
        public static void WriteSpan<T, TFormatter>(ReadOnlySpan<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            writer.WriteLength(values.Length);
            NinoElements.Write<T, TFormatter>(values, ref writer);
        }

        /// <summary>Reads a new <c>T[]</c> (null for a null length).</summary>
        public static T[] ReadArray<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            return array;
        }
    }
}
