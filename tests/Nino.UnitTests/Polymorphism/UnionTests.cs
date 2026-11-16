#if NET11_0_OR_GREATER
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

[NinoType]
internal union UnionValue(int, string);

[NinoType]
internal union UnionReordered(string, int);

[NinoType]
internal union UnionRenamedCase(UnionRenamed, int);

[NinoType, NinoFormerName("Nino.UnitTests.UnionOriginal")]
internal sealed class UnionRenamed
{
    public int Value;
}

[NinoType]
internal union UnionOption<T>(UnionNone, UnionSome<T>);

[NinoType]
internal sealed class UnionNone
{
}

[NinoType]
internal sealed class UnionSome<T>
{
    public T Value;
}

[NinoType]
internal union UnionNullableCase(int?, string);

[NinoType]
internal union UnionObjectCase(object, string);

[NinoType]
internal union UnionAnimalCase(UnionAnimal, int);

[NinoType]
internal abstract class UnionAnimal
{
}

internal sealed class UnionDog : UnionAnimal
{
    public int Age;
}

[NinoType]
internal union RecursiveUnion(RecursiveUnion[], int);

[NinoType]
internal sealed class UnionHolder
{
    public List<UnionOption<UnionValue>> Values;
}

[TestClass]
public class UnionTests
{
    [TestMethod]
    public void CaseTypeIdsAndDefaultHaveStableBytes()
    {
        byte[] number = NinoSerializer.Serialize(new UnionValue(42));
        CollectionAssert.AreEqual(new byte[] { 0xA4, 0xA2, 0x97, 0x20, 42, 0, 0, 0 }, number);
        Assert.AreEqual(42, NinoDeserializer.Deserialize<UnionValue>(number).Value);
        Assert.AreEqual(42, NinoDeserializer.Deserialize<UnionReordered>(number).Value);
        CollectionAssert.AreEqual(number, NinoSerializer.Serialize(new UnionReordered(42)));

        byte[] text = NinoSerializer.Serialize(new UnionValue("Nino"));
        Assert.AreEqual(NinoTypeId.Of(typeof(string)), BinaryPrimitives.ReadInt32LittleEndian(text));
        Assert.AreEqual("Nino", NinoDeserializer.Deserialize<UnionValue>(text).Value);

        byte[] empty = NinoSerializer.Serialize(default(UnionValue));
        CollectionAssert.AreEqual(new byte[4], empty);
        UnionValue reused = new("previous value");
        NinoDeserializer.Deserialize(empty, ref reused);
        Assert.IsNull(reused.Value);
    }

    [TestMethod]
    public void GenericCasesWorkInMembersAndCollections()
    {
        var holder = new UnionHolder
        {
            Values = new()
            {
                new(new UnionSome<UnionValue> { Value = new UnionValue(42) }),
                new(new UnionNone()),
                default,
            },
        };
        byte[] option = NinoSerializer.Serialize(holder.Values[0]);
        Assert.AreEqual(NinoTypeId.Of(typeof(UnionSome<UnionValue>)), BinaryPrimitives.ReadInt32LittleEndian(option));
        var result = NinoDeserializer.Deserialize<UnionHolder>(NinoSerializer.Serialize(holder));
        Assert.AreEqual(3, result.Values.Count);
        var some = (UnionSome<UnionValue>)result.Values[0].Value;
        Assert.AreEqual(42, some.Value.Value);
        Assert.IsInstanceOfType<UnionNone>(result.Values[1].Value);
        Assert.IsNull(result.Values[2].Value);
    }

    [TestMethod]
    public void FormerNameDeterminesTheCaseTypeId()
    {
        var source = new UnionRenamedCase(new UnionRenamed { Value = 42 });
        byte[] bytes = NinoSerializer.Serialize(source);
        Assert.AreEqual(NinoTypeId.Compute("Nino.UnitTests.UnionOriginal"), BinaryPrimitives.ReadInt32LittleEndian(bytes));
        var result = (UnionRenamed)NinoDeserializer.Deserialize<UnionRenamedCase>(bytes).Value;
        Assert.AreEqual(42, result.Value);
    }

