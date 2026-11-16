using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Tests;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

namespace Nino.UnitTests;

// Host-sized vectors (NINO029).
public partial class AnalyzerTests
{
    [TestMethod]
    public async Task NINO029_VectorTMembers()
    {
        // Vector<T> wherever Nino would write it: alone, as an element, a type argument a [NinoType] uses, a field at
        // any depth of a plain struct, a settable property. One diagnostic: no NINO023 for a struct of vectors only.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public struct Raw { public int A; public Vector<int> V; }
public struct Outer { public byte B; public Raw Inner; }

[NinoType]
public class Box<T> { public T Value; }

// T reaches the base's member under the base's own parameter.
[NinoType]
public class ListBox<T> : Box<List<T>> { }

[NinoType]
public class Holder
{
    public Vector<float> {|#0:Alone|};
    public List<Vector<int>> {|#1:InList|};
    public Vector<int>[] {|#2:InArray|};
    public Outer {|#3:Deep|};
    public Box<Vector<int>> {|#4:InBox|};
    public Vector<int>? {|#5:Nullable|};
    public Dictionary<int, (byte, Vector<long>)> {|#6:InTuple|};
    public Vector<float> {|#7:Settable|} { get => default; set { } }
    public ListBox<Vector<short>> {|#10:InBaseOfBox|};
}

[NinoType]
public struct NinoStruct { public int A; public Vector<double> {|#8:V|}; }

[NinoType]
public struct OnlyVectors { public Vector<byte> {|#9:V|}; }
";

        await SetUpAnalyzerTest(code,
            Nino029(0, "Alone", "Holder", "System.Numerics.Vector<float>"),
            Nino029(1, "InList", "Holder", "System.Collections.Generic.List<System.Numerics.Vector<int>>",
                "System.Numerics.Vector<int>"),
            Nino029(2, "InArray", "Holder", "System.Numerics.Vector<int>[]", "System.Numerics.Vector<int>"),
            Nino029(3, "Deep", "Holder", "Outer", "System.Numerics.Vector<int>"),
            Nino029(4, "InBox", "Holder", "Box<System.Numerics.Vector<int>>", "System.Numerics.Vector<int>"),
            Nino029(5, "Nullable", "Holder", "System.Numerics.Vector<int>?", "System.Numerics.Vector<int>"),
            Nino029(6, "InTuple", "Holder",
                "System.Collections.Generic.Dictionary<int, (byte, System.Numerics.Vector<long>)>",
                "System.Numerics.Vector<long>"),
            Nino029(7, "Settable", "Holder", "System.Numerics.Vector<float>"),
            Nino029(8, "V", "NinoStruct", "System.Numerics.Vector<double>"),
            Nino029(9, "V", "OnlyVectors", "System.Numerics.Vector<byte>"),
            Nino029(10, "InBaseOfBox", "Holder", "ListBox<System.Numerics.Vector<short>>",
                "System.Numerics.Vector<short>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_TopLevelCalls()
    {
        // The calls the generator would register or intercept; IsSupported only asks (it answers false).
        var code = @"
using System;
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public struct Raw { public int A; public Vector<int> V; }

public static class Calls
{
    public static void Run()
    {
        var bytes = {|#0:NinoSerializer.Serialize(new Vector<int>(1))|};
        {|#1:NinoDeserializer.Deserialize<List<Raw>>(bytes)|};
        {|#2:NinoSerializer.SerializeBoxed(new Raw(), typeof(Raw))|};
        NinoSerializer.IsSupported(typeof(Raw));
        NinoSerializer.Serialize(new System.Runtime.Intrinsics.Vector128<int>());
    }
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO029").WithLocation(0).WithArguments("The value of this NinoSerializer.Serialize call",
                "System.Numerics.Vector<int>", "is a Vector<T>"),
            Verify.Diagnostic("NINO029").WithLocation(1).WithArguments(
                "The value of this NinoDeserializer.Deserialize call", "System.Collections.Generic.List<Raw>",
                "holds 'System.Numerics.Vector<int>'"),
            Verify.Diagnostic("NINO029").WithLocation(2).WithArguments(
                "The value of this NinoSerializer.SerializeBoxed call", "Raw", "holds 'System.Numerics.Vector<int>'"))
            .RunAsync();
    }

    [TestMethod]
    public async Task NINO029_InBaseTypesAndCollections()
    {
        // A type that names no vector itself but whose base type or IEnumerable<T> holds one: NINO029 alone, not
        // NINO007 (it has no formatter for the same reason). A struct holding a list of itself is searched to the end.
        var code = @"
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public class VectorList : List<Vector<int>> { }
public class Derived : VectorList { }
public class VectorMap : Dictionary<int, Vector<float>> { }
public class Bag : IEnumerable<Vector<long>>
{
    public void Add(Vector<long> item) { }
    public IEnumerator<Vector<long>> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}
public struct Chain { public int A; public List<Chain> Rest; public Vector<int> V; }

[NinoType]
public class Holder
{
    public VectorList {|#0:InBase|};
    public Derived {|#1:InBaseOfBase|};
    public VectorMap {|#2:InMapBase|};
    public Bag {|#3:InEnumerable|};
    public List<VectorList> {|#4:Nested|};
    public Chain {|#5:SelfHolding|};
}

public static class Calls
{
    public static void Run() => {|#6:NinoSerializer.Serialize(new VectorList())|};
}
";

        await SetUpAnalyzerTest(code,
            Nino029(0, "InBase", "Holder", "VectorList", "System.Numerics.Vector<int>"),
            Nino029(1, "InBaseOfBase", "Holder", "Derived", "System.Numerics.Vector<int>"),
            Nino029(2, "InMapBase", "Holder", "VectorMap", "System.Numerics.Vector<float>"),
            Nino029(3, "InEnumerable", "Holder", "Bag", "System.Numerics.Vector<long>"),
            Nino029(4, "Nested", "Holder", "System.Collections.Generic.List<VectorList>", "System.Numerics.Vector<int>"),
            Nino029(5, "SelfHolding", "Holder", "Chain", "System.Numerics.Vector<int>"),
            Verify.Diagnostic("NINO029").WithLocation(6).WithArguments("The value of this NinoSerializer.Serialize call",
                "VectorList", "holds 'System.Numerics.Vector<int>'")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForBaseTypesWithoutVectors()
    {
        // Base types and collections without a Vector<T>, types that hold themselves (searched once, no stack
        // overflow), and a struct of itself without a formatter, which stays NINO007's.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

public class IntList : List<int> { }
public class Vector4Map : Dictionary<string, Vector4> { }
public class Tree : List<Tree> { public int Value; }
public struct Node { public int A; public List<Node> Children; }

[NinoType]
public class Holder
{
    public IntList Ints;
    public Vector4Map Map;
    public Tree Root;
    public Node {|#0:Graph|};
}
";

        await SetUpAnalyzerTest(code,
            Verify.Diagnostic("NINO007").WithLocation(0)
                .WithArguments("Graph", "Node", "Holder", "", "the member is skipped")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForVectorsNinoNeverWrites()
    {
        // The documented workaround: fixed lanes stored, the Vector<T> exposed through [NinoIgnore] members. Neither
        // members Nino does not serialize (get-only, ref structs, [NinoIgnore]), nor fixed-size vectors, nor a type
        // argument no serialized member uses, nor a [NinoMemberFormatter] that writes the member its own way.
        var code = @"
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nino.Core;

[NinoType]
public class Particle
{
    public float[] Lanes = new float[16];

    [NinoIgnore]
    public Span<Vector<float>> Vectors => MemoryMarshal.Cast<float, Vector<float>>(Lanes.AsSpan());

    [NinoIgnore]
    public Vector<float> Head { get => Vectors[0]; set => Vectors[0] = value; }

    public Vector<float> First => new Vector<float>(Lanes);

    [NinoIgnore]
    public Vector<int> Scratch;
}

[NinoType]
public struct Packed
{
    public Vector128<float> Low;
    public Vector256<float> High;

    [NinoIgnore]
    public Vector<float> View { get => default; set { } }
}

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class UsesTag { public Tag<Vector<int>> Tagged; }

public struct VectorFormatter : INinoFormatter<Vector<int>>
{
    public void Serialize(Vector<int> value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref Vector<int> value) { }
}

[NinoType]
public class Custom
{
    [NinoMemberFormatter(typeof(VectorFormatter))]
    public Vector<int> Mine;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForTypeArgumentsNeverWritten()
    {
        // A generic [NinoType] closed over Vector<T> where its formatter never writes the argument, decided by
        // substitution through the serialized members: T only in a [NinoIgnore] member, a method, a constraint or a
        // computed property; T only in a nested generic that does not write it (Outer<T> holding Tag<T>, also two
        // levels down); T reaching a base type that does not write it; a type holding itself over a bigger argument
        // that never writes it (Expanding<T>, whose closed member types grow without end).
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public class IgnoredOnly<T> { public int Id; [NinoIgnore] public T Scratch; [NinoIgnore] public List<T> Cache; }

[NinoType]
public class MethodOnly<T> where T : struct
{
    public int Id;
    public T Make() => default;
    public T Computed => default;
}

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class Outer<T> { public Tag<T> Inner; public List<Tag<T>> Many; }

[NinoType]
public class TwoLevels<T> { public Outer<List<T>> Inner; }

[NinoType]
public class TagBase<T> { public Tag<T> Inner; }

[NinoType]
public sealed class FromTagBase<T> : TagBase<T> { public int Id; }

[NinoType]
public class Expanding<T> { public int Id; public Expanding<List<T>> Next; }

[NinoType]
public class Holder
{
    public IgnoredOnly<Vector<int>> Ignored;
    public MethodOnly<Vector<int>> Methods;
    public Outer<Vector<int>> Nested;
    public TwoLevels<Vector<int>> Deeper;
    public FromTagBase<Vector<int>> ThroughBase;
    public Expanding<Vector<int>> Expanding;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NINO029_TypeArgumentsWritten()
    {
        // The argument a generic [NinoType]'s formatter writes holds a Vector<T>: directly, through List<T>, a
        // nested generic that writes it, a dictionary value, a base type, the elements of a collection class, a
        // generic subtype of a slot, and a type holding itself over a bigger argument (Growing<T>: decided on the
        // definitions, so it ends at once). A ring of 40 generic types over growing arguments ends fast too.
        const int count = 40;
        var ring = string.Join("\n", Enumerable.Range(0, count).Select(i => i == count - 1
            ? $"[NinoType] public class G{i}<T> {{ public T Value; public G0<T[]> Next; }}"
            : $"[NinoType] public class G{i}<T> {{ public int Id; public G{i + 1}<List<T>> Next; }}"));
        var code = $@"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public class Box<T> {{ public T Value; }}

[NinoType]
public class Listed<T> {{ public List<T> Items; }}

[NinoType]
public class Nested<T> {{ public int Id; public Box<T[]> Inner; }}

[NinoType]
public class Mapped<T> {{ public Dictionary<string, T> Map; }}

[NinoType]
public sealed class FromBase<T> : Box<T> {{ }}

[NinoType]
public class Bag<T> : List<T> {{ }}

[NinoType]
public class Growing<T> {{ public T Value; public Growing<List<T>> Next; }}

[NinoType]
public abstract class Shelf<T> {{ public int Id; }}

[NinoType]
public class ListShelf<T> : Shelf<T> {{ public List<T> Items; }}

{ring}

[NinoType]
public class Holder
{{
    public Box<Vector<int>> {{|#0:Direct|}};
    public Listed<Vector<int>> {{|#1:InList|}};
    public Nested<Vector<int>> {{|#2:InNested|}};
    public Mapped<Vector<int>> {{|#3:InDictionary|}};
    public FromBase<Vector<int>> {{|#4:InBase|}};
    public Bag<Vector<int>> {{|#5:InElements|}};
    public Growing<Vector<int>> {{|#6:InGrowing|}};
    public Shelf<Vector<int>> {{|#7:InSubtype|}};
    public G0<Vector<int>> {{|#8:InRing|}};
}}
";

        const string Vector = "System.Numerics.Vector<int>";
        await SetUpAnalyzerTest(code,
            Nino029(0, "Direct", "Holder", "Box<System.Numerics.Vector<int>>", Vector),
            Nino029(1, "InList", "Holder", "Listed<System.Numerics.Vector<int>>", Vector),
            Nino029(2, "InNested", "Holder", "Nested<System.Numerics.Vector<int>>", Vector),
            Nino029(3, "InDictionary", "Holder", "Mapped<System.Numerics.Vector<int>>", Vector),
            Nino029(4, "InBase", "Holder", "FromBase<System.Numerics.Vector<int>>", Vector),
            Nino029(5, "InElements", "Holder", "Bag<System.Numerics.Vector<int>>", Vector),
            Nino029(6, "InGrowing", "Holder", "Growing<System.Numerics.Vector<int>>", Vector),
            Nino029(7, "InSubtype", "Holder", "Shelf<System.Numerics.Vector<int>>", Vector),
            Nino029(8, "InRing", "Holder", "G0<System.Numerics.Vector<int>>", Vector)).RunAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NINO029_PartsOfArgumentsSubtypesWrite()
    {
        // A slot holds the generic subtypes whose base matches its argument, and they write what they write of it,
        // to any depth: TagShelf<U> : Shelf<Tag<U>> writes the X of a Shelf<Tag<X>> though Tag<T> writes nothing of
        // its own; through a base between them (Deep<U> : Middle<U> : Shelf<Tag<U[]>>), an interface, a type nested
        // in a generic one, a generic that passes its argument to the slot (Outer<T> { Shelf<T> S; }), and a subtype
        // holding its base over its own parameter, which matches Tag<Tag<...>> at every depth (every type inside the
        // argument then counts).
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public abstract class Shelf<T> { public int Id; }

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class TagShelf<U> : Shelf<Tag<U>> { public U Value; }

[NinoType]
public abstract class Middle<V> : Shelf<Tag<V[]>> { }

[NinoType]
public class Deep<U> : Middle<U> { public List<U> Items; }

[NinoType]
public interface ICarrier<T> { }

[NinoType]
public class Carrier<U> : ICarrier<Dictionary<int, Tag<U>>> { public U Value; }

public class Container<T>
{
    [NinoType]
    public class Inner : Shelf<KeyValuePair<string, T>> { public T Value; }
}

[NinoType]
public class Outer<T> { public Shelf<T> Inner; }

[NinoType]
public abstract class Rack<T> { public int Id; }

[NinoType]
public class SelfRack<U> : Rack<Tag<U>> { public U Value; public Rack<U> Next; }

[NinoType]
public class Holder
{
    public Shelf<Tag<Vector<int>>> {|#0:ThroughTag|};
    public Shelf<Tag<Vector<int>[]>> {|#1:ThroughMiddle|};
    public ICarrier<Dictionary<int, Tag<Vector<int>>>> {|#2:ThroughInterface|};
    public Shelf<KeyValuePair<string, Vector<int>>> {|#3:ThroughNested|};
    public Outer<Tag<Vector<int>>> {|#4:ThroughOuter|};
    public Rack<Tag<Tag<Tag<Tag<Tag<Tag<Tag<Tag<Vector<int>>>>>>>>>> {|#5:AtAnyDepth|};
}
";

        const string Vector = "System.Numerics.Vector<int>";
        await SetUpAnalyzerTest(code,
            Nino029(0, "ThroughTag", "Holder", "Shelf<Tag<System.Numerics.Vector<int>>>", Vector),
            Nino029(1, "ThroughMiddle", "Holder", "Shelf<Tag<System.Numerics.Vector<int>[]>>", Vector),
            Nino029(2, "ThroughInterface", "Holder",
                "ICarrier<System.Collections.Generic.Dictionary<int, Tag<System.Numerics.Vector<int>>>>", Vector),
            Nino029(3, "ThroughNested", "Holder",
                "Shelf<System.Collections.Generic.KeyValuePair<string, System.Numerics.Vector<int>>>", Vector),
            Nino029(4, "ThroughOuter", "Holder", "Outer<Tag<System.Numerics.Vector<int>>>", Vector),
            Nino029(5, "AtAnyDepth", "Holder",
                "Rack<Tag<Tag<Tag<Tag<Tag<Tag<Tag<Tag<System.Numerics.Vector<int>>>>>>>>>>", Vector)).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForPartsSubtypesNeverWrite()
    {
        // The parts of a slot's argument no subtype writes: a subtype that writes nothing of its parameter, an
        // argument of another shape than every subtype's base (no such subtype fits the slot), a subtype's
        // parameter in a position of its base's argument that is matched but not written (Pair<U, int>'s int),
        // and a sealed class, which has no subtypes.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public class Tag<T> { public int Id; }

[NinoType]
public class Box<T> { public T Value; }

[NinoType]
public abstract class Rack<T> { public int Id; }

[NinoType]
public class TagRack<U> : Rack<Tag<U>> { public U Value; }

[NinoType]
public class QuietRack<U> : Rack<Box<U>> { public int Count; }

[NinoType]
public abstract class Pairs<T> { public int Id; }

[NinoType]
public class FirstOnly<U, W> : Pairs<KeyValuePair<U, W>> { public U First; }

[NinoType]
public sealed class Sealed<T> { public int Id; }

[NinoType]
public class Holder
{
    public Rack<Box<Vector<int>>> Quiet;
    public Rack<List<Vector<int>>> OtherShape;
    public Rack<Tag<int>> NoVector;
    public Pairs<KeyValuePair<int, Vector<int>>> SecondNeverWritten;
    public Sealed<Tag<Vector<int>>> NoSubtypes;
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_OnlyArgumentsASubtypeCanBe()
    {
        // A slot holds a subtype only when its argument can be what the subtype gives its base: a parameter the base
        // names twice (Same<U> : Shelf<KeyValuePair<U, U>>, TwinOf<U> : Twin<U, U>) stands for one type in every
        // place, and a fixed type (Fixed<U> : Shelf<KeyValuePair<int, U[]>>) must be that type. Arguments no
        // subtype can be are not searched.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

[NinoType]
public abstract class Shelf<T> { public int Id; }

[NinoType]
public class Same<U> : Shelf<KeyValuePair<U, U>> { public U Value; }

[NinoType]
public class Fixed<U> : Shelf<KeyValuePair<int, U[]>> { public U[] Values; }

[NinoType]
public abstract class Twin<A, B> { public int Id; }

[NinoType]
public class TwinOf<U> : Twin<U, U> { public U Value; }

[NinoType]
public class Holder
{
    public Shelf<KeyValuePair<Vector<int>, Vector<int>>> {|#0:SameFits|};
    public Shelf<KeyValuePair<int, Vector<int>[]>> {|#1:FixedFits|};
    public Twin<Vector<int>, Vector<int>> {|#2:TwinFits|};
    public Shelf<KeyValuePair<long, Vector<int>[]>> NeitherFits;
    public Shelf<KeyValuePair<int, Vector<int>>> NotTheSame;
    public Twin<int, Vector<int>> TwinNeverHolds;
}
";

        const string Vector = "System.Numerics.Vector<int>";
        await SetUpAnalyzerTest(code,
            Nino029(0, "SameFits", "Holder",
                "Shelf<System.Collections.Generic.KeyValuePair<System.Numerics.Vector<int>, System.Numerics.Vector<int>>>", Vector),
            Nino029(1, "FixedFits", "Holder",
                "Shelf<System.Collections.Generic.KeyValuePair<int, System.Numerics.Vector<int>[]>>", Vector),
            Nino029(2, "TwinFits", "Holder", "Twin<System.Numerics.Vector<int>, System.Numerics.Vector<int>>", Vector))
            .RunAsync();
    }

    [TestMethod]
    public void NINO029_AcrossAssemblies()
    {
        // A referenced class's private members are not in its symbols (a compilation imports only public and
        // protected members of referenced classes): its published layout names them, so Hidden<Vector<int>> is
        // reported and Quiet<Vector<int>>, whose private T is not serialized, is not. A subtype in another referenced
        // assembly that references the base's writes the part of the argument it matches.
        var contracts = CornerCompiler.Compile(@"
using Nino.Core;

namespace Contracts
{
    [NinoType] public abstract class Shelf<T> { public int Id; }
    [NinoType] public class Tag<T> { public int Id; }
    [NinoType(includeNonPublicMembers: true)] public partial class Hidden<T> { public int Id; private T _value; }
    [NinoType] public class Quiet<T> { public int Id; [NinoIgnore] private T _scratch; }
    [NinoType] public class Visible<T> { public T Value; }
}
", load: false);
        Assert.AreEqual("", contracts.ErrorText);
        var shelves = CornerCompiler.CompileAgainst(@"
using Nino.Core;
using Contracts;

namespace Shelves
{
    [NinoType] public class TagShelf<U> : Shelf<Tag<U>> { public U Value; }
}
", contracts);
        Assert.AreEqual("", shelves.ErrorText);
        var app = CornerCompiler.CompileAgainst(@"
using System.Numerics;
using Nino.Core;
using Contracts;

namespace App
{
    [NinoType]
    public class Holder
    {
        public Shelf<Tag<Vector<int>>> InSubtype;
        public Hidden<Vector<int>> InPrivate;
        public Visible<Vector<int>> InPublic;
        public Quiet<Vector<int>> NotWritten;
        public Tag<Vector<int>> NotWrittenEither;
    }
}
", contracts, shelves);
        var reported = app.AnalyzerDiagnostics.Where(d => d.Id == "NINO029")
            .Select(d => d.GetMessage().Split(' ')[1]).OrderBy(m => m).ToArray();
        CollectionAssert.AreEqual(new[] { "'InPrivate'", "'InPublic'", "'InSubtype'" }, reported,
            string.Join("\n", app.AnalyzerDiagnostics));
    }

    /// <summary>A NINO029 on a formatter or a call used by hand: its value <paramref name="value"/>.</summary>
    private static DiagnosticResult Nino029ByHand(int location, string what, string value, string vector = null) =>
        Verify.Diagnostic("NINO029").WithLocation(location).WithArguments(what, value,
            vector == null ? "is a Vector<T>" : $"holds '{vector}'");

    [TestMethod]
    public async Task NINO029_FormattersMadeByHand()
    {
        // Nino.Core's formatters and the Writer / Reader / NinoDirect generic methods used by hand refuse a value that
        // holds Vector<T> at run time: constructed, defaulted, inferred or explicit, nested in a collection formatter,
        // or named by [NinoMemberFormatter].
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;
using Nino.Core.CompilerServices;

public struct Raw { public int A; public Vector<int> V; }

[NinoType]
public class Holder
{
    [NinoMemberFormatter(typeof(UnmanagedFormatter<Vector<int>>))]
    public Vector<int> {|#0:Lanes|};
}

public static class ByHand
{
    public static void Run(ref Writer writer, ref Reader reader, Vector<int> v, Raw raw)
    {
        {|#1:new UnmanagedFormatter<Vector<int>>()|}.Serialize(v, ref writer);
        Take({|#2:default(UnmanagedFormatter<Raw>)|});
        {|#3:default(ArrayFormatter<Vector<int>, UnmanagedFormatter<Vector<int>>>)|}.Serialize(null, ref writer);
        Take({|#4:new UnmanagedListFormatter<List<Raw>, Raw>()|});
        {|#5:writer.Write(v)|};
        {|#6:writer.Write(new[] { raw })|};
        {|#7:reader.Read(out Vector<long> back)|};
        {|#8:reader.Read<Raw>()|};
        {|#9:NinoDirect.Serialize<Vector<int>, UnmanagedFormatter<Vector<int>>>(v)|};
    }

    private static void Take<T>(T formatter) { }
}
";

        await SetUpAnalyzerTest(code,
            Nino029(0, "Lanes", "Holder", "System.Numerics.Vector<int>"),
            Nino029ByHand(1, "The value of this UnmanagedFormatter<T>", "System.Numerics.Vector<int>"),
            Nino029ByHand(2, "The value of this UnmanagedFormatter<T>", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(3, "The value of this ArrayFormatter<T, TFormatter>", "System.Numerics.Vector<int>"),
            Nino029ByHand(4, "The value of this UnmanagedListFormatter<TList, T>", "System.Collections.Generic.List<Raw>",
                "System.Numerics.Vector<int>"),
            Nino029ByHand(5, "The value of this Writer.Write call", "System.Numerics.Vector<int>"),
            Nino029ByHand(6, "The value of this Writer.Write call", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(7, "The value of this Reader.Read call", "System.Numerics.Vector<long>"),
            Nino029ByHand(8, "The value of this Reader.Read call", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(9, "The value of this NinoDirect.Serialize call", "System.Numerics.Vector<int>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_FormattersHeldInFieldsPropertiesAndLocals()
    {
        // A formatter struct is ready to use without a constructor call: a field, property or local of one is reported
        // at its declaration, once, whether or not a new or default fills it (that value is not reported again).
        var code = @"
using System.Numerics;
using Nino.Core;

public struct Raw { public int A; public Vector<int> V; }

[NinoType]
public class Stored
{
    [NinoIgnore] public UnmanagedFormatter<Vector<int>> {|#7:Ignored|};
    public UnmanagedFormatter<Vector<int>> {|#8:Written|};
}

public class Cache
{
    private static readonly UnmanagedFormatter<Vector<int>> {|#0:Lanes|};
    private UnmanagedFormatter<Raw> {|#1:Raws|} = new UnmanagedFormatter<Raw>();
    public static UnmanagedArrayFormatter<Vector<long>> {|#2:Arrays|} { get; } = default;
    private ArrayFormatter<Vector<int>, UnmanagedFormatter<Vector<int>>> {|#3:Nested|};

    public Cache() => Raws = default;

    public void Run(ref Writer writer, Vector<int> v)
    {
        Lanes.Serialize(v, ref writer);
        UnmanagedFormatter<Vector<short>> {|#4:local|};
        local.Serialize(default, ref writer);
        var {|#5:made|} = new UnmanagedFormatter<Vector<byte>>();
        made = new UnmanagedFormatter<Vector<byte>>();
        {|#6:new UnmanagedFormatter<Vector<byte>>()|}.Serialize(default, ref writer);
    }
}
";

        await SetUpAnalyzerTest(code,
            Nino029ByHand(0, "The UnmanagedFormatter<T> in 'Cache.Lanes'", "System.Numerics.Vector<int>"),
            Nino029ByHand(1, "The UnmanagedFormatter<T> in 'Cache.Raws'", "Raw", "System.Numerics.Vector<int>"),
            Nino029ByHand(2, "The UnmanagedArrayFormatter<T> in 'Cache.Arrays'", "System.Numerics.Vector<long>"),
            Nino029ByHand(3, "The ArrayFormatter<T, TFormatter> in 'Cache.Nested'", "System.Numerics.Vector<int>"),
            Nino029ByHand(4, "The UnmanagedFormatter<T> in 'local'", "System.Numerics.Vector<short>"),
            Nino029ByHand(5, "The UnmanagedFormatter<T> in 'made'", "System.Numerics.Vector<byte>"),
            Nino029ByHand(6, "The value of this UnmanagedFormatter<T>", "System.Numerics.Vector<byte>"),
            Nino029ByHand(7, "The UnmanagedFormatter<T> in 'Stored.Ignored'", "System.Numerics.Vector<int>"),
            // A serialized member: the member rule's diagnostic alone.
            Nino029(8, "Written", "Stored", "Nino.Core.UnmanagedFormatter<System.Numerics.Vector<int>>",
                "System.Numerics.Vector<int>")).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForFormattersHeldOverOtherTypes()
    {
        // Fields, properties and locals of Nino.Core formatters over fixed-size vectors, of the user's own Vector<T>
        // formatter and of a collection formatter over it, and of a generic formatter over a type parameter.
        var code = @"
using System.Numerics;
using System.Runtime.Intrinsics;
using Nino.Core;

public struct VectorFormatter : INinoFormatter<Vector<int>>
{
    public void Serialize(Vector<int> value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref Vector<int> value) { }
}

public class Cache<T> where T : unmanaged
{
    private static readonly UnmanagedFormatter<Vector128<int>> Fixed;
    private VectorFormatter Own;
    public ArrayFormatter<Vector<int>, VectorFormatter> OverOwn { get; set; }
    private UnmanagedFormatter<T> Open;
    private UnmanagedFormatter<Vector4> Four = new UnmanagedFormatter<Vector4>();

    public void Run(ref Writer writer)
    {
        UnmanagedFormatter<long> local;
        local.Serialize(1, ref writer);
        Vector<int> zero = default;
        Own.Serialize(zero, ref writer);
    }
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    public async Task NINO029_NotForOtherFormattersByHand()
    {
        // Fixed-size vectors and Vector4 through the same calls, a collection formatter over the user's own
        // Vector<T> formatter (which writes the lanes its own way), and unrelated generic types and methods that take
        // a Vector<T>.
        var code = @"
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Intrinsics;
using Nino.Core;
using Nino.Core.CompilerServices;

public struct VectorFormatter : INinoFormatter<Vector<int>>
{
    public void Serialize(Vector<int> value, ref Writer writer) { }
    public void Deserialize(ref Reader reader, ref Vector<int> value) { }
}

public struct Box<T> { public T Value; }

public static class Unrelated
{
    public static void Take<T>(T value) { }
}

public static class ByHand
{
    public static void Run(ref Writer writer, ref Reader reader, Vector<int> v)
    {
        new UnmanagedFormatter<Vector128<int>>().Serialize(Vector128<int>.Zero, ref writer);
        writer.Write(Vector256<float>.Zero);
        writer.Write(Vector4.One);
        reader.Read(out Vector128<byte> fixedLanes);
        default(ArrayFormatter<Vector<int>, VectorFormatter>).Serialize(null, ref writer);
        new VectorFormatter().Serialize(v, ref writer);
        NinoDirect.Serialize<Vector<int>, VectorFormatter>(v);
        var box = new Box<Vector<int>> { Value = v };
        Unrelated.Take(v);
        var list = new List<Vector<int>>();
        Vector<int> zero = default;
    }
}
";

        await SetUpAnalyzerTest(code).RunAsync();
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NINO029_TypesHoldingEachOtherAreSearchedOnce()
    {
        // A ring of collections, each derived from a list of the next: every class was searched again on each path to
        // it (its list's type argument, the list's IEnumerable<T>, its own IEnumerable<T>), three times more work per
        // class; 18 took two minutes, 40 would never end.
        const int count = 40;
        var ring = string.Join("\n", Enumerable.Range(0, count)
            .Select(i => $"public class C{i} : List<C{(i + 1) % count}> {{ }}"));
        var code = $@"
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

{ring}
public class Last : List<C0> {{ public Vector<int> NotWritten; }}
public class Lanes : List<C{count / 2}>, IEnumerable<Vector<int>>
{{
    IEnumerator<Vector<int>> IEnumerable<Vector<int>>.GetEnumerator() => null;
}}

[NinoType]
public class Holder
{{
    public C0 Ring;
    public List<C7> Inside;
    public Last Fields;
    public Lanes {{|#0:Mixed|}};
}}
";
        await SetUpAnalyzerTest(code, Nino029(0, "Mixed", "Holder", "Lanes", "System.Numerics.Vector<int>")).RunAsync();
    }
}
