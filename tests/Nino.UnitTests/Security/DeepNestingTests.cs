using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Deep;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public sealed class DeepLink
{
    public int Value;
    public DeepLink Next;
}

[NinoType]
public class DeepTree
{
    public int Value;
    public List<DeepTree> Children;
}

[NinoType]
public class DeepTreeLeaf : DeepTree
{
    public string Tag;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class DeepTolerant
{
    public int Value;
    public DeepTolerant Next;
    public string Name;
}

[NinoType(format: NinoFormat.Keyed)]
public sealed class DeepKeyed
{
    public int Value;
    public DeepKeyed Next;
}

[NinoType(trackReferences: true)]
public sealed class DeepTracked
{
    public int Value;
    public DeepTracked Next;
    public DeepTracked Prev;
}

[NinoType]
public sealed class DeepObject
{
    public int Value;
    public object Next;
}

[NinoType]
public sealed record DeepRecord(int Value, DeepRecord Next);

[NinoType]
public sealed class DeepHashed
{
    public int Value;
    public DeepHashed Next;

    public override int GetHashCode() => Value;

    public override bool Equals(object obj) => obj is DeepHashed other && other.Value == Value;
}

[NinoType(format: NinoFormat.Keyed)]
public struct DeepKeyedRoot
{
    public int Before;
    public DeepLink Head;
    public int After;
}

// Two versions of one type, for readers that skip members they do not know.
[NinoType(format: NinoFormat.Keyed)]
public sealed class DeepKeyedV1
{
    public int A;
    public DeepLink Known;
    public int B;
}

[NinoType(format: NinoFormat.Keyed)]
public sealed class DeepKeyedV2
{
    public int A;
    public DeepLink Extra;
    public DeepLink Known;
    public int B;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class DeepLevelV1
{
    public int A;
    public DeepLink Known;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class DeepLevelV2
{
    public int A;
    public DeepLink Known;
    public DeepLink Extra;
    public int B;
}

// A class with no class members (it nests nothing): its formatter skips the depth count, which must not change
// the bytes or where the bodies around it are deferred.
[NinoType]
public sealed class DeepPayload
{
    public int A;
    public string S;
    public List<int> Items;
}

[NinoType]
public sealed class DeepCarrier
{
    public int Value;
    public DeepPayload Payload;
    public List<DeepPayload> Payloads;
    public DeepCarrier Next;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class DeepTolerantCarrier
{
    public int Value;
    public DeepPayload Payload;
    public DeepTolerantCarrier Next;
}

/// <summary>
/// Graphs nested deeper than the stack could hold: bodies below NinoDepth.Inline are deferred, so writing and
/// reading take a bounded stack (checked on threads with a small one).
/// </summary>
[TestClass]
public class DeepNestingTests
{
    private const int SmallStack = 512 * 1024;
    private const int NinoDepthInline = 128;

    private static void OnSmallStack(Action action)
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, SmallStack);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new AssertFailedException("failed on the small stack: " + error, error);
    }

    private static PlainLink PlainChain(int length, int seed = 0)
    {
        PlainLink head = null;
        for (int i = length - 1; i >= 0; i--)
            head = new PlainLink { Value = seed + i, Next = head };
        return head;
    }

    private static void AssertPlainChain(PlainLink head, int length, int seed = 0)
    {
        int i = 0;
        for (var node = head; node != null; node = node.Next, i++)
            Assert.AreEqual(seed + i, node.Value);
        Assert.AreEqual(length, i);
    }

    private static DeepLink Chain(int length, int seed = 0)
    {
        DeepLink head = null;
        for (int i = length - 1; i >= 0; i--)
            head = new DeepLink { Value = seed + i, Next = head };
        return head;
    }

    private static void AssertChain(DeepLink head, int length, int seed = 0)
    {
        int i = 0;
        for (var node = head; node != null; node = node.Next, i++)
            Assert.AreEqual(seed + i, node.Value);
        Assert.AreEqual(length, i);
    }

    [TestMethod]
    public void ShallowDataIsUnchanged()
    {
        // Less than NinoDepth.Inline deep: the bytes are exactly the nested bodies.
        var bytes = NinoSerializer.Serialize(PlainChain(3));
        CollectionAssert.AreEqual(new byte[]
        {
            1, 0, 0, 0, 0,
            1, 1, 0, 0, 0,
            1, 2, 0, 0, 0,
            0
        }, bytes);
    }

    private static DeepPayload Payload(int i) =>
        new() { A = i, S = "p" + i, Items = new List<int> { i, i + 1 } };

    private static void AssertPayload(DeepPayload p, int i)
    {
        Assert.AreEqual(i, p.A);
        Assert.AreEqual("p" + i, p.S);
        CollectionAssert.AreEqual(new List<int> { i, i + 1 }, p.Items);
    }

    [TestMethod]
    public void BodiesThatNestNothingAroundTheLimit()
    {
        OnSmallStack(() =>
        {
            // (length, size, FNV-1a of the bytes) of both graphs, as written before bodies that nest nothing
            // skipped the depth count.
            var expected = new Dictionary<int, (int, uint, int, uint)>
            {
                [1] = (69, 162451830, 37, 3899213036),
                [126] = (8853, 3832973221, 4679, 536831388),
                [127] = (8925, 2206857791, 4717, 2249074411),
                [128] = (9017, 2900941090, 4767, 2843536528),
                [129] = (9097, 4168339147, 4813, 2306299766),
                [130] = (9169, 1858569429, 4851, 442040951),
                [300] = (21433, 4166383925, 11327, 2281265845),
                [10_000] = (739657, 2555663702, 390143, 3238745364),
            };
            foreach (int length in expected.Keys)
            {
                DeepCarrier head = null;
                DeepTolerantCarrier tolerant = null;
                for (int i = length - 1; i >= 0; i--)
                {
                    head = new DeepCarrier
                    {
                        Value = i, Payload = Payload(i), Payloads = new List<DeepPayload> { Payload(i), null },
                        Next = head
                    };
                    tolerant = new DeepTolerantCarrier { Value = i, Payload = Payload(i), Next = tolerant };
                }

                var bytes = NinoSerializer.Serialize(head);
                var tolerantBytes = NinoSerializer.Serialize(tolerant);
                Assert.AreEqual(expected[length],
                    (bytes.Length, Checksum(bytes), tolerantBytes.Length, Checksum(tolerantBytes)));
                for (int pass = 0; pass < 2; pass++)
                {
                    // Pass 1 reads into the instances of pass 0.
                    NinoDeserializer.Deserialize(bytes, ref head);
                    NinoDeserializer.Deserialize(tolerantBytes, ref tolerant);
                    int i = 0;
                    for (var node = head; node != null; node = node.Next, i++)
                    {
                        Assert.AreEqual(i, node.Value);
                        AssertPayload(node.Payload, i);
                        Assert.AreEqual(2, node.Payloads.Count);
                        AssertPayload(node.Payloads[0], i);
                        Assert.IsNull(node.Payloads[1]);
                    }

                    Assert.AreEqual(length, i);
                    i = 0;
                    for (var node = tolerant; node != null; node = node.Next, i++)
                    {
                        Assert.AreEqual(i, node.Value);
                        AssertPayload(node.Payload, i);
                    }

                    Assert.AreEqual(length, i);
                }
            }
        });
    }

    private static uint Checksum(byte[] bytes)
    {
        uint h = 2166136261;
        foreach (byte b in bytes)
            h = (h ^ b) * 16777619;
        return h;
    }

    [TestMethod]
    public void LongLinkedList()
    {
        OnSmallStack(() =>
        {
            foreach (int length in new[] { 1, 127, 128, 129, 255, 256, 257, 100_000 })
            {
                var bytes = NinoSerializer.Serialize(Chain(length));
                AssertChain(NinoDeserializer.Deserialize<DeepLink>(bytes), length);
            }
        });
    }

    [TestMethod]
    public void LongLinkedListIsReadIntoTheSameObjectsWithoutAllocating()
    {
        OnSmallStack(() =>
        {
            var bytes = NinoSerializer.Serialize(Chain(100_000, seed: 7));
            var target = Chain(100_000);
            var nodes = new List<DeepLink>();
            for (var node = target; node != null; node = node.Next)
                nodes.Add(node);

            NinoDeserializer.Deserialize(bytes, ref target);
            // The second read can count about 8 KB once, apparently while its methods change tier: seen once in 16
            // runs of the suite with DOTNET_EnableHWIntrinsic=0, never with the test alone (0 in 30) or after a 200 ms
            // wait (0 in 25 runs of the suite), and a program doing the same read allocated nothing at Tier0, at
            // Tier1 or with tiering off. An allocation of the read itself would show in every attempt, so one
            // attempt without any is enough.
            long allocated = long.MaxValue;
            for (int attempt = 0; attempt < 3 && allocated != 0; attempt++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                NinoDeserializer.Deserialize(bytes, ref target);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.AreEqual(0, allocated);
            AssertChain(target, 100_000, seed: 7);
            int i = 0;
            for (var node = target; node != null; node = node.Next)
                Assert.AreSame(nodes[i++], node);
        });
    }

    [TestMethod]
    public void ListsOfDeepLists()
    {
        OnSmallStack(() =>
        {
            var lists = new List<DeepLink>();
            for (int i = 0; i < 20; i++)
                lists.Add(Chain(1000 + i, seed: i * 10_000));
            var result = NinoDeserializer.Deserialize<List<DeepLink>>(NinoSerializer.Serialize(lists));
            Assert.AreEqual(lists.Count, result.Count);
            for (int i = 0; i < lists.Count; i++)
                AssertChain(result[i], 1000 + i, seed: i * 10_000);
        });
    }

    private static DeepTree Spine(int depth)
    {
        // Each node has a leaf and the rest of the spine: depth nodes deep, 2 * depth nodes.
        DeepTree node = null;
        for (int i = depth - 1; i >= 0; i--)
        {
            var children = new List<DeepTree> { new DeepTreeLeaf { Value = -i, Tag = "leaf" + i } };
            if (node != null)
                children.Add(node);
            node = new DeepTree { Value = i, Children = children };
        }

        return node;
    }

    [TestMethod]
    public void DeepPolymorphicTree()
    {
        OnSmallStack(() =>
        {
            var result = NinoDeserializer.Deserialize<DeepTree>(NinoSerializer.Serialize(Spine(20_000)));
            int i = 0;
            for (var node = result; node != null; i++)
            {
                Assert.AreEqual(typeof(DeepTree), node.GetType());
                Assert.AreEqual(i, node.Value);
                var leaf = (DeepTreeLeaf)node.Children[0];
                Assert.AreEqual(-i, leaf.Value);
                Assert.AreEqual("leaf" + i, leaf.Tag);
                node = node.Children.Count > 1 ? node.Children[1] : null;
            }

            Assert.AreEqual(20_000, i);
        });
    }

    [TestMethod]
    public void DeepVersionTolerantAndKeyedTypes()
    {
        OnSmallStack(() =>
        {
            DeepTolerant tolerant = null;
            DeepKeyed keyed = null;
            for (int i = 50_000 - 1; i >= 0; i--)
            {
                tolerant = new DeepTolerant { Value = i, Next = tolerant, Name = i % 1000 == 0 ? "n" + i : null };
                keyed = new DeepKeyed { Value = i, Next = keyed };
            }

            var t = NinoDeserializer.Deserialize<DeepTolerant>(NinoSerializer.Serialize(tolerant));
            var k = NinoDeserializer.Deserialize<DeepKeyed>(NinoSerializer.Serialize(keyed));
            for (int i = 0; i < 50_000; i++, t = t.Next, k = k.Next)
            {
                Assert.AreEqual(i, t.Value);
                Assert.AreEqual(i % 1000 == 0 ? "n" + i : null, t.Name);
                Assert.AreEqual(i, k.Value);
            }

            Assert.IsNull(t);
            Assert.IsNull(k);
        });
    }

    [TestMethod]
    public void KeyedStructRoot()
    {
        OnSmallStack(() =>
        {
            var root = new DeepKeyedRoot { Before = 1, Head = Chain(5000), After = 2 };
            var result = NinoDeserializer.Deserialize<DeepKeyedRoot>(NinoSerializer.Serialize(root));
            Assert.AreEqual(1, result.Before);
            Assert.AreEqual(2, result.After);
            AssertChain(result.Head, 5000);
        });
    }

    [TestMethod]
    public void ReadersSkipTheDeferredBodiesOfMembersTheyDoNotKnow()
    {
        OnSmallStack(() =>
        {
            var v2 = new DeepKeyedV2 { A = 1, Extra = Chain(3000, seed: 100), Known = Chain(2000), B = 2 };
            var v1 = NinoDeserializer.Deserialize<DeepKeyedV1>(NinoSerializer.Serialize(v2));
            Assert.AreEqual(1, v1.A);
            Assert.AreEqual(2, v1.B);
            AssertChain(v1.Known, 2000);

            var back = NinoDeserializer.Deserialize<DeepKeyedV2>(NinoSerializer.Serialize(v1));
            Assert.IsNull(back.Extra);
            AssertChain(back.Known, 2000);

            var level2 = new DeepLevelV2 { A = 1, Known = Chain(2000), Extra = Chain(3000, seed: 100), B = 2 };
            var level1 = NinoDeserializer.Deserialize<DeepLevelV1>(NinoSerializer.Serialize(level2));
            Assert.AreEqual(1, level1.A);
            AssertChain(level1.Known, 2000);

            var levelBack = NinoDeserializer.Deserialize<DeepLevelV2>(NinoSerializer.Serialize(level1));
            Assert.AreEqual(1, levelBack.A);
            AssertChain(levelBack.Known, 2000);
            Assert.IsNull(levelBack.Extra);
            Assert.AreEqual(0, levelBack.B);
        });
    }

    [TestMethod]
    public void DeepTrackedListWithBackReferences()
    {
        OnSmallStack(() =>
        {
            var nodes = new DeepTracked[100_000];
            for (int i = 0; i < nodes.Length; i++)
                nodes[i] = new DeepTracked { Value = i };
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i].Next = i + 1 < nodes.Length ? nodes[i + 1] : nodes[0];
                nodes[i].Prev = i > 0 ? nodes[i - 1] : nodes[nodes.Length - 1];
            }

            var head = NinoDeserializer.Deserialize<DeepTracked>(NinoSerializer.Serialize(nodes[0]));
            var node = head;
            for (int i = 0; i < nodes.Length; i++, node = node.Next)
            {
                Assert.AreEqual(i, node.Value);
                Assert.AreSame(node, node.Next.Prev);
            }

            Assert.AreSame(head, node);
        });
    }

