using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset;

namespace Nino.UnitTests
{
    [TestClass]
    public class RawStructTests
    {
        [TestMethod]
        public void RawStructIsWrittenAsMemory()
        {
            var value = new SubsetRawStruct { X = 1, Y = 2 };
            var bytes = NinoSerializer.Serialize(value);
            Assert.AreEqual(Unsafe.SizeOf<SubsetRawStruct>(), bytes.Length);
            Assert.AreEqual(value, NinoDeserializer.Deserialize<SubsetRawStruct>(bytes));

            var outer = new SubsetRawOuter { Inner = value, Z = 3 };
            bytes = NinoSerializer.Serialize(outer);
            Assert.AreEqual(Unsafe.SizeOf<SubsetRawOuter>(), bytes.Length);
            Assert.AreEqual(outer, NinoDeserializer.Deserialize<SubsetRawOuter>(bytes));
        }

        [TestMethod]
        public void RawStructMultiDimArraysAreOneBlock()
        {
            var grid = new SubsetRawStruct[3, 4];
            for (int i = 0; i < 3; i++)
            for (int j = 0; j < 4; j++)
                grid[i, j] = new SubsetRawStruct { X = i, Y = j };
            var bytes = NinoSerializer.Serialize(grid);
            Assert.AreEqual(8 + 12 * Unsafe.SizeOf<SubsetRawStruct>(), bytes.Length);
            var result = NinoDeserializer.Deserialize<SubsetRawStruct[,]>(bytes);
            CollectionAssert.AreEqual(grid, result);
        }

        [TestMethod]
        public void RawStructCollectionsAreOneBlock()
        {
            var array = new SubsetRawStruct[100];
            for (int i = 0; i < array.Length; i++)
                array[i] = new SubsetRawStruct { X = i, Y = -i };

            var bytes = NinoSerializer.Serialize(array);
            Assert.AreEqual(4 + array.Length * Unsafe.SizeOf<SubsetRawStruct>(), bytes.Length);
            CollectionAssert.AreEqual(array, NinoDeserializer.Deserialize<SubsetRawStruct[]>(bytes));

            var list = new List<SubsetRawStruct>(array);
            CollectionAssert.AreEqual(list,
                NinoDeserializer.Deserialize<List<SubsetRawStruct>>(NinoSerializer.Serialize(list)));
        }

        [TestMethod]
        public void NonRawStructsAreWrittenMemberWise()
        {
            var partial = new SubsetPartialStruct { X = 1, Ignored = 5, Y = 2 };
            var bytes = NinoSerializer.Serialize(partial);
            Assert.AreEqual(12, bytes.Length);
            var result = NinoDeserializer.Deserialize<SubsetPartialStruct>(bytes);
            Assert.AreEqual(1, result.X);
            Assert.AreEqual(0, result.Ignored);
            Assert.AreEqual(2, result.Y);

            var tolerant = new SubsetTolerantStruct { X = 1, Y = 2 };
            bytes = NinoSerializer.Serialize(tolerant);
            Assert.AreEqual(4 + 12, bytes.Length);
            Assert.AreEqual(tolerant, NinoDeserializer.Deserialize<SubsetTolerantStruct>(bytes));
        }
    }
}

[TestClass]
public class TopLevelFastPathTests
{
    private static byte[] ViaWriter<T>(T value)
    {
        var buffer = new NinoArrayBufferWriter();
        NinoSerializer.Serialize(value, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    [TestMethod]
    public void RawArraysMatchTheWriterPath()
    {
        var ints = new[] { 1, -2, 3 };
        CollectionAssert.AreEqual(ViaWriter(ints), NinoSerializer.Serialize(ints));
        CollectionAssert.AreEqual(ints, NinoDeserializer.Deserialize<int[]>(NinoSerializer.Serialize(ints)));
        var empty = new long[0];
        CollectionAssert.AreEqual(ViaWriter(empty), NinoSerializer.Serialize(empty));
        int[] none = null;
        CollectionAssert.AreEqual(ViaWriter(none), NinoSerializer.Serialize(none));
        var guids = new[] { System.Guid.NewGuid(), System.Guid.NewGuid() };
        CollectionAssert.AreEqual(ViaWriter(guids), NinoSerializer.Serialize(guids));
        CollectionAssert.AreEqual(guids, NinoDeserializer.Deserialize<System.Guid[]>(NinoSerializer.Serialize(guids)));
    }

    [TestMethod]
    public void NullableRawValuesRoundTrip()
    {
        var values = new int?[] { 1, null, -3 };
        var bytes = NinoSerializer.Serialize(values);
        Assert.AreEqual(4 + 3 * 8, bytes.Length);
        CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<int?[]>(bytes));
        // The 64-bit Nullable<int> layout: has-value byte, 3 zero bytes, the value (zero when null).
        CollectionAssert.AreEqual(new byte[]
        {
            3, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0xFD, 0xFF, 0xFF, 0xFF
        }, bytes);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new List<int?>(values)), bytes);
        var holder = NinoDeserializer.Deserialize<NullableHolder>(NinoSerializer.Serialize(
            new NullableHolder { Array = values, List = new List<int?>(values), Dates = new List<System.DateTime?> { null } }));
        CollectionAssert.AreEqual(values, holder.Array);
        CollectionAssert.AreEqual(values, holder.List);
        Assert.AreEqual(1, holder.Dates.Count);
        Assert.IsNull(holder.Dates[0]);
        CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<List<int?>>(bytes));
        var reused = new int?[] { 9, 9, 9 };
        NinoDeserializer.Deserialize(bytes, ref reused);
        CollectionAssert.AreEqual(values, reused);
        Assert.ThrowsExactly<System.InvalidOperationException>(() =>
            NinoDeserializer.Deserialize<int?[]>(bytes.AsSpan(0, bytes.Length - 1).ToArray()));
        var dates = new System.DateTime?[] { null, System.DateTime.UtcNow };
        CollectionAssert.AreEqual(dates, NinoDeserializer.Deserialize<System.DateTime?[]>(NinoSerializer.Serialize(dates)));
    }
}

[NinoType]
public class NullableHolder
{
    public int?[] Array;
    public List<int?> List;
    public List<System.DateTime?> Dates;
}
