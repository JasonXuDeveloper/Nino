using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using static Nino.UnitTests.Rt;

#nullable disable
namespace Nino.UnitTests;

[NinoType(format: NinoFormat.VersionTolerant)]
public class BclTypeHolder
{
    public BigInteger Big;
    public Uri Link;
    public Version Version;
    public StringBuilder Text;
    public BitArray Bits;
    public CultureInfo Culture;
    public TimeZoneInfo Zone;
    public Lazy<string> Lazy;
    public Memory<int> Memory;
    public ReadOnlyMemory<string> Names;
    public ReadOnlySequence<byte> Sequence;
    public ReadOnlyCollection<int> ReadOnly;
    public ReadOnlyObservableCollection<string> Observed;
    public ILookup<string, int> Lookup;
    public IGrouping<int, string> Group;
    public BlockingCollection<int> Blocking;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct BclScalarRegion
{
    public BitArray Bits;
    public Memory<string> Names;
    public string[] Labels;
    public List<string> Tags;
    public BigInteger Big;
    public Version Version;
    public Uri Link;
    public StringBuilder Text;
    public CultureInfo Culture;
    public TimeZoneInfo Zone;
}

/// <summary>The BCL types with formatters of their own: round trips, null and the bytes they share with other types.</summary>
[TestClass]
public class BclTypeTests
{
    [TestMethod]
    public void BclScalarRegionAcceptsOldDataAndRejectsTruncation()
    {
        var value = new BclScalarRegion
        {
            Bits = new BitArray(new[] { true, false, true }),
            Names = new[] { "alpha", null },
            Labels = new[] { "label", null },
            Tags = new List<string> { "tag", null },
            Big = new BigInteger(128),
            Version = new Version(1, 2, 3),
            Link = new Uri("../relative", UriKind.Relative),
            Text = new StringBuilder("héllo"),
            Culture = CultureInfo.InvariantCulture,
            Zone = TimeZoneInfo.Utc,
        };
        var bytes = NinoSerializer.Serialize(value);
        var read = NinoDeserializer.Deserialize<BclScalarRegion>(bytes);
        CollectionAssert.AreEqual(value.Bits, read.Bits);
        CollectionAssert.AreEqual(value.Names.ToArray(), read.Names.ToArray());
        CollectionAssert.AreEqual(value.Labels, read.Labels);
        CollectionAssert.AreEqual(value.Tags, read.Tags);
        Assert.AreEqual(value.Big, read.Big);
        Assert.AreEqual(value.Version, read.Version);
        Assert.AreEqual(value.Link, read.Link);
        Assert.AreEqual(value.Text.ToString(), read.Text.ToString());
        Assert.AreEqual(value.Culture, read.Culture);
        Assert.AreEqual(value.Zone, read.Zone);

        var builder = read.Text;
        NinoDeserializer.Deserialize(new byte[sizeof(int)], ref read);
        CollectionAssert.AreEqual(value.Bits, read.Bits);
        Assert.IsTrue(value.Names.Span.SequenceEqual(read.Names.Span));
        CollectionAssert.AreEqual(value.Labels, read.Labels);
        CollectionAssert.AreEqual(value.Tags, read.Tags);
        Assert.AreEqual(value.Big, read.Big);
        Assert.AreEqual(value.Version, read.Version);
        Assert.AreEqual(value.Link, read.Link);
        Assert.AreSame(builder, read.Text);
        Assert.AreEqual(value.Culture, read.Culture);
        Assert.AreEqual(value.Zone, read.Zone);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<BclScalarRegion>(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [TestMethod]
    public void BigIntegers()
    {
        foreach (var value in new[]
                 {
                     BigInteger.Zero, BigInteger.One, BigInteger.MinusOne, new BigInteger(long.MaxValue) * 1000 + 7,
                     -BigInteger.Pow(3, 200), BigInteger.Pow(2, 64), new BigInteger(128), new BigInteger(-129),
                     new BigInteger(32768), new BigInteger(-32769), new BigInteger(int.MinValue), new BigInteger(int.MaxValue),
                 })
        {
            var bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(BitConverter.GetBytes(value.ToByteArray().Length).Concat(value.ToByteArray())
                .ToArray(), bytes);
            Assert.AreEqual(value, NinoDeserializer.Deserialize<BigInteger>(bytes));
        }

        Assert.AreEqual(BigInteger.Zero, NinoDeserializer.Deserialize<BigInteger>(new byte[4]));
    }

    [TestMethod]
    public void UrisVersionsCultures()
    {
        foreach (var uri in new[] { new Uri("https://example.com/a?b=c#d"), new Uri("../relative/path", UriKind.Relative), null })
        {
            var back = RoundTrip(uri);
            Assert.AreEqual(uri?.OriginalString, back?.OriginalString);
            Assert.AreEqual(uri?.IsAbsoluteUri, back?.IsAbsoluteUri);
        }

        CollectionAssert.AreEqual(NinoSerializer.Serialize("https://example.com/"),
            NinoSerializer.Serialize(new Uri("https://example.com/")));

        foreach (var version in new[] { new Version(1, 2), new Version(1, 2, 3), new Version(1, 2, 3, 4), new Version(), null })
        {
            var back = RoundTrip(version);
            Assert.AreEqual(version, back);
            Assert.AreEqual(version?.ToString(), back?.ToString());
            var reused = version;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(version), ref reused);
            Assert.AreSame(version, reused);
        }

        Assert.AreEqual(4, NinoSerializer.Serialize((Version)null).Length);

        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US"), null })
            Assert.AreEqual(culture?.Name, RoundTrip(culture)?.Name);
    }

    [TestMethod]
    public void StringBuilders()
    {
        var text = new StringBuilder("héllo, ");
        for (int i = 0; i < 2000; i++)
            text.Append(i); // several chunks
        var bytes = NinoSerializer.Serialize(text);
        // The string encoding with UTF-16 chars: read as a string too.
        Assert.AreEqual(2ul * (ulong)text.Length, StringEncodingTests.Header(bytes, 0, out _));
        Assert.AreEqual(text.ToString(), NinoDeserializer.Deserialize<string>(bytes));
        Assert.AreEqual(text.ToString(), NinoDeserializer.Deserialize<StringBuilder>(bytes).ToString());

        // Headers of one, two and three bytes (written and read inline up to 8,191 chars), with their sizes.
        foreach (int length in new[] { 1, 63, 64, 32767, 32768, 100_000 })
        {
            var builder = new StringBuilder();
            for (int i = 0; i < length; i++)
                builder.Append((char)('a' + i % 26));
            bytes = NinoSerializer.Serialize(builder);
            Assert.AreEqual(2ul * (ulong)length, StringEncodingTests.Header(bytes, 0, out int headerSize));
            Assert.AreEqual(length < 64 ? 1 : length < 32768 ? 3 : 5, headerSize);
            Assert.AreEqual(headerSize + 2 * length, bytes.Length);
            Assert.AreEqual(builder.ToString(), NinoDeserializer.Deserialize<string>(bytes));
            Assert.AreEqual(builder.ToString(), NinoDeserializer.Deserialize<StringBuilder>(bytes).ToString());
        }

        // An existing builder is refilled.
        var existing = new StringBuilder("old text that is longer");
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new StringBuilder("new")), ref existing);
        Assert.AreEqual("new", existing.ToString());

