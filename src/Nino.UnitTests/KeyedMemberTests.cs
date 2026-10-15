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

// Same prefix as V1, then diverges: the in-order fast path reads Id and Name, then falls back.
[NinoType(keyed: true)]
public sealed class KeyedV3
{
    public int Id;
    public string Name;
    public long Extra = -1;
    public float Old;
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

// A member renamed twice: Hp, then Health, then HitPoints.
[NinoType(keyed: true)]
public sealed class KeyedHpV1
{
    public int Hp;
    public string Name;
}

[NinoType(keyed: true)]
public sealed class KeyedHpV2
{
    [NinoFormerName("Hp")] public int Health;
    public string Name;
}

[NinoType(keyed: true)]
public sealed class KeyedHpV3
{
    public string Name;
    [NinoFormerName("Hp")] [NinoFormerName("Health")] public int HitPoints;
}

[NinoType(keyed: true, versionTolerant: true)]
public struct KeyedTolerantHpV1
{
    public int Hp;
    public string Name;
}

[NinoType(keyed: true, versionTolerant: true)]
public struct KeyedTolerantHpV2
{
    public string Name;
    [NinoFormerName("Hp")] public int Health;
}

[NinoType(keyed: true, trackReferences: true)]
public sealed class KeyedTrackedV1
{
    public TrackedLeaf Leaf;
    public TrackedLeaf Same;
    public KeyedTrackedV1 Self;
}

[NinoType(keyed: true, trackReferences: true)]
public sealed class KeyedTrackedV2
{
    [NinoFormerName("Leaf")] public TrackedLeaf First;
    [NinoFormerName("Same")] public TrackedLeaf Second;
    [NinoFormerName("Self")] public KeyedTrackedV2 Me;
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
    public void FallsBackAfterAMatchingPrefix()
    {
        var bytes = NinoSerializer.Serialize(new KeyedV1
            { Id = 7, Name = "nino", Items = new List<int> { 1 }, Old = 2.5f });
        var v3 = NinoDeserializer.Deserialize<KeyedV3>(bytes);
        Assert.AreEqual(7, v3.Id);
        Assert.AreEqual("nino", v3.Name);
        Assert.AreEqual(-1, v3.Extra);
        Assert.AreEqual(2.5f, v3.Old);
        CollectionAssert.AreEqual(new List<int> { 1 }, v3.Items);
    }

    [TestMethod]
    public void MissingMembersKeepTheirValueInPlace()
    {
        var bytes = NinoSerializer.Serialize(new KeyedV1 { Id = 1, Name = "a" });
        var target = new KeyedV2 { New = 5 };
        NinoDeserializer.Deserialize(bytes, ref target);
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

    [TestMethod]
    public void FormerNamesReadRenamedMembers()
    {
        var v1 = NinoSerializer.Serialize(new KeyedHpV1 { Hp = 12, Name = "n" });
        var v2 = NinoDeserializer.Deserialize<KeyedHpV2>(v1);
        Assert.AreEqual(12, v2.Health);
        Assert.AreEqual("n", v2.Name);

        // Every former name is accepted.
        var v3 = NinoDeserializer.Deserialize<KeyedHpV3>(v1);
        Assert.AreEqual(12, v3.HitPoints);
        Assert.AreEqual("n", v3.Name);
        v3 = NinoDeserializer.Deserialize<KeyedHpV3>(NinoSerializer.Serialize(new KeyedHpV2 { Health = 34, Name = "m" }));
        Assert.AreEqual(34, v3.HitPoints);
        Assert.AreEqual("m", v3.Name);
        var target = new KeyedHpV3 { HitPoints = -1 };
        NinoDeserializer.Deserialize(v1, ref target);
        Assert.AreEqual(12, target.HitPoints);
    }

    [TestMethod]
    public void FormerNamesAreNotWritten()
    {
        // Writes use the current name, so the renamed type round-trips and old code no longer sees the member.
        var v2 = new KeyedHpV2 { Health = 5, Name = "x" };
        var bytes = NinoSerializer.Serialize(v2);
        var back = NinoDeserializer.Deserialize<KeyedHpV2>(bytes);
        Assert.AreEqual(5, back.Health);
        Assert.AreEqual("x", back.Name);
        var old = NinoDeserializer.Deserialize<KeyedHpV1>(bytes);
        Assert.AreEqual(0, old.Hp);
        Assert.AreEqual("x", old.Name);

        var v3 = new KeyedHpV3 { HitPoints = int.MaxValue, Name = "y" };
        Assert.AreEqual(int.MaxValue,
            NinoDeserializer.Deserialize<KeyedHpV3>(NinoSerializer.Serialize(v3)).HitPoints);
    }

    [TestMethod]
    public void FormerNamesInVersionTolerantKeyedTypes()
    {
        var v2 = NinoDeserializer.Deserialize<KeyedTolerantHpV2>(
            NinoSerializer.Serialize(new KeyedTolerantHpV1 { Hp = 7, Name = "t" }));
        Assert.AreEqual(7, v2.Health);
        Assert.AreEqual("t", v2.Name);
        var arr = NinoDeserializer.Deserialize<KeyedTolerantHpV2[]>(NinoSerializer.Serialize(new[]
            { new KeyedTolerantHpV1 { Hp = 1 }, new KeyedTolerantHpV1 { Hp = 2, Name = "b" } }));
        Assert.AreEqual(2, arr.Length);
        Assert.AreEqual(1, arr[0].Health);
        Assert.AreEqual("b", arr[1].Name);
    }

    [TestMethod]
    public void FormerNamesKeepTrackedReferences()
    {
        var leaf = new TrackedLeaf { Name = "leaf" };
        var v1 = new KeyedTrackedV1 { Leaf = leaf, Same = leaf };
        v1.Self = v1;
        var v2 = NinoDeserializer.Deserialize<KeyedTrackedV2>(NinoSerializer.Serialize(v1));
        Assert.AreEqual("leaf", v2.First.Name);
        Assert.AreSame(v2.First, v2.Second);
        Assert.AreSame(v2, v2.Me);
    }
}