    [TestMethod]
    public void DeepThroughObjectMembers()
    {
        OnSmallStack(() =>
        {
            DeepObject head = null;
            for (int i = 10_000 - 1; i >= 0; i--)
                head = new DeepObject { Value = i, Next = head };
            var node = NinoDeserializer.Deserialize<DeepObject>(NinoSerializer.Serialize(head));
            for (int i = 0; i < 10_000; i++, node = (DeepObject)node.Next)
                Assert.AreEqual(i, node.Value);
            Assert.IsNull(node);
        });
    }

    [TestMethod]
    public void UntrackedCycleThrows()
    {
        OnSmallStack(() =>
        {
            var a = new DeepLink { Value = 1 };
            a.Next = a;
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(a));

            var b = new DeepLink { Value = 2, Next = a };
            a.Next = new DeepLink { Value = 3, Next = b };
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(b));

            // A long cycle: the check compares with the chain's object at the last power of two.
            var ring = Chain(10_000);
            var last = ring;
            while (last.Next != null)
                last = last.Next;
            last.Next = ring;
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(ring));
        });
    }

    [TestMethod]
    public void TypesThatCannotBeDeferredFailPastTheLimit()
    {
        OnSmallStack(() =>
        {
            DeepRecord record = null;
            DeepHashed hashed = null;
            for (int i = 299; i >= 0; i--)
            {
                record = new DeepRecord(i, record);
                hashed = new DeepHashed { Value = i, Next = hashed };
            }

            var r = NinoDeserializer.Deserialize<DeepRecord>(NinoSerializer.Serialize(record));
            var h = NinoDeserializer.Deserialize<DeepHashed>(NinoSerializer.Serialize(hashed));
            for (int i = 0; i < 300; i++, r = r.Next, h = h.Next)
            {
                Assert.AreEqual(i, r.Value);
                Assert.AreEqual(i, h.Value);
            }

            for (int i = 0; i < 1000; i++)
                record = new DeepRecord(i, record);
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(record));
        });
    }

    private static byte[] HostilePlainChain(int depth)
    {
        // What a PlainLink chain of the given depth is, nested inline: (present, value) per node, then null.
        var bytes = new byte[depth * 5 + 1];
        for (int i = 0; i < depth; i++)
            bytes[i * 5] = 1;
        return bytes;
    }

    private static byte[] HostileTolerantChain(int depth)
    {
        // A DeepLink chain nested inline: per node (present, length of the rest, value), the lengths nested.
        var bytes = new byte[depth * 9 + 1];
        for (int i = 0; i < depth; i++)
        {
            bytes[i * 9] = 1;
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 9 + 1), (depth - i) * 9 + 1 - 5);
        }

        return bytes;
    }

    private static int Length(DeepLink head)
    {
        int length = 0;
        for (var node = head; node != null; node = node.Next)
            length++;
        return length;
    }

    [TestMethod]
    public void DeepDataCannotExhaustTheStack()
    {
        OnSmallStack(() =>
        {
            var plain = HostilePlainChain(1_000_000);
            int length = 0;
            for (var node = NinoDeserializer.Deserialize<PlainLink>(plain); node != null; node = node.Next)
                length++;
            Assert.AreEqual(1_000_000, length);
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<PlainLink>(plain.AsSpan(0, plain.Length - 1)));
            // Types that cannot be deferred stop at NinoDepth.Max.
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<PlainRecordLike>(plain));

            // Inside a region, bodies deeper than NinoDepth.Inline are read from the region's section, which data
            // nested inline does not have: it fails cleanly, and data shallower than that still reads.
            Assert.ThrowsExactly<NinoDataException>(() =>
                NinoDeserializer.Deserialize<DeepLink>(HostileTolerantChain(1_000_000)));
            Assert.AreEqual(NinoDepthInline, Length(NinoDeserializer.Deserialize<DeepLink>(
                HostileTolerantChain(NinoDepthInline))));
        });
    }

    [TestMethod]
    public void PlainDeepGraphs()
    {
        OnSmallStack(() =>
        {
            foreach (int length in new[] { 127, 128, 129, 256, 257, 100_000 })
                AssertPlainChain(NinoDeserializer.Deserialize<PlainLink>(NinoSerializer.Serialize(PlainChain(length))),
                    length);

            var lists = new List<PlainLink>();
            for (int i = 0; i < 10; i++)
                lists.Add(PlainChain(1000 + i, seed: i));
            var result = NinoDeserializer.Deserialize<List<PlainLink>>(NinoSerializer.Serialize(lists));
            for (int i = 0; i < 10; i++)
                AssertPlainChain(result[i], 1000 + i, seed: i);

            PlainTree tree = null;
            for (int i = 20_000 - 1; i >= 0; i--)
            {
                var children = new List<PlainTree> { new PlainTreeLeaf { Value = -i, Tag = "t" + i } };
                if (tree != null)
                    children.Add(tree);
                tree = new PlainTree { Value = i, Children = children };
            }

            var t = NinoDeserializer.Deserialize<PlainTree>(NinoSerializer.Serialize(tree));
            for (int i = 0; i < 20_000; i++)
            {
                Assert.AreEqual(i, t.Value);
                Assert.AreEqual("t" + i, ((PlainTreeLeaf)t.Children[0]).Tag);
                t = t.Children.Count > 1 ? t.Children[1] : null;
            }

            Assert.IsNull(t);

            var nodes = new PlainTracked[50_000];
            for (int i = 0; i < nodes.Length; i++)
                nodes[i] = new PlainTracked { Value = i };
            for (int i = 0; i < nodes.Length; i++)
            {
                nodes[i].Next = nodes[(i + 1) % nodes.Length];
                nodes[i].Prev = nodes[(i + nodes.Length - 1) % nodes.Length];
            }

            var head = NinoDeserializer.Deserialize<PlainTracked>(NinoSerializer.Serialize(nodes[0]));
            var node = head;
            for (int i = 0; i < nodes.Length; i++, node = node.Next)
            {
                Assert.AreEqual(i, node.Value);
                Assert.AreSame(node, node.Next.Prev);
            }

            Assert.AreSame(head, node);

            // Plain and version-tolerant bodies alternating: regions open and close around the deferral points.
            PlainOuter outer = null;
            for (int i = 10_000 - 1; i >= 0; i--)
                outer = new PlainOuter { Value = i, Inner = new TolerantInner { Value = -i, Outer = outer } };
            var o = NinoDeserializer.Deserialize<PlainOuter>(NinoSerializer.Serialize(outer));
            for (int i = 0; i < 10_000; i++, o = o.Inner.Outer)
            {
                Assert.AreEqual(i, o.Value);
                Assert.AreEqual(-i, o.Inner.Value);
            }

            Assert.IsNull(o);
        });
    }

    [TestMethod]
    public void ConstructedTypesFailPastTheLimit()
    {
        PlainRecordLike chain = null;
        for (int i = 0; i < 300; i++)
            chain = new PlainRecordLike(i, chain);
        var back = NinoDeserializer.Deserialize<PlainRecordLike>(NinoSerializer.Serialize(chain));
        Assert.AreEqual(299, back.Value);
        for (int i = 0; i < 1000; i++)
            chain = new PlainRecordLike(i, chain);
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(chain));
    }

    [TestMethod]
    public void CorruptSectionsFailCleanly()
    {
        var bytes = NinoSerializer.Serialize(new DeepKeyedV1 { A = 1, Known = Chain(1000), B = 2 });
        var random = new Random(1);
        for (int i = 0; i < 2000; i++)
        {
            var copy = (byte[])bytes.Clone();
            copy[random.Next(copy.Length)] = (byte)random.Next(256);
            try
            {
                NinoDeserializer.Deserialize<DeepKeyedV1>(copy);
            }
            catch (InvalidOperationException)
            {
            }
            catch (OutOfMemoryException)
            {
            }
        }
    }
}