        var same = existing;
        NinoDeserializer.Deserialize(new byte[] { 3, 0xE9 }, ref existing);
        Assert.AreSame(same, existing);
        Assert.AreEqual("é", existing.ToString());

        const string shortLatin1 = "é\0ñöàßæÿé\0ñöàßæÿ";
        var shortBytes = NinoSerializer.Serialize(shortLatin1);
        Assert.AreEqual((ulong)(shortLatin1.Length * 2 + 1), StringEncodingTests.Header(shortBytes, 0, out _));
        NinoDeserializer.Deserialize(shortBytes, ref existing);
        Assert.AreSame(same, existing);
        Assert.AreEqual(shortLatin1, existing.ToString());

        foreach (int count in new[] { 17, 256, 257 })
        {
            string latin1 = new('é', count);
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(latin1), ref existing);
            Assert.AreSame(same, existing);
            Assert.AreEqual(latin1, existing.ToString());
        }

        Assert.IsNull(RoundTrip((StringBuilder)null));
        Assert.AreEqual("", RoundTrip(new StringBuilder()).ToString());
    }

    [TestMethod]
    public void BitArrays()
    {
        foreach (int count in new[] { 0, 1, 7, 8, 9, 64, 1001 })
        {
            var bits = new BitArray(count);
            for (int i = 0; i < count; i++)
                bits[i] = i % 3 == 0;
            var bytes = NinoSerializer.Serialize(bits);
            Assert.AreEqual(4 + (count + 7) / 8, bytes.Length);
            var back = NinoDeserializer.Deserialize<BitArray>(bytes);
            Assert.AreEqual(count, back.Length);
            for (int i = 0; i < count; i++)
                Assert.AreEqual(bits[i], back[i]);
        }

        Assert.IsNull(RoundTrip((BitArray)null));
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(65)]
    [DataRow(128)]
    [DataRow(129)]
    [DataRow(24 * 1024)]
    public void BitArrayWritesIntoAnExistingBufferAtItsCurrentPosition(int length)
    {
        var payload = new byte[length];
        new Random(123).NextBytes(payload);
        var bits = new BitArray(payload) { Length = payload.Length * 8 - 3 };
        payload[payload.Length - 1] &= 31;

        using var buffer = new NinoArrayBufferWriter(8);
        buffer.GetSpan(1)[0] = 0xA5;
        buffer.Advance(1);
        NinoSerializer.Serialize(bits, buffer);
        buffer.GetSpan(1)[0] = 0x5A;
        buffer.Advance(1);

        var bytes = buffer.WrittenSpan;
        Assert.AreEqual(payload.Length + 6, bytes.Length);
        Assert.AreEqual((byte)0xA5, bytes[0]);
        Assert.AreEqual(bits.Length, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(1)));
        Assert.IsTrue(bytes.Slice(5, payload.Length).SequenceEqual(payload));
        Assert.AreEqual((byte)0x5A, bytes[bytes.Length - 1]);
    }

