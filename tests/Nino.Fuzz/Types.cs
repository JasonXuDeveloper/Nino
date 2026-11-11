using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Nino.Core;

// The root types the fuzzer reads, one per feature: Samples.cs builds a value of each, Targets.cs lists them.
namespace Nino.Fuzz
{
    public enum Color : byte { Red, Green, Blue }
    public enum Big : long { A = 1, B = long.MaxValue }

    // Raw structs (unmanaged, copied as their memory) and an empty one.
    [NinoType] public struct RawS { public int A; public bool B; public Color C; public long D; }
    [NinoType] public struct Pair2 { public byte A; public int B; }
    [NinoType] public struct Empty { }
    public struct PlainBlock { public long A; public int B; public short C; public byte D; }

    // BCL structs inside a raw struct, in the canonical layout (spec §5.2): tuples and pairs by 64-bit CoreCLR's
    // auto-layout rule, DateTimeOffset's int minutes, nint and nuint as 8 bytes, Int128 and the vectors aligned to
    // their size. A runtime whose memory layout differs converts each field (NinoLayout.Convert: .NET 8 narrows the
    // minutes to a short, rejecting what does not fit); Untrusted mode checks the dates, decimals and bools inside.
    public struct RawBcl
    {
        public byte A; public (int, long) T; public (byte, short) S; public KeyValuePair<int, long> P;
        public DateTimeOffset O; public nint N; public nuint U; public Int128 W; public decimal D; public DateTime Dt;
        public TimeSpan Ts; public Guid G; public DateOnly Do; public TimeOnly To;
        public bool B; public Half H; public (byte, DateTimeOffset) TO;
        public System.Runtime.Intrinsics.Vector64<int> V64; public System.Runtime.Intrinsics.Vector128<long> V128;
        public System.Runtime.Intrinsics.Vector256<long> V256; public System.Runtime.Intrinsics.Vector512<byte> V512;
    }

    // Nullables inside a raw struct: only Untrusted mode checks their flags (spec §9.3, §10 item 12). A flag of 2 read
    // in the default mode makes the value unequal to itself once the JIT tiers up (a long? boxed as null by one
    // compiled body and as a value by another), which the Raws target's dictionary checks report: RawsUntrusted.
    public struct RawFlags { public byte A; public long? L; public (int, long)? T; public DateTimeOffset? O; public bool B; }

    [NinoType]
    public sealed class RawUntrusted
    {
        public RawFlags One; public RawFlags[] Many; public List<RawFlags> List; public Dictionary<int, RawFlags> ById;
        public RawBcl Bcl; public RawBcl[] Bcls; public Dictionary<(int, long), RawBcl> ByTuple;
        // Last, so that the input ends with it: raw pairs whose values the vector checks read (a run that ends a key
        // short of a whole pair).
        public Dictionary<long, DateTime> Dates;
    }

    [NinoType]
    public sealed class RawRoot
    {
        public RawS One; public RawS[] Many; public List<Pair2> Pairs; public PlainBlock[] Blocks; public Empty[] Empties;
        public (byte, int)[] Tuples; public List<(short, long)> Wide; public Guid[] Guids; public decimal[] Decimals;
        public RawS? Maybe; public Dictionary<int, RawS> ById;
        public RawBcl Bcl; public RawBcl[] Bcls; public List<RawBcl> BclList; public RawBcl? MaybeBcl;
        public (byte, DateTimeOffset)[] DtoTuples; public KeyValuePair<long, (int, long)>[] PairArray;
        public Dictionary<(int, long), RawBcl> ByTuple;
    }

    // Deep nesting: positional, version tolerant and keyed chains.
    [NinoType] public sealed class Node { public int V; public Node Next; }
    [NinoType(format: NinoFormat.VersionTolerant)] public sealed class VNode { public int V; public VNode Next; public string S; }
    [NinoType(format: NinoFormat.Keyed)] public sealed class KNode { public int V; public KNode Next; public List<int> L; public string S; }
    [NinoType(format: NinoFormat.VersionTolerant)] public class VBase { public int A; public VBase Child; }
    [NinoType(format: NinoFormat.VersionTolerant)] public class VDerived : VBase { public string B; public List<VBase> Kids; }

