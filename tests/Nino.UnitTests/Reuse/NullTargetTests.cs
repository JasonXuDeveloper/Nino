using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.UserCollections;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// An existing instance whose members are null: the reuse read of the outer instance reads each member into a null
/// slot, through its formatter (the <c>__Shallow</c> twin where the member has one).
/// </summary>
[NinoType]
public sealed class NullTargetOuter
{
    public int Id;
    public AliasHolder Alias;
    public TrackedAliasHolder Tracked;
    public TrackedAliasHolder TrackedAgain;
    public PooledPacket Packet;
    public HierarchicalBase Base;
}

/// <summary>
/// Reads into a slot that holds null, through every public entry point that takes an existing value. The types are
/// sealed classes whose generated read is split in two (a body for a new instance and one for an existing one, chosen
/// by <c>ReadBody</c>): plain, tracked, pooled with an empty pool and with a returned instance, and a subtype read
/// through its base's slot.
/// </summary>
[TestClass]
[DoNotParallelize]
public class NullTargetTests
{
    [TestInitialize]
    public void Reset()
    {
        NinoPool<PooledPacket>.Shared.Clear();
        NinoPool<PooledChild>.Shared.Clear();
    }

    private static AliasHolder Alias(int seed) => new()
    {
        First = new RawList { seed, seed + 1 },
        Second = new RawList { seed + 2 },
    };

    private static TrackedAliasHolder Tracked(int seed)
    {
        var shared = new RawList { seed, seed + 1 };
        return new TrackedAliasHolder { First = shared, Second = shared };
    }

    private static PooledPacket Packet(int seed) => new()
        { Id = seed, Values = new List<int> { seed, seed * 2 }, Child = new PooledChild { Name = "c" + seed } };

    private static HierarchicalSub2 Sub(int seed) => new() { B = "b" + seed, D = seed, F = new List<int> { seed } };

    private static void AssertAlias(AliasHolder v, int seed)
    {
        Assert.IsNotNull(v);
        CollectionAssert.AreEqual(new[] { seed, seed + 1 }, v.First);
        CollectionAssert.AreEqual(new[] { seed + 2 }, v.Second);
    }

    private static void AssertTracked(TrackedAliasHolder v, int seed)
    {
        Assert.IsNotNull(v);
        // Tracking covers the holder, not the lists it holds (UserCollectionCornerCaseTests).
        CollectionAssert.AreEqual(new[] { seed, seed + 1 }, v.First);
        CollectionAssert.AreEqual(new[] { seed, seed + 1 }, v.Second);
    }

    private static void AssertPacket(PooledPacket v, int seed)
    {
        Assert.IsNotNull(v);
        Assert.AreEqual(seed, v.Id);
        CollectionAssert.AreEqual(new[] { seed, seed * 2 }, v.Values);
        Assert.AreEqual("c" + seed, v.Child.Name);
    }

    private static void AssertSub(HierarchicalBase v, int seed)
    {
        Assert.IsInstanceOfType(v, typeof(HierarchicalSub2));
        var sub = (HierarchicalSub2)v;
        Assert.AreEqual("b" + seed, sub.B);
        Assert.AreEqual(seed, sub.D);
        CollectionAssert.AreEqual(new[] { seed }, sub.F);
    }

    /// <summary>
    /// Reads <paramref name="data"/> into a null slot through each entry point, then checks the result and that a
    /// second read reuses it.
    /// </summary>
    private static void EveryEntryPoint<T>(byte[] data, System.Action<T> check) where T : class
    {
        T value = null;
        NinoDeserializer.Deserialize(data, ref value);
        check(value);

        value = null;
        var reader = new Reader(data);
        NinoDeserializer.Deserialize(ref value, ref reader);
        check(value);

        object boxed = null;
        NinoDeserializer.DeserializeBoxed(data, ref boxed, typeof(T));
        check((T)boxed);

        boxed = null;
        reader = new Reader(data);
        NinoDeserializer.DeserializeBoxed(ref boxed, ref reader, typeof(T));
        check((T)boxed);

        check(NinoDeserializer.Deserialize<T>(data));
        reader = new Reader(data);
        check(NinoDeserializer.Deserialize<T>(ref reader));
        check((T)NinoDeserializer.DeserializeBoxed(data, typeof(T)));

        // Into the instance just read: the reuse body.
        var existing = value;
        NinoDeserializer.Deserialize(data, ref value);
        Assert.AreSame(existing, value);
        check(value);
    }