#if NET10_0_OR_GREATER
    [TestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(8)]
    [DataRow(9)]
    [DataRow(517)]
    [DataRow(1021)]
    public void BitArrayPaddingStaysOffTheWireAndOutOfTheDecodedArray(int count)
    {
        var bits = new BitArray(count);
        var storage = System.Runtime.InteropServices.CollectionsMarshal.AsBytes(bits);
        storage[^1] = 0xFF;

        var data = NinoSerializer.Serialize(bits);
        var expected = new byte[4 + storage.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(expected, count);
        int lastBits = (count - 1) % 8 + 1;
        expected[^1] = (byte)((1 << lastBits) - 1);
        CollectionAssert.AreEqual(expected, data);
        Assert.AreEqual((byte)0xFF, storage[^1]);

        data[^1] = 0xFF;
        var decoded = NinoDeserializer.Deserialize<BitArray>(data);
        Assert.AreEqual(count, decoded.Length);
        Assert.IsTrue(decoded[count - 1]);
        Assert.AreEqual(expected[^1], System.Runtime.InteropServices.CollectionsMarshal.AsBytes(decoded)[^1]);
    }

#if !NINO_CORE_NETSTANDARD
    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    [DataRow(9)]
    public void MatchingBitArrayIsRefilledAndInvalidatesEnumerators(int count)
    {
        var target = new BitArray(count, true);
        var existing = target;
        var enumerator = target.GetEnumerator();
        Assert.AreEqual(count != 0, enumerator.MoveNext());

        byte[] bytes = count switch
        {
            0 => new byte[] { 0, 0, 0, 0 },
            7 => new byte[] { 7, 0, 0, 0, 0x82 },
            _ => new byte[] { 9, 0, 0, 0, 0x02, 0xFF },
        };
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(existing, target);
        Assert.AreEqual(count, target.Length);
        if (count != 0)
            bytes[^1] &= (byte)((1 << (count & 7)) - 1);
        Assert.IsTrue(System.Runtime.InteropServices.CollectionsMarshal.AsBytes(target).SequenceEqual(bytes.AsSpan(4)));
        Assert.ThrowsExactly<InvalidOperationException>(() => enumerator.MoveNext());
    }

    [TestMethod]
    [DataRow(517)]
    [DataRow(1021)]
    public void BitArrayReuseAcceptsInputOverlappingItsStorage(int count)
    {
        var value = new BitArray(count);
        for (int i = 0; i < count; i++)
            value[i] = i % 3 == 1;

        byte[] wire = NinoSerializer.Serialize(value);
        var target = new BitArray(wire.Length * 8);
        var input = System.Runtime.InteropServices.CollectionsMarshal.AsBytes(target);
        target.Length = count;
        Assert.IsTrue(input.Overlaps(System.Runtime.InteropServices.CollectionsMarshal.AsBytes(target)));
        wire.AsSpan().CopyTo(input);
        var existing = target;

        NinoDeserializer.Deserialize(input, ref target);

        Assert.AreSame(existing, target);
        CollectionAssert.AreEqual(wire, NinoSerializer.Serialize(target));
    }
#endif
#endif

    [TestMethod]
    public void TimeZones()
    {
        var custom = TimeZoneInfo.CreateCustomTimeZone("Nino/Test", TimeSpan.FromHours(5.5), "Test", "Test Standard");
        var back = RoundTrip(custom);
        Assert.AreEqual(custom.Id, back.Id);
        Assert.AreEqual(custom.BaseUtcOffset, back.BaseUtcOffset);
        Assert.AreEqual(TimeZoneInfo.Utc.BaseUtcOffset, RoundTrip(TimeZoneInfo.Utc).BaseUtcOffset);
        Assert.IsNull(RoundTrip((TimeZoneInfo)null));

        // A system zone is written as its id alone and read back as the reader's zone of that id (the runtime cannot
        // read back the serialized form of many IANA zones).
        var system = TimeZoneInfo.FindSystemTimeZoneById(System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "Eastern Standard Time" : "America/New_York");
        CollectionAssert.AreEqual(NinoSerializer.Serialize(system.Id).Concat(NinoSerializer.Serialize((string)null)).ToArray(),
            NinoSerializer.Serialize(system));
        Assert.AreSame(system, RoundTrip(system));
        Assert.AreSame(TimeZoneInfo.Utc, RoundTrip(TimeZoneInfo.Utc));
    }

    [TestMethod]
    public void Lazies()
    {
        int created = 0;
        var lazy = new Lazy<List<int>>(() =>
        {
            created++;
            return new List<int> { 1, 2, 3 };
        });
        var back = RoundTrip(lazy);
        Assert.AreEqual(1, created);
        Assert.IsTrue(back.IsValueCreated);
        CollectionAssert.AreEqual(lazy.Value, back.Value);
        Assert.IsNull(RoundTrip((Lazy<int>)null));
        Assert.AreEqual(5, RoundTrip(new Lazy<int>(() => 5)).Value);
    }

    [TestMethod]
    public void MemoriesAndSequencesAreArrays()
    {
        var ints = Enumerable.Range(0, 100).ToArray();
        var slice = new Memory<int>(ints, 10, 50);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(ints.Skip(10).Take(50).ToArray()), NinoSerializer.Serialize(slice));
        CollectionAssert.AreEqual(slice.ToArray(), RoundTrip(slice).ToArray());

        // Read into the current memory when the length matches, as an array is.
        var target = new int[50];
        Memory<int> current = target;
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(slice), ref current);
        Assert.IsTrue(current.Span == target.AsSpan());
        CollectionAssert.AreEqual(slice.ToArray(), target);

        ReadOnlyMemory<string> names = new[] { "a", null, "c" };
        CollectionAssert.AreEqual(names.ToArray(), RoundTrip(names).ToArray());
        Assert.AreEqual(0, RoundTrip(Memory<int>.Empty).Length);

        // Several segments, written as one array.
        var first = new Segment<byte>(new byte[] { 1, 2, 3 });
        var last = first.Append(new byte[] { 4, 5 });
        var sequence = new ReadOnlySequence<byte>(first, 0, last, 2);
        Assert.IsFalse(sequence.IsSingleSegment);
        var bytes = NinoSerializer.Serialize(sequence);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new byte[] { 1, 2, 3, 4, 5 }), bytes);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, NinoDeserializer.Deserialize<ReadOnlySequence<byte>>(bytes).ToArray());

        last = first.Append(Array.Empty<byte>()).Append(new byte[] { 4, 5 }).Append(Array.Empty<byte>());
        var withEmptySegments = new ReadOnlySequence<byte>(first, 0, last, 0);
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(withEmptySegments));

        var wireHead = new Segment<byte>(bytes[..2]);
        var wireEnd = wireHead.Append(Array.Empty<byte>()).Append(bytes[2..]).Append(Array.Empty<byte>());
        var segmentedWire = new ReadOnlySequence<byte>(wireHead, 0, wireEnd, 0);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, NinoDeserializer.Deserialize<byte[]>(segmentedWire));
    }

    [TestMethod]
    public void DeserializeFromSegments()
    {
        var value = new SimpleClass { Id = 7, Name = "segmented", CreateTime = new DateTime(2026, 11, 8) };
        byte[] bytes = NinoSerializer.Serialize(value);

        // One segment is read in place.
        var single = new ReadOnlySequence<byte>(bytes);
        Assert.AreEqual("segmented", NinoDeserializer.Deserialize<SimpleClass>(single).Name);

        // Every split into three segments reads the same value, new or into an existing instance.
        SimpleClass reused = null;
        for (int a = 1; a < bytes.Length - 1; a++)
        for (int b = a + 1; b < bytes.Length; b++)
        {
            var first = new Segment<byte>(bytes[..a]);
            var last = first.Append(bytes[a..b]).Append(bytes[b..]);
            var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            Assert.IsFalse(sequence.IsSingleSegment);

            SimpleClass read = NinoDeserializer.Deserialize<SimpleClass>(sequence);
            Assert.AreEqual(value.Id, read.Id);
            Assert.AreEqual(value.Name, read.Name);
            Assert.AreEqual(value.CreateTime, read.CreateTime);

            SimpleClass before = reused;
            NinoDeserializer.Deserialize(sequence, ref reused);
            Assert.AreEqual(value.Name, reused.Name);
            if (before != null)
                Assert.AreSame(before, reused);
        }

        // Data over NinoSequences.StackBytes is copied into a pooled array instead of the stack.
        var large = new SimpleClass { Id = 8, Name = new string('n', 2000), CreateTime = value.CreateTime };
        byte[] largeBytes = NinoSerializer.Serialize(large);
        var start = new Segment<byte>(largeBytes[..1000]);
        var end = start.Append(largeBytes[1000..]);
        var largeSequence = new ReadOnlySequence<byte>(start, 0, end, end.Memory.Length);
        Assert.AreEqual(large.Name, NinoDeserializer.Deserialize<SimpleClass>(largeSequence).Name);
        NinoDeserializer.Deserialize(largeSequence, ref reused);
        Assert.AreEqual(large.Name, reused.Name);

        // Truncated data throws as it does from a span.
        var head = new Segment<byte>(bytes[..2]);
        var tail = head.Append(bytes[2..^1]);
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<SimpleClass>(new ReadOnlySequence<byte>(head, 0, tail, tail.Memory.Length)));
    }

    [TestMethod]
    public void SegmentedInputChecksItsActualLength()
    {
        foreach (int count in new[] { 32, 512, 1020, 1024 })
        {
            var value = Enumerable.Range(0, count).Select(i => (byte)i).ToArray();
            byte[] bytes = NinoSerializer.Serialize(value);
            var first = new Segment<byte>(bytes[..4]);
            var empty = first.Append(Array.Empty<byte>());
            var end = empty.Append(bytes[4..]);
            CollectionAssert.AreEqual(value,
                NinoDeserializer.Deserialize<byte[]>(new ReadOnlySequence<byte>(first, 0, end, end.Memory.Length)));

            foreach (int offset in new[] { -1, 1 })
            {
                var bad = empty.Append(bytes[4..], offset);
                var sequence = new ReadOnlySequence<byte>(first, 0, bad, bad.Memory.Length);
                Assert.ThrowsExactly<ArgumentException>(() => NinoDeserializer.Deserialize<byte[]>(sequence));
            }
        }
    }

    [TestMethod]
    public void TwoSegmentByteArrayReadsReuseWithoutOverwritingAliasedInput()
    {
        byte[] expected = { 10, 11, 4, 5, 6, 7 };
        var first = new Segment<byte>(new byte[] { 6, 0, 0, 0, 10, 11 });
        var last = first.Append(new byte[] { 4, 5, 6, 7 });
        var data = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        CollectionAssert.AreEqual(expected, NinoDeserializer.Deserialize<byte[]>(data));

        byte[] existing = new byte[expected.Length];
        byte[] value = existing;
        NinoDeserializer.Deserialize(data, ref value);
        Assert.AreSame(existing, value);
        CollectionAssert.AreEqual(expected, value);

        first = new Segment<byte>(new byte[] { 6, 0, 0, 0, 10, 11 });
        existing = new byte[] { 4, 5, 6, 7, 8, 9 };
        last = first.Append(existing);
        data = new ReadOnlySequence<byte>(first, 0, last, 4);
        value = existing;
        NinoDeserializer.Deserialize(data, ref value);
        Assert.AreSame(existing, value);
        CollectionAssert.AreEqual(expected, value);

        first = new Segment<byte>(new byte[] { 6, 0, 0, 0, 10, 11 });
        last = first.Append(new byte[] { 4, 5, 6, 7 }, offset: 1);
        data = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        Assert.ThrowsExactly<ArgumentException>(() => NinoDeserializer.Deserialize(data, ref value));
        CollectionAssert.AreEqual(expected, value);
    }

    [TestMethod]
    public void TwoSegmentByteArrayReadsAcceptSplitHeaders()
    {
        byte[] expected = { 10, 11, 4, 5, 6, 7 };
        byte[] wire = NinoSerializer.Serialize(expected);
        for (int split = 0; split < sizeof(int); split++)
        {
            var first = new Segment<byte>(wire[..split]);
            var last = first.Append(wire[split..]);
            var data = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
            CollectionAssert.AreEqual(expected, NinoDeserializer.Deserialize<byte[]>(data));

            var truncated = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length - 1);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<byte[]>(truncated));
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void TwoSegmentByteArrayReplacementPreservesRecyclerInput()
    {
        byte[] expected = { 10, 11, 4, 5, 6, 7 };
        byte[] old = { 4, 5, 6, 7 };
        var first = new Segment<byte>(new byte[] { 6, 0, 0, 0, 10, 11 });
        var last = first.Append(old);
        var data = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        byte[] value = old;
        NinoDeserializer.Deserialize(data, ref value);
        Assert.AreNotSame(old, value);
        CollectionAssert.AreEqual(expected, value);

        int recycled = 0;
        NinoSettings.SetRecycler<byte[]>(array =>
        {
            Assert.AreSame(old, array);
            recycled++;
            Array.Clear(array, 0, array.Length);
        });
        try
        {
            value = old;
            NinoDeserializer.Deserialize(data, ref value);
            Assert.AreEqual(1, recycled);
            CollectionAssert.AreEqual(expected, value);
        }
        finally
        {
            NinoSettings.SetRecycler<byte[]>(null);
        }
    }

    private sealed class Segment<T> : ReadOnlySequenceSegment<T>
    {
        public Segment(T[] items) => Memory = items;

        public Segment<T> Append(T[] items, int offset = 0)
        {
            var next = new Segment<T>(items) { RunningIndex = RunningIndex + Memory.Length + offset };
            Next = next;
            return next;
        }
    }

    [TestMethod]
    public void ReadOnlyCollectionsAreLists()
    {
        var list = new List<int> { 1, 2, 3 };
        var readOnly = new ReadOnlyCollection<int>(list);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(list), NinoSerializer.Serialize(readOnly));
        var back = RoundTrip(readOnly);
        Assert.IsInstanceOfType(back, typeof(ReadOnlyCollection<int>));
        CollectionAssert.AreEqual(list, back);

        var observed = new ReadOnlyObservableCollection<string>(new ObservableCollection<string> { "x", "y" });
        CollectionAssert.AreEqual(observed, RoundTrip(observed));
        // A collection of raw elements that can copy them out is written as one block, the bytes of enumerating it.
        foreach (int n in new[] { 1, 7, 300 })
        {
            var values = Enumerable.Range(0, n).ToList();
            var enumerated = NinoSerializer.Serialize<IEnumerable<int>>(values.Select(i => i));
            CollectionAssert.AreEqual(enumerated, NinoSerializer.Serialize<IEnumerable<int>>(new ReadOnlyCollection<int>(values)));
            CollectionAssert.AreEqual(enumerated, NinoSerializer.Serialize(new ReadOnlyCollection<int>(values)));
            CollectionAssert.AreEqual(enumerated, NinoSerializer.Serialize<ICollection<int>>(new Collection<int>(values)));
        }

        // Elements written one by one are copied out from NinoCollections.CopiedFrom on: the same bytes either way.
        foreach (int n in new[] { 3, 16, 300 })
        {
            var names = Enumerable.Range(0, n).Select(i => i % 5 == 0 ? null : "name" + i).ToList();
            var enumerated = NinoSerializer.Serialize<IEnumerable<string>>(names.Select(x => x));
            CollectionAssert.AreEqual(enumerated, NinoSerializer.Serialize(new ReadOnlyCollection<string>(names)));
            CollectionAssert.AreEqual(enumerated, NinoSerializer.Serialize<IList<string>>(new Collection<string>(names)));
            CollectionAssert.AreEqual(names, RoundTrip(new ReadOnlyCollection<string>(names)).ToList());
        }

        Assert.IsNull(RoundTrip((ReadOnlyCollection<int>)null));
        Assert.IsNull(RoundTrip((ReadOnlyObservableCollection<int>)null));
    }

    [TestMethod]
    public void LookupsAndGroupings()
    {
        var words = new[] { "apple", "avocado", "banana", "blueberry", "cherry", null };
        var lookup = words.ToLookup(w => w?.Substring(0, 1), w => w?.Length ?? 0);
        var back = RoundTrip(lookup);
        Assert.AreEqual(lookup.Count, back.Count);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(lookup), NinoSerializer.Serialize(back));
        foreach (var group in lookup)
            CollectionAssert.AreEqual(group.ToArray(), back[group.Key].ToArray());
        CollectionAssert.AreEqual(lookup.Select(g => g.Key).ToArray(), back.Select(g => g.Key).ToArray());

        var grouping = Enumerable.Range(0, 10).GroupBy(i => i % 3).First(g => g.Key == 1);
        var groupBack = RoundTrip(grouping);
        Assert.AreEqual(1, groupBack.Key);
        CollectionAssert.AreEqual(grouping.ToArray(), groupBack.ToArray());

        Assert.IsFalse(back.Contains("z"));
        Assert.AreEqual(0, back["z"].Count());
        CollectionAssert.AreEqual(lookup[null].ToArray(), back[null].ToArray());

        // The same key in two groupings is the data's fault, as for a dictionary.
        var groupings = new[] { new[] { 1 }.GroupBy(i => 0).Single(), new[] { 2 }.GroupBy(i => 0).Single() };
        var twice = NinoSerializer.Serialize(groupings.Length).Concat(groupings.SelectMany(g =>
            NinoSerializer.Serialize(g).AsEnumerable())).ToArray();
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<ILookup<int, int>>(twice));

        // A grouping that only enumerates (the count patched after the elements), .NET's grouping (a list, copied
        // out) and the grouping Nino reads (its array) write the same bytes, raw and reference elements alike.
        foreach (var g in Enumerable.Range(0, 300).GroupBy(i => i % 3))
        {
            var bytes = NinoSerializer.Serialize<IGrouping<int, int>>(g);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize<IGrouping<int, int>>(new EnumeratedGrouping<int, int>(g)));
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<IGrouping<int, int>>(bytes)));
        }

        var many = Enumerable.Range(0, 40).Select(i => "w" + i).GroupBy(w => w.Length).First();
        CollectionAssert.AreEqual(NinoSerializer.Serialize<IGrouping<int, string>>(new EnumeratedGrouping<int, string>(many)),
            NinoSerializer.Serialize<IGrouping<int, string>>(many));

        var named = words.Where(w => w != null).GroupBy(w => w.Length > 5).First();
        var namedBytes = NinoSerializer.Serialize<IGrouping<bool, string>>(named);
        CollectionAssert.AreEqual(namedBytes,
            NinoSerializer.Serialize<IGrouping<bool, string>>(new EnumeratedGrouping<bool, string>(named)));
        CollectionAssert.AreEqual(named.ToArray(), RoundTrip<IGrouping<bool, string>>(named).ToArray());

        Assert.IsNull(RoundTrip((ILookup<int, int>)null));
        Assert.IsNull(RoundTrip((IGrouping<int, int>)null));
        Assert.AreEqual(0, RoundTrip(Array.Empty<int>().ToLookup(i => i)).Count);
    }

    /// <summary>A grouping that only enumerates its elements (no count), as a user's own may.</summary>
    private sealed class EnumeratedGrouping<TKey, TElement> : IGrouping<TKey, TElement>
    {
        private readonly IGrouping<TKey, TElement> _inner;

        public EnumeratedGrouping(IGrouping<TKey, TElement> inner) => _inner = inner;

        public TKey Key => _inner.Key;

        public IEnumerator<TElement> GetEnumerator()
        {
            foreach (var element in _inner)
                yield return element;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public void BlockingCollections()
    {
        var blocking = new BlockingCollection<int>();
        for (int i = 0; i < 100; i++)
            blocking.Add(i);
        var back = RoundTrip(blocking);
        CollectionAssert.AreEqual(blocking.ToArray(), back.ToArray());
        back.Add(100);
        Assert.AreEqual(0, back.Take());
        Assert.AreEqual(0, RoundTrip(new BlockingCollection<int>()).Count);
        var single = RoundTrip(new BlockingCollection<string> { "only" });
        Assert.AreEqual("only", single.Take());
        Assert.AreEqual(0, single.Count);
        Assert.IsNull(RoundTrip((BlockingCollection<int>)null));
    }

    [TestMethod]
    public void AsMembers()
    {
        var holder = new BclTypeHolder
        {
            Big = BigInteger.Pow(10, 40),
            Link = new Uri("https://nino.example/"),
            Version = new Version(4, 0, 0),
            Text = new StringBuilder("text"),
            Bits = new BitArray(new[] { true, false, true }),
            Culture = CultureInfo.GetCultureInfo("fr-FR"),
            Zone = TimeZoneInfo.Utc,
            Lazy = new Lazy<string>(() => "lazy"),
            Memory = new[] { 1, 2 },
            Names = new[] { "n" },
            Sequence = new ReadOnlySequence<byte>(new byte[] { 9 }),
            ReadOnly = new ReadOnlyCollection<int>(new[] { 7 }),
            Observed = new ReadOnlyObservableCollection<string>(new ObservableCollection<string> { "o" }),
            Lookup = new[] { 1, 2, 3 }.ToLookup(i => i % 2 == 0 ? "even" : "odd"),
            Group = new[] { "g" }.GroupBy(s => s.Length).Single(),
            Blocking = new BlockingCollection<int> { 5 },
        };
        var back = RoundTrip(holder);
        Assert.AreEqual(holder.Big, back.Big);
        Assert.AreEqual(holder.Link, back.Link);
        Assert.AreEqual(holder.Version, back.Version);
        Assert.AreEqual("text", back.Text.ToString());
        Assert.IsTrue(back.Bits[0] && !back.Bits[1] && back.Bits[2]);
        Assert.AreEqual("fr-FR", back.Culture.Name);
        Assert.AreEqual(TimeZoneInfo.Utc.Id, back.Zone.Id);
        Assert.AreEqual("lazy", back.Lazy.Value);
        CollectionAssert.AreEqual(new[] { 1, 2 }, back.Memory.ToArray());
        CollectionAssert.AreEqual(new[] { "n" }, back.Names.ToArray());
        CollectionAssert.AreEqual(new byte[] { 9 }, back.Sequence.ToArray());
        CollectionAssert.AreEqual(new[] { 7 }, back.ReadOnly);
        CollectionAssert.AreEqual(new[] { "o" }, back.Observed);
        CollectionAssert.AreEqual(new[] { 1, 3 }, back.Lookup["odd"].ToArray());
        Assert.AreEqual(1, back.Group.Key);
        CollectionAssert.AreEqual(new[] { 5 }, back.Blocking.ToArray());

        var empty = RoundTrip(new BclTypeHolder());
        Assert.IsNull(empty.Link);
        Assert.IsNull(empty.Lookup);
        Assert.IsNull(empty.Lazy);
        Assert.AreEqual(0, empty.Memory.Length);
        Assert.IsTrue(empty.Sequence.IsEmpty);
    }

    [TestMethod]
    public void TruncatedDataThrows()
    {
        var bytes = NinoSerializer.Serialize(BigInteger.Pow(2, 100));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<BigInteger>(bytes.AsSpan(0, bytes.Length - 1)));
        var bits = NinoSerializer.Serialize(new BitArray(100));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<BitArray>(bits.AsSpan(0, bits.Length - 1)));
        var text = NinoSerializer.Serialize(new StringBuilder("abc"));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<StringBuilder>(text.AsSpan(0, text.Length - 1)));
    }

    [TestMethod]
    public void ImmutableCollectionsShareTheBytesOfTheirMutableCounterparts()
    {
        var ints = new[] { 5, 3, 9, 1 };
        var set = ImmutableHashSet.CreateRange(ints);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new HashSet<int>(set)), NinoSerializer.Serialize(set));
        CollectionAssert.AreEquivalent(ints, RoundTrip(set).ToArray());

        var sorted = ImmutableSortedSet.CreateRange(new[] { "b", "a", "c" });
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new SortedSet<string>(sorted)), NinoSerializer.Serialize(sorted));
        CollectionAssert.AreEqual(sorted.ToArray(), RoundTrip(sorted).ToArray());

        var queue = ImmutableQueue.CreateRange(ints);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new Queue<int>(ints)), NinoSerializer.Serialize(queue));
        CollectionAssert.AreEqual(ints, RoundTrip(queue).ToArray());

        var stack = ImmutableStack.CreateRange(new[] { "bottom", "middle", "top" });
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new Stack<string>(new[] { "bottom", "middle", "top" })),
            NinoSerializer.Serialize(stack));
        var stackBack = RoundTrip(stack);
        Assert.AreEqual("top", stackBack.Peek());
        CollectionAssert.AreEqual(stack.ToArray(), stackBack.ToArray());

        var dictionary = ImmutableDictionary.CreateRange(ints.Select(i => new KeyValuePair<int, string>(i, i.ToString())));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new Dictionary<int, string>(dictionary)),
            NinoSerializer.Serialize(dictionary));
        CollectionAssert.AreEquivalent(dictionary.ToArray(), RoundTrip(dictionary).ToArray());

        var sortedDictionary = ImmutableSortedDictionary.CreateRange(dictionary);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new SortedDictionary<int, string>(sortedDictionary)),
            NinoSerializer.Serialize(sortedDictionary));
        CollectionAssert.AreEqual(sortedDictionary.ToArray(), RoundTrip(sortedDictionary).ToArray());

        Assert.IsNull(RoundTrip((ImmutableHashSet<int>)null));
        Assert.IsNull(RoundTrip((ImmutableSortedSet<int>)null));
        Assert.IsNull(RoundTrip((ImmutableQueue<int>)null));
        Assert.IsNull(RoundTrip((ImmutableStack<int>)null));
        Assert.IsNull(RoundTrip((ImmutableDictionary<int, int>)null));
        Assert.IsNull(RoundTrip((ImmutableSortedDictionary<int, int>)null));
        Assert.IsTrue(RoundTrip(ImmutableQueue<int>.Empty).IsEmpty);
        Assert.IsTrue(RoundTrip(ImmutableStack<int>.Empty).IsEmpty);
    }

    [TestMethod]
    public void ImmutableDuplicatesAreAClearError()
    {
        var twice = NinoSerializer.Serialize(new List<int> { 1, 1 });
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<ImmutableHashSet<int>>(twice));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<ImmutableSortedSet<int>>(twice));

        foreach (var pairs in new[]
                 {
                     new List<KeyValuePair<int, int>> { new(1, 2), new(1, 2) },
                     new List<KeyValuePair<int, int>> { new(1, 2), new(1, 3) },
                 })
        {
            var bytes = NinoSerializer.Serialize(pairs);
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<ImmutableDictionary<int, int>>(bytes));
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<ImmutableSortedDictionary<int, int>>(bytes));
        }
    }

    /// <summary>A key whose hash is its value divided by four: four keys to a bucket.</summary>
    private readonly record struct Crowded(int Value)
    {
        public override int GetHashCode() => Value / 4;
    }

    [TestMethod]
    public void LinkedListsAndSortedSetsReadAsBuiltBehaveAsBuilt()
    {
        Assert.IsTrue(Nino.Core.Internal.CollectionViews.LinkedList);
        Assert.AreNotEqual(0, SortedSetNodes.Layout);
        var random = new Random(7);

        void Same<T>(T[] items, Func<int, T> other)
        {
            var linked = new LinkedList<T>(items);
            var linkedBack = RoundTrip(linked);
            CollectionAssert.AreEqual(linked.ToArray(), linkedBack.ToArray());
            CollectionAssert.AreEqual(linked.Reverse().ToArray(), Backwards(linkedBack).ToArray());
            if (items.Length != 0)
            {
                Assert.AreSame(linkedBack, linkedBack.Find(items[items.Length / 2])!.List);
                linkedBack.Remove(items[0]);
                linkedBack.AddFirst(other(0));
                linkedBack.AddLast(other(1));
                Assert.AreEqual(items.Length + 1, linkedBack.Count);
                Assert.AreEqual(other(1), linkedBack.Last!.Value);
                Assert.AreEqual(other(0), linkedBack.First!.Value);
            }

            var set = new SortedSet<T>(items);
            var setBack = RoundTrip(set);
            CollectionAssert.AreEqual(set.ToArray(), setBack.ToArray());
            // Random adds and removes, against a set built the usual way: the tree stays a red-black tree.
            var pool = items.Concat(Enumerable.Range(0, items.Length + 8).Select(other)).ToArray();
            for (int i = 0; i < 3 * pool.Length; i++)
            {
                var item = pool[random.Next(pool.Length)];
                if (random.Next(2) == 0)
                    Assert.AreEqual(set.Add(item), setBack.Add(item));
                else
                    Assert.AreEqual(set.Remove(item), setBack.Remove(item));
            }

            CollectionAssert.AreEqual(set.ToArray(), setBack.ToArray());
            CollectionAssert.AreEqual(set.Reverse().ToArray(), setBack.Reverse().ToArray());
            Assert.AreEqual(set.Min, setBack.Min);
            Assert.AreEqual(set.Max, setBack.Max);
            // Copied out of the tree (built by Nino, then changed through the public API), as the array is written;
            // a view of a set (its root is the whole set's) with its own CopyTo.
            CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToArray()), NinoSerializer.Serialize(setBack));
            if (set.Count > 2)
            {
                var view = setBack.GetViewBetween(setBack.ElementAt(1), setBack.ElementAt(setBack.Count - 2));
                CollectionAssert.AreEqual(NinoSerializer.Serialize(view.ToArray()), NinoSerializer.Serialize(view));
            }

            var dictionary = new SortedDictionary<T, int>(items.Select((k, i) => (k, i)).ToDictionary(p => p.k, p => p.i));
            var dictionaryBack = RoundTrip(dictionary);
            CollectionAssert.AreEqual(dictionary.ToArray(), dictionaryBack.ToArray());
            for (int i = 0; i < items.Length; i += 3)
                dictionaryBack.Remove(items[i]);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(dictionaryBack.ToArray()),
                NinoSerializer.Serialize(dictionaryBack));
            foreach (var item in items.Where((_, i) => i % 3 != 0))
                Assert.IsTrue(dictionaryBack.Remove(item));
            Assert.AreEqual(0, dictionaryBack.Count);
        }

        foreach (int n in new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 64, 100, 1000 })
        {
            Same(Enumerable.Range(-n / 2, n).Select(i => i * 7919).ToArray(), i => int.MaxValue - i);
            Same(Enumerable.Range(0, n).Select(i => "key" + i.ToString("D5", CultureInfo.InvariantCulture)).ToArray(),
                i => "other" + i);
            Same(Enumerable.Range(0, n).Select(i => new Guid(i, 0, 0, new byte[8])).ToArray(),
                i => new Guid(-1 - i, 1, 1, new byte[8]));
        }

        // Out of order on the wire (written under another comparer): built the usual way.
        var reversed = new SortedSet<int>(Enumerable.Range(0, 50), Comparer<int>.Create((a, b) => b.CompareTo(a)));
        var bytes = NinoSerializer.Serialize(reversed);
        CollectionAssert.AreEqual(Enumerable.Range(0, 50).ToArray(), NinoDeserializer.Deserialize<SortedSet<int>>(bytes).ToArray());
        var withNull = new SortedSet<string>(new[] { null, "a", "b" });
        CollectionAssert.AreEqual(withNull.ToArray(), RoundTrip(withNull).ToArray());

        static IEnumerable<T> Backwards<T>(LinkedList<T> list)
        {
            for (var node = list.Last; node != null; node = node.Previous)
                yield return node.Value;
        }
    }

    [TestMethod]
    public void ImmutableCollectionsReadAsTreesBehaveAsBuilt()
    {
        Assert.IsTrue(Nino.Immutable.HashTrees.Usable);
        Assert.IsTrue(Nino.Immutable.SortedTrees.Usable);

        foreach (int n in new[] { 1, 2, 3, 7, 64, 1000 })
        {
            SameImmutable(Enumerable.Range(-n / 2, n).Select(i => i * 7919).ToArray(), i => int.MaxValue - i);
            SameImmutable(Enumerable.Range(0, n).Select(i => "key" + i).ToArray(), i => "other" + i);
            // Equal hashes: built the usual way.
            SameImmutable(Enumerable.Range(0, n).Select(i => new Crowded(i)).ToArray(), i => new Crowded(-1 - i));
        }

        // Keys of one hash code (longs whose halves cancel out): up to NinoSettings.MaxHashCollisions here, and 1000
        // rejected; read in full under a raised limit by ImmutableCollidingKeysUnderARaisedLimit.
        foreach (int n in new[] { 1, 2, 3, 7, 64, NinoSettings.DefaultMaxHashCollisions })
            SameImmutable(CancellingLongs(n), i => -1L - i);
        var colliding = CancellingLongs(1000);
        Assert.ThrowsExactly<NinoDataException>(() =>
            RoundTrip(colliding.Select((k, i) => new KeyValuePair<long, int>(k, i)).ToImmutableDictionary()));
        Assert.ThrowsExactly<NinoDataException>(() => RoundTrip(colliding.ToImmutableHashSet()));

        static void SameSorted<TKey>(TKey[] keys, TKey absent)
        {
            var dictionary = keys.Select((k, i) => new KeyValuePair<TKey, int>(k, i)).ToImmutableSortedDictionary();
            CollectionAssert.AreEqual(NinoSerializer.Serialize(dictionary.ToArray()), NinoSerializer.Serialize(dictionary));
            var back = RoundTrip(dictionary);
            CollectionAssert.AreEqual(dictionary.ToArray(), back.ToArray());
            Assert.IsFalse(back.ContainsKey(absent));
            var changed = back.Add(absent, -1).Remove(keys[0]);
            Assert.AreEqual(back.Count, changed.Count);
            Assert.AreEqual(-1, changed[absent]);
            Assert.IsTrue(keys.Aggregate(back, (d, k) => d.Remove(k)).IsEmpty);

            var set = keys.ToImmutableSortedSet();
            CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToArray()), NinoSerializer.Serialize(set));
            var setBack = RoundTrip(set);
            CollectionAssert.AreEqual(set.ToArray(), setBack.ToArray());
            for (int i = 0; i < set.Count; i++)
                Assert.AreEqual(set[i], setBack[i]);
            Assert.AreEqual(set.Count - 1, setBack.Remove(keys[0]).Count);
            Assert.AreEqual(set.IndexOf(keys[keys.Length - 1]), setBack.IndexOf(keys[keys.Length - 1]));
            Assert.IsTrue(setBack.Add(absent).Contains(absent));
            Assert.IsTrue(keys.Aggregate(setBack, (x, k) => x.Remove(k)).IsEmpty);
        }

        foreach (int n in new[] { 1, 2, 3, 7, 64, 1000 })
        {
            SameSorted(Enumerable.Range(-n / 2, n).Select(i => i * 7919).ToArray(), int.MaxValue);
            SameSorted(Enumerable.Range(0, n).Select(i => "key" + i).ToArray(), "other");
            SameSorted(Enumerable.Range(0, n).Select(i => new Guid(i, 1, 2, new byte[8])).ToArray(), Guid.Empty);
        }

        // Out of order (another comparer wrote them): sorted on the way in.
        var unsorted = NinoSerializer.Serialize(new List<KeyValuePair<int, int>> { new(3, 30), new(1, 10), new(2, 20) });
        CollectionAssert.AreEqual(new[] { 1, 2, 3 },
            NinoDeserializer.Deserialize<ImmutableSortedDictionary<int, int>>(unsorted).Keys.ToArray());
        var unsortedSet = NinoSerializer.Serialize(new List<int> { 3, 1, 2 });
        CollectionAssert.AreEqual(new[] { 1, 2, 3 },
            NinoDeserializer.Deserialize<ImmutableSortedSet<int>>(unsortedSet).ToArray());

        var withNull = ImmutableHashSet.Create("a", null, "b");
        Assert.IsTrue(RoundTrip(withNull).SetEquals(withNull));
        Assert.AreEqual(0, RoundTrip(ImmutableDictionary<string, int>.Empty).Count);
        Assert.AreEqual(0, RoundTrip(ImmutableHashSet<string>.Empty).Count);
    }

