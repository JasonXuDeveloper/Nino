using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Nino.Core;

// Round trips of every kind of type Nino handles, run trimmed and with NativeAOT by CI. The hash of everything written
// is printed so the runs can be compared byte for byte (raw structs included: padding must be cleared the same way).

namespace Nino.AotTests;

[NinoType]
public class Plain
{
    public int Id;
    public string Name;
    public double Score { get; set; }
    public int[] Numbers;
    public List<string> Tags;
    public Dictionary<string, List<int>> Map;
    public int[][] Jagged;
    public HashSet<long> Set;
    public Point Where;
    public Point? MaybeWhere;
    public (int, string) Tuple;
    public KeyValuePair<int, string> Pair;
}

[NinoType]
public struct Point
{
    public float X;
    public float Y;
}

[NinoType]
public struct Padded
{
    public byte A;
    public long B;
    public byte C;
}

public struct Vec
{
    public float X;
    public double Y;
    public Inner In;
}

public struct Inner
{
    public short S;
    public long L;
}

public struct Vec2
{
    public int A;
    public long B;
}

[NinoType]
public class WithRaw
{
    public Padded P;
    public Vec V;
    public Vec[] Vs;
    public List<Padded> Ps;
}

[NinoType]
public abstract class Shape
{
    public string Label;
}

[NinoType]
public class Circle : Shape
{
    public float Radius;
}

[NinoType]
public class Rect : Shape
{
    public float W, H;
}

[NinoType]
public interface IAnimal
{
    string Name { get; set; }
}

[NinoType]
public class Dog : IAnimal
{
    public string Name { get; set; }
    public int Barks;
}

[NinoType]
public class Box<T>
{
    public T Value;
    public List<T> Items;
}

[NinoType(format: NinoFormat.Keyed)]
public class KeyedThing
{
    public int A;
    public string B;
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class TolerantThing
{
    public int A;
    public List<Shape> Shapes;
}

[NinoType, NinoPooled]
public sealed partial class Packet
{
    public int Id;
    public List<int> Values;

    private static partial IEnumerable<Packet> OnNinoPoolWarmup() => null;
    private partial void OnNinoPoolReturn() { }
}

[NinoType(trackReferences: true)]
public class Node
{
    public int Value;
    public Node Next;
    public List<Node> Children;
}

[NinoType]
public class Holder
{
    public Shape One;
    public List<Shape> Many;
    public Shape[] Arr;
    public IAnimal Pet;
    public Box<int> IntBox;
    public Box<Shape> ShapeBox;
    public Dictionary<int, Shape> ShapeMap;
    public Queue<int> Q;
    public Stack<string> S;
    public LinkedList<int> LL;
    public SortedDictionary<string, int> Sorted;
    public IList<int> IL;
    public IReadOnlyList<string> IRL;
    public IDictionary<string, int> ID;
    public List<Point> Points;
    public Point[] PointArr;
    public int?[] NullableArr;
    public List<int?> NullableList;
    public Dictionary<Point, Shape> StructKey;
}

// Nino.Immutable's formatters, reached from generated code (members) and top level. Hash-ordered collections have
// keys whose hash codes are the same in every process (not strings), so the payload hash compares across runs.
[NinoType]
public class Frozen
{
    public ImmutableArray<int> Array;
    public ImmutableList<string> List;
    public ImmutableHashSet<long> Set;
    public ImmutableSortedSet<string> Sorted;
    public ImmutableQueue<int> Queue;
    public ImmutableStack<string> Stack;
    public ImmutableDictionary<int, Shape> Map;
    public ImmutableSortedDictionary<int, string> SortedMap;
}

public static class Program
{
    private static int _failures;
    private static readonly MemoryStream AllBytes = new MemoryStream();

    private static byte[] Ser<T>(T value)
    {
        var bytes = NinoSerializer.Serialize(value);
        AllBytes.Write(bytes);
        return bytes;
    }

