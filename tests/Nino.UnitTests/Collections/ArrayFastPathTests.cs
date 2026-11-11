using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using static Nino.UnitTests.Rt;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Arrays, lists and Memory of raw and [NinoType] elements: short and large raw blocks reused in place, struct elements
/// through their generated formatter, small class bodies inlined into the loop, sized collections written straight into
/// an array, and ImmutableArray / ImmutableList written as arrays.
/// </summary>
[TestClass]
public class ArrayFastPathTests
{
    [TestMethod]
    public void ShortRawArrayReuseKeepsTheFollowingValue()
    {
        foreach (int length in new[] { 64, 65, 80, 81, 127, 128, 129 })
        {
            var items = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
            using var buffer = OffsetWriter();
            var writer = new Writer(buffer);
            var formatter = default(UnmanagedArrayFormatter<byte>);
            formatter.Serialize(items, ref writer);
            writer.Write(123456);
            writer.Flush();

            CollectionAssert.AreEqual(buffer.WrittenSpan.Slice(1, sizeof(int) + length).ToArray(),
                NinoSerializer.Serialize(items));

            var reader = new Reader(buffer.WrittenSpan.Slice(1));
            var destination = new byte[length];
            var original = destination;
            formatter.Deserialize(ref reader, ref destination);
            Assert.AreSame(original, destination);
            CollectionAssert.AreEqual(items, destination);
            Assert.AreEqual(123456, reader.Read<int>());

            var list = new List<byte>(length);
            var originalList = list;
            reader = new Reader(buffer.WrittenSpan.Slice(1));
            var listFormatter = default(UnmanagedListFormatter<List<byte>, byte>);
            listFormatter.Deserialize(ref reader, ref list);
            Assert.AreSame(originalList, list);
            CollectionAssert.AreEqual(items, list);
            Assert.AreEqual(123456, reader.Read<int>());

#if NET8_0_OR_GREATER && !NINO_CORE_NETSTANDARD
            if (length is >= 81 and <= 128 && System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
            {
                list = buffer.WrittenSpan.ToArray().ToList();
                reader = new Reader(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list).Slice(1));
                listFormatter.Deserialize(ref reader, ref list);
                CollectionAssert.AreEqual(items, list);
                Assert.AreEqual(123456, reader.Read<int>());
            }
#endif
        }
    }

    [TestMethod]
    public void ShortRawMemoryReusePreservesOverlapAndFollowingValue()
    {
        foreach (int length in new[] { 81, 128 })
        {
            var items = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
            using var buffer = new NinoArrayBufferWriter();
            var writer = new Writer(buffer);
            var formatter = default(MemoryFormatter<byte, UnmanagedFormatter<byte>>);
            formatter.Serialize(items.AsMemory(), ref writer);
            writer.Write(123456);
            writer.Flush();

            foreach (bool overlap in new[] { false, true })
            {
                var data = buffer.WrittenSpan.ToArray();
                var backing = overlap ? data : new byte[length + 1];
                var original = backing.AsMemory(1, length);
                var destination = original;
                var reader = new Reader(data);
                formatter.Deserialize(ref reader, ref destination);
                Assert.AreEqual(original, destination);
                CollectionAssert.AreEqual(items, destination.ToArray());
                Assert.AreEqual(123456, reader.Read<int>());
            }
        }
    }