    // Keyed members: each written with its name hash and length, unknown keys skipped.
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Keyed
    {
        public string Name;
        public int Id;
        public List<KeyedPart> Parts;
        public Dictionary<string, int> Tags;
        public KeyedStruct Inline;
    }

    [NinoType(format: NinoFormat.Keyed)] public sealed class KeyedPart { public long At; public string Note; }
    [NinoType(format: NinoFormat.Keyed)] public struct KeyedStruct { public double X; public int[] Ys; }

    // Polymorphism: abstract class, interface, object.
    [NinoType] public abstract class Shape { public int Id; }
    [NinoType] public class Circle : Shape { public double R; }
    [NinoType] public class Square : Shape { public float S; public Shape Inner; public List<Shape> More; }
    [NinoType] public interface IAnimal { }
    [NinoType] public class Dog : IAnimal { public string Name; public IAnimal Friend; }
    [NinoType] public class Cat : IAnimal { public int Lives; }

    // Reference tracking: cycles, shared objects, a subtype, an untracked type that points back.
    [NinoType(trackReferences: true)] public class TNode { public int V; public TNode Next; public TNode Other; public object Any; }
    [NinoType(trackReferences: true)] public class TLeaf : TNode { public string S; }
    [NinoType(trackReferences: true)] public sealed class TOther { public string S; public TNode Back; }

    // Explicit implementations of a [NinoType] interface's members, read and written through the interface: a class
    // (a cast) and a raw struct (constrained accessors) whose bool and DateTime are explicit, so its runs take the
    // raw paths and their checks.
    [NinoType] public interface ITagged { int Tag { get; set; } string Label { get; set; } List<int> Items { get; set; } }
    [NinoType] public interface IFlag { bool On { get; set; } DateTime At { get; set; } }

    [NinoType]
    public class Bird : IAnimal, ITagged
    {
        int ITagged.Tag { get; set; }
        string ITagged.Label { get; set; }
        List<int> ITagged.Items { get; set; }
        public IFlag Flag;
    }

    [NinoType]
    public struct Flag : IFlag
    {
        public byte Id;
        bool IFlag.On { get; set; }
        DateTime IFlag.At { get; set; }
    }

    [NinoType]
    public class Holder
    {
        public object O; public TNode T; public TOther TO; public Shape S; public IAnimal A; public List<object> Objs;
        public ITagged Tagged; public Flag F; public Flag[] Flags; public List<Bird> Birds;
    }

    [NinoType]
    public sealed class Prims
    {
        public bool B; public Color C; public Big Bg; public decimal Dec; public DateTime Dt; public DateTimeOffset Dto;
        public Guid G; public TimeSpan Ts; public char Ch; public double D; public int? NI; public RawS Raw;
        public string Str; [NinoUtf8] public string U8; public BigInteger BI; public Uri Uri; public Version Ver;
        public System.Globalization.CultureInfo Cul; public StringBuilder SB; public System.Collections.BitArray Bits;
        public (int, string) Tup; public KeyValuePair<int, string> Kvp; public Lazy<int> Lz; public Half H; public Int128 I128;
        public DateOnly DO; public TimeOnly TO; public Rune Ru;
    }

    // Strings in every encoding Nino writes: ASCII and Latin-1 (one byte a char), UTF-16, UTF-8.
    [NinoType]
    public sealed class Strings
    {
        public string Ascii; public string Latin1; public string Utf16; [NinoUtf8] public string Utf8;
        public string[] Many; [NinoUtf8] public List<string> ManyUtf8; public Dictionary<string, string> Map;
        public char[] Chars; public List<char> CharList; public string Empty; public string Null;
    }

