using System;
using System.Collections.Generic;
using Nino.Core;

namespace Samples
{
    public enum Rank : byte { None, Bronze, Silver, Gold }

    [Flags]
    public enum Access { Read = 1, Write = 2, Admin = 4 }

    public enum Big : long { Small = 1, Huge = 1L << 40 }

    [NinoType]
    public struct Vec2
    {
        public float X;
        public float Y;
    }

    public readonly record struct PlayerId(long Value);

    /// <summary>A struct that is not raw (it holds a string): written member by member, without a header.</summary>
    [NinoType]
    public struct Tag
    {
        public string Name;
        public int Weight;
    }

    [NinoType]
    public sealed class Player
    {
        public PlayerId Id;
        public string Name;
        [NinoUtf8] public string Motto;
        public Rank Rank;
        public Access Access;
        public Big Big;
        public bool Online;
        public char Initial;
        public double Score;
        public decimal Balance;
        public Guid Session;
        public DateTime Joined;
        public TimeSpan Played;
        public List<Vec2> Path;
        public int[] Levels;
        public byte[] Avatar;
        public Dictionary<string, int[]> Inventory;
        public HashSet<int> Friends;
        public int? Maybe;
        public long?[] Gaps;
        public (int, string) Pair;
        public Tuple<int> Boxed;
        public KeyValuePair<string, Vec2> Spawn;
        public Tag Tag;
        public Animal Pet;
        public List<Animal> Zoo;
        public IShape Shape;
        public object Anything;
        public Saved Saved;
        public Tolerant Tolerant;
    }

    [NinoType]
    public abstract class Animal
    {
        public string Name;
    }

    [NinoType]
    public class Dog : Animal
    {
        public int Bones;
    }

    [NinoType]
    public sealed class Puppy : Dog
    {
        public bool Sleepy;
    }

    [NinoType]
    public sealed class Cat : Animal
    {
        public float Lives;
    }

    [NinoType]
    public interface IShape
    {
        double Area { get; set; }
    }

    [NinoType]
    public sealed class Square : IShape
    {
        public double Area { get; set; }
        public double Side;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Saved
    {
        [NinoFormerName("Hp")] public int Health;
        public string Note;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class Tolerant
    {
        public int A;
        public string B;
    }

    /// <summary>A raw struct with padding (3 bytes after A), which Nino writes zeroed.</summary>
    [NinoType]
    public struct Padded
    {
        public byte A;
        public int B;
    }

    /// <summary>Members of the shapes Player has none of, each read and written its own way in TypeScript.</summary>
    [NinoType]
    public sealed class Extras
    {
        public Padded Padded;
        public Padded[] PaddedArray;
        public (byte, long) RawPair;
        public int?[] Maybes;
        public (int?, string) NullablePair;
        public Dictionary<int, Vec2> Spots;
        public Dictionary<int, string> Names;
        public HashSet<long> Ids;
        public List<string> Words;
        public Rank[] Ranks;
        public string Text;
        [NinoUtf8] public string Ascii;
        public Guid[] Guids;
        public decimal[] Decimals;
    }

    // Deep nesting: past 128 open class bodies, a body is deferred (see "Deep nesting" in docs/v4-design.md).

    /// <summary>Deferred bodies follow the top-level object.</summary>
    [NinoType]
    public sealed class Chain
    {
        public int Value;
        public Chain Next;
    }

    /// <summary>Deferred behind a type id: the id is written in place, the body later.</summary>
    [NinoType]
    public class Link
    {
        public string Name;
        public Link Next;
    }

    [NinoType]
    public sealed class BigLink : Link
    {
        public long Extra;
    }

    /// <summary>Deferred inside a region: in a section at the end of the outermost one.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantChain
    {
        public int Value;
        public TolerantChain Next;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedChain
    {
        public int Value;
        public KeyedChain Next;
        public List<KeyedChain> More;
    }

    /// <summary>A tree: deferred bodies in lists.</summary>
    [NinoType]
    public sealed class Tree
    {
        public string Name;
        public List<Tree> Children;
    }

    /// <summary>Built by its constructor: never deferred, nested in place up to 512 bodies.</summary>
    [NinoType]
    public sealed record RecordChain(int Value, RecordChain Next);

    /// <summary>A struct that holds itself: no class body counts its nesting.</summary>
    [NinoType]
    public struct Nest
    {
        public int Value;
        public List<Nest> Inner;
    }

    /// <summary>All of them, in a struct.</summary>
    [NinoType]
    public struct Deep
    {
        public Chain Chain;
        public Link Link;
        public TolerantChain Tolerant;
        public KeyedChain Keyed;
        public Tree Tree;
        public RecordChain Record;
        public Nest Nest;
    }

    // Generic types: generated as generic types, read and written with the codecs of their type arguments.

    /// <summary>One type parameter.</summary>
    [NinoType]
    public sealed class Box<T>
    {
        public string Label;
        public T Value;
    }

    /// <summary>Two type parameters, in a struct (not raw: its formatter counts its nesting).</summary>
    [NinoType]
    public struct Duo<TFirst, TSecond>
    {
        public TFirst First;
        public TSecond Second;
    }

    /// <summary>Type parameters in collections and in other generic types, in a keyed body.</summary>
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Many<T>
    {
        public List<T> Items;
        public Dictionary<string, T> ByName;
        public Box<T> Inner;
        public Duo<int, T> Pair;
        public Many<T> Next;
        public (int, T) Tuple;
    }

    /// <summary>Closed generic types as members: one and two arguments, nested, in collections.</summary>
    [NinoType]
    public sealed class Generics
    {
        public Box<int> Number;
        public Box<string> Text;
        public Duo<long, string> Duo;
        public Box<Box<int>> Nested;
        public Box<Duo<string, Vec2>> Mixed;
        public List<Box<int>> Boxes;
        public Many<Box<long>> Many;
        public Box<int[]> Array;
        public Dictionary<string, Duo<int, Box<string>>> ByName;
        public Box<List<Tag>> Tags;
        public Box<(int, string)> Tuple;
        public Box<int?> Maybe;
        public Box<KeyValuePair<string, long>> Entry;
        public Many<int> Ints;
    }

    /// <summary>
    /// A generic subtype: written behind the id of its closed .NET name (Crate&lt;int&gt;), so a slot allows the closed
    /// types the assembly names (<see cref="Pens"/>'s values, in GenericSamples).
    /// </summary>
    [NinoType]
    public sealed class Crate<T> : Animal
    {
        public T Content;
    }

    /// <summary>A generic struct behind an interface: its closed types sit in the slots of IShape and object.</summary>
    [NinoType]
    public struct Patch<T> : IShape
    {
        public double Area { get; set; }
        public T Value;
    }

    /// <summary>Slots holding closed generic types next to the other types they allow.</summary>
    [NinoType]
    public sealed class Pens
    {
        public Animal Star;
        public List<Animal> Animals;
        public IShape Shape;
        public List<IShape> Shapes;
        public object Anything;
    }

    /// <summary>Reference tracking: a node may hold itself, and the same node is written once.</summary>
    [NinoType(trackReferences: true)]
    public sealed class Node
    {
        public Node Next;
    }

    /// <summary>Left out of TypeScript (<c>Half</c> has no TypeScript form), with a warning.</summary>
    [NinoType]
    public sealed class Halves
    {
        public Half Value;
    }
}
