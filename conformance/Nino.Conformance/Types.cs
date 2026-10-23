using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Nino.Core;

// The sample types of the corpus. Every wire feature a runtime outside .NET can support is reachable from one of the
// root types the cases name. Renaming or reordering anything here changes the corpus (type ids hash the full name).
namespace Conformance
{
    // ------------------------------------------------------------------ enums, every underlying type

    public enum E8 : sbyte { Min = sbyte.MinValue, Zero = 0, Max = sbyte.MaxValue }
    public enum EU8 : byte { Zero = 0, Max = byte.MaxValue }
    public enum E16 : short { Min = short.MinValue, Max = short.MaxValue }
    public enum EU16 : ushort { Zero = 0, Max = ushort.MaxValue }
    public enum E32 { Min = int.MinValue, Zero = 0, Max = int.MaxValue }
    public enum EU32 : uint { Zero = 0, Max = uint.MaxValue }
    public enum E64 : long { Min = long.MinValue, Max = long.MaxValue }
    public enum EU64 : ulong { Zero = 0, Max = ulong.MaxValue }

    [Flags]
    public enum Permissions : byte { None = 0, Read = 1, Write = 2, Execute = 4, All = Read | Write | Execute }

    [Flags]
    public enum WideFlags : ulong { None = 0, Low = 1, High = 1UL << 63 }

    // ------------------------------------------------------------------ primitives

    [NinoType]
    public sealed class Integers
    {
        public sbyte I8;
        public byte U8;
        public short I16;
        public ushort U16;
        public int I32;
        public uint U32;
        public long I64;
        public ulong U64;
        public Int128 I128;
        public UInt128 U128;
    }

    [NinoType]
    public sealed class Floats
    {
        public Half F16;
        public float F32;
        public double F64;
    }

    [NinoType]
    public sealed class Scalars
    {
        public bool Flag;
        public char Char;
        public decimal Decimal;
        public Guid Guid;
        public TimeSpan Span;
        public DateOnly Date;
        public TimeOnly Time;
    }

    [NinoType]
    public sealed class Dates
    {
        public DateTime Utc;
        public DateTime Local;
        public DateTime Unspecified;
        public DateTimeOffset Offset;
    }

    [NinoType]
    public sealed class Enums
    {
        public E8 A;
        public EU8 B;
        public E16 C;
        public EU16 D;
        public E32 E;
        public EU32 F;
        public E64 G;
        public EU64 H;
        public Permissions Flags;
        public WideFlags Wide;
        public E32[] Array;
    }

    [NinoType]
    public sealed class BigValues
    {
        public BigInteger Big;
        public Version Version;
    }

    // ------------------------------------------------------------------ strings and bytes

    [NinoType]
    public sealed class Strings
    {
        public string Text;
        [NinoUtf8] public string Utf8;
    }

    [NinoType]
    public sealed class StringCollections
    {
        public string[] Array;
        public List<string> List;
        [NinoUtf8] public string Utf8A;
        public string Plain;
        [NinoUtf8] public string Utf8B;
    }

    [NinoType]
    public sealed class Bytes
    {
        public byte[] Array;
        public List<byte> List;
        public sbyte[] Signed;
    }

    // ------------------------------------------------------------------ objects and structs

    [NinoType]
    public sealed class Point
    {
        public int X;
        public int Y;
    }

    /// <summary>Raw: written as its 8 bytes.</summary>
    [NinoType]
    public struct Vec2
    {
        public float X;
        public float Y;
    }

    /// <summary>Raw with padding: 1 byte, 7 zeroed bytes, 8 bytes (canonical 64-bit layout).</summary>
    [NinoType]
    public struct Padded
    {
        public byte A;
        public long B;
    }

    /// <summary>Raw with padding in the middle and at the end: byte, pad 1, short, int, byte, pad 3.</summary>
    [NinoType]
    public struct Mixed
    {
        public byte A;
        public short B;
        public int C;
        public byte D;
    }

    /// <summary>Raw, packed: no padding.</summary>
    [NinoType]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Packed
    {
        public byte A;
        public long B;
        public short C;
    }

    /// <summary>Raw, with an explicit size larger than its fields (trailing zero bytes).</summary>
    [NinoType]
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public struct Sized
    {
        public int A;
    }

    /// <summary>Raw, holding raw structs and an enum.</summary>
    [NinoType]
    public struct Nested
    {
        public Padded Inner;
        public Vec2 Position;
        public EU8 Kind;
        public bool Flag;
    }