    [NinoType]
    public sealed class Colls
    {
        public List<int> LI; public List<string> LS; public int[] AI; public string[] AS; public List<(byte, int)> LBI;
        public (short, int)[] ASI; public DateTimeOffset[] ADto; public List<RawS> LRaw; public Pair2[] AP;
        public HashSet<int> HI; public HashSet<string> HS; public HashSet<long> HL;
        public Dictionary<int, string> DIS; public Dictionary<long, int> DLI; public Dictionary<string, int> DSI; public Dictionary<int, int> DII;
        public SortedSet<int> SSI; public SortedSet<string> SSS; public SortedDictionary<int, int> SDI; public SortedDictionary<string, string> SDS;
        public SortedList<int, string> SLI; public SortedList<string, int> SLS; public SortedList<int, int> SLII;
        public PriorityQueue<int, int> PQ; public bool?[] ANB; public List<bool?> LNB;
        public LinkedList<int> LLI; public LinkedList<string> LLS; public Queue<int> QI; public Stack<string> StS; public Queue<string> QS;
        public ConcurrentDictionary<int, int> CD; public ConcurrentQueue<int> CQ; public ConcurrentStack<int> CS;
        public int[,] M2; public string[,,] M3; public List<int?> LNI; public int?[] ANI; public bool[] AB; public List<bool> LB;
        public Color[] AC; public List<List<int>> LLInt; public Dictionary<int, List<string>> DNested;
        public System.Linq.ILookup<int, string> Look; public IList<int> IL; public IEnumerable<string> IE; public IReadOnlyDictionary<int, int> IRD;
        public Memory<int> Mem; public ArraySegment<int> Seg;
    }

    [NinoType]
    public sealed class Imm
    {
        public ImmutableArray<int> IA; public ImmutableList<string> IL; public ImmutableHashSet<int> IHI; public ImmutableHashSet<string> IHS;
        public ImmutableDictionary<int, string> IDI; public ImmutableDictionary<string, int> IDS; public ImmutableSortedSet<int> ISS;
        public ImmutableSortedDictionary<string, int> ISD; public ImmutableQueue<int> IQ; public ImmutableStack<string> IS;
        public ImmutableArray<string> IAS; public ImmutableSortedSet<string> ISSS; public ImmutableDictionary<long, Shape> IDShape;
    }

    // Nesting through collections, structs, nullables and tuples (each bounded by Nino's depth limit).
    [NinoType] public struct SNode { public int V; public List<SNode> Kids; }
    [NinoType] public sealed class Rec { public int V; public List<Rec> Kids; }
    [NinoType] public struct SRec { public int V; public SRec[] Kids; }
    [NinoType] public sealed class LazyRec { public Lazy<LazyRec> L; }
    [NinoType] public sealed class KvRec { public KeyValuePair<int, KvRec> P; }
    [NinoType] public sealed class DictRec { public Dictionary<int, DictRec> D; }
    [NinoType] public sealed class TupleRec { public Tuple<int, TupleRec> T; }
    [NinoType(format: NinoFormat.VersionTolerant)] public sealed class VRecList { public List<VRecList> K; }

    [NinoType]
    public sealed class Nest
    {
        public SNode S; public Rec R; public SRec SR; public DictRec D; public VRecList V; public List<List<List<int>>> L3;
        public Dictionary<string, Dictionary<int, List<string>>> DD; public List<Empty>[] Empties;
    }

    // Reads that allocate their array uninitialized (from 6 KB) and can still reject the data once they have: blocks
    // with a bool, raw T? and bools, raw structs with a bool, values Untrusted mode checks (DateTime, decimal), and
    // entries of a dictionary and a set of such blocks. The Stale targets read invalid data into an existing value
    // over poisoned freed memory (StaleMemory.cs).
    [NinoType]
    public sealed class Stale
    {
        public (int, bool)[] Pairs; public List<(int, bool)> PairList; public Memory<(int, bool)> PairMemory;
        public int?[] Nullables; public List<int?> NullableList; public bool[] Bools; public List<bool> BoolList;
        public RawS[] Raws; public List<DateTime> Times; public decimal[] Decimals;
        public Dictionary<int, bool> Map; public HashSet<(int, bool)> Set; public RawBcl[] Bcls;
    }

    // Collections that hold themselves (Probes.cs: the "tree" and "dtree" probes) and colliding hashes ("hash").
    public class Tree : List<Tree> { }
    public class DTree : Dictionary<string, DTree> { }
    [NinoType] public sealed class TreeHolder { public Tree T; public DTree D; }
    [NinoType] public sealed class HashColl { public Dictionary<long, int> D; public HashSet<long> H; }
}
