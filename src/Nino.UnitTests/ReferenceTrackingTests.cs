using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[NinoType(trackReferences: true)]
public class TrackedNode
{
    public int Value;
    public TrackedNode Next;
    public List<TrackedNode> Children;
}

[NinoType(trackReferences: true)]
public sealed class TrackedLeaf
{
    public string Name;
}

[NinoType(trackReferences: true)]
public abstract class TrackedShape
{
    public TrackedShape Parent;
}

[NinoType]
public class TrackedCircle : TrackedShape
{
    public float Radius;
}

[NinoType]
public class TrackedSquare : TrackedShape
{
    public float Side;
}

// Untracked holders whose bodies are sliced: references must stay shared across the slices.
[NinoType(versionTolerant: true)]
public class TolerantHolder
{
    public TrackedLeaf A;
    public TrackedLeaf B;
    public List<TrackedLeaf> More;
}

[NinoType(keyed: true)]
public class KeyedHolder
{
    public TrackedLeaf A;
    public int Number;
    public TrackedLeaf B;
}

[NinoType]
public class PlainHolder
{
    public TolerantHolder First;
    public TrackedLeaf Again;
}

[TestClass]
public class ReferenceTrackingTests
{
    private static T RoundTrip<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

    [TestMethod]
    public void SharedReferencesStayShared()
    {
        var leaf = new TrackedLeaf { Name = "x" };
        var list = RoundTrip(new List<TrackedLeaf> { leaf, null, leaf, new TrackedLeaf { Name = "y" } });
        Assert.AreSame(list[0], list[2]);
        Assert.IsNull(list[1]);
        Assert.AreEqual("x", list[0].Name);
        Assert.AreEqual("y", list[3].Name);
        Assert.AreNotSame(list[0], list[3]);
    }

    [TestMethod]
    public void SharedReferencesAreWrittenOnce()
    {
        var leaf = new TrackedLeaf { Name = new string('a', 100) };
        int once = NinoSerializer.Serialize(new List<TrackedLeaf> { leaf }).Length;
        int twice = NinoSerializer.Serialize(new List<TrackedLeaf> { leaf, leaf }).Length;
        Assert.AreEqual(once + 4, twice);
    }

    [TestMethod]
    public void CyclesRoundTrip()
    {
        var a = new TrackedNode { Value = 1 };
        var b = new TrackedNode { Value = 2, Next = a };
        a.Next = b;
        a.Children = new List<TrackedNode> { a, b };

        var r = RoundTrip(a);
        Assert.AreEqual(1, r.Value);
        Assert.AreEqual(2, r.Next.Value);
        Assert.AreSame(r, r.Next.Next);
        Assert.AreSame(r, r.Children[0]);
        Assert.AreSame(r.Next, r.Children[1]);
    }

    [TestMethod]
    public void PolymorphicTrackedTypes()
    {
        var root = new TrackedSquare { Side = 2 };
        var circle = new TrackedCircle { Radius = 1, Parent = root };
        root.Parent = circle;
        var list = RoundTrip(new List<TrackedShape> { circle, root, circle });
        var c = (TrackedCircle)list[0];
        var s = (TrackedSquare)list[1];
        Assert.AreSame(c, list[2]);
        Assert.AreSame(s, c.Parent);
        Assert.AreSame(c, s.Parent);
        Assert.AreEqual(1, c.Radius);
        Assert.AreEqual(2, s.Side);
    }

    [TestMethod]
    public void ReferencesSurviveSlicedBodies()
    {
        var leaf = new TrackedLeaf { Name = "shared" };
        var holder = new PlainHolder
        {
            First = new TolerantHolder { A = leaf, B = leaf, More = new List<TrackedLeaf> { leaf } },
            Again = leaf,
        };
        var r = RoundTrip(holder);
        Assert.AreEqual("shared", r.Again.Name);
        Assert.AreSame(r.Again, r.First.A);
        Assert.AreSame(r.Again, r.First.B);
        Assert.AreSame(r.Again, r.First.More[0]);

        var k = RoundTrip(new KeyedHolder { A = leaf, Number = 3, B = leaf });
        Assert.AreSame(k.A, k.B);
        Assert.AreEqual(3, k.Number);

        var list = RoundTrip(new List<KeyedHolder> { new() { A = leaf }, new() { B = leaf } });
        Assert.AreSame(list[0].A, list[1].B);
    }

    [TestMethod]
    public void DeserializeRefReusesInstances()
    {
        var bytes = NinoSerializer.Serialize(new TrackedNode { Value = 5, Next = new TrackedNode { Value = 6 } });
        var target = new TrackedNode { Next = new TrackedNode() };
        var next = target.Next;
        NinoDeserializer.DeserializeRef(ref target, bytes);
        Assert.AreEqual(5, target.Value);
        Assert.AreSame(next, target.Next);
        Assert.AreEqual(6, next.Value);
    }
}