    /// <summary>Not raw (holds a string): member by member, no header.</summary>
    [NinoType]
    public struct Label
    {
        public string Name;
        public int Weight;
    }

    [NinoType]
    public sealed class Structs
    {
        public Vec2 Vec;
        public Padded Padded;
        public Mixed Mixed;
        public Packed Packed;
        public Sized Sized;
        public Nested Nested;
        public Label Label;
        public Vec2[] VecArray;
        public List<Padded> PaddedList;
        public Mixed[] MixedArray;
        public Label[] Labels;
        public (byte, long) RawTuple;
        public (int, string) Tuple;
        public KeyValuePair<string, Vec2> Pair;
    }

    [NinoType]
    public sealed class Outer
    {
        public string Name;
        public Point Point;
        public Middle Middle;
    }

    [NinoType]
    public sealed class Middle
    {
        public Point First;
        public Point Second;
        public Inner Inner;
    }

    [NinoType]
    public sealed class Inner
    {
        public long Value;
        public string Text;
        public Point Point;
    }

    // ------------------------------------------------------------------ collections

    [NinoType]
    public sealed class Collections
    {
        public int[] Ints;
        public List<int> IntList;
        public HashSet<int> IntSet;
        public long[] Longs;
        public double[] Doubles;
        public bool[] Bools;
        public char[] Chars;
        public List<string> Strings;
        public HashSet<string> StringSet;
        public Point[] Points;
        public List<Point> PointList;
        public Dictionary<int, int> IntMap;
        public Dictionary<string, string> StringMap;
        public Dictionary<string, Point> PointMap;
        public Dictionary<int, List<string>> ListMap;
        public SortedDictionary<int, string> SortedInts;
        public SortedDictionary<string, int> SortedStrings;
        public List<List<int>> Nested;
        public int[][] Jagged;
        public int[,] Grid;
        public string[,] StringGrid;
    }

    // ------------------------------------------------------------------ nullables

    [NinoType]
    public sealed class Nullables
    {
        public int? Int;
        public long? Long;
        public double? Double;
        public bool? Bool;
        public Guid? Guid;
        public E16? Enum;
        public Vec2? Vec;
        public Padded? Padded;
        public Label? Label;
        public int?[] IntArray;
        public List<long?> LongList;
        public Padded?[] PaddedArray;
        public Dictionary<int, int?> Map;
    }

    // ------------------------------------------------------------------ version tolerance and keyed members

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantV1
    {
        public int A;
        public string B;
    }

