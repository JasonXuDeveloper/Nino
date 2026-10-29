using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Blocks;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Structs of fixed size that are not raw (a tuple or pair member, a runtime layout, auto-properties, another such
/// struct inside): their generated formatters write collections of them as blocks, several values at a time by vector
/// shuffles where the CPU has them. The bytes must be those of the values written one at a time, every value must read
/// back, and a bool byte other than 0 or 1 must be rejected wherever it is.
/// </summary>
[TestClass]
public class BlockStructTests
{
    private static readonly int[] Lengths = { 0, 1, 2, 3, 7, 16, 17, 33, 64, 100, 257 };

    private static BlockTuple Tuple(int i) => new BlockTuple { A = i * 3 - 7, T = (i, (long)i << 40 | (uint)i), B = i % 3 == 0 };

    private static BlockAuto Auto(int i) =>
        new BlockAuto { Tag = (byte)i, L = -((long)i << 33) - i, S = (short)(i * 7), On = i % 2 == 1 };

    private static BlockPair Pair(int i) =>
        new BlockPair { K = new KeyValuePair<int, short>(i * 11, (short)-i), F = i / 4f };

    private static BlockProps Props(int i) => new BlockProps { X = i ^ 0x5A5A, Flag = i % 5 == 0, P = ((short)(i * 3), i % 2 == 0) };

    private static BlockWaypoint Waypoint(int i) =>
        new BlockWaypoint { X = i * 1.5f, Y = -i, Stamp = (i * 7, (short)(i % 300)), Visited = i % 3 == 1 };

    private static BlockNested Nested(int i) => new BlockNested { Inner = Tuple(i), Tag = (byte)(255 - i) };

    private static BlockDated Dated(int i) =>
        new BlockDated { Id = i, When = new DateTime(2026, 1, 1).AddMinutes(i), Flags = ((short)i, i % 2 == 0) };

    /// <summary>The bytes of a collection of <paramref name="values"/> written one value at a time.</summary>
    private static byte[] OneByOne<T>(T[] values) =>
        BitConverter.GetBytes(values.Length).Concat(values.SelectMany(v => NinoSerializer.Serialize(v))).ToArray();

    private static void RoundTrips<T>(Func<int, T> create, int size)
    {
        Assert.AreEqual(size, NinoSerializer.GetFixedSize<T>(), typeof(T).Name);
        foreach (int length in Lengths)
        {
            var values = Enumerable.Range(0, length).Select(create).ToArray();
            var expected = OneByOne(values);
            Assert.AreEqual(4 + length * size, expected.Length);
            string what = $"{typeof(T).Name}[{length}]";
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values), what);
            CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values.ToList()), "List of " + what);
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<T[]>(expected), what);
            CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<List<T>>(expected), "List of " + what);
            // Into existing arrays: one of the same length is overwritten in place.
            var existing = Enumerable.Range(1000, length).Select(create).ToArray();
            NinoDeserializer.Deserialize(expected, ref existing);
            CollectionAssert.AreEqual(values, existing, "existing " + what);
            // Inside an array, unaligned to the length prefix: the span overload.
            var buffer = new NinoArrayBufferWriter(1);
            NinoSerializer.Serialize((byte)1, buffer);
            NinoSerializer.Serialize(values, buffer);
            CollectionAssert.AreEqual(expected, buffer.WrittenSpan.Slice(1).ToArray(), "after a byte, " + what);
        }
    }

    [TestMethod]
    public void TupleMember() => RoundTrips(Tuple, 17);

    [TestMethod]
    public void AutoLayout() => RoundTrips(Auto, 12);

    [TestMethod]
    public void PairMember() => RoundTrips(Pair, 10);

    [TestMethod]
    public void AutoProperties() => RoundTrips(Props, 8);

    [TestMethod]
    public void NestedBlockStruct() => RoundTrips(Nested, 18);

    [TestMethod]
    public void PaddedOverSixteenBytes() => RoundTrips(Waypoint, 15);

    /// <summary>
    /// Every byte of a new array read as blocks is written, padding as zero: below 6 KB the array is zeroed, so a
    /// path that fills padding with other bytes shows; from 6 KB it is allocated uninitialized
    /// (NinoElements.CopiedWhole), after memory of its size was filled with 0xCC and dropped, so a byte left unwritten
    /// shows. Every count up to a few vectors, and long ones. (An existing array may be read a value at a time, through
    /// a formatter that leaves its padding as it was.)
    /// </summary>
    [TestMethod]
    public void ReadsWriteEveryByte()
    {
        WritesEveryByte(Tuple);
        WritesEveryByte(Auto);
        WritesEveryByte(Pair);
        WritesEveryByte(Props);
        WritesEveryByte(Waypoint);
        WritesEveryByte(Nested);
        WritesEveryByte(Dated);
    }

    private static void WritesEveryByte<T>(Func<int, T> create) where T : unmanaged
    {
        foreach (int length in Enumerable.Range(0, 70).Concat(new[] { 300, 1000, 1001 }))
        {
            // Built in zeroed memory: their padding is zero.
            var values = new T[length];
            for (int i = 0; i < length; i++)
                values[i] = create(i);
            var data = NinoSerializer.Serialize(values);
            string what = $"{typeof(T).Name}[{length}]";
            var garbage = new T[length];
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(garbage.AsSpan()).Fill(0xCC);
            garbage = null;
            GC.Collect(0);
            SameBytes(values, NinoDeserializer.Deserialize<T[]>(data), "new " + what);
            SameBytes(values, NinoDeserializer.Deserialize<List<T>>(data).ToArray(), "List of " + what);
        }
    }

    private static void SameBytes<T>(T[] expected, T[] actual, string what) where T : unmanaged
    {
        var want = System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.AsSpan());
        var got = System.Runtime.InteropServices.MemoryMarshal.AsBytes(actual.AsSpan());
        int at = 0;
        while (at < got.Length && at < want.Length && got[at] == want[at])
            at++;
        if (at < want.Length || got.Length != want.Length)
            Assert.Fail($"{what}: byte {at} is {(at < got.Length ? got[at] : -1):X2}, not {(at < want.Length ? want[at] : -1):X2}");
    }

    [TestMethod]
    public void CheckedValues() => RoundTrips(Dated, 15);

    [TestMethod]
    public void OtherFixedSizeTypesWithTuples()
    {
        // Of fixed size, but not blocks: written and read one at a time, as before.
        Assert.AreEqual(17, NinoSerializer.GetFixedSize<FixedTupleSealed>());
        Assert.AreEqual(12, NinoSerializer.GetFixedSize<NotBlockIgnored>());
        Assert.AreEqual(12, NinoSerializer.GetFixedSize<NotBlockConstructed>());
        var sealedValue = new FixedTupleSealed { Id = 5, T = (6, 7L) };
        var bytes = NinoSerializer.Serialize(sealedValue);
        Assert.AreEqual(17, bytes.Length);
        var back = NinoDeserializer.Deserialize<FixedTupleSealed>(bytes);
        Assert.AreEqual(5, back.Id);
        Assert.AreEqual((6, 7L), back.T);

        // An ignored field keeps the value of the instance read into.
        var ignored = Enumerable.Range(0, 40).Select(i => new NotBlockIgnored { A = i, Kept = 99, T = (i, -i) }).ToArray();
        var data = NinoSerializer.Serialize(ignored);
        CollectionAssert.AreEqual(OneByOne(ignored), data);
        var into = Enumerable.Range(0, 40).Select(_ => new NotBlockIgnored { Kept = 7 }).ToArray();
        NinoDeserializer.Deserialize(data, ref into);
        for (int i = 0; i < into.Length; i++)
        {
            Assert.AreEqual(i, into[i].A);
            Assert.AreEqual((i, -i), into[i].T);
            Assert.AreEqual(7, into[i].Kept);
        }

        var constructed = Enumerable.Range(0, 40).Select(i => new NotBlockConstructed(i, (i, i * 2))).ToArray();
        data = NinoSerializer.Serialize(constructed);
        CollectionAssert.AreEqual(OneByOne(constructed), data);
        CollectionAssert.AreEqual(constructed, NinoDeserializer.Deserialize<NotBlockConstructed[]>(data));
    }

    private static void Invalid<T>(byte[] data, string what) =>
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T>(data), what);

    /// <summary>
    /// Every bool of <paramref name="create"/>'s blocks (at <paramref name="bools"/> in a block of
    /// <paramref name="size"/> bytes) set to each invalid byte in the first, a middle and the last value: rejected in an
    /// array and a list, whichever path (vectors, one at a time) reads that value.
    /// </summary>
    private static void RejectsBools<T>(Func<int, T> create, int size, params int[] bools)
    {
        foreach (int length in new[] { 1, 9, 33, 100 })
        {
            var data = NinoSerializer.Serialize(Enumerable.Range(0, length).Select(create).ToArray());
            // Every place in a step of eight (the strided check), a middle and the last.
            foreach (int at in new[] { 0, 1, 2, 3, 4, 5, 6, 7, length / 2, length - 1 }.Where(a => a < length).Distinct())
            foreach (int offset in bools)
            foreach (byte b in new byte[] { 2, 0x80, 0xFF })
            {
                var bad = (byte[])data.Clone();
                bad[4 + at * size + offset] = b;
                string what = $"{typeof(T).Name}[{length}], value {at}, byte {offset}: {b}";
                Invalid<T[]>(bad, what);
                Invalid<List<T>>(bad, "List of " + what);
            }
        }
    }

    [TestMethod]
    public void BoolsOtherThanZeroOrOneAreErrors()
    {
        RejectsBools(Tuple, 17, 16);
        RejectsBools(Auto, 12, 11);
        RejectsBools(Props, 8, 4, 7);
        RejectsBools(Nested, 18, 16);
        RejectsBools(Waypoint, 15, 14);
        RejectsBools(Dated, 15, 14);
    }

    [TestMethod]
    public void TruncatedDataIsAnError()
    {
        var data = NinoSerializer.Serialize(Enumerable.Range(0, 50).Select(Tuple).ToArray());
        foreach (int cut in new[] { 1, 16, 17, 18, 17 * 49 })
            Invalid<BlockTuple[]>(data.AsSpan(0, data.Length - cut).ToArray(), $"cut {cut}");
        // A length past the bytes there are.
        var longer = (byte[])data.Clone();
        BitConverter.GetBytes(51).CopyTo(longer, 0);
        Invalid<List<BlockTuple>>(longer, "length 51");
    }
}
