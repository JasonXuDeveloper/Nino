using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// Two versions of the same keyed schema: V2 reorders members, removes Old and inserts New.
[NinoType(keyed: true)]
public sealed class KeyedV1
{
    public int Id;
    public string Name;
    public List<int> Items;
    public float Old;
}

[NinoType(keyed: true)]
public sealed class KeyedV2
{
    public string Name;
    public double New = 42;
    public int Id;
    public List<int> Items;
}

[NinoType(keyed: true)]
public struct KeyedStruct
{
    public int A;
    public long B;
    public string C;
}

[NinoType(keyed: true)]
public sealed record KeyedRecord(int Id, string Name);

[NinoType(keyed: true)]
public sealed record KeyedRecordV2(string Name, int Extra, int Id);

[NinoType(keyed: true)]
public class KeyedBase
{
    public int BaseValue;
}

[NinoType(keyed: true)]
public class KeyedDerived : KeyedBase
{
    public string DerivedValue;
}

[TestClass]
public class KeyedMemberTests
{
    [TestMethod]
    public void RoundTrips()
    {
        var value = new KeyedV1 { Id = 7, Name = "nino", Items = new List<int> { 1, 2, 3 }, Old = 1.5f };
        var result = NinoDeserializer.Deserialize<KeyedV1>(NinoSerializer.Serialize(value));
        Assert.AreEqual(7, result.Id);
        Assert.AreEqual("nino", result.Name);
        CollectionAssert.AreEqual(value.Items, result.Items);
        Assert.AreEqual(1.5f, result.Old);

        var s = new KeyedStruct { A = 1, B = long.MaxValue, C = "c" };
        Assert.AreEqual(s, NinoDeserializer.Deserialize<KeyedStruct>(NinoSerializer.Serialize(s)));

        var r = new KeyedRecord(3, "r");
        Assert.AreEqual(r, NinoDeserializer.Deserialize<KeyedRecord>(NinoSerializer.Serialize(r)));
    }

    [TestMethod]
    public void ReadsReorderedRemovedAndInsertedMembers()
    {
        var bytes = NinoSerializer.Serialize(new KeyedV1
            { Id = 7, Name = "nino", Items = new List<int> { 1, 2 }, Old = 1.5f });
        var v2 = NinoDeserializer.Deserialize<KeyedV2>(bytes);
        Assert.AreEqual(7, v2.Id);
        Assert.AreEqual("nino", v2.Name);
        CollectionAssert.AreEqual(new List<int> { 1, 2 }, v2.Items);
        Assert.AreEqual(42, v2.New); // missing: keeps the constructed value

        var back = NinoDeserializer.Deserialize<KeyedV1>(NinoSerializer.Serialize(new KeyedV2
            { Id = 9, Name = "x", New = 3, Items = null }));
        Assert.AreEqual(9, back.Id);
        Assert.AreEqual("x", back.Name);
        Assert.IsNull(back.Items);
        Assert.AreEqual(0f, back.Old);
    }

    [TestMethod]
    public void MissingMembersKeepTheirValueInPlace()
    {
        var bytes = NinoSerializer.Serialize(new KeyedV1 { Id = 1, Name = "a" });
        var target = new KeyedV2 { New = 5 };
        NinoDeserializer.DeserializeRef(ref target, bytes);
        Assert.AreEqual(1, target.Id);
        Assert.AreEqual(5, target.New);
    }

    [TestMethod]
    public void RecreatedTypesUseDefaultsForMissingMembers()
    {
        var v2 = NinoDeserializer.Deserialize<KeyedRecordV2>(NinoSerializer.Serialize(new KeyedRecord(4, "n")));
        Assert.AreEqual(new KeyedRecordV2("n", 0, 4), v2);
    }

    [TestMethod]
    public void InheritedKeyedMembersRoundTrip()
    {
        KeyedBase value = new KeyedDerived { BaseValue = 1, DerivedValue = "d" };
        var result = (KeyedDerived)NinoDeserializer.Deserialize<KeyedBase>(NinoSerializer.Serialize(value));
        Assert.AreEqual(1, result.BaseValue);
        Assert.AreEqual("d", result.DerivedValue);
    }
}