#if NET8_0_OR_GREATER
    [TestMethod]
    public void ImmutableCollidingKeysUnderARaisedLimit() =>
        SettingsTests.RunProbe(nameof(SettingsProbes.ImmutableCollidingKeys));
#endif

    /// <summary>Longs whose halves cancel out: every one hashes to 0.</summary>
    internal static long[] CancellingLongs(int n) => Enumerable.Range(0, n).Select(i => (long)i << 32 | (uint)i).ToArray();

    internal static void SameImmutable<TKey>(TKey[] keys, Func<int, TKey> other)
    {
        var dictionary = keys.Select((k, i) => new KeyValuePair<TKey, int>(k, i)).ToImmutableDictionary();
        // Copied out of the tree, written as the enumerator yields it.
        CollectionAssert.AreEqual(NinoSerializer.Serialize(dictionary.ToArray()), NinoSerializer.Serialize(dictionary));
        var back = RoundTrip(dictionary);
        Assert.AreEqual(dictionary.Count, back.Count);
        foreach (var pair in dictionary)
            Assert.AreEqual(pair.Value, back[pair.Key]);
        CollectionAssert.AreEquivalent(dictionary.ToArray(), back.ToArray());
        var changed = back.Add(other(0), -1).SetItem(keys[keys.Length - 1], -2).Remove(keys[0]);
        Assert.AreEqual(back.Count, changed.Count);
        Assert.AreEqual(-1, changed[other(0)]);
        Assert.IsFalse(changed.ContainsKey(keys[0]));
        if (keys.Length > 1)
            Assert.AreEqual(-2, changed[keys[keys.Length - 1]]);
        Assert.AreEqual(0, back[keys[0]]);
        var emptied = keys.Aggregate(back, (d, k) => d.Remove(k));
        Assert.IsTrue(emptied.IsEmpty);

        var set = keys.ToImmutableHashSet();
        CollectionAssert.AreEqual(NinoSerializer.Serialize(set.ToArray()), NinoSerializer.Serialize(set));
        var setBack = RoundTrip(set);
        Assert.IsTrue(setBack.SetEquals(set));
        Assert.IsFalse(setBack.Contains(other(1)));
        var setChanged = setBack.Add(other(1)).Remove(keys[0]);
        Assert.IsTrue(setChanged.Contains(other(1)));
        Assert.IsFalse(setChanged.Contains(keys[0]));
        Assert.IsTrue(setBack.Contains(keys[0]));
        Assert.IsTrue(keys.Aggregate(setBack, (s, k) => s.Remove(k)).IsEmpty);
    }
}