    /// <summary><see cref="TolerantV1"/> with members appended.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantV2
    {
        public int A;
        public string B;
        public long C;
        public List<int> D;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantEmpty
    {
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class TolerantBase
    {
        public int A;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantLeaf : TolerantBase
    {
        public int B;
    }

    [NinoType]
    public sealed class TolerantHolder
    {
        public TolerantV1 First;
        public TolerantBase Polymorphic;
        public TolerantEmpty Empty;
        public int After;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedV1
    {
        public int Health;
        public string Name;
        public List<int> Items;
        public long Score;
    }

    /// <summary>
    /// <see cref="KeyedV1"/> reordered, with Health renamed to Hp, Items removed and Level added: reads data of
    /// either version.
    /// </summary>
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedV2
    {
        public long Score;
        public string Name;
        [NinoFormerName("Health")] public int Hp;
        public int Level;
    }

    [NinoType(format: NinoFormat.Keyed)]
    public struct KeyedStruct
    {
        public int A;
        public double B;
    }

    [NinoType]
    public sealed class KeyedHolder
    {
        public KeyedV1 Keyed;
        public KeyedStruct Struct;
        public KeyedV1[] Array;
    }

    // ------------------------------------------------------------------ polymorphism

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

    /// <summary>A generic subtype: its id is computed from its type arguments.</summary>
    [NinoType]
    public sealed class Box<T> : Animal
    {
        public T Value;
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

    [NinoType]
    public class Circle : IShape
    {
        public double Area { get; set; }
        public double Radius;
    }

    /// <summary>A non-sealed class with no subtype in use: still written behind a type id.</summary>
    [NinoType]
    public class Plain
    {
        public int Value;
    }

    [NinoType]
    public sealed class Zoo
    {
        public Animal Star;
        public Dog Dog;
        public List<Animal> Animals;
        public Animal[] Cages;
        public IShape Shape;
        public List<IShape> Shapes;
        public Plain Plain;
        public object Anything;
        public Dictionary<string, Animal> ByName;
    }

    // ------------------------------------------------------------------ deep nesting

    /// <summary>A sealed recursive class: bodies past 128 open ones are deferred to after the top-level object.</summary>
    [NinoType]
    public sealed class Chain
    {
        public int Value;
        public Chain Next;
    }

    /// <summary>Recursive and version tolerant: deferred bodies go to the outermost region's section.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantChain
    {
        public int Value;
        public TolerantChain Next;
    }

    /// <summary>Recursive and keyed: deferred bodies go to the outermost region's section.</summary>
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedChain
    {
        public int Value;
        public KeyedChain Next;
    }

    /// <summary>Recursive through a list.</summary>
    [NinoType]
    public sealed class TreeNode
    {
        public int Value;
        public List<TreeNode> Children;
    }

    /// <summary>Recursive and polymorphic: written behind type ids.</summary>
    [NinoType]
    public abstract class Link
    {
        public int Value;
        public Link Next;
    }

    [NinoType]
    public sealed class RedLink : Link
    {
    }

    [NinoType]
    public sealed class BlueLink : Link
    {
        public string Note;
    }

    /// <summary>Built by its constructor: never deferred, nests inline up to the depth limit (512 open bodies).</summary>
    [NinoType]
    public sealed record RecordChain(int Value, RecordChain Next);

    /// <summary>A recursive tolerant class holding a positional recursive one: a deferral inside a region.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class TolerantRoot
    {
        public string Name;
        public Chain Chain;
        public int After;
    }

    // ------------------------------------------------------------------ reference tracking (optional feature)

    [NinoType(trackReferences: true)]
    public class Tracked
    {
        public int Value;
        public Tracked Next;
        public Tracked Other;
    }

    [NinoType(trackReferences: true)]
    public sealed class TrackedLeaf : Tracked
    {
        public string Name;
    }

    [NinoType]
    public sealed class TrackedHolder
    {
        public Tracked A;
        public Tracked B;
        public List<Tracked> All;
        public object Any;
    }

    // ------------------------------------------------------------------ self-nesting collections

    /// <summary>A collection of itself: no class body in between, so nothing counts its depth but the stack.</summary>
    public class Tree : List<Tree>
    {
    }

    public class DictTree : Dictionary<string, DictTree>
    {
    }

    [NinoType]
    public sealed class TreeHolder
    {
        public Tree Tree;
        public DictTree DictTree;
    }

    // ------------------------------------------------------------------ everything empty

    [NinoType]
    public sealed class Empty
    {
    }

    [NinoType]
    public struct EmptyStruct
    {
    }

    [NinoType]
    public sealed class Empties
    {
        public Empty Class;
        public EmptyStruct Struct;
        public EmptyStruct[] Structs;
        public List<Empty> Classes;
    }
}

// Small roots the malicious cases are forged against: one member each, after the sealed class's null flag.
namespace Conformance.Targets
{
    [NinoType] public sealed class IntArray { public int[] Values; }
    [NinoType] public sealed class LongList { public List<long> Values; }
    [NinoType] public sealed class PaddedArray { public Padded[] Values; }
    [NinoType] public sealed class StringList { public List<string> Values; }
    [NinoType] public sealed class PointList { public List<Point> Values; }
    [NinoType] public sealed class ByteArray { public byte[] Values; }
    [NinoType] public sealed class IntMap { public Dictionary<int, int> Values; }
    [NinoType] public sealed class StringMap { public Dictionary<string, int> Values; }
    [NinoType] public sealed class IntSet { public HashSet<int> Values; }
    [NinoType] public sealed class StringSet { public HashSet<string> Values; }
    [NinoType] public sealed class SortedMap { public SortedDictionary<int, int> Values; }
    [NinoType] public sealed class Grid { public int[,] Values; }
    [NinoType] public sealed class TextValue { public string Value; }
    [NinoType] public sealed class Utf8Value { [NinoUtf8] public string Value; }
    [NinoType] public sealed class BoolValue { public bool Value; }
    [NinoType] public sealed class BoolArray { public bool[] Values; }
    [NinoType] public sealed class MaybeInt { public int? Value; }
    [NinoType] public sealed class MaybeIntArray { public int?[] Values; }
    [NinoType] public sealed class AnimalSlot { public Animal Value; }
    [NinoType] public sealed class DogSlot { public Dog Value; }
    [NinoType] public sealed class ShapeSlot { public IShape Value; }
    [NinoType] public sealed class ObjectSlot { public object Value; }
    [NinoType] public sealed class BigSlot { public BigInteger Value; }
    [NinoType] public sealed class VersionSlot { public Version Value; }
}
