using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

// Collections that hold themselves, with no [NinoType] body in between to count the depth.
public sealed class SelfTree : List<SelfTree> { }

public sealed class SelfMap : Dictionary<string, SelfMap> { }

public sealed class SelfSet : HashSet<SelfSet> { }

public sealed class SelfQueue : Queue<SelfQueue> { }

public sealed class SelfStack : Stack<SelfStack> { }

public sealed class SelfLinked : LinkedList<SelfLinked> { }

public sealed class SelfSorted : SortedDictionary<int, SelfSorted> { }

public sealed class SelfConcurrent : ConcurrentDictionary<int, SelfConcurrent> { }

public sealed class SelfArrays : List<SelfArrays[]> { }

// Only ever read through its Type: the formatter the resolver builds at run time, which no generated code names.
public sealed class SelfRuntimeOnly : List<SelfRuntimeOnly> { }

[NinoType]
public sealed class SelfNestingHolder
{
    public SelfTree T;
    public SelfMap D;
}

[NinoType]
public sealed class SelfNestingKinds
{
    public SelfSet Set;
    public SelfQueue Queue;
    public SelfStack Stack;
    public SelfLinked Linked;
    public SelfSorted Sorted;
    public SelfConcurrent Concurrent;
    public SelfArrays Arrays;
}

/// <summary>
/// Data nesting a collection that holds itself deeper than NinoDepth.Max throws InvalidOperationException, as
/// [NinoType] bodies do, instead of overflowing the stack (which aborts the process).
/// </summary>
[TestClass]
public class SelfNestingCollectionTests
{
    private const int Hostile = 100_000;
    private const int Legal = 400; // under NinoDepth.Max (512), with the holder's body

