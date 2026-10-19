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
            if (writer.TryWriteNullLength(value))
                return;

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
            if (writer.TryWriteNullLength(value))
                return;

            // The string encoding with UTF-16 chars (header: the length), which the reader of strings reads too: a
            // copy of the builder's chunks. Narrowing Latin-1 text to one byte a char would halve the bytes but
            // cost a widening pass on every read, where a StringBuilder cannot take chars without copying them.
            // Its length, then its chars: up to 4 GB of them, which ReserveCounted rejects past 2 GB.
            int length = value.Length;
            ref byte body = ref writer.ReserveCounted(length, sizeof(char));
#if NET8_0_OR_GREATER
            foreach (var chunk in value.GetChunks())
            {
                var chars = MemoryMarshal.AsBytes(chunk.Span);
                NinoUnsafe.Copy(ref body, ref MemoryMarshal.GetReference(chars), (uint)chars.Length);
                body = ref Unsafe.Add(ref body, chars.Length);
            }
#else
            var chars = MemoryMarshal.AsBytes(value.ToString().AsSpan());
            NinoUnsafe.Copy(ref body, ref MemoryMarshal.GetReference(chars), (uint)chars.Length);
#endif
        }

        public unsafe void Deserialize(ref StringBuilder value, ref Reader reader)
        {
            var source = reader.ReadRaw(NinoWire.LengthSize);
            int header = NinoUnsafe.Read<int>(ref MemoryMarshal.GetReference(source));
            if (header == NinoWire.NullLength)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            // ~length for Latin-1 chars, one byte each; the length for UTF-16.
            int length = header < 0 ? ~header : header;
            var builder = value ??= new StringBuilder(length);
            builder.Clear();
            if (header < 0)
            {
                var latin1 = reader.ReadRaw(length);
                NinoStrings.AppendLatin1(builder, ref MemoryMarshal.GetReference(latin1), length);
                return;
            }

            var chars = MemoryMarshal.Cast<byte, char>(reader.ReadRaw(checked(length * sizeof(char))));
            fixed (char* p = chars)
            {
                builder.Append(p, chars.Length);
            }
        }
    }

    /// <summary><c>BitArray</c>: bit count (-1 = null), then the bits, eight to a byte, the first in the lowest bit.</summary>
    public struct BitArrayFormatter : INinoFormatter<BitArray>
    {
        public void Serialize(BitArray value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            int count = value.Length, size = (count + 7) / 8;
            var bytes = ArrayPool<byte>.Shared.Rent(size);
            value.CopyTo(bytes, 0);
            writer.WriteLength(count);
            writer.WriteRaw(bytes.AsSpan(0, size));
            ArrayPool<byte>.Shared.Return(bytes);
        }

        public void Deserialize(ref BitArray value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, 0, out int count))
                return;

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
    /// <c>TimeZoneInfo</c>: its id (null for null), then, for a zone that is not one of this machine's system zones
    /// (a custom zone), its serialized form (<c>ToSerializedString</c>), else null. A system zone reads back as the
    /// reader's system zone of that id (Windows and IANA ids are converted where the runtime can); a custom one from
    /// its serialized form. System zones are not read from their serialized form: the runtime cannot read back that
    /// of many IANA zones, whose adjustment rules overlap.
    /// </summary>
    public struct TimeZoneInfoFormatter : INinoFormatter<TimeZoneInfo>
    {
        public void Serialize(TimeZoneInfo value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            writer.Write(value.Id);
            writer.Write(IsSystemZone(value) ? null : value.ToSerializedString());
        }

        public void Deserialize(ref TimeZoneInfo value, ref Reader reader)
        {
            string id = reader.ReadString();
            if (id == null)
            {
                value = null;
                return;
            }

            string rules = reader.ReadString();
            value = rules == null ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.FromSerializedString(rules);
        }

        private static bool IsSystemZone(TimeZoneInfo zone)
        {
            if (ReferenceEquals(zone, TimeZoneInfo.Utc))
                return true;
            try
            {
                // Cached by the runtime: the same instance for a system zone, else one with the same rules.
                var system = TimeZoneInfo.FindSystemTimeZoneById(zone.Id);
                return ReferenceEquals(system, zone) ||
                       (system.BaseUtcOffset == zone.BaseUtcOffset && system.HasSameRules(zone));
            }
            catch (TimeZoneNotFoundException)
            {
                return false;
            }
            catch (InvalidTimeZoneException)
            {
                return false;
            }
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
            if (writer.WritePresence(value))
                default(TFormatter).Serialize(value.Value, ref writer);
        }

        public void Deserialize(ref Lazy<T> value, ref Reader reader)
        {
            if (!reader.TryReadPresent(ref value))
                return;

            // A Lazy is immutable once created, as a tuple: the value is read into a local and the Lazy replaced.
            T item = default;
            default(TFormatter).Deserialize(ref item, ref reader);
#if NETSTANDARD2_0
            // No constructor takes the value: create it here, so the Lazy reads back created on every target.
            var created = new Lazy<T>(() => item);
            _ = created.Value;
#else
            var created = new Lazy<T>(item);
#endif
            NinoRecycler.Replace(ref value, created);
        }
    }

    /// <summary>
    /// <c>Memory&lt;T&gt;</c>: the bytes of a <c>T[]</c> of its elements (length, then the elements; raw ones as one
    /// block). Read into the current memory when its length matches, as an array is, else into a new array; an
    /// array's null reads as empty memory.
    /// </summary>
    public struct MemoryFormatter<T, TFormatter> : INinoFormatter<Memory<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(Memory<T> value, ref Writer writer) =>
            NinoCollections.WriteSpan<T, TFormatter>(value.Span, ref writer);

        public void Deserialize(ref Memory<T> value, ref Reader reader)
        {
            if (!reader.TryReadLength(1, out int length))
            {
                value = default;
                return;
            }

            // A new array is left uninitialized only when the read copies it in whole.
            if (value.Length != length)
                value = NinoUnsafe.NewArray<T>(length, NinoElements.CopiedWhole<T, TFormatter>());
            NinoElements.Read<T, TFormatter>(value.Span, ref reader);
        }
    }

    /// <summary>
    /// <c>ReadOnlyMemory&lt;T&gt;</c>: as <see cref="MemoryFormatter{T,TFormatter}"/>, always read into a new array
    /// (its memory is not the reader's to overwrite).
    /// </summary>
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
            if (writer.TryWriteNullLength(value))
                return;

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

            value = Read(count, ref reader);
        }

        /// <summary>Reads a grouping of <paramref name="count"/> elements: the key, then the elements.</summary>
        internal static NinoGrouping<TKey, TElement> Read(int count, ref Reader reader)
        {
            TKey key = default;
            default(TKeyFormatter).Deserialize(ref key, ref reader);
            var elements = NinoUnsafe.NewArray<TElement>(count,
                NinoElements.CopiedWhole<TElement, TElementFormatter>());
            NinoElements.Read<TElement, TElementFormatter>(elements, ref reader);
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

        public void Deserialize(ref ILookup<TKey, TElement> value, ref Reader reader)
        {
            int count = reader.ReadLength();
            if (count < 0)
            {
                value = null;
                return;
            }

            var lookup = new NinoLookup<TKey, TElement>(count);
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadLength();
                if (length < 0)
                    NinoThrow.InvalidLength(length);
                lookup.Add(GroupingFormatter<TKey, TElement, TKeyFormatter, TElementFormatter>.Read(length, ref reader));
            }

            value = lookup;
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
            if (writer.TryWriteNullLength(value))
                return;

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

    /// <summary>The lookup <see cref="LookupFormatter{TKey,TElement,TKeyFormatter,TElementFormatter}"/> reads.</summary>
    internal sealed class NinoLookup<TKey, TElement> : ILookup<TKey, TElement>
    {
        private readonly NinoGrouping<TKey, TElement>[] _groupings;
        private readonly Dictionary<TKey, NinoGrouping<TKey, TElement>> _byKey;
        private NinoGrouping<TKey, TElement> _nullKey; // a Dictionary takes no null key; a lookup does
        private int _count;

        public NinoLookup(int capacity)
        {
            _groupings = new NinoGrouping<TKey, TElement>[capacity];
            _byKey = new Dictionary<TKey, NinoGrouping<TKey, TElement>>(capacity);
        }

        public void Add(NinoGrouping<TKey, TElement> grouping)
        {
            bool added;
            if (grouping.Key == null)
            {
                added = _nullKey == null;
                _nullKey = grouping;
            }
            else
            {
                added = NinoCollections.TryAdd(_byKey, grouping.Key, grouping);
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

    internal static partial class NinoCollections
    {
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
        public static T[] ReadArray<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            return array;
        }
    }
}
