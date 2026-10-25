using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// Types whose nesting is bounded (sealed classes and structs, no recursion): their formatters have a __Shallow twin
// that counts and checks no depth where the bound is known to stay below NinoDepth.Inline. Bounds: BoundLeaf 0,
// BoundMid 1, BoundStruct 1 (a struct counts no level), BoundTop 2, BoundTolerant 2, BoundKeyed 2.

[NinoType]
public sealed class BoundLeaf
{
    public int A;
    public string S;
    public List<int> Items;
}

[NinoType]
public sealed class BoundMid
{
    public int V;
    public BoundLeaf Leaf;
    public List<BoundLeaf> Leaves;
    public BoundLeaf[] Array;
    public Dictionary<int, BoundLeaf> Map;
}

[NinoType]
public struct BoundStruct
{
    public int X;
    public BoundLeaf Leaf;
    public BoundMid Mid;
}

[NinoType]
public sealed class BoundTop
{
    public int V;
    public BoundMid Mid;
    public List<BoundMid> Mids;
    public BoundStruct Struct;
    public BoundStruct? Nullable;
    public (BoundLeaf, BoundMid) Pair;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class BoundTolerant
{
    public int V;
    public BoundMid Mid;
    public List<BoundLeaf> Leaves;
}

[NinoType(format: NinoFormat.Keyed)]
public sealed class BoundKeyed
{
    public int V;
    public BoundLeaf Leaf;
    public List<BoundMid> Mids;
}

// Unbounded (recursive) chains holding bounded types: those near NinoDepth.Inline take the counting path and defer.
[NinoType]
public sealed class BoundChain
{
    public int V;
    public BoundTop Top;
    public List<BoundTop> Tops;
    public BoundStruct Struct;
    public BoundKeyed Keyed;
    public BoundChain Next;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class BoundTolerantChain
{
    public int V;
    public BoundTolerant Tolerant;
    public List<BoundMid> Mids;
    public BoundTolerantChain Next;
}

[NinoType(format: NinoFormat.Keyed)]
public sealed class BoundKeyedChain
{
    public int V;
    public BoundTop Top;
    public BoundKeyedChain Next;
}

// A bounded type holding an unbounded one is unbounded: it always counts.
[NinoType]
public sealed class BoundHoldsChain
{
    public BoundMid Mid;
    public BoundChain Chain;
}

[TestClass]
public class BoundedNestingTests
{
    private const int SmallStack = 512 * 1024;

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

    private static uint Checksum(byte[] bytes)
    {
        uint h = 2166136261;
        foreach (byte b in bytes)
            h = (h ^ b) * 16777619;
        return h;
    }

    private static BoundLeaf Leaf(int i) => new() { A = i, S = "l" + i, Items = new List<int> { i, -i } };

    private static BoundMid Mid(int i) => new()
    {
        V = i, Leaf = Leaf(i), Leaves = new List<BoundLeaf> { Leaf(i + 1), null, Leaf(i + 2) },
        Array = new[] { Leaf(i + 3) }, Map = new Dictionary<int, BoundLeaf> { [i] = Leaf(i + 4), [i + 1] = null }
    };

    private static BoundStruct Struct(int i) => new() { X = i, Leaf = Leaf(i + 5), Mid = Mid(i + 6) };

    private static BoundTop Top(int i) => new()
    {
        V = i, Mid = Mid(i), Mids = new List<BoundMid> { Mid(i + 10), null }, Struct = Struct(i + 20),
        Nullable = i % 2 == 0 ? Struct(i + 30) : null, Pair = (Leaf(i + 40), Mid(i + 50))
    };

    private static BoundKeyed Keyed(int i) => new()
        { V = i, Leaf = Leaf(i), Mids = new List<BoundMid> { Mid(i + 1) } };

    private static BoundChain Chain(int length)
    {
        BoundChain head = null;
        for (int i = length - 1; i >= 0; i--)
        {
            head = new BoundChain
            {
                V = i, Top = Top(i), Tops = new List<BoundTop> { Top(i + 100), null, Top(i + 200) },
                Struct = Struct(i + 300), Keyed = Keyed(i + 400), Next = head
            };
        }

        return head;
    }

    private static BoundTolerantChain TolerantChain(int length)
    {
        BoundTolerantChain head = null;
        for (int i = length - 1; i >= 0; i--)
        {
            head = new BoundTolerantChain
            {
                V = i,
                Tolerant = new BoundTolerant
                    { V = i, Mid = Mid(i), Leaves = new List<BoundLeaf> { Leaf(i + 1) } },
                Mids = new List<BoundMid> { Mid(i + 2), Mid(i + 3) }, Next = head
            };
        }

        return head;
    }

    private static BoundKeyedChain KeyedChain(int length)
    {
        BoundKeyedChain head = null;
        for (int i = length - 1; i >= 0; i--)
            head = new BoundKeyedChain { V = i, Top = Top(i), Next = head };
        return head;
    }

    private static void AssertLeaf(BoundLeaf l, int i)
    {
        Assert.AreEqual(i, l.A);
        Assert.AreEqual("l" + i, l.S);
        CollectionAssert.AreEqual(new List<int> { i, -i }, l.Items);
    }

