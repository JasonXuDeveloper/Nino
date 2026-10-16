using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Fixed;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Exact-size serialization of fixed-size types: NinoSerializer.Serialize(T) to byte[] (and the NinoDirect.SerializeFixed
/// call the interceptors route it to) allocates the array at its final size and writes it in place. The bytes must
/// equal the regular writer's.
/// </summary>
[TestClass]
public class FixedSizeTests
{
    private static FixedIgnored Ignored() => new FixedIgnored { A = -5, Skipped = 99, B = 0x0102030405060708, C = true };

    private static FixedNested Nested() => new FixedNested
    {
        Tag = 7, G = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"), S = -2, Inner = Ignored()
    };

    private static FixedSealed Sealed() => new FixedSealed
    {
        Id = 42, Nested = Nested(), D = 1.25, T = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc)
    };

    private static byte[] ViaWriter<T>(T value)
    {
        var buffer = new NinoArrayBufferWriter(1);
        NinoSerializer.Serialize(value, buffer);
        return buffer.WrittenSpan.ToArray();
    }

    private static void Check<T>(T value, byte[] direct, int size)
    {
        var expected = ViaWriter(value);
        Assert.AreEqual(size, expected.Length);
        CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(value));
        CollectionAssert.AreEqual(expected, NinoSerializer.Serialize((object)value));
        CollectionAssert.AreEqual(expected, direct);
    }

    [TestMethod]
    public void FixedSizes()
    {
        Assert.AreEqual(13, NinoSerializer.GetFixedSize<FixedIgnored>());
        Assert.AreEqual(32, NinoSerializer.GetFixedSize<FixedNested>());
        Assert.AreEqual(53, NinoSerializer.GetFixedSize<FixedSealed>());
        Assert.AreEqual(8, NinoSerializer.GetFixedSize<long>());
        Assert.AreEqual(16, NinoSerializer.GetFixedSize<Guid>());
        Assert.AreEqual(-1, NinoSerializer.GetFixedSize<NotFixedSealed>());
        Assert.AreEqual(-1, NinoSerializer.GetFixedSize<NotFixedOpen>());
        Assert.AreEqual(-1, NinoSerializer.GetFixedSize<string>());
        Assert.AreEqual(-1, NinoSerializer.GetFixedSize<int[]>());
        // This assembly defines WEAK_VERSION_TOLERANCE: its types carry a length and are never fixed size.
        Assert.AreEqual(-1, NinoSerializer.GetFixedSize<Corner.VerSealedLeafV1>());
    }

    [TestMethod]
    public void StructWithIgnoredField()
    {
        var value = Ignored();
        Check(value, FixedDirect.Serialize(value), 13);
        var back = NinoDeserializer.Deserialize<FixedIgnored>(FixedDirect.Serialize(value));
        Assert.AreEqual(value.A, back.A);
        Assert.AreEqual(value.B, back.B);
        Assert.AreEqual(value.C, back.C);
        Assert.AreEqual(0, back.Skipped);
    }

    [TestMethod]
    public void NestedStruct()
    {
        var value = Nested();
        Check(value, FixedDirect.Serialize(value), 32);
        var back = NinoDeserializer.Deserialize<FixedNested>(FixedDirect.Serialize(value));
        Assert.AreEqual(value.G, back.G);
        Assert.AreEqual(value.S, back.S);
        Assert.AreEqual(value.Inner.B, back.Inner.B);
    }

    [TestMethod]
    public void SealedClass()
    {
        var value = Sealed();
        Check(value, FixedDirect.Serialize(value), 53);
        var bytes = FixedDirect.Serialize(value);
        Assert.AreEqual(1, bytes[0]);
        var back = NinoDeserializer.Deserialize<FixedSealed>(bytes);
        Assert.AreEqual(value.Id, back.Id);
        Assert.AreEqual(value.Nested.Inner.A, back.Nested.Inner.A);
        Assert.AreEqual(value.D, back.D);
        Assert.AreEqual(value.T, back.T);
    }

    [TestMethod]
    public void NullSealedClass()
    {
        // The not-null flag alone.
        CollectionAssert.AreEqual(new byte[] { 0 }, ViaWriter<FixedSealed>(null));
        CollectionAssert.AreEqual(new byte[] { 0 }, NinoSerializer.Serialize<FixedSealed>(null));
        CollectionAssert.AreEqual(new byte[] { 0 }, FixedDirect.Serialize((FixedSealed)null));
        Assert.IsNull(NinoDeserializer.Deserialize<FixedSealed>(new byte[] { 0 }));
    }

    [TestMethod]
    public void ArraysOfFixedTypesAreNotAffected()
    {
        var array = new[] { Sealed(), null, Sealed() };
        CollectionAssert.AreEqual(ViaWriter(array), NinoSerializer.Serialize(array));
        Assert.AreEqual(3, NinoDeserializer.Deserialize<FixedSealed[]>(NinoSerializer.Serialize(array)).Length);
    }
}
