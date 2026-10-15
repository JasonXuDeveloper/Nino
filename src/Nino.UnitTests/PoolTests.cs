using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[NinoType, NinoPooled]
public sealed partial class PooledPacket
{
    public static int Warmups, Returns;

    public int Id;
    public List<int> Values;
    public PooledChild Child;

    // Not serialized: must be cleared by the return hook.
    [NinoIgnore] public object Session;

    static partial void OnNinoPoolWarmup()
    {
        Warmups++;
        for (int i = 0; i < 2; i++)
            new PooledPacket().ReturnToPool();
    }

    partial void OnNinoPoolReturn()
    {
        Returns++;
        Session = null;
    }
}

[NinoType, NinoPooled]
public sealed partial class PooledChild
{
    public static int Returns;

    public string Name;

    static partial void OnNinoPoolWarmup()
    {
    }

    partial void OnNinoPoolReturn() => Returns++;
}

[NinoType]
public class PooledOwner
{
    public PooledChild Child;
    public List<PooledChild> Children;
}

[TestClass]
[DoNotParallelize]
public class PoolTests
{
    [TestInitialize]
    public void Reset()
    {
        NinoPool<PooledPacket>.Clear();
        NinoPool<PooledChild>.Clear();
    }

    private static byte[] Packet(int id, params int[] values) => NinoSerializer.Serialize(new PooledPacket
        { Id = id, Values = new List<int>(values), Child = new PooledChild { Name = "c" + id } });

    [TestMethod]
    public void WarmupRunsOnceBeforeFirstDeserialization()
    {
        var first = NinoDeserializer.Deserialize<PooledPacket>(Packet(1, 1));
        NinoDeserializer.Deserialize<PooledPacket>(Packet(2, 2));
        Assert.AreEqual(1, PooledPacket.Warmups);
        Assert.AreEqual(1, first.Id);
    }

    [TestMethod]
    public void ReturnedInstanceAndItsGraphAreReused()
    {
        var packet = NinoDeserializer.Deserialize<PooledPacket>(Packet(1, 1, 2, 3));
        var values = packet.Values;
        var child = packet.Child;
        packet.Session = new object();
        packet.ReturnToPool();
        Assert.IsNull(packet.Session, "the return hook runs");

        var next = NinoDeserializer.Deserialize<PooledPacket>(Packet(2, 4, 5));
        Assert.AreSame(packet, next);
        Assert.AreSame(values, next.Values);
        Assert.AreSame(child, next.Child);
        Assert.AreEqual(2, next.Id);
        CollectionAssert.AreEqual(new[] { 4, 5 }, next.Values);
        Assert.AreEqual("c2", next.Child.Name);
    }

    [TestMethod]
    public void EmptyPoolCreatesInstances()
    {
        NinoDeserializer.Deserialize<PooledPacket>(Packet(0)); // warmup happened in another test or now
        NinoPool<PooledPacket>.Clear();
        var a = NinoDeserializer.Deserialize<PooledPacket>(Packet(1));
        var b = NinoDeserializer.Deserialize<PooledPacket>(Packet(2));
        Assert.AreNotSame(a, b);
        Assert.AreEqual(2, b.Id);
    }

    [TestMethod]
    public void ReturningTwiceThrows()
    {
        var packet = NinoDeserializer.Deserialize<PooledPacket>(Packet(1));
        packet.ReturnToPool();
        Assert.ThrowsException<InvalidOperationException>(() => packet.ReturnToPool());

        // Once rented again it can be returned again.
        var again = NinoDeserializer.Deserialize<PooledPacket>(Packet(2));
        Assert.AreSame(packet, again);
        again.ReturnToPool();
    }

    [TestMethod]
    public void NullInDataReturnsTheDroppedInstance()
    {
        var owner = new PooledOwner { Child = new PooledChild { Name = "a" } };
        var child = owner.Child;
        int returns = PooledChild.Returns;
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PooledOwner()), ref owner);
        Assert.IsNull(owner.Child);
        Assert.AreEqual(returns + 1, PooledChild.Returns);
        Assert.AreSame(child, NinoDeserializer.Deserialize<PooledChild>(NinoSerializer.Serialize(new PooledChild())));
    }

    [TestMethod]
    public void ShrinkingCollectionReturnsDroppedElements()
    {
        var owner = new PooledOwner
            { Children = new List<PooledChild> { new() { Name = "a" }, new() { Name = "b" }, new() { Name = "c" } } };
        var dropped = owner.Children[2];
        var data = NinoSerializer.Serialize(new PooledOwner
            { Children = new List<PooledChild> { new() { Name = "x" }, new() { Name = "y" } } });
        NinoDeserializer.Deserialize(data, ref owner);
        Assert.AreEqual(2, owner.Children.Count);
        Assert.AreSame(dropped, NinoDeserializer.Deserialize<PooledChild>(NinoSerializer.Serialize(new PooledChild())));
    }

    [TestMethod]
    public void ElementsComeFromThePool()
    {
        var reused = new PooledChild();
        reused.ReturnToPool();
        var owner = NinoDeserializer.Deserialize<PooledOwner>(NinoSerializer.Serialize(new PooledOwner
            { Children = new List<PooledChild> { new() { Name = "x" } } }));
        Assert.AreSame(reused, owner.Children[0]);
        Assert.AreEqual("x", reused.Name);
    }

    [TestMethod]
    public void ConcurrentRentAndReturn()
    {
        var data = Packet(7, 1, 2, 3);
        Parallel.For(0, 64, _ =>
        {
            var seen = new HashSet<PooledPacket>();
            for (int i = 0; i < 1000; i++)
            {
                var packet = NinoDeserializer.Deserialize<PooledPacket>(data);
                Assert.AreEqual(7, packet.Id);
                Assert.AreEqual(3, packet.Values.Count);
                packet.ReturnToPool();
                seen.Add(packet);
            }
        });
        Assert.IsTrue(NinoPool<PooledPacket>.Count <= NinoPool<PooledPacket>.MaxRetained);
    }
}
