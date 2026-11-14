using System.Collections.Generic;
using Nino.Core;

// A layout as a Nino generator before spec 4.4 could publish it for a member of Vector<T>, which no runtime reads
// since (Vector<T> is not serializable: spec 3): nino-gen leaves the type out (Shapes.OldVector).
[assembly: NinoTypeLayout(typeof(Shapes.OldVector), NinoFormat.Positional, NinoHeader.Presence, false, false, 0x0BADF00D,
    "Lanes:System.Numerics.Vector<int>:raw(16){_00:ulong@0:raw(8),_01:ulong@8:raw(8)}")]

// Shapes of types the conformance corpus does not declare, generated into every language (runtimes/go/internal/
// generated/shapes, runtimes/rust/tests/generated/shapes.rs, runtimes/typescript/test/generated) so each runtime's
// build compiles them: names each language reserves, collisions, a struct holding itself, a struct in a polymorphic
// slot. A new way to declare a type gets a shape here (src/Nino.Codegen/AGENTS.md).
namespace Shapes
{
    /// <summary>Members named after keywords and the generated code's own names.</summary>
    [NinoType]
    public sealed class Keywords
    {
        public int Type;
        public int Self;
        public int Match;
        public string Default;
        public int Body;
        public int Key;
        public int Value;
        public int R;
        public int W;
        public int V;
    }

    /// <summary>A name the Go and Rust code use for their own types.</summary>
    [NinoType]
    public sealed class NinoGrid
    {
        public int Value;
    }

    /// <summary>A struct that can hold itself through no class body: its nesting is counted.</summary>
    [NinoType]
    public struct Recursive
    {
        public int Value;
        public List<Recursive> Children;
    }

    /// <summary>A struct written behind a type id, next to a class, in an interface slot.</summary>
    [NinoType]
    public interface IThing
    {
        int Id { get; set; }
    }

    [NinoType]
    public struct ThingStruct : IThing
    {
        public int Id { get; set; }
        public long Extra;
    }

    [NinoType]
    public sealed class ThingClass : IThing
    {
        public int Id { get; set; }
        public string Label;
    }

    [NinoType]
    public sealed class Holder
    {
        public IThing Thing;
        public List<IThing> Things;
        public Recursive Tree;
        public Outer.Inner Inner;
        public Left.Item LeftItem;
        public Right.Item RightItem;
        public Flags Flags;
        public Gappy[] Gaps;
        public Dictionary<int, string> Names;
        public HashSet<string> Tags;
        [NinoUtf8] public HashSet<string> Utf8Tags;
        public (int, string) Pair;
        public KeyValuePair<string, int> Entry;
        public int? Maybe;
        public Keywords Keywords;
    }

    /// <summary>
    /// Arrays Go copies as one block: of dates, decimals, Guids and 128-bit integers (<c>nino.ReadBlock</c>), and of
    /// a raw struct with a bool and no padding (<c>nino.ReadRaw</c>, <c>nino.WriteRaw</c>).
    /// </summary>
    [NinoType]
    public sealed class Stamps
    {
        public System.DateTime[] Times;
        public List<System.DateOnly> Days;
        public System.TimeOnly[] Clocks;
        public decimal[] Prices;
        public List<System.Guid> Ids;
        public System.Int128[] Signed;
        public System.UInt128[] Unsigned;
        public Marked[] Marks;
    }

    /// <summary>A raw struct with a bool and no padding.</summary>
    [NinoType]
    public struct Marked
    {
        public int Id;
        public bool On;
        public byte Kind;
        public short Code;
    }

    /// <summary>Keys Rust cannot hash: Rust leaves the type out, with a warning; Go and TypeScript keep it.</summary>
    [NinoType]
    public sealed class FloatKeys
    {
        public Dictionary<float, int> Map;
    }

    /// <summary>A plain struct holding a value tuple: (byte, short) is the short, then the byte, aligned to 4.</summary>
    public struct TupleInside
    {
        public byte A;
        public (byte, short) T;
    }

    /// <summary>A raw struct aligned to 4 by the auto layout of the tuple it holds (wire-format spec §5.2).</summary>
    [NinoType]
    public struct TupleCell
    {
        public byte A;
        public TupleInside Inside;
    }

    /// <summary>
    /// Nullables of raw values at a fixed stride: the presence byte, padding to the value's canonical alignment, then
    /// the value (wire-format spec §6.2); as a member (its layout publishes the stride) and as a type argument (nino-gen
    /// works it out).
    /// </summary>
    [NinoType]
    public sealed class Strides
    {
        public TupleCell?[] Cells;
        public Generic.Box<TupleCell?[]> Boxed;
    }

