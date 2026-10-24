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