    // Generic code: the generator cannot see these closed types.
    private static List<T> GenericList<T>(List<T> list) =>
        NinoDeserializer.Deserialize<List<T>>(NinoSerializer.Serialize(list));

    private static SortedSet<T> GenericSet<T>(SortedSet<T> set) =>
        NinoDeserializer.Deserialize<SortedSet<T>>(NinoSerializer.Serialize(set));

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok)
            _failures++;
    }

    public static int Main()
    {
        var p = new Plain
        {
            Id = 7, Name = "seven", Score = 1.5, Numbers = new[] { 1, 2, 3 }, Tags = new List<string> { "a", "b" },
            Map = new Dictionary<string, List<int>> { ["x"] = new List<int> { 9, 8 } },
            Jagged = new[] { new[] { 1 }, new[] { 2, 3 } }, Set = new HashSet<long> { 5, 6 },
            Where = new Point { X = 1, Y = 2 }, MaybeWhere = new Point { X = 3, Y = 4 }, Tuple = (4, "four"),
            Pair = new KeyValuePair<int, string>(1, "one")
        };
        var p2 = NinoDeserializer.Deserialize<Plain>(Ser(p));
        Check(p2.Id == 7 && p2.Name == "seven" && p2.Score == 1.5 && p2.Numbers.SequenceEqual(p.Numbers)
              && p2.Tags.SequenceEqual(p.Tags) && p2.Map["x"].SequenceEqual(new[] { 9, 8 })
              && p2.Jagged[1].SequenceEqual(new[] { 2, 3 }) && p2.Set.SetEquals(p.Set)
              && p2.Where.Y == 2 && p2.MaybeWhere.Value.X == 3 && p2.Tuple == (4, "four") && p2.Pair.Value == "one",
            "plain class with collections");

        var pt = NinoDeserializer.Deserialize<Point>(Ser(new Point { X = 5, Y = 6 }));
        Check(pt.X == 5 && pt.Y == 6, "struct");

        Shape s = new Circle { Label = "c", Radius = 2 };
        var s2 = NinoDeserializer.Deserialize<Shape>(Ser(s));
        Check(s2 is Circle c && c.Radius == 2 && c.Label == "c", "polymorphic base -> subtype");

        var h = new Holder
        {
            One = new Rect { Label = "r", W = 1, H = 2 },
            Many = new List<Shape> { new Circle { Radius = 1 }, new Rect { W = 3 }, null },
            Arr = new Shape[] { new Rect { H = 9 } },
            Pet = new Dog { Name = "rex", Barks = 3 },
            IntBox = new Box<int> { Value = 4, Items = new List<int> { 1, 2 } },
            ShapeBox = new Box<Shape> { Value = new Circle { Radius = 8 }, Items = new List<Shape> { new Rect() } },
            ShapeMap = new Dictionary<int, Shape> { [1] = new Circle { Radius = 5 } },
            Q = new Queue<int>(new[] { 1, 2 }), S = new Stack<string>(new[] { "a", "b" }),
            LL = new LinkedList<int>(new[] { 7, 8 }),
            Sorted = new SortedDictionary<string, int> { ["b"] = 2, ["a"] = 1 },
            IL = new List<int> { 3, 4 }, IRL = new List<string> { "z" },
            ID = new Dictionary<string, int> { ["k"] = 1 },
            Points = new List<Point> { new Point { X = 1 } }, PointArr = new[] { new Point { Y = 2 } },
            NullableArr = new int?[] { 1, null }, NullableList = new List<int?> { null, 2 },
            StructKey = new Dictionary<Point, Shape> { [new Point { X = 1 }] = new Rect { W = 4 } },
        };
        var h2 = NinoDeserializer.Deserialize<Holder>(Ser(h));
        Check(h2.One is Rect r && r.H == 2 && h2.Many[0] is Circle && h2.Many[1] is Rect { W: 3 } && h2.Many[2] == null
              && h2.Arr[0] is Rect { H: 9 } && h2.Pet is Dog { Name: "rex", Barks: 3 }
              && h2.IntBox.Value == 4 && h2.IntBox.Items.SequenceEqual(new[] { 1, 2 })
              && h2.ShapeBox.Value is Circle { Radius: 8 } && h2.ShapeBox.Items[0] is Rect
              && h2.ShapeMap[1] is Circle { Radius: 5 } && h2.Q.SequenceEqual(new[] { 1, 2 })
              && h2.S.SequenceEqual(h.S) && h2.LL.SequenceEqual(new[] { 7, 8 }) && h2.Sorted["b"] == 2
              && h2.IL.SequenceEqual(new[] { 3, 4 }) && h2.IRL[0] == "z" && h2.ID["k"] == 1
              && h2.Points[0].X == 1 && h2.PointArr[0].Y == 2 && h2.NullableArr[1] == null && h2.NullableList[1] == 2
              && h2.StructKey[new Point { X = 1 }] is Rect { W: 4 },
            "polymorphic members, generic NinoType, collections, interfaces");

        var gb = NinoDeserializer.Deserialize<Box<string>>(Ser(new Box<string> { Value = "v", Items = new List<string> { "i" } }));
        Check(gb.Value == "v" && gb.Items[0] == "i", "generic NinoType top level");

        var k = NinoDeserializer.Deserialize<KeyedThing>(Ser(new KeyedThing { A = 1, B = "b" }));
        Check(k.A == 1 && k.B == "b", "keyed format");

        var t = NinoDeserializer.Deserialize<TolerantThing>(Ser(new TolerantThing { A = 2, Shapes = new List<Shape> { new Circle { Radius = 3 } } }));
        Check(t.A == 2 && t.Shapes[0] is Circle { Radius: 3 }, "version tolerant format");

        var pk = NinoDeserializer.Deserialize<Packet>(Ser(new Packet { Id = 1, Values = new List<int> { 1, 2 } }));
        Check(pk.Id == 1 && pk.Values.Count == 2, "pooled deserialize");
        pk.ReturnToPool();
        var pk2 = NinoDeserializer.Deserialize<Packet>(Ser(new Packet { Id = 2, Values = new List<int> { 3 } }));
        Check(ReferenceEquals(pk, pk2) && pk2.Id == 2 && pk2.Values.SequenceEqual(new[] { 3 }), "pooled instance reused");

        var a = new Node { Value = 1 };
        var b = new Node { Value = 2, Next = a };
        a.Next = b;
        a.Children = new List<Node> { b, a };
        var a2 = NinoDeserializer.Deserialize<Node>(Ser(a));
        Check(a2.Next.Next == a2 && a2.Children[0] == a2.Next && a2.Children[1] == a2 && a2.Next.Value == 2, "reference tracking (cycle)");

        var existing = new Plain { Id = 0, Tags = new List<string> { "old" } };
        var keepTags = existing.Tags;
        NinoDeserializer.Deserialize(Ser(p), ref existing);
        Check(existing.Id == 7 && existing.Tags.SequenceEqual(new[] { "a", "b" }) && ReferenceEquals(existing.Tags, keepTags), "deserialize into existing object (reuse)");

        var boxed = NinoSerializer.SerializeBoxed(new Circle { Radius = 6 }, typeof(Shape));
        var ub = NinoDeserializer.DeserializeBoxed(boxed, typeof(Shape));
        Check(ub is Circle { Radius: 6 }, "boxed serialize/deserialize");

        var lst = NinoDeserializer.Deserialize<List<Dictionary<string, int[]>>>(Ser(new List<Dictionary<string, int[]>> { new() { ["q"] = new[] { 1 } } }));
        Check(lst[0]["q"][0] == 1, "top-level nested built-in collections");

        var wr = new WithRaw
        {
            P = new Padded { A = 1, B = 2, C = 3 }, V = new Vec { X = 1.5f, Y = 2.5, In = new Inner { S = 7, L = 8 } },
            Vs = new[] { new Vec { X = 3, In = new Inner { L = 9 } } }, Ps = new List<Padded> { new Padded { C = 4 } }
        };
        var wr2 = NinoDeserializer.Deserialize<WithRaw>(Ser(wr));
        Check(wr2.P.A == 1 && wr2.P.B == 2 && wr2.P.C == 3 && wr2.V.Y == 2.5 && wr2.V.In.S == 7 && wr2.V.In.L == 8
              && wr2.Vs[0].In.L == 9 && wr2.Ps[0].C == 4, "raw structs with padding");
        var pd = NinoDeserializer.Deserialize<Padded>(Ser(new Padded { A = 5, B = 6, C = 7 }));
        Check(pd.A == 5 && pd.B == 6 && pd.C == 7, "padded raw NinoType struct top level");

        var fr = new Frozen
        {
            Array = ImmutableArray.Create(1, 2, 3), List = ImmutableList.Create("a", "b"),
            Set = ImmutableHashSet.Create(5L, 6L, 7L), Sorted = ImmutableSortedSet.Create("z", "y"),
            Queue = ImmutableQueue.Create(8, 9), Stack = ImmutableStack.Create("bottom", "top"),
            Map = ImmutableDictionary<int, Shape>.Empty.Add(1, new Circle { Radius = 2 }).Add(2, new Rect { W = 3 }),
            SortedMap = ImmutableSortedDictionary<int, string>.Empty.Add(2, "two").Add(1, "one"),
        };
        var fr2 = NinoDeserializer.Deserialize<Frozen>(Ser(fr));
        Check(fr2.Array.SequenceEqual(fr.Array) && fr2.List.SequenceEqual(fr.List) && fr2.Set.SetEquals(fr.Set)
              && fr2.Sorted.SequenceEqual(new[] { "y", "z" }) && fr2.Queue.SequenceEqual(fr.Queue)
              && fr2.Stack.SequenceEqual(fr.Stack) && fr2.Map[1] is Circle { Radius: 2 } && fr2.Map[2] is Rect { W: 3 }
              && fr2.SortedMap.Keys.SequenceEqual(new[] { 1, 2 }) && fr2.SortedMap[2] == "two",
            "immutable collections as members");

        var imap = NinoDeserializer.Deserialize<ImmutableDictionary<int, string>>(
            Ser(Enumerable.Range(0, 100).ToImmutableDictionary(i => i, i => i.ToString())));
        Check(imap.Count == 100 && imap[42] == "42", "immutable dictionary top level");
        var iset = NinoDeserializer.Deserialize<ImmutableHashSet<int>>(Ser(ImmutableHashSet.Create(4, 5)));
        Check(iset.Count == 2 && iset.Contains(5), "immutable hash set top level");
        var iarr = NinoDeserializer.Deserialize<ImmutableArray<Point>>(Ser(ImmutableArray.Create(new Point { X = 1, Y = 2 })));
        Check(iarr.Length == 1 && iarr[0].Y == 2, "immutable array of structs top level");

        Console.WriteLine("payload sha256: " + Convert.ToHexString(SHA256.HashData(AllBytes.ToArray())));

        // Types the generator cannot see (built at run time from generic code) need dynamic code; without it Nino
        // says so instead of failing in the runtime.
        try
        {
            var gl = GenericList(new List<Vec2> { new Vec2 { A = 1, B = 2 } });
            var gs = GenericSet(new SortedSet<long> { 3, 1, 2 });
            Check(gl[0].B == 2 && gs.SequenceEqual(new long[] { 1, 2, 3 }), "runtime-built formatters (generic code)");
        }
        catch (InvalidOperationException e) when (!RuntimeFeature.IsDynamicCodeSupported)
        {
            Check(e.Message.Contains("cannot build formatters at run time"), "runtime-built formatters refused without dynamic code: " + e.Message.Substring(0, 60));
        }

        Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