    private static void AssertMid(BoundMid m, int i)
    {
        Assert.AreEqual(i, m.V);
        AssertLeaf(m.Leaf, i);
        Assert.AreEqual(3, m.Leaves.Count);
        AssertLeaf(m.Leaves[0], i + 1);
        Assert.IsNull(m.Leaves[1]);
        AssertLeaf(m.Leaves[2], i + 2);
        Assert.AreEqual(1, m.Array.Length);
        AssertLeaf(m.Array[0], i + 3);
        Assert.AreEqual(2, m.Map.Count);
        AssertLeaf(m.Map[i], i + 4);
        Assert.IsNull(m.Map[i + 1]);
    }

    private static void AssertStruct(BoundStruct s, int i)
    {
        Assert.AreEqual(i, s.X);
        AssertLeaf(s.Leaf, i + 5);
        AssertMid(s.Mid, i + 6);
    }

    private static void AssertTop(BoundTop t, int i)
    {
        Assert.AreEqual(i, t.V);
        AssertMid(t.Mid, i);
        Assert.AreEqual(2, t.Mids.Count);
        AssertMid(t.Mids[0], i + 10);
        Assert.IsNull(t.Mids[1]);
        AssertStruct(t.Struct, i + 20);
        if (i % 2 == 0)
            AssertStruct(t.Nullable!.Value, i + 30);
        else
            Assert.IsNull(t.Nullable);
        AssertLeaf(t.Pair.Item1, i + 40);
        AssertMid(t.Pair.Item2, i + 50);
    }

    private static void AssertChain(BoundChain head, int length)
    {
        int i = 0;
        for (var node = head; node != null; node = node.Next, i++)
        {
            Assert.AreEqual(i, node.V);
            AssertTop(node.Top, i);
            Assert.AreEqual(3, node.Tops.Count);
            AssertTop(node.Tops[0], i + 100);
            Assert.IsNull(node.Tops[1]);
            AssertTop(node.Tops[2], i + 200);
            AssertStruct(node.Struct, i + 300);
            Assert.AreEqual(i + 400, node.Keyed.V);
            AssertLeaf(node.Keyed.Leaf, i + 400);
            AssertMid(node.Keyed.Mids[0], i + 401);
        }

        Assert.AreEqual(length, i);
    }

    private static void AssertTolerantChain(BoundTolerantChain head, int length)
    {
        int i = 0;
        for (var node = head; node != null; node = node.Next, i++)
        {
            Assert.AreEqual(i, node.V);
            Assert.AreEqual(i, node.Tolerant.V);
            AssertMid(node.Tolerant.Mid, i);
            AssertLeaf(node.Tolerant.Leaves[0], i + 1);
            AssertMid(node.Mids[0], i + 2);
            AssertMid(node.Mids[1], i + 3);
        }

        Assert.AreEqual(length, i);
    }

    private static void AssertKeyedChain(BoundKeyedChain head, int length)
    {
        int i = 0;
        for (var node = head; node != null; node = node.Next, i++)
        {
            Assert.AreEqual(i, node.V);
            AssertTop(node.Top, i);
        }

        Assert.AreEqual(length, i);
    }

    /// <summary>
    /// Bounded types alone, at the top level (their twins) and through the generic path (their formatters, which
    /// choose the twin at depth 0): the bytes written before the twins existed, read back fresh and in place.
    /// </summary>
    [TestMethod]
    public void BoundedTypesWriteTheSameBytes()
    {
        var top = Top(7);
        var bytes = NinoSerializer.Serialize(top);
        Assert.AreEqual((694, 720275216u), (bytes.Length, Checksum(bytes)));
        // Through a caller's writer (no interceptor twin): the formatter itself.
        var buffer = new NinoArrayBufferWriter();
        NinoSerializer.Serialize(top, buffer);
        CollectionAssert.AreEqual(bytes, buffer.WrittenSpan.ToArray());
        AssertTop(NinoDeserializer.Deserialize<BoundTop>(bytes), 7);
        var existing = Top(1);
        var mid = existing.Mid;
        NinoDeserializer.Deserialize(bytes, ref existing);
        AssertTop(existing, 7);
        Assert.AreSame(mid, existing.Mid);

        var list = new List<BoundTop> { Top(1), null, Top(2) };
        var listBytes = NinoSerializer.Serialize(list);
        Assert.AreEqual((1578, 777872161u), (listBytes.Length, Checksum(listBytes)));
        var read = NinoDeserializer.Deserialize<List<BoundTop>>(listBytes);
        AssertTop(read[0], 1);
        Assert.IsNull(read[1]);
        AssertTop(read[2], 2);

        var s = Struct(3);
        var structBytes = NinoSerializer.Serialize(s);
        Assert.AreEqual((187, 544345348u), (structBytes.Length, Checksum(structBytes)));
        AssertStruct(NinoDeserializer.Deserialize<BoundStruct>(structBytes), 3);

        var keyed = Keyed(5);
        var keyedBytes = NinoSerializer.Serialize(keyed);
        Assert.AreEqual((213, 1372761807u), (keyedBytes.Length, Checksum(keyedBytes)));
        var keyedRead = NinoDeserializer.Deserialize<BoundKeyed>(keyedBytes);
        AssertLeaf(keyedRead.Leaf, 5);
        AssertMid(keyedRead.Mids[0], 6);

        var holder = new BoundHoldsChain { Mid = Mid(1), Chain = Chain(2) };
        var holderBytes = NinoSerializer.Serialize(holder);
        Assert.AreEqual((5862, 294524101u), (holderBytes.Length, Checksum(holderBytes)));
        var holderRead = NinoDeserializer.Deserialize<BoundHoldsChain>(holderBytes);
        AssertMid(holderRead.Mid, 1);
        AssertChain(holderRead.Chain, 2);
    }