    private static byte[] Repeat(byte[] head, byte[] level, int count, byte[] tail)
    {
        var bytes = new byte[head.Length + level.Length * count + tail.Length];
        head.CopyTo(bytes, 0);
        for (int i = 0; i < count; i++)
            level.CopyTo(bytes, head.Length + i * level.Length);
        tail.CopyTo(bytes, head.Length + level.Length * count);
        return bytes;
    }

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
        }, 512 * 1024);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new AssertFailedException("failed on the small stack: " + error, error);
    }

    [TestMethod]
    public void SelfHoldingListAndDictionaryThrowPastTheLimit()
    {
        // The audit's two shapes: a list one element wide, and a map of one entry, nested a million deep.
        Check(d => new SelfNestingHolder { T = Nest<SelfTree>(d, (o, i) => o.Add(i)) }, h => h.T,
            t => t.FirstOrDefault(), 1_000_000);
        Check(d => new SelfNestingHolder { D = Nest<SelfMap>(d, (o, i) => o.Add("", i)) }, h => h.D,
            m => m.Count == 0 ? null : m[""], 1_000_000);

        // The list as the audit wrote it: the holder's body length, then counts of 1, the innermost list empty and
        // the map null.
        CollectionAssert.AreEqual(
            Convert.FromHexString("01" + "10000000" + "01000000" + "01000000" + "00000000" + "FFFFFFFF"),
            NinoSerializer.Serialize(new SelfNestingHolder { T = Nest<SelfTree>(3, (o, i) => o.Add(i)) }));
    }

    [TestMethod]
    public void SelfHoldingCollectionsAsTheTopLevelType()
    {
        var tree = Nest<SelfTree>(Legal, (outer, inner) => outer.Add(inner));
        Assert.AreEqual(Legal, Depth(NinoDeserializer.Deserialize<SelfTree>(NinoSerializer.Serialize(tree)),
            t => t.FirstOrDefault()));
        var hostile = new byte[4 * Hostile + 4];
        for (int i = 0; i < Hostile; i++)
            hostile[4 * i] = 1;
        OnSmallStack(() =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<SelfTree>(hostile));
            Assert.ThrowsExactly<InvalidOperationException>(
                () => NinoDeserializer.DeserializeBoxed(hostile, typeof(SelfRuntimeOnly)));
        });

        var runtimeOnly = Nest<SelfRuntimeOnly>(Legal, (outer, inner) => outer.Add(inner));
        var read = (SelfRuntimeOnly)NinoDeserializer.DeserializeBoxed(
            NinoSerializer.SerializeBoxed(runtimeOnly, typeof(SelfRuntimeOnly)), typeof(SelfRuntimeOnly));
        Assert.AreEqual(Legal, Depth(read, t => t.FirstOrDefault()));
    }

    [TestMethod]
    public void EveryCollectionKindThrowsPastTheLimit()
    {
        Check(d => new SelfNestingKinds { Set = Nest<SelfSet>(d, (o, i) => o.Add(i)) }, k => k.Set,
            s => s.FirstOrDefault());
        Check(d => new SelfNestingKinds { Queue = Nest<SelfQueue>(d, (o, i) => o.Enqueue(i)) }, k => k.Queue,
            q => q.FirstOrDefault());
        Check(d => new SelfNestingKinds { Stack = Nest<SelfStack>(d, (o, i) => o.Push(i)) }, k => k.Stack,
            s => s.FirstOrDefault());
        Check(d => new SelfNestingKinds { Linked = Nest<SelfLinked>(d, (o, i) => o.AddLast(i)) }, k => k.Linked,
            l => l.First?.Value);
        Check(d => new SelfNestingKinds { Sorted = Nest<SelfSorted>(d, (o, i) => o.Add(1, i)) }, k => k.Sorted,
            s => s.Count == 0 ? null : s[1]);
        Check(d => new SelfNestingKinds { Concurrent = Nest<SelfConcurrent>(d, (o, i) => o[1] = i) },
            k => k.Concurrent, c => c.TryGetValue(1, out var next) ? next : null);
        Check(d => new SelfNestingKinds { Arrays = Nest<SelfArrays>(d, (o, i) => o.Add(new[] { i })) }, k => k.Arrays,
            a => a.Count == 0 ? null : a[0][0]);
    }

    /// <summary>
    /// Writes a holder with one member nested 1, 2 and 3 deep and takes the bytes one level adds from that (and the
    /// lengths in front of them that grow with it). The same bytes nested <paramref name="hostile"/> deep throw; nested
    /// <see cref="Legal"/> deep, they read.
    /// </summary>
    private static void Check<THolder, TCollection>(Func<int, THolder> make, Func<THolder, TCollection> get,
        Func<TCollection, TCollection> inner, int hostile = Hostile) where TCollection : class
    {
        string name = typeof(TCollection).Name;
        var one = NinoSerializer.Serialize(make(1));
        var two = NinoSerializer.Serialize(make(2));
        var three = NinoSerializer.Serialize(make(3));
        int width = two.Length - one.Length;
        Assert.IsTrue(width > 0, name);
        bool Grows(int p) => p >= 0 && p + 4 <= one.Length && BitConverter.ToInt32(two, p) ==
            BitConverter.ToInt32(one, p) + width && BitConverter.ToInt32(three, p) == BitConverter.ToInt32(one, p) + 2 * width;
        // Where the first level starts: the first byte that differs, past the lengths that grow with the nesting.
        int at = 0;
        while (true)
        {
            while (one[at] == two[at])
                at++;
            int length = at - 3;
            while (length <= at && !Grows(length))
                length++;
            if (length > at)
                break;
            at = length + 4;
        }

        byte[] Nested(int levels)
        {
            var head = one.AsSpan(0, at).ToArray();
            // Lengths in front (the holder's body): each level adds its width.
            for (int p = 0; p + 4 <= at; p++)
            {
                if (Grows(p))
                    BitConverter.TryWriteBytes(head.AsSpan(p), BitConverter.ToInt32(one, p) + (levels - 1) * width);
            }

            return Repeat(head, two.AsSpan(at, width).ToArray(), levels - 1, one.AsSpan(at).ToArray());
        }

        CollectionAssert.AreEqual(three, Nested(3), name);
        var bytes = Nested(hostile);
        OnSmallStack(() =>
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(
                () => NinoDeserializer.Deserialize<THolder>(bytes), name);
            StringAssert.Contains(error.Message, "deep", name);
        });

        var read = NinoDeserializer.Deserialize<THolder>(Nested(Legal));
        Assert.AreEqual(Legal, Depth(get(read), inner), name);
    }

    /// <summary>A collection nested <paramref name="depth"/> deep, the innermost one empty.</summary>
    private static T Nest<T>(int depth, Action<T, T> add) where T : new()
    {
        var current = new T();
        for (int i = 1; i < depth; i++)
        {
            var outer = new T();
            add(outer, current);
            current = outer;
        }

        return current;
    }

    private static int Depth<T>(T value, Func<T, T> inner) where T : class
    {
        int depth = 0;
        for (; value != null; value = inner(value))
            depth++;
        return depth;
    }
}