    /// <summary>
    /// <c>NinoNullable&lt;T&gt;</c> where the corpus lacks it: inside type arguments, which nino-gen works out as it does
    /// those of <c>T?</c> (a presence byte and the value alone and in sets and maps, the fixed stride in arrays and
    /// lists), and of an enum. <see cref="NullableArgs"/> is the same type with <c>T?</c>.
    /// </summary>
    [NinoType]
    public sealed class NinoNullableArgs
    {
        public NinoNullable<Flags> Enum;
        public NinoNullable<Flags>[] Enums;
        public Generic.Box<NinoNullable<int>> One;
        public Generic.Box<NinoNullable<TupleCell>[]> Array;
        public Generic.Box<List<NinoNullable<long>>> List;
        public HashSet<NinoNullable<int>> Set;
        public Dictionary<int, NinoNullable<double>> Map;
    }

    /// <summary><see cref="NinoNullableArgs"/> declared with <c>T?</c>: the same layout encodings and bytes.</summary>
    [NinoType]
    public sealed class NullableArgs
    {
        public Flags? Enum;
        public Flags?[] Enums;
        public Generic.Box<int?> One;
        public Generic.Box<TupleCell?[]> Array;
        public Generic.Box<List<long?>> List;
        public HashSet<int?> Set;
        public Dictionary<int, double?> Map;
    }

    /// <summary>
    /// <c>NinoNullable&lt;T&gt;</c> inside a plain struct: raw memory, its presence a whole <c>T</c> (1 in its first
    /// byte) and then the value. For a <c>T</c> as large as its alignment those are the bytes of <c>T?</c> there (the
    /// flag, padding to the alignment, the value): 8 for an int.
    /// </summary>
    public struct HoldsNinoNullable
    {
        public byte A;
        public NinoNullable<int> B;
    }

    /// <summary>
    /// A <c>T</c> larger than its alignment (a decimal: 16 bytes aligned to 8) is 32 bytes there, the value at 16,
    /// where a <c>decimal?</c> is 24, the value at 8. TypeScript and Go read the flag and the value where they are;
    /// Rust's <c>Option&lt;T&gt;</c> in raw memory is the layout of <c>T?</c> only, so it leaves the type out.
    /// </summary>
    public struct HoldsWideNinoNullable
    {
        public byte A;
        public NinoNullable<decimal> B;
    }

    [NinoType]
    public sealed class NinoNullableRaw
    {
        public HoldsNinoNullable One;
        public HoldsNinoNullable[] Many;
    }

    [NinoType]
    public sealed class NinoNullableRawWide
    {
        public HoldsWideNinoNullable One;
        public HoldsWideNinoNullable[] Many;
    }

    /// <summary>
    /// The BCL leaves whose layout differs by runtime, inside a raw struct, in the canonical layout (wire-format spec
    /// §5.2): Int128 aligned to 16, nint sign-extended, nuint and pointers (delegate* too) zero-extended to 8 bytes.
    /// </summary>
    public unsafe struct HostLeaves
    {
        public byte A;
        public System.Int128 Wide;
        public nint Signed;
        public nuint Unsigned;
        public int* Pointer;
        public delegate*<int> Callback;
    }

    /// <summary>DateTimeOffset inside a raw struct: its canonical fields, the minutes (an int) at 0, the ticks at 8.</summary>
    public struct OffsetLeaf
    {
        public short A;
        public System.DateTimeOffset At;
    }

    /// <summary>Raw structs holding <see cref="HostLeaves"/>, and Int128? at a stride of 32 (the value at 16).</summary>
    [NinoType]
    public sealed class HostHolder
    {
        public HostLeaves Leaves;
        public System.Int128?[] Gaps;
    }

    /// <summary>Not written to TypeScript, whose DateTime is a Date (milliseconds, written as UTC).</summary>
    [NinoType]
    public sealed class OffsetHolder
    {
        public OffsetLeaf Leaf;
    }

    /// <summary>
    /// A SIMD vector of fixed size (Vector256: 32 bytes aligned to 32 in the canonical layout), which no target
    /// expresses: every target leaves the type out, with a warning. (Vector&lt;T&gt; is not serializable at all:
    /// NINO029.)
    /// </summary>
    [NinoType]
    public sealed class Simd
    {
        public int Id;
        public System.Runtime.Intrinsics.Vector256<int> Lanes;
    }

    /// <summary>
    /// Not a [NinoType]: its layout is the hand-written one above, which no target may write code for (Vector&lt;T&gt;
    /// is as wide as the CPU's vectors).
    /// </summary>
    public sealed class OldVector
    {
        public System.Numerics.Vector<int> Lanes;
    }