    /// <summary>
    /// Unbounded chains of bounded types, around NinoDepth.Inline: the bounded bodies take the twin while the bound
    /// leaves room, and count (and are deferred) near the limit. The bytes are those written before the twins
    /// existed, read back fresh and into the same instances.
    /// </summary>
    [TestMethod]
    public void BoundedTypesInDeepChainsAroundTheLimit()
    {
        OnSmallStack(() =>
        {
            // (length, FNV-1a) of the three chains' bytes, as written before bounded types had twins (this project
            // defines WEAK_VERSION_TOLERANCE: every type here is version tolerant).
            var expected = new Dictionary<int, (int, uint, int, uint, int, uint)>
            {
                [1] = (3143, 2296704423, 504, 414289861, 915, 3230055819),
                [124] = (355537, 1210105534, 64693, 4288779054, 103445, 831889757),
                [125] = (358712, 2351183875, 65228, 1564123677, 104392, 2852862148),
                [126] = (361826, 871762284, 65807, 2327920290, 105308, 2823844786),
                [127] = (365249, 527861980, 66438, 779952477, 106319, 2204311962),
                [128] = (367887, 2488175073, 66997, 2165049988, 107079, 732395996),
                [129] = (371070, 685102569, 67540, 1536586051, 108034, 2851225841),
                [130] = (373660, 2448997262, 68075, 3173287996, 108786, 4214936510),
                [131] = (376835, 3469285194, 68610, 2986354682, 109733, 399083318),
                [300] = (864581, 1383885263, 159193, 2763350553, 253465, 214321375),
            };
            foreach (int length in expected.Keys)
            {
                var chain = Chain(length);
                var tolerant = TolerantChain(length);
                var keyed = KeyedChain(length);
                var bytes = NinoSerializer.Serialize(chain);
                var tolerantBytes = NinoSerializer.Serialize(tolerant);
                var keyedBytes = NinoSerializer.Serialize(keyed);
                Assert.AreEqual(expected[length],
                    (bytes.Length, Checksum(bytes), tolerantBytes.Length, Checksum(tolerantBytes),
                        keyedBytes.Length, Checksum(keyedBytes)), "length " + length);

                // Through a caller's writer: the formatters themselves.
                var buffer = new NinoArrayBufferWriter();
                NinoSerializer.Serialize(chain, buffer);
                CollectionAssert.AreEqual(bytes, buffer.WrittenSpan.ToArray());

                BoundChain readChain = null;
                BoundTolerantChain readTolerant = null;
                BoundKeyedChain readKeyed = null;
                for (int pass = 0; pass < 2; pass++)
                {
                    // Pass 1 reads into the instances of pass 0.
                    var first = readChain;
                    NinoDeserializer.Deserialize(bytes, ref readChain);
                    NinoDeserializer.Deserialize(tolerantBytes, ref readTolerant);
                    NinoDeserializer.Deserialize(keyedBytes, ref readKeyed);
                    if (first != null)
                        Assert.AreSame(first, readChain);
                    AssertChain(readChain, length);
                    AssertTolerantChain(readTolerant, length);
                    AssertKeyedChain(readKeyed, length);
                    CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(readChain));
                    CollectionAssert.AreEqual(tolerantBytes, NinoSerializer.Serialize(readTolerant));
                    CollectionAssert.AreEqual(keyedBytes, NinoSerializer.Serialize(readKeyed));
                }
            }
        });
    }

    /// <summary>
    /// A list of chains, each starting at depth 0 again, and a bounded type around a chain: every element's depth
    /// is counted from where it starts.
    /// </summary>
    [TestMethod]
    public void ListsOfDeepChainsHoldingBoundedTypes()
    {
        OnSmallStack(() =>
        {
            var list = new List<BoundChain> { Chain(130), null, Chain(3), Chain(129) };
            var bytes = NinoSerializer.Serialize(list);
            Assert.AreEqual((753583, 896369397u), (bytes.Length, Checksum(bytes)));
            var read = NinoDeserializer.Deserialize<List<BoundChain>>(bytes);
            AssertChain(read[0], 130);
            Assert.IsNull(read[1]);
            AssertChain(read[2], 3);
            AssertChain(read[3], 129);
            NinoDeserializer.Deserialize(bytes, ref read);
            AssertChain(read[3], 129);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(read));
        });
    }
}
