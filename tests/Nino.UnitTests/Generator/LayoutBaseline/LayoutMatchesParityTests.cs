#if NET8_0_OR_GREATER
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// Parity of <c>LayoutEmitter.Matches</c> with <c>LayoutEmitter.Emit</c>. The analyzer finds a type unchanged against its
/// layout baseline section with <c>Matches</c>, which compares the model field by field with the parsed section instead
/// of formatting the layout: a second definition of the layout text. For every type of the shapes
/// <see cref="ShapeMatrixTests"/> declares (every kind, arity, place, format, tracking and type between), and of a few
/// more shapes it lacks (raw structs, collections, former names, UTF-8 strings, nested generic member types), the
/// <c>[assembly: NinoTypeLayout]</c> the generator emits, parsed as a baseline section (<c>LayoutEntry.FromLayout</c>),
/// must match the type's model, and must not after any one of its fields changes. A drift either way is invisible
/// elsewhere: a false match hides a layout change from NINO034-NINO036, a false mismatch only costs the comparison.
/// </summary>
public static class LayoutMatchesParityTests
{
    // Two rows a class, as ShapeMatrixTests: a row generates some 70 shapes.
    [TestClass]
    public class Rows0And1
    {
        [TestMethod, DataRow(0), DataRow(1)]
        public void EveryEmittedLayoutMatchesItsModel(int row) => Run(ShapeSource(row));
    }

    [TestClass]
    public class Rows2And3
    {
        [TestMethod, DataRow(2), DataRow(3)]
        public void EveryEmittedLayoutMatchesItsModel(int row) => Run(ShapeSource(row));
    }

    [TestClass]
    public class Rows4And5
    {
        [TestMethod, DataRow(4), DataRow(5)]
        public void EveryEmittedLayoutMatchesItsModel(int row) => Run(ShapeSource(row));
    }

    [TestClass]
    public class Rows6And7
    {
        [TestMethod, DataRow(6), DataRow(7)]
        public void EveryEmittedLayoutMatchesItsModel(int row) => Run(ShapeSource(row));
    }

    [TestClass]
    public class Extras
    {
        [TestMethod]
        public void EveryEmittedLayoutMatchesItsModel() => Run(ExtraSource);
    }

