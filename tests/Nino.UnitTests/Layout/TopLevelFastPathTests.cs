using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

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
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<int?[]>(bytes.AsSpan(0, bytes.Length - 1).ToArray()));
        var dates = new System.DateTime?[] { null, System.DateTime.UtcNow };
        CollectionAssert.AreEqual(dates, NinoDeserializer.Deserialize<System.DateTime?[]>(NinoSerializer.Serialize(dates)));
    }

    // 16-byte T? read and written as two words (NullableWords): T at offsets 4, 2, 1 and 8 of the nullable.
    [TestMethod]
    public void SixteenByteNullablesRoundTripAsPresenceAndRawBytes()
    {
        CheckNullable(new Packed4 { A = -7, B = 0x0102030405060708 });
        CheckNullable(new Packed2 { A = 0x1234, B = -5, C = long.MinValue + 3 });
        CheckNullable(new Packed1 { A = 0xAB, B = -2, C = 0x0A0B0C0D, D = -0x1122334455667788 });
        CheckNullable(0x7F6E5D4C3B2A1908L);
        CheckNullable(new System.DateTime(2024, 2, 29, 23, 59, 58, System.DateTimeKind.Utc));

        var holder = new NullableWordsHolder
        {
            Four = new Packed4 { A = 1, B = 2 }, Two = null, One = new Packed1 { A = 3, B = 4, C = 5, D = 6 }, Long = 7,
        };
        var read = NinoDeserializer.Deserialize<NullableWordsHolder>(NinoSerializer.Serialize(holder));
        Assert.AreEqual(holder.Four, read.Four);
        Assert.IsNull(read.Two);
        Assert.AreEqual(holder.One, read.One);
        Assert.AreEqual(holder.Long, read.Long);
        // Read over present values: the nulls in the data replace them.
        var over = new NullableWordsHolder { Four = new Packed4 { A = 9 }, Two = new Packed2 { A = 9 }, Long = 9 };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new NullableWordsHolder()), ref over);
        Assert.IsNull(over.Four);
        Assert.IsNull(over.Two);
        Assert.IsNull(over.One);
        Assert.IsNull(over.Long);
    }

    private static void CheckNullable<T>(T value) where T : unmanaged
    {
        T? present = value;
        byte[] bytes = NinoSerializer.Serialize(present);
        var expected = new byte[1 + Unsafe.SizeOf<T>()];
        expected[0] = 1;
        Unsafe.WriteUnaligned(ref expected[1], value);
        CollectionAssert.AreEqual(expected, bytes);
        CollectionAssert.AreEqual(expected, ViaWriter(present));
        Assert.AreEqual(present, NinoDeserializer.Deserialize<T?>(bytes));
        T? into = null;
        NinoDeserializer.Deserialize(bytes, ref into);
        Assert.AreEqual(present, into);

        CollectionAssert.AreEqual(new byte[] { 0 }, NinoSerializer.Serialize((T?)null));
        Assert.IsNull(NinoDeserializer.Deserialize<T?>(new byte[] { 0 }));
        NinoDeserializer.Deserialize(new byte[] { 0 }, ref into);
        Assert.IsNull(into);
        // A present value one byte short, an absent one with nothing after it, a presence byte of neither 0 nor 1.
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T?>(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T?>(new byte[] { 1 }));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T?>(System.ReadOnlySpan<byte>.Empty));
        bytes[0] = 2;
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<T?>(bytes));
    }

    [TestMethod]
    public void ANullReadOverASixteenByteNullableRecyclesTheValueHeld()
    {
        var recycled = new List<RecycledPacked4?>();
        NinoSettings.SetRecycler<RecycledPacked4?>(recycled.Add);
        try
        {
            RecycledPacked4? value = new RecycledPacked4 { A = 5, B = 6 };
            NinoDeserializer.Deserialize(new byte[] { 0 }, ref value);
            Assert.IsNull(value);
            CollectionAssert.AreEqual(new RecycledPacked4?[] { new RecycledPacked4 { A = 5, B = 6 } }, recycled);
            NinoDeserializer.Deserialize(new byte[] { 0 }, ref value);
            Assert.AreEqual(1, recycled.Count);
        }
        finally
        {
            NinoSettings.SetRecycler<RecycledPacked4?>(null);
        }
    }
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct Packed4
{
    public int A;
    public long B;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct RecycledPacked4
{
    public int A;
    public long B;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 2)]
public struct Packed2
{
    public short A;
    public int B;
    public long C;
}

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct Packed1
{
    public byte A;
    public short B;
    public int C;
    public long D;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class NullableWordsHolder
{
    public Packed4? Four;
    public Packed2? Two;
    public Packed1? One;
    public long? Long;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class NullableHolder
{
    public int?[] Array;
    public List<int?> List;
    public List<System.DateTime?> Dates;
}
