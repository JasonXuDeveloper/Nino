using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

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