    private const string ExtraSource = @"
using System;
using System.Collections.Generic;
using Nino.Core;

namespace Extra
{
    [NinoType] public struct RawPoint { public int X; public long Y; public byte Z; }
    [NinoType(format: NinoFormat.VersionTolerant)] public struct TolerantPoint { public int X; public string Name; }
    [NinoType(format: NinoFormat.VersionTolerant)] public sealed class Bag : List<int> { public string Name; }
    [NinoType(format: NinoFormat.Keyed)] public sealed class KeyedBag : List<Item> { public int Tag; }
    [NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)] public class Item { public int A; public Item Next; }
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Renamed
    {
        [NinoFormerName(""Old"")] [NinoFormerName(""Older"")] public string Name;
        public int A;
        [NinoUtf8] public string Utf8;
    }
    [NinoType(format: NinoFormat.Keyed)]
    public sealed record KeyedRecord([property: NinoFormerName(""Title"")] string Name, int Count, Item Pet);
    [NinoType(format: NinoFormat.VersionTolerant)]
    public sealed class Types
    {
        public int? Nullable;
        public List<int?> Nullables;
        public Dictionary<string, int[]> Map;
        public (int A, string B) Tuple;
        public Outer<int>.Inner<string> Nested;
        public Box<Item> Boxed;
        public int[][] Jagged;
        public DateTimeOffset When;
        public Uri Link;
        public object Any;
    }
    public class Outer<T>
    {
        [NinoType(format: NinoFormat.VersionTolerant)]
        public sealed class Inner<U> { public T A; public List<U> B; }
    }
    [NinoType(format: NinoFormat.VersionTolerant)] public sealed class Box<T> { public T Value; public List<T> Items; }
    [NinoType] public abstract class Animal { public string Name; }
    [NinoType] public sealed class Dog : Animal { public int Bones; }
}
";

    /// <summary>The source ShapeMatrixTests compiles for one of its rows with the subtypes in the bases' assembly.</summary>
    private static string ShapeSource(int row)
    {
        // Rows 0 to 3: each kind in a namespace and the global namespace; 4 to 7: nested in a class and a generic class.
        var matrix = typeof(ShapeMatrixTests);
        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var shape = matrix.GetNestedType("Shape", BindingFlags.NonPublic)!;
        var kind = matrix.GetNestedType("Kind", BindingFlags.NonPublic)!;
        var place = matrix.GetNestedType("Place", BindingFlags.NonPublic)!;
        var create = shape.GetConstructor(any, null,
            new[] { kind, typeof(int), place, typeof(string), typeof(bool), typeof(bool) }, null)!;
        var emit = shape.GetMethod("Emit", any)!;
        string prelude = (string)matrix.GetField("Prelude", any)!.GetValue(null);
        var formats = (string[])matrix.GetField("Formats", any)!.GetValue(null);

        var places = row < 4 ? new[] { "Namespace", "Global" } : new[] { "Nested", "GenericNested" };
        var bases = new StringBuilder();
        var subtypes = new StringBuilder();
        foreach (int arity in new[] { 0, 1, 2 })
        foreach (string where in places)
        foreach (string format in formats)
        foreach (bool track in new[] { false, true })
        foreach (bool middle in new[] { false, true })
        {
            string ns = $"Layout{Enum.ToObject(kind, row % 4)}{arity}{where}{format}{(track ? "Tracked" : "")}{(middle ? "Middle" : "")}";
            var instance = create.Invoke(new[]
            {
                Enum.ToObject(kind, row % 4), arity, Enum.Parse(place, where), format, track, middle,
            });
            subtypes.Append((string)emit.Invoke(instance, new object[] { ns, bases, false }));
        }

        return prelude + bases + subtypes;
    }

    private static readonly Assembly GeneratorAssembly = typeof(Generator.Generation.NinoGenerator).Assembly;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private static MethodInfo GeneratorMethod(string type, string name) =>
        GeneratorAssembly.GetType(type, true)!.GetMethods(Static).Single(m => m.Name == name &&
            (name != "Build" || m.GetParameters().Length == 4) && (name != "FromLayout" || m.GetParameters().Length == 8));

    private static readonly MethodInfo FactsFor = GeneratorMethod("Nino.Generator.Generation.TypeFacts", "For");
    private static readonly MethodInfo Build = GeneratorMethod("Nino.Generator.Generation.ModelBuilder", "Build");
    private static readonly MethodInfo Matches = GeneratorMethod("Nino.Generator.Generation.Emit.LayoutEmitter", "Matches");
    private static readonly MethodInfo FromLayout = GeneratorMethod("Nino.Generator.Common.LayoutEntry", "FromLayout");
    private static readonly Type MemberType = GeneratorAssembly.GetType("Nino.Generator.Common.LayoutMember", true)!;

    /// <summary>
    /// Generates <paramref name="source"/>, then checks each emitted layout against the model of its type, built as
    /// the analyzer builds it (on the compilation the generator ran on).
    /// </summary>
    private static void Run(string source)
    {
        var compilation = TestCompilation.Create("LayoutParity", new[] { source });
        var run = TestCompilation.Generate(compilation);
        var errors = run.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        var output = compilation.AddSyntaxTrees(run.GeneratedTrees);
        var facts = FactsFor.Invoke(null, new object[] { compilation, compilation.SyntaxTrees.First().Options });

        int checkedLayouts = 0;
        var failures = new List<string>();
        foreach (var attribute in output.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != "NinoTypeLayoutAttribute")
                continue;
            var args = attribute.ConstructorArguments;
            var emitted = (INamedTypeSymbol)args[0].Value!;
            string name = MetadataName(emitted);
            var type = compilation.GetTypeByMetadataName(name);
            Assert.IsNotNull(type, name);
            var model = Build.Invoke(null, new object[] { type, facts, CancellationToken.None, null });
            Assert.IsNotNull(model, "no model for " + name);
            var deferrable = attribute.NamedArguments.Where(a => a.Key == "Deferrable")
                .Select(a => (bool?)(bool)a.Value.Value!).FirstOrDefault();

            // The section as a baseline would hold it, parsed again for each change.
            object Parse() => FromLayout.Invoke(null, new object[]
            {
                name, Convert.ToInt32(args[1].Value), Convert.ToInt32(args[2].Value), (bool)args[3].Value!,
                (bool)args[4].Value!, Convert.ToInt32(args[5].Value), deferrable, (string)args[6].Value!,
            });

            checkedLayouts++;
            if (!(bool)Matches.Invoke(null, new[] { model, Parse() })!)
                failures.Add($"{name}: the emitted layout does not match its model");
            foreach (var (change, apply) in Changes(Parse()))
            {
                var entry = Parse();
                apply(entry);
                if ((bool)Matches.Invoke(null, new[] { model, entry })!)
                    failures.Add($"{name}: matches with {change}");
            }
        }

        Assert.IsTrue(checkedLayouts > 0, "no layouts emitted");
        Assert.AreEqual(0, failures.Count, string.Join("\n", failures));
    }

    /// <summary>Every change of one field of a parsed section (named, and applied to a fresh parse).</summary>
    private static IEnumerable<(string, Action<object>)> Changes(object parsed)
    {
        yield return ("another format", e => Set(e, "Format", (string)Get(e, "Format") == "Keyed" ? "Positional" : "Keyed"));
        yield return ("another header", e => Set(e, "Header", (string)Get(e, "Header") == "TypeId" ? "None" : "TypeId"));
        yield return ("tracking flipped", e => Set(e, "TrackReferences", !(bool)Get(e, "TrackReferences")));
        yield return ("another id", e => Set(e, "Id", (int)Get(e, "Id") ^ 1));
        yield return ("deferrable changed", e => Set(e, "Deferrable", Get(e, "Deferrable") is bool d ? (bool?)!d : true));
        yield return ("another raw layout", e => Set(e, "Raw", Get(e, "Raw") is string r ? r + "x" : "raw(4)"));
        yield return ("other elements", e => Set(e, "Elements", Get(e, "Elements") is string s ? s + "x" : "seq(raw(4))"));
        yield return ("a member more", e => Members(e).Add(NewMember("Extra", "int", "raw(4)", null)));

        int count = Members(parsed).Count;
        if (count == 0)
            yield break;
        yield return ("a member less", e => Members(e).RemoveAt(Members(e).Count - 1));
        if (count > 1)
            yield return ("the first member moved to the end", e =>
            {
                var members = Members(e);
                object first = members[0];
                members.RemoveAt(0);
                members.Add(first);
            });
        for (int i = 0; i < count; i++)
        {
            int at = i;
            yield return ($"member {at} renamed", e => Set(Members(e)[at], "Name", (string)Get(Members(e)[at], "Name") + "x"));
            yield return ($"member {at} of another type", e => Set(Members(e)[at], "Type", (string)Get(Members(e)[at], "Type") + "x"));
            yield return ($"member {at} encoded otherwise", e => Set(Members(e)[at], "Encoding", (string)Get(Members(e)[at], "Encoding") + "x"));
            yield return ($"member {at} keyed otherwise", e => Set(Members(e)[at], "Key", Get(Members(e)[at], "Key") is int k ? (int?)(k + 1) : 1));
            yield return ($"member {at} with a former key more", e => ((List<int>)Get(Members(e)[at], "Former")).Add(0x1234));
            yield return ($"member {at} with a former key changed", e =>
            {
                var former = (List<int>)Get(Members(e)[at], "Former");
                if (former.Count == 0)
                    former.Add(0x1234);
                else
                    former[former.Count - 1]++;
            });
        }
    }

    private static object Get(object target, string field) => target.GetType().GetField(field)!.GetValue(target);

    private static void Set(object target, string field, object value) => target.GetType().GetField(field)!.SetValue(target, value);

    private static IList Members(object entry) => (IList)Get(entry, "Members");

    private static object NewMember(string name, string type, string encoding, int? key)
    {
        var member = Activator.CreateInstance(MemberType)!;
        Set(member, "Name", name);
        Set(member, "Type", type);
        Set(member, "Encoding", encoding);
        Set(member, "Key", key);
        return member;
    }

    /// <summary>The name <see cref="Compilation.GetTypeByMetadataName"/> finds <paramref name="type"/>'s definition by.</summary>
    private static string MetadataName(INamedTypeSymbol type)
    {
        type = type.OriginalDefinition;
        string name = type.MetadataName;
        for (var outer = type.ContainingType; outer != null; outer = outer.ContainingType)
            name = outer.MetadataName + "+" + name;
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }
}
#endif