    [TestMethod]
    public void StructElementsKeepTheirBytesAfterTopLevelUse()
    {
        var items = Enumerable.Range(0, 100).Select(i => new PaddedItem
            { Id = i, Count = (short)(i * 3), Bound = i % 2 == 0 }).ToArray();
        var named = Enumerable.Range(0, 20).Select(i => new NamedItem
            { Id = i, Name = i % 3 == 0 ? null : "n" + i }).ToList();

        byte[] before = NinoSerializer.Serialize(items);
        byte[] namedBefore = NinoSerializer.Serialize(named);
        // Top-level use registers the element type; collections of it must keep their bytes.
        Assert.AreEqual(items[1], NinoDeserializer.Deserialize<PaddedItem>(NinoSerializer.Serialize(items[1])));
        Assert.AreEqual(named[1], NinoDeserializer.Deserialize<NamedItem>(NinoSerializer.Serialize(named[1])));
        CollectionAssert.AreEqual(before, NinoSerializer.Serialize(items));
        CollectionAssert.AreEqual(namedBefore, NinoSerializer.Serialize(named));
        CollectionAssert.AreEqual(before, NinoSerializer.Serialize(items.ToList()));

        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<PaddedItem[]>(before));
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<List<PaddedItem>>(before));
        CollectionAssert.AreEqual(named, NinoDeserializer.Deserialize<List<NamedItem>>(namedBefore));
        CollectionAssert.AreEqual(named, NinoDeserializer.Deserialize<NamedItem[]>(namedBefore));
        var byId = items.ToDictionary(i => i.Id);
        CollectionAssert.AreEqual(byId.ToList(),
            NinoDeserializer.Deserialize<Dictionary<int, PaddedItem>>(NinoSerializer.Serialize(byId)).ToList());
    }

    [TestMethod]
    public void SmallBlocksOfEverySizeRoundTrip()
    {
        // Raw element blocks of 0 to 40 bytes (copied inline up to 32), and strings.
        for (int n = 0; n <= 40; n++)
        {
            var block = Enumerable.Range(0, n).Select(i => (byte)(i * 7 + n)).ToArray();
            CollectionAssert.AreEqual(block, NinoDeserializer.Deserialize<byte[]>(NinoSerializer.Serialize(block)));
            var shorts = block.Select(b => (short)(b * 300)).Take(n / 2).ToList();
            CollectionAssert.AreEqual(shorts, NinoDeserializer.Deserialize<List<short>>(NinoSerializer.Serialize(shorts)));
        }

        // Strings are allocated by the runtime's FastAllocateString on .NET 8 and later and filled inline up to 32
        // chars: every length around 16 and 32 chars and some past them.
        foreach (int n in Enumerable.Range(0, 70).Concat(new[] { 100, 257, 1000 }))
        {
            var text = new string(Enumerable.Range(0, n).Select(i => (char)('a' + i % 26 + (i % 7 == 0 ? 0x400 : 0))).ToArray());
            Assert.AreEqual(text, NinoDeserializer.Deserialize<string>(NinoSerializer.Serialize(text)));
            var words = new[] { text, null, text + "é" };
            CollectionAssert.AreEqual(words, NinoDeserializer.Deserialize<string[]>(NinoSerializer.Serialize(words)));
        }
    }

    [TestMethod]
    public void BlocksUpToTheVectorLimitRoundTrip()
    {
        // Writes of 64 bytes to 8 KB use 64-byte vectors where the CPU has them, padded structs a masked copy:
        // every size around the vector widths, then sizes past the limit.
        var sizes = Enumerable.Range(0, 300).Concat(Enumerable.Range(0, 60).Select(i => 300 + i * 151))
            .Concat(new[] { 8191, 8192, 8193, 9000 });
        foreach (int n in sizes)
        {
            var block = Enumerable.Range(0, n).Select(i => (byte)(i * 31 + n)).ToArray();
            CollectionAssert.AreEqual(block, NinoDeserializer.Deserialize<byte[]>(NinoSerializer.Serialize(block)));
        }

        foreach (int n in Enumerable.Range(0, 40).Concat(new[] { 63, 64, 65, 150, 999, 1024, 1025, 1200 }))
        {
            var items = Enumerable.Range(0, n).Select(i => new PaddedItem
                { Id = i * 7, Count = (short)i, Bound = i % 3 == 0 }).ToArray();
            byte[] bytes = NinoSerializer.Serialize(items);
            CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<PaddedItem[]>(bytes));
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(items.ToList()));
        }
    }

    [TestMethod]
    public void SmallClassBodiesInCollectionsKeepTheirSubtypes()
    {
        // A small non-sealed class body is inlined into the collection's loop; its subtypes and nulls still go
        // through the polymorphic path.
        var book = new NoteBook
        {
            Title = "book",
            Notes = Enumerable.Range(0, 50).Select(i => i % 5 == 0 ? null
                : i % 3 == 0 ? new TaggedNote { Id = i, Text = "t" + i, Tag = "#" + i }
                : new InlineNote { Id = i, Text = i % 4 == 0 ? null : "n" + i }).ToList()
        };
        var books = new[] { book, null, new NoteBook { Title = "empty", Notes = new List<InlineNote>() } };
        var result = NinoDeserializer.Deserialize<NoteBook[]>(NinoSerializer.Serialize(books));

        Assert.AreEqual(3, result.Length);
        Assert.IsNull(result[1]);
        Assert.AreEqual(0, result[2].Notes.Count);
        Assert.AreEqual("book", result[0].Title);
        for (int i = 0; i < book.Notes.Count; i++)
        {
            InlineNote expected = book.Notes[i], actual = result[0].Notes[i];
            if (expected == null)
            {
                Assert.IsNull(actual);
                continue;
            }

            Assert.AreEqual(expected.GetType(), actual.GetType());
            Assert.AreEqual(expected.Id, actual.Id);
            Assert.AreEqual(expected.Text, actual.Text);
            if (expected is TaggedNote tagged)
                Assert.AreEqual(tagged.Tag, ((TaggedNote)actual).Tag);
        }

        CollectionAssert.AreEqual(NinoSerializer.Serialize(book.Notes),
            NinoSerializer.Serialize(result[0].Notes));
    }

    [TestMethod]
    public void WideStructsWrittenByReferenceRoundTrip()
    {
        // Structs with a member that is not raw are passed to their out-of-line body by reference.
        var items = Enumerable.Range(0, 100).Select(i => new WideItem
            { A = i, B = -i, C = i * 3L, D = long.MaxValue - i, Name = i % 7 == 0 ? null : "w" + i, E = i * 5 }).ToList();
        byte[] bytes = NinoSerializer.Serialize(items);
        CollectionAssert.AreEqual(items, NinoDeserializer.Deserialize<List<WideItem>>(bytes));
        CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(items.ToArray()));
        Assert.AreEqual(items[3], NinoDeserializer.Deserialize<WideItem>(NinoSerializer.Serialize(items[3])));
    }

    [TestMethod]
    public void LargeRawListsRoundTrip()
    {
        // A new list of raw elements gets an uninitialized backing array from 6 KB on; a reused one, a derived list
        // and a list of nullable values take their own paths.
        foreach (int n in new[] { 0, 1, 1535, 1536, 5000 })
        {
            var ints = Enumerable.Range(0, n).Select(i => i * 7 - 3).ToList();
            byte[] bytes = NinoSerializer.Serialize(ints);
            var read = NinoDeserializer.Deserialize<List<int>>(bytes);
            CollectionAssert.AreEqual(ints, read);
            Assert.AreEqual(n, read.Capacity);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(ints.ToArray()));

            var reused = Enumerable.Repeat(-1, 7000).ToList();
            NinoDeserializer.Deserialize(bytes, ref reused);
            CollectionAssert.AreEqual(ints, reused);

            var derived = NinoDeserializer.Deserialize<DerivedIntList>(bytes);
            CollectionAssert.AreEqual(ints, derived);

            var nullable = Enumerable.Range(0, n).Select(i => i % 3 == 0 ? (long?)null : i * 11L).ToList();
            CollectionAssert.AreEqual(nullable,
                NinoDeserializer.Deserialize<List<long?>>(NinoSerializer.Serialize(nullable)));
        }
    }

    [TestMethod]
    public void ImmutableCollectionsWriteTheBytesOfArrays()
    {
        // ImmutableArray<T> is written and read through its array, ImmutableList<T> built from one: the bytes are
        // those of T[], and a read never reuses the array of the old value.
        foreach (int n in new[] { 0, 1, 3, 2000 })
        {
            var ints = Enumerable.Range(0, n).Select(i => i * 31).ToArray();
            var strings = Enumerable.Range(0, n).Select(i => i % 5 == 0 ? null : "s" + i).ToArray();

            var intArray = ImmutableArray.Create(ints);
            byte[] bytes = NinoSerializer.Serialize(intArray);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(ints), bytes);
            var old = ImmutableArray.Create(Enumerable.Repeat(9, n).ToArray());
            var kept = old.ToArray();
            var read = old;
            NinoDeserializer.Deserialize(bytes, ref read);
            CollectionAssert.AreEqual(ints, read.ToArray());
            CollectionAssert.AreEqual(kept, old.ToArray());

            var stringArray = ImmutableArray.Create(strings);
            bytes = NinoSerializer.Serialize(stringArray);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(strings), bytes);
            CollectionAssert.AreEqual(strings, NinoDeserializer.Deserialize<ImmutableArray<string>>(bytes).ToArray());

            var intList = ImmutableList.Create(ints);
            bytes = NinoSerializer.Serialize(intList);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(ints), bytes);
            CollectionAssert.AreEqual(ints, NinoDeserializer.Deserialize<ImmutableList<int>>(bytes).ToArray());

            var stringList = ImmutableList.Create(strings);
            bytes = NinoSerializer.Serialize(stringList);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(strings), bytes);
            CollectionAssert.AreEqual(strings, NinoDeserializer.Deserialize<ImmutableList<string>>(bytes).ToArray());
        }

        Assert.IsTrue(NinoDeserializer.Deserialize<ImmutableArray<int>>(NinoSerializer.Serialize(default(ImmutableArray<int>))).IsDefault);
        Assert.IsNull(NinoDeserializer.Deserialize<ImmutableList<int>>(NinoSerializer.Serialize((ImmutableList<int>)null)));
    }

    [TestMethod]
    public void SizedCollectionsWriteTheWritersBytes()
    {
        // Collections whose size is known up front are written straight into an array of that size: the same bytes
        // as through a writer, for null, empty and full ones of every kind of block.
        var at = new DateTimeOffset(2026, 10, 19, 1, 2, 3, TimeSpan.FromHours(8));
        SameBytes(new[] { 1, 2, 3 });
        SameBytes(new List<int> { 1, 2, 3 });
        SameBytes(new List<int>());
        SameBytes((List<int>)null);
        SameBytes(Array.Empty<(int, long)>());
        SameBytes(((int, long)[])null);
        SameBytes(Enumerable.Range(0, 100).Select(i => (i, i * 3L)).ToList());
        SameBytes(Enumerable.Range(0, 100).Select(i => at.AddMinutes(i)).ToArray());
        SameBytes(Enumerable.Range(0, 100).Select(i => at.AddMinutes(i)).ToList());
        SameBytes(Enumerable.Range(0, 100).Select(i => new PaddedItem { Id = i, Count = (short)i, Bound = i % 2 == 0 })
            .ToList());
    }

    private static void SameBytes<T>(T value)
    {
        var buffer = new NinoArrayBufferWriter();
        NinoSerializer.Serialize(value, buffer);
        CollectionAssert.AreEqual(buffer.WrittenSpan.ToArray(), NinoSerializer.Serialize(value), typeof(T).ToString());
    }
}
