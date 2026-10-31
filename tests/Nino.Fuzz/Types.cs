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

    [NinoType]
    public sealed class RawRoot
    {
        public RawS One; public RawS[] Many; public List<Pair2> Pairs; public PlainBlock[] Blocks; public Empty[] Empties;
        public (byte, int)[] Tuples; public List<(short, long)> Wide; public Guid[] Guids; public decimal[] Decimals;
        public RawS? Maybe; public Dictionary<int, RawS> ById;
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

    [NinoType]
    public class Holder
    {
        public object O; public TNode T; public TOther TO; public Shape S; public IAnimal A; public List<object> Objs;
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
        public SortedList<int, string> SLI; public SortedList<string, int> SLS;
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
        public Dictionary<int, bool> Map; public HashSet<(int, bool)> Set;
    }

    // Collections that hold themselves (Probes.cs: the "tree" and "dtree" probes) and colliding hashes ("hash").
    public class Tree : List<Tree> { }
    public class DTree : Dictionary<string, DTree> { }
    [NinoType] public sealed class TreeHolder { public Tree T; public DTree D; }
    [NinoType] public sealed class HashColl { public Dictionary<long, int> D; public HashSet<long> H; }
}