    [TestMethod]
    public void NullableAndOverlappingCasesUseTheirOwnFormatters()
    {
        var number = new UnionNullableCase((int?)42);
        byte[] nullable = NinoSerializer.Serialize(number);
        Assert.AreEqual(NinoTypeId.Of(typeof(int?)), BinaryPrimitives.ReadInt32LittleEndian(nullable));
        Assert.AreEqual(42, NinoDeserializer.Deserialize<UnionNullableCase>(nullable).Value);
        CollectionAssert.AreEqual(new byte[4], NinoSerializer.Serialize(new UnionNullableCase((int?)null)));

        var text = new UnionObjectCase((object)"Nino");
        byte[] bytes = NinoSerializer.Serialize(text);
        Assert.AreEqual(NinoTypeId.Of(typeof(string)), BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.AreEqual("Nino", NinoDeserializer.Deserialize<UnionObjectCase>(bytes).Value);

        byte[] boxed = NinoSerializer.SerializeBoxed(new UnionValue(42), typeof(UnionValue));
        var result = (UnionValue)NinoDeserializer.DeserializeBoxed(boxed, typeof(UnionValue));
        Assert.AreEqual(42, result.Value);

        byte[] polymorphic = NinoSerializer.Serialize<object>(new UnionValue(42));
        var throughObject = (UnionValue)NinoDeserializer.Deserialize<object>(polymorphic);
        Assert.AreEqual(42, throughObject.Value);
    }

    [TestMethod]
    public void InvalidCasesAndTruncatedPayloadsThrow()
    {
        var bytes = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, NinoTypeId.Of(typeof(double)));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<UnionValue>(bytes));
        BinaryPrimitives.WriteInt32LittleEndian(bytes, NinoTypeId.Of(typeof(int)));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<UnionValue>(bytes));
        BinaryPrimitives.WriteInt32LittleEndian(bytes, NinoTypeId.Of(typeof(string)));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<UnionValue>(bytes));
    }

    [TestMethod]
    public void AbstractCaseKeepsTheDerivedPayload()
    {
        var value = new UnionAnimalCase(new UnionDog { Age = 4 });
        byte[] bytes = NinoSerializer.Serialize(value);
        Assert.AreEqual(NinoTypeId.Of(typeof(UnionAnimal)), BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.AreEqual(NinoTypeId.Of(typeof(UnionDog)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)));
        var copy = NinoDeserializer.Deserialize<UnionAnimalCase>(bytes);
        Assert.IsInstanceOfType<UnionDog>(copy.Value);
        Assert.AreEqual(4, ((UnionDog)copy.Value).Age);
    }

    [TestMethod]
    public void RecursiveCasesRoundTrip()
    {
        var source = new RecursiveUnion(new[] { new RecursiveUnion(42), default });
        var result = NinoDeserializer.Deserialize<RecursiveUnion[]>(NinoSerializer.Serialize(new[] { source, default }));
        var children = (RecursiveUnion[])result[0].Value;
        Assert.AreEqual(42, children[0].Value);
        Assert.IsNull(children[1].Value);
        Assert.IsNull(result[1].Value);
    }

    [TestMethod]
    public void RecursiveCasesUseTheDepthLimit()
    {
        var value = new RecursiveUnion(42);
        byte[] prefix = NinoSerializer.Serialize(new RecursiveUnion(new[] { default(RecursiveUnion) }))[..^4];
        byte[] leaf = NinoSerializer.Serialize(value);
        int levels = NinoSettings.DefaultMaxDepth + 1;
        var bytes = new byte[levels * prefix.Length + leaf.Length];
        for (int i = 0; i < levels; i++)
        {
            value = new RecursiveUnion(new[] { value });
            prefix.CopyTo(bytes, i * prefix.Length);
        }
        leaf.CopyTo(bytes, levels * prefix.Length);

        Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(value));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<RecursiveUnion>(bytes));
    }
}
#endif
