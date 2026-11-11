using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// Regions the generated writes only patch the length of (Writer.ReserveInt32 / PatchLength, no BeginRegion / EndRegion):
// every member is raw, a string or a Nino.Core formatter over such values (MemberModel.OpensBodies false). Each has a
// twin of the same members whose formatters are CachedFormatter<T> (Nino.Core's formatter for T, resolved at run time),
// which the generator cannot see through, so the twin takes the general region path and writes the same bytes.

[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public sealed class PatchTolerant
{
    public string S;
    public int A;
    public List<int> L;
}

[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public sealed class PatchTolerantGeneral
{
    [NinoMemberFormatter(typeof(CachedFormatter<string>))] public string S;
    public int A;
    [NinoMemberFormatter(typeof(CachedFormatter<List<int>>))] public List<int> L;
}

// Raw members first: the region's length and them in one reservation (BodyEmitter.TryEmitRegionWithBlock).
[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public sealed class PatchBlock
{
    public int A;
    public long B;
    public string S;
    public int[] R;
}

[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public sealed class PatchBlockGeneral
{
    public int A;
    public long B;
    [NinoMemberFormatter(typeof(CachedFormatter<string>))] public string S;
    [NinoMemberFormatter(typeof(CachedFormatter<int[]>))] public int[] R;
}

// Keyed: the body's region and each member's.
[NinoType(format: NinoFormat.Keyed, trackReferences: true)]
public sealed class PatchKeyed
{
    public int A;
    public string S;
    public List<int> L;
    public int[] R;
}

[NinoType(format: NinoFormat.Keyed, trackReferences: true)]
public sealed class PatchKeyedGeneral
{
    public int A;
    [NinoMemberFormatter(typeof(CachedFormatter<string>))] public string S;
    [NinoMemberFormatter(typeof(CachedFormatter<List<int>>))] public List<int> L;
    [NinoMemberFormatter(typeof(CachedFormatter<int[]>))] public int[] R;
}

// Keyed members: the body opens a general region (Next nests), its string and list members only patch theirs.
[NinoType(format: NinoFormat.Keyed, trackReferences: true)]
public sealed class PatchKeyedNode
{
    public int Value;
    public string S;
    public PatchKeyedNode Next;
    public List<int> L;
}

[NinoType(format: NinoFormat.Keyed, trackReferences: true)]
public sealed class PatchKeyedNodeGeneral
{
    public int Value;
    [NinoMemberFormatter(typeof(CachedFormatter<string>))] public string S;
    public PatchKeyedNodeGeneral Next;
    [NinoMemberFormatter(typeof(CachedFormatter<List<int>>))] public List<int> L;
}

// A version-tolerant collection: its elements (which nest and are deferred) come before the members' region, which
// only patches its length.
[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class PatchBag : List<PatchTolerant>
{
    public string Name;
    public int Tag;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class PatchBagGeneral : List<PatchTolerantGeneral>
{
    [NinoMemberFormatter(typeof(CachedFormatter<string>))] public string Name;
    public int Tag;
}

[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public sealed class PatchNode
{
    public int Value;
    public PatchTolerant T;
    public PatchBlock B;
    public PatchKeyed K;
    public PatchBag Bag;
    public PatchNode Next;
}

[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public sealed class PatchNodeGeneral
{
    public int Value;
    public PatchTolerantGeneral T;
    public PatchBlockGeneral B;
    public PatchKeyedGeneral K;
    public PatchBagGeneral Bag;
    public PatchNodeGeneral Next;
}

/// <summary>
/// The regions generated writes only patch the length of (<c>BodyEmitter.CanPatchMember</c> / <c>CanPatchBody</c>: no
/// <c>Writer.BeginRegion</c>, so the writer's count of open regions does not see them, and no <c>EndRegion</c>, which at
/// the outermost region writes the section of bodies deferred inside it). That is only right while nothing inside such
/// a region can defer a body or need the section. Each shape (version tolerant, version tolerant with a raw block
/// first, keyed body, keyed members of a body that nests, the members of a version-tolerant collection) is written
/// with references tracked, alone (the patched region outermost) and in chains nested past <c>NinoDepth.Inline</c>
/// (bodies deferred into the outermost region's section around the patched regions), and its bytes are compared with
/// those of its general twin, which writes every region with BeginRegion / EndRegion: the twin reads the bytes and
/// writes them again. The values read back are checked, the deferred bodies (past the inline depth) and the back
/// references (shared leaves) among them.
/// </summary>
[TestClass]
public class PatchOnlyRegionTests
{
    private static readonly int[] Lengths = { 1, 2, NinoDepth.Inline - 1, NinoDepth.Inline, NinoDepth.Inline + 1, 300 };

    private static PatchTolerant Tolerant(int i) =>
        new() { S = i % 7 == 0 ? null : "t" + i, A = i, L = i % 4 == 3 ? null : new List<int> { i, -i } };

    private static void AssertTolerant(PatchTolerant t, int i)
    {
        Assert.AreEqual(i % 7 == 0 ? null : "t" + i, t.S);
        Assert.AreEqual(i, t.A);
        if (i % 4 == 3)
            Assert.IsNull(t.L);
        else
            CollectionAssert.AreEqual(new List<int> { i, -i }, t.L);
    }

    private static PatchBlock Block(int i) => new() { A = i, B = (long)i << 33, S = "b" + i, R = new[] { i, i + 1 } };

    private static void AssertBlock(PatchBlock b, int i)
    {
        Assert.AreEqual(i, b.A);
        Assert.AreEqual((long)i << 33, b.B);
        Assert.AreEqual("b" + i, b.S);
        CollectionAssert.AreEqual(new[] { i, i + 1 }, b.R);
    }

    private static PatchKeyed Keyed(int i) => new()
    {
        A = i, S = i % 3 == 0 ? null : "k" + i, L = i % 4 == 0 ? null : new List<int> { i }, R = new int[0]
    };

    private static void AssertKeyed(PatchKeyed k, int i)
    {
        Assert.AreEqual(i, k.A);
        Assert.AreEqual(i % 3 == 0 ? null : "k" + i, k.S);
        if (i % 4 == 0)
            Assert.IsNull(k.L);
        else
            CollectionAssert.AreEqual(new List<int> { i }, k.L);
        Assert.AreEqual(0, k.R.Length);
    }

    /// <summary>
    /// <paramref name="length"/> nodes; each odd node shares its leaves with the node before it (back references), and
    /// each bag holds that node's own tolerant leaf, a leaf of its own and null.
    /// </summary>
    private static PatchNode Chain(int length)
    {
        PatchNode head = null;
        var nodes = new PatchNode[length];
        for (int i = 0; i < length; i++)
        {
            int leaf = i - i % 2;
            nodes[i] = i % 2 == 1
                ? new PatchNode { Value = i, T = nodes[i - 1].T, B = nodes[i - 1].B, K = nodes[i - 1].K }
                : new PatchNode { Value = i, T = Tolerant(leaf), B = Block(leaf), K = Keyed(leaf) };
            nodes[i].Bag = new PatchBag { nodes[i].T, Tolerant(i + 1000), null };
            nodes[i].Bag.Name = "bag" + i;
            nodes[i].Bag.Tag = i;
        }

        for (int i = length - 1; i >= 0; i--)
        {
            nodes[i].Next = head;
            head = nodes[i];
        }

        return head;
    }

    private static void AssertChain(PatchNode head, int length)
    {
        int i = 0;
        PatchNode previous = null;
        for (var node = head; node != null; previous = node, node = node.Next, i++)
        {
            int leaf = i - i % 2;
            Assert.AreEqual(i, node.Value);
            AssertTolerant(node.T, leaf);
            AssertBlock(node.B, leaf);
            AssertKeyed(node.K, leaf);
            if (i % 2 == 1)
            {
                Assert.AreSame(previous.T, node.T);
                Assert.AreSame(previous.B, node.B);
                Assert.AreSame(previous.K, node.K);
            }

            Assert.AreEqual(3, node.Bag.Count);
            Assert.AreSame(node.T, node.Bag[0]);
            AssertTolerant(node.Bag[1], i + 1000);
            Assert.IsNull(node.Bag[2]);
            Assert.AreEqual("bag" + i, node.Bag.Name);
            Assert.AreEqual(i, node.Bag.Tag);
        }

        Assert.AreEqual(length, i);
    }

    private static PatchKeyedNode KeyedChain(int length)
    {
        PatchKeyedNode head = null;
        for (int i = length - 1; i >= 0; i--)
            head = new PatchKeyedNode
            {
                Value = i, S = i % 3 == 0 ? null : "n" + i, L = i % 5 == 4 ? null : new List<int> { i, i }, Next = head
            };
        return head;
    }

    private static void AssertKeyedChain(PatchKeyedNode head, int length)
    {
        int i = 0;
        for (var node = head; node != null; node = node.Next, i++)
        {
            Assert.AreEqual(i, node.Value);
            Assert.AreEqual(i % 3 == 0 ? null : "n" + i, node.S);
            if (i % 5 == 4)
                Assert.IsNull(node.L);
            else
                CollectionAssert.AreEqual(new List<int> { i, i }, node.L);
        }

        Assert.AreEqual(length, i);
    }

    /// <summary>
    /// The bytes of <paramref name="value"/> (through patched regions) and those its general twin writes for the same
    /// values (read from them), which must be the same; returns the value read back.
    /// </summary>
    private static T SameAsGeneral<T, TGeneral>(T value, string name)
    {
        byte[] patched = NinoSerializer.Serialize(value);
        byte[] general = NinoSerializer.Serialize(NinoDeserializer.Deserialize<TGeneral>(patched));
        CollectionAssert.AreEqual(general, patched, name + ": the patched regions wrote other bytes");
        // And back: the general bytes read through the patched types write them again.
        CollectionAssert.AreEqual(general, NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(general)), name);
        return NinoDeserializer.Deserialize<T>(patched);
    }

    [TestMethod]
    public void PatchedRegionsAloneWriteTheGeneralBytes()
    {
        // The patched region is the outermost one, with a reference table open.
        for (int i = 0; i < 12; i++)
        {
            AssertTolerant(SameAsGeneral<PatchTolerant, PatchTolerantGeneral>(Tolerant(i), "tolerant " + i), i);
            AssertBlock(SameAsGeneral<PatchBlock, PatchBlockGeneral>(Block(i), "block " + i), i);
            AssertKeyed(SameAsGeneral<PatchKeyed, PatchKeyedGeneral>(Keyed(i), "keyed " + i), i);
        }

        var bag = new PatchBag { Tolerant(1), null, Tolerant(2) };
        bag.Name = "bag";
        bag.Tag = 3;
        bag.Add(bag[0]);
        var read = SameAsGeneral<PatchBag, PatchBagGeneral>(bag, "bag");
        Assert.AreEqual(4, read.Count);
        AssertTolerant(read[0], 1);
        Assert.IsNull(read[1]);
        AssertTolerant(read[2], 2);
        Assert.AreSame(read[0], read[3]);
        Assert.AreEqual(("bag", 3), (read.Name, read.Tag));
    }

    [TestMethod]
    public void PatchedRegionsAmongDeferredBodiesWriteTheGeneralBytes()
    {
        foreach (int length in Lengths)
        {
            AssertChain(SameAsGeneral<PatchNode, PatchNodeGeneral>(Chain(length), "chain " + length), length);
            AssertKeyedChain(SameAsGeneral<PatchKeyedNode, PatchKeyedNodeGeneral>(KeyedChain(length), "keyed chain " + length),
                length);
        }
    }

    [TestMethod]
    public void PatchedRegionsInDeferredBodiesOfALargerGraphWriteTheGeneralBytes()
    {
        // Lists of chains: each chain's bodies past the inline depth are deferred, every list element in turn.
        var chains = new List<PatchNode> { Chain(NinoDepth.Inline + 5), null, Chain(3), Chain(NinoDepth.Inline * 2) };
        var read = SameAsGeneral<List<PatchNode>, List<PatchNodeGeneral>>(chains, "list of chains");
        Assert.AreEqual(4, read.Count);
        AssertChain(read[0], NinoDepth.Inline + 5);
        Assert.IsNull(read[1]);
        AssertChain(read[2], 3);
        AssertChain(read[3], NinoDepth.Inline * 2);
    }
}