    [TestMethod]
    public void PlainIntoNull() =>
        EveryEntryPoint<AliasHolder>(NinoSerializer.Serialize(Alias(3)), v => AssertAlias(v, 3));

    [TestMethod]
    public void TrackedIntoNull() =>
        EveryEntryPoint<TrackedAliasHolder>(NinoSerializer.Serialize(Tracked(4)), v => AssertTracked(v, 4));

    [TestMethod]
    public void PooledIntoNullWithEmptyPool() =>
        EveryEntryPoint<PooledPacket>(NinoSerializer.Serialize(Packet(5)), v =>
        {
            AssertPacket(v, 5);
            NinoPool<PooledPacket>.Shared.Clear();
        });

    [TestMethod]
    public void PooledIntoNullWithReturnedInstance()
    {
        // A null slot takes the returned instance from the pool and reads it as an existing one.
        var data = NinoSerializer.Serialize(Packet(6));
        var returned = NinoDeserializer.Deserialize<PooledPacket>(NinoSerializer.Serialize(Packet(1)));
        returned.ReturnToPool();
        PooledPacket slot = null;
        NinoDeserializer.Deserialize(data, ref slot);
        Assert.AreSame(returned, slot);
        AssertPacket(slot, 6);

        slot.ReturnToPool();
        PooledPacket other = null;
        var reader = new Reader(data);
        NinoDeserializer.Deserialize(ref other, ref reader);
        Assert.AreSame(returned, other);
        AssertPacket(other, 6);

        other.ReturnToPool();
        object boxed = null;
        NinoDeserializer.DeserializeBoxed(data, ref boxed, typeof(PooledPacket));
        Assert.AreSame(returned, boxed);
        AssertPacket((PooledPacket)boxed, 6);
    }

    [TestMethod]
    public void SubtypeIntoNullBaseSlot()
    {
        var data = NinoSerializer.Serialize<HierarchicalBase>(Sub(7));
        EveryEntryPoint<HierarchicalBase>(data, v => AssertSub(v, 7));
        EveryEntryPoint<HierarchicalSub2>(NinoSerializer.Serialize(Sub(8)), v => AssertSub(v, 8));
    }

    [TestMethod]
    public void NullMembersOfExistingInstance()
    {
        var tracked = Tracked(9);
        var data = NinoSerializer.Serialize(new NullTargetOuter
        {
            Id = 9, Alias = Alias(9), Tracked = tracked, TrackedAgain = tracked, Packet = Packet(9), Base = Sub(9),
        });
        var outer = new NullTargetOuter();
        var same = outer;
        NinoDeserializer.Deserialize(data, ref outer);
        Assert.AreSame(same, outer);
        Assert.AreEqual(9, outer.Id);
        AssertAlias(outer.Alias, 9);
        AssertTracked(outer.Tracked, 9);
        Assert.AreSame(outer.Tracked, outer.TrackedAgain);
        AssertPacket(outer.Packet, 9);
        AssertSub(outer.Base, 9);

        // Again with the members' instances in place: the reuse bodies.
        var alias = outer.Alias;
        var packet = outer.Packet;
        NinoDeserializer.Deserialize(data, ref outer);
        Assert.AreSame(alias, outer.Alias);
        Assert.AreSame(packet, outer.Packet);
        AssertAlias(outer.Alias, 9);
        AssertPacket(outer.Packet, 9);
    }
}