    public static class Outer
    {
        [NinoType]
        public sealed class Inner
        {
            public int Value;
        }
    }

    [System.Flags]
    public enum Flags : long
    {
        None = 0,
        A = 1,
        B = 1L << 40,
    }

    /// <summary>A raw struct with padding: (byte, int).</summary>
    [NinoType]
    public struct Gappy
    {
        public byte Small;
        public int Large;
    }

    /// <summary>Keyed, with a former name.</summary>
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Renamed
    {
        [NinoFormerName("OldName")] public int NewName;
        public string Other;
    }

    /// <summary>A collection of itself.</summary>
    public class Forest : List<Forest>
    {
    }

    [NinoType]
    public sealed class ForestHolder
    {
        public Forest Forest;
    }
}

namespace Shapes.Left
{
    [NinoType]
    public sealed class Item
    {
        public int Left;
    }
}

namespace Shapes.Right
{
    [NinoType]
    public sealed class Item
    {
        public int Right;
    }
}

namespace Shapes.Generic
{
    /// <summary>A sealed generic class of one parameter.</summary>
    [NinoType]
    public sealed class Box<T>
    {
        public T Value;
    }

    /// <summary>A generic struct of two parameters.</summary>
    [NinoType]
    public struct Pair<TKey, TValue>
    {
        public TKey Key;
        public TValue Value;
    }

    /// <summary>Type parameters inside collections, and other generic types of them.</summary>
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class Bag<T>
    {
        public List<T> Items;
        public Dictionary<string, T> ByName;
        public T[] Array;
        public Box<T> Inner;
        public Pair<int, T> Pair;
        public List<Box<T>> Boxes;
        public (int, T) Tuple;
    }

    /// <summary>Holds itself with the same arguments.</summary>
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Node<T>
    {
        public T Value;
        public Node<T> Next;
    }

    /// <summary>Written behind a type id (not sealed): each closed type is a slot of its own.</summary>
    [NinoType]
    public class Open<T>
    {
        public T Value;
    }

    /// <summary>A generic subtype: its closed types sit in the slots of <see cref="Open{T}"/> with the same argument.</summary>
    [NinoType]
    public class MoreOpen<T> : Open<T>
    {
        public T Extra;
    }

    /// <summary>
    /// Holds slots of its own type parameter: its codec is given the slot of <c>Open&lt;T&gt;</c> for each argument it
    /// is closed with.
    /// </summary>
    [NinoType]
    public sealed class OpenHolder<T>
    {
        public Open<T> Inner;
        public List<Open<T>> All;
    }

    /// <summary>Unmanaged when closed: a closed use .NET copies as raw memory is a raw struct of its own.</summary>
    [NinoType]
    public struct RawPair<T> where T : unmanaged
    {
        public T A;
        public T B;
    }

    /// <summary>A type parameter as a set element and a map key: in Rust, its arguments must be hashable.</summary>
    [NinoType]
    public sealed class Keys<T>
    {
        public HashSet<T> Set;
        public Dictionary<T, int> ByKey;
    }

    /// <summary>Keys of a type Rust cannot hash: left out of Rust, with a warning.</summary>
    [NinoType]
    public sealed class UnhashedKeys
    {
        public Keys<Box<long>> Keys;
    }

    /// <summary>Closed uses of the generic types.</summary>
    [NinoType]
    public sealed class Uses
    {
        public Box<int> Number;
        public Box<string> Text;
        public Pair<int, string> Pair;
        public Box<Box<int>> Nested;
        public Box<Pair<string, Shapes.Gappy>> Mixed;
        public List<Box<int>> Boxes;
        public Bag<Box<long>> Bag;
        public Node<string> Chain;
        public Box<IThing> Thing;
        public Box<int[]> Array;
        public Pair<Box<int>, Dictionary<int, string>> Complex;
        public Dictionary<string, Pair<int, int>> Pairs;
        public RawPair<int> Raw;
        public Box<(int, string)> Tuple;
        public Box<int?> Maybe;
        public Box<KeyValuePair<int, string>> Entry;
        public Box<HashSet<string>> Set;
        public Keys<int> IntKeys;
        public Keys<string> StringKeys;
    }

    /// <summary>A slot of a closed generic type the assembly names.</summary>
    [NinoType]
    public sealed class OpenUse
    {
        public Open<int> Open;
    }

    /// <summary>
    /// Closed types the assembly never names: <c>Open&lt;long&gt;</c> and <c>MoreOpen&lt;long&gt;</c>, reached through
    /// <c>OpenHolder&lt;long&gt;</c>.
    /// </summary>
    [NinoType]
    public sealed class OpenHolderUse
    {
        public OpenHolder<long> Holder;
    }
}
