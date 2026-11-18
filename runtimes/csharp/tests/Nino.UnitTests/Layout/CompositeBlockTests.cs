using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using Nino.UnitTests.Subset.Blocks;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Composites (value tuples, pairs, generic [NinoType] structs) compose their kind from their items (#348): a block
/// where every item is raw or a block (DateTimeOffset, a block struct, another composite), its map built item by item
/// (NinoBlockMap.Item), each item at the bytes the items before it take. The bytes are those of the items written one
/// after another, as before; every value reads back, alone and in collections; hostile data is rejected.
/// </summary>
[TestClass]
public class CompositeBlockTests
{
    private static readonly int[] Lengths = { 0, 1, 2, 3, 7, 16, 17, 33, 100 };

    private static DateTimeOffset Date(int i) =>
        new(637_000_000_000_000_000 + i * 1_000_003L, TimeSpan.FromMinutes((i % 29 - 14) * 30));

    private static BlockTuple Tuple(int i) => new() { A = i * 3 - 7, T = (i, (long)i << 40 | (uint)i), B = i % 3 == 0 };

    /// <summary>The bytes of the items written one after another: what a composite of them wrote before.</summary>
    private static byte[] Items(params byte[][] items) => items.SelectMany(b => b).ToArray();

    private static byte[] Bytes<T>(T value) => NinoSerializer.Serialize(value);

    private static byte[] OneByOne<T>(IEnumerable<T> values)
    {
        var list = values.ToList();
        return BitConverter.GetBytes(list.Count).Concat(list.SelectMany(v => NinoSerializer.Serialize(v))).ToArray();
    }

    /// <summary>
    /// <paramref name="create"/>'s values as a block of <paramref name="size"/> bytes under <typeparamref name="TFormatter"/>
    /// (the formatter the resolver binds): the kind, the bytes of one value (<paramref name="items"/>: its items alone),
    /// and arrays, lists and existing arrays of every length, the same bytes as the values one at a time.
    /// </summary>
    private static void Composes<T, TFormatter>(Func<int, T> create, Func<T, byte[]> items, int size)
        where TFormatter : struct, INinoFormatter<T>
    {
        string name = typeof(T).Name;
        Assert.AreEqual(typeof(TFormatter), ((NinoFormatterBase)NinoFormatterCache<T>.Instance).StructType, name);
        Assert.AreEqual(NinoElementKind.Block, NinoKind.Of<T, TFormatter>(), name);
        Assert.AreEqual(size, NinoKind.Stride<T, TFormatter>(), name);
        Assert.AreEqual(size, NinoBlocks.ItemSize<T, TFormatter>(), name);
        Assert.AreEqual(size, default(TFormatter).FixedSize, name);
        Assert.AreEqual(size, default(TFormatter).BlockMap().Size, name);
        foreach (int i in new[] { 0, 1, 42 })
        {
            var value = create(i);
            var bytes = Bytes(value);
            CollectionAssert.AreEqual(items(value), bytes, $"{name} {i}");
            Assert.AreEqual(value, NinoDeserializer.Deserialize<T>(bytes), $"{name} {i}");
        }

        foreach (int length in Lengths)
        {
            var values = Enumerable.Range(0, length).Select(create).ToArray();
            var expected = OneByOne(values);
            Assert.AreEqual(4 + length * size, expected.Length);
            string what = $"{name}[{length}]";
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values), what);
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()), "List of " + what);
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<T[]>(expected), what);
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<List<T>>(expected), "List of " + what);
            var existing = Enumerable.Range(1000, length).Select(create).ToArray();
            NinoDeserializer.Deserialize(expected, ref existing);
            CollectionAssert.AreEqual(values, existing, "existing " + what);
        }
    }

    /// <summary>
    /// <see cref="Composes{T, TFormatter}"/> under the formatter the resolver binds to <typeparamref name="T"/>: for
    /// types whose formatter names a generated one, internal to the Subset assembly.
    /// </summary>
    private static void ComposesResolved<T>(Func<int, T> create, Func<T, byte[]> items, int size)
    {
        var formatter = ((NinoFormatterBase)NinoFormatterCache<T>.Instance).StructType;
        typeof(CompositeBlockTests).GetMethod(nameof(Composes), System.Reflection.BindingFlags.NonPublic |
                                                             System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(typeof(T), formatter).Invoke(null, new object[] { create, items, size });
    }

    [TestMethod]
    public void TuplesOfRawAndBlockItems()
    {
        Composes<(int, DateTimeOffset), ValueTupleFormatter<int, DateTimeOffset, UnmanagedFormatter<int>,
            DateTimeOffsetFormatter>>(i => (i * 7, Date(i)), v => Items(Bytes(v.Item1), Bytes(v.Item2)), 14);
        Composes<(DateTimeOffset, byte, DateTimeOffset), ValueTupleFormatter<DateTimeOffset, byte, DateTimeOffset,
            DateTimeOffsetFormatter, UnmanagedFormatter<byte>, DateTimeOffsetFormatter>>(
            i => (Date(i), (byte)i, Date(i + 500)), v => Items(Bytes(v.Item1), Bytes(v.Item2), Bytes(v.Item3)), 21);
        // A generated block struct as an item.
        ComposesResolved(i => ((short)-i, Tuple(i)), v => Items(Bytes(v.Item1), Bytes(v.Item2)), 19);
    }

    [TestMethod]
    public void PairsOfRawAndBlockItems()
    {
        Composes<KeyValuePair<int, DateTimeOffset>, KeyValuePairFormatter<int, DateTimeOffset, UnmanagedFormatter<int>,
            DateTimeOffsetFormatter>>(i => new(i, Date(i)), v => Items(Bytes(v.Key), Bytes(v.Value)), 14);
        ComposesResolved(i => new KeyValuePair<int, BlockTuple>(i, Tuple(i)),
            v => Items(Bytes(v.Key), Bytes(v.Value)), 21);
        // A user's block formatter as the value (the resolver binds Cell to its raw bytes): its own map merged in.
        Assert.AreEqual(NinoElementKind.Block,
            NinoKind.Of<KeyValuePair<long, Cell>, KeyValuePairFormatter<long, Cell, UnmanagedFormatter<long>,
                CellBlockFormatter>>());
        Assert.AreEqual(14, NinoKind.Stride<KeyValuePair<long, Cell>, KeyValuePairFormatter<long, Cell,
            UnmanagedFormatter<long>, CellBlockFormatter>>());
    }

    [TestMethod]
    public void NestedComposites()
    {
        Composes<(Guid, (int, bool)), ValueTupleFormatter<Guid, (int, bool), UnmanagedFormatter<Guid>,
            ValueTupleFormatter<int, bool, UnmanagedFormatter<int>, UnmanagedFormatter<bool>>>>(
            i => (new Guid(i, (short)i, 7, 1, 2, 3, 4, 5, 6, 7, 8), (i, i % 2 == 0)),
            v => Items(Bytes(v.Item1), Bytes(v.Item2.Item1), Bytes(v.Item2.Item2)), 21);
        Composes<((int, short), KeyValuePair<long, DateTimeOffset>), ValueTupleFormatter<(int, short),
            KeyValuePair<long, DateTimeOffset>, ValueTupleFormatter<int, short, UnmanagedFormatter<int>,
                UnmanagedFormatter<short>>, KeyValuePairFormatter<long, DateTimeOffset, UnmanagedFormatter<long>,
                DateTimeOffsetFormatter>>>(i => ((i, (short)i), new(i * 9L, Date(i))),
            v => Items(Bytes(v.Item1.Item1), Bytes(v.Item1.Item2), Bytes(v.Item2.Key), Bytes(v.Item2.Value)), 24);
    }

    [TestMethod]
    public void GenericStructs()
    {
        ComposesResolved(i => new GenericBlock<long> { A = i, Item = -i * 5L, B = i % 2 == 1 },
            v => Items(Bytes(v.A), Bytes(v.Item), Bytes(v.B)), 13);
        ComposesResolved(i => new GenericBlock<DateTimeOffset> { A = i, Item = Date(i), B = i % 3 == 1 },
            v => Items(Bytes(v.A), Bytes(v.Item), Bytes(v.B)), 15);
        ComposesResolved(i => new GenericBlock<(int, bool)> { A = i, Item = (i * 2, i % 2 == 0), B = true },
            v => Items(Bytes(v.A), Bytes(v.Item.Item1), Bytes(v.Item.Item2), Bytes(v.B)), 10);
        ComposesResolved(i => new GenericBlockPair<int, DateTimeOffset> { Key = i, Value = (Date(i), (short)-i) },
            v => Items(Bytes(v.Key), Bytes(v.Value.Item1), Bytes(v.Value.Item2)), 16);
        ComposesResolved(i => new GenericBlockNested<short>
            {
                Tag = (byte)i, Inner = new GenericBlock<short> { A = -i, Item = (short)i, B = i % 2 == 0 },
            },
            v => Items(Bytes(v.Tag), Bytes(v.Inner.A), Bytes(v.Inner.Item), Bytes(v.Inner.B)), 8);
    }

    [TestMethod]
    public void ItemsOfNoFixedSizeLeaveTheCompositePerValue()
    {
        // A string, a slot (int?) and a class: written member by member, the same bytes as before.
        var strings = new GenericBlock<string> { A = 1, Item = "nino", B = true };
        CollectionAssert.AreEqual(Items(Bytes(1), Bytes("nino"), Bytes(true)), Bytes(strings));
        Assert.AreEqual(strings, NinoDeserializer.Deserialize<GenericBlock<string>>(Bytes(strings)));
        var nullable = new GenericBlock<int?> { A = 2, Item = 5, B = false };
        CollectionAssert.AreEqual(Items(Bytes(2), Bytes((int?)5), Bytes(false)), Bytes(nullable));
        var array = new[] { nullable, new GenericBlock<int?> { A = 3 } };
        CollectionAssert.AreEqual(array, NinoDeserializer.Deserialize<GenericBlock<int?>[]>(Bytes(array)));
        Assert.AreEqual(NinoElementKind.PerValue,
            NinoKind.Of<(int, int?), ValueTupleFormatter<int, int?, UnmanagedFormatter<int>,
                UnmanagedNullableFormatter<int>>>());
        Assert.AreEqual(-1, NinoBlocks.ItemSize<(int, int?), ValueTupleFormatter<int, int?,
            UnmanagedFormatter<int>, UnmanagedNullableFormatter<int>>>());
        var pairs = new Dictionary<int, (int, string)> { [1] = (2, "x"), [3] = (4, null) };
        var back = NinoDeserializer.Deserialize<Dictionary<int, (int, string)>>(Bytes(pairs));
        CollectionAssert.AreEquivalent(pairs.ToList(), back.ToList());
    }

    [TestMethod]
    public void CompositesInsideCollections()
    {
        var dictionary = Enumerable.Range(0, 40).ToDictionary(i => i, i => (i * 3, Date(i)));
        var back = NinoDeserializer.Deserialize<Dictionary<int, (int, DateTimeOffset)>>(Bytes(dictionary));
        CollectionAssert.AreEquivalent(dictionary.ToList(), back.ToList());
        var set = new HashSet<(int, DateTimeOffset)>(Enumerable.Range(0, 40).Select(i => (i, Date(i))));
        CollectionAssert.AreEquivalent(set.ToList(),
            NinoDeserializer.Deserialize<HashSet<(int, DateTimeOffset)>>(Bytes(set)).ToList());
        var tuple = new Tuple<int, DateTimeOffset>(7, Date(7));
        CollectionAssert.AreEqual(Items(new byte[] { 1 }, Bytes(7), Bytes(Date(7))), Bytes(tuple));
        Assert.AreEqual(tuple, NinoDeserializer.Deserialize<Tuple<int, DateTimeOffset>>(Bytes(tuple)));
    }

    private static void Invalid<T>(byte[] data, string what) =>
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T>(data), what);

    /// <summary>
    /// Hostile data in the new blocks: a count past the bytes there are (rejected before the array is allocated), cut
    /// data, a DateTimeOffset out of range and a bool other than 0 or 1 inside a nested item, in the first, a middle
    /// and the last value, alone and in arrays and lists.
    /// </summary>
    [TestMethod]
    public void HostileDataIsRejected()
    {
        var values = Enumerable.Range(0, 33).Select(i => (i, Date(i))).ToArray();
        var data = Bytes(values);
        foreach (int cut in new[] { 1, 13, 14, 15, 14 * 32 })
            Invalid<(int, DateTimeOffset)[]>(data.AsSpan(0, data.Length - cut).ToArray(), $"cut {cut}");
        var huge = (byte[])data.Clone();
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 0);
        Invalid<(int, DateTimeOffset)[]>(huge, "length int.MaxValue");
        Invalid<List<KeyValuePair<int, DateTimeOffset>>>(huge, "pairs, length int.MaxValue");

        foreach (int at in new[] { 0, 16, 32 })
        {
            // Offset minutes beyond 14 hours, then UTC ticks past DateTime.MaxValue.
            var minutes = (byte[])data.Clone();
            BitConverter.GetBytes((short)(15 * 60)).CopyTo(minutes, 4 + at * 14 + 4 + 8);
            Invalid<(int, DateTimeOffset)[]>(minutes, $"minutes at {at}");
            Invalid<List<(int, DateTimeOffset)>>(minutes, $"List, minutes at {at}");
            Invalid<KeyValuePair<int, DateTimeOffset>[]>(minutes, $"pairs, minutes at {at}");
            var ticks = (byte[])data.Clone();
            BitConverter.GetBytes(long.MaxValue).CopyTo(ticks, 4 + at * 14 + 4);
            Invalid<(int, DateTimeOffset)[]>(ticks, $"ticks at {at}");
        }

        Invalid<(int, DateTimeOffset)>(data.AsSpan(4 + 14, 14).ToArray().Select((b, i) => i == 13 ? (byte)0x7F : b)
            .ToArray(), "one value, minutes");

        var nested = Bytes(Enumerable.Range(0, 20).Select(i => (Guid.Empty, (i, true))).ToArray());
        foreach (int at in new[] { 0, 9, 19 })
        foreach (byte b in new byte[] { 2, 0x80, 0xFF })
        {
            var bad = (byte[])nested.Clone();
            bad[4 + at * 21 + 20] = b;
            Invalid<(Guid, (int, bool))[]>(bad, $"bool at {at}: {b}");
            Invalid<List<(Guid, (int, bool))>>(bad, $"List, bool at {at}: {b}");
        }

        var generic = Bytes(Enumerable.Range(0, 20).Select(i => new GenericBlock<DateTimeOffset>
            { A = i, Item = Date(i), B = true }).ToArray());
        foreach (int at in new[] { 0, 19 })
        {
            var bad = (byte[])generic.Clone();
            bad[4 + at * 15 + 14] = 3;
            Invalid<GenericBlock<DateTimeOffset>[]>(bad, $"generic bool at {at}");
            bad = (byte[])generic.Clone();
            BitConverter.GetBytes(short.MinValue).CopyTo(bad, 4 + at * 15 + 12);
            Invalid<GenericBlock<DateTimeOffset>[]>(bad, $"generic minutes at {at}");
        }
    }
}
