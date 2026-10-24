using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>A struct that can hold itself, holding a record (built by its constructor, never deferred).</summary>
[NinoType]
public struct WriteDepthMixedNode
{
    public int V;
    public List<WriteDepthMixedNode> Kids;
    public WriteDepthRecord Record;
}

[NinoType]
public sealed record WriteDepthRecord(int Value, WriteDepthRecord Next);

/// <summary>
/// Object graphs that nest without a class body to count them (structs and collections that can hold themselves)
/// count their levels on the write side too: nested past <see cref="NinoSettings.MaxDepth"/>, or holding themselves
/// (a cycle Nino cannot write without reference tracking), they throw <see cref="InvalidOperationException"/> (not
/// <see cref="NinoDataException"/>: the caller's graph, not data) instead of overflowing the stack, which aborts the
/// process.
/// </summary>
[TestClass]
public class WriteDepthTests
{
    private const int SmallStack = 256 * 1024;

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
            throw new AssertFailedException(error.ToString());
    }

    private static void TooDeep<T>(T value)
    {
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(value), typeof(T).Name);
        StringAssert.StartsWith(e.Message, "Nino: ");
    }

    [TestMethod]
    public void SelfHoldingCollections()
    {
        OnSmallStack(() =>
        {
            var tree = new SelfTree();
            tree.Add(tree);
            TooDeep(tree);
            TooDeep(new SelfNestingHolder { T = tree });

            var map = new SelfMap();
            map["self"] = map;
            TooDeep(map);

            TooDeep(Nest(100_000));
        });

        // Deep, without a cycle, within the limit (on a thread's usual stack): written and read back.
        var legal = NinoDeserializer.Deserialize<SelfTree>(NinoSerializer.Serialize(Nest(400)));
        int levels = 1;
        for (var node = legal; node.Count != 0; node = node[0])
            levels++;
        Assert.AreEqual(400, levels);
    }

    [TestMethod]
    public void MillionDeepOnAnOrdinaryStack()
    {
        // A thread of the default size (1 MB): a million levels would overflow it many times over.
        Exception error = null;
        var thread = new Thread(() =>
        {
            try
            {
                TooDeep(Nest(1_000_000));
                TooDeep(new SelfNestingHolder { T = Nest(1_000_000) });
                TooDeep(Chain(1_000_000));
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.Start();
        thread.Join();
        if (error != null)
            throw new AssertFailedException(error.ToString());
    }

    [TestMethod]
    public void UpToTheLimitRoundTrips()
    {
        // NinoSettings.MaxDepth nested levels (below the top-level tree, which is not counted, on either side) are
        // written and read; one more is not written.
        int max = NinoSettings.MaxDepth;
        var back = NinoDeserializer.Deserialize<SelfTree>(NinoSerializer.Serialize(Nest(max + 1)));
        int levels = 1;
        for (var node = back; node.Count != 0; node = node[0])
            levels++;
        Assert.AreEqual(max + 1, levels);
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(Nest(max + 2)));
        StringAssert.Contains(e.Message, max.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void StructsAndClassesThatCannotBeDeferredCountTogether()
    {
        // 300 struct levels around 300 records: each under the limit alone, 600 together. The reader counts them
        // together, so the writer does too: what it writes, the reader reads.
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize(Mixed(300, 300)));
        var legal = Mixed(200, 200);
        var back = NinoDeserializer.Deserialize<WriteDepthMixedNode>(NinoSerializer.Serialize(legal));
        int structs = 1;
        var node = back;
        for (; node.Kids.Count != 0; node = node.Kids[0])
            structs++;
        int records = 0;
        for (var record = node.Record; record != null; record = record.Next)
            records++;
        Assert.AreEqual((200, 200), (structs, records));
    }

    private static WriteDepthMixedNode Mixed(int structs, int records)
    {
        WriteDepthRecord record = null;
        for (int i = 0; i < records; i++)
            record = new WriteDepthRecord(i, record);
        var node = new WriteDepthMixedNode { V = structs, Kids = new List<WriteDepthMixedNode>(), Record = record };
        for (int i = structs - 1; i > 0; i--)
            node = new WriteDepthMixedNode { V = i, Kids = new List<WriteDepthMixedNode> { node } };
        return node;
    }

    private static SelfTree Nest(int depth)
    {
        var root = new SelfTree();
        var node = root;
        for (int i = 1; i < depth; i++)
        {
            var next = new SelfTree();
            node.Add(next);
            node = next;
        }

        return root;
    }

    [TestMethod]
    public void SelfHoldingStructs()
    {
        OnSmallStack(() =>
        {
            // A struct holds itself through a list, which is shared: the copy in the list holds the same list.
            var kids = new List<UntrustedStructNode>();
            var node = new UntrustedStructNode { V = 1, Kids = kids };
            kids.Add(node);
            TooDeep(node);
            TooDeep(Chain(100_000));
        });

        var chain = Chain(300);
        var legal = NinoDeserializer.Deserialize<UntrustedStructNode>(NinoSerializer.Serialize(chain));
        Assert.AreEqual(chain.V, legal.V);
    }

    private static UntrustedStructNode Chain(int depth)
    {
        var node = new UntrustedStructNode { V = depth, Kids = new List<UntrustedStructNode>() };
        for (int i = depth - 1; i > 0; i--)
            node = new UntrustedStructNode { V = i, Kids = new List<UntrustedStructNode> { node } };
        return node;
    }
}
