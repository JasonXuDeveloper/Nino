using System.Collections.Generic;
using Nino.Core;

// Shapes of types the conformance corpus does not declare, generated into every language (runtimes/go/internal/
// generated/shapes, runtimes/rust/tests/generated/shapes.rs, runtimes/typescript/test/generated) so each runtime's
// build compiles them: names each language reserves, collisions, a struct holding itself, a struct in a polymorphic
// slot. A new way to declare a type gets a shape here (CLAUDE.md, "Code generation for other languages").
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
        public (int, string) Pair;
        public KeyValuePair<string, int> Entry;
        public int? Maybe;
        public Keywords Keywords;
    }

    /// <summary>Keys Rust cannot hash: Rust leaves the type out, with a warning; Go and TypeScript keep it.</summary>
    [NinoType]
    public sealed class FloatKeys
    {
        public Dictionary<float, int> Map;
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
