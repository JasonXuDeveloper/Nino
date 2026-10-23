#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// Every combination of the ways a type hierarchy can be declared, generated, compiled with the generator and
    /// round-tripped: the kind of the base (abstract class, class, interface, abstract record), its type parameters
    /// (none, one, two) and, for a generic base, more closed instances with subtypes of their own (tuple and class
    /// type arguments), where it is declared (namespace, the global namespace, nested in a class, nested in a generic
    /// class), the format,
    /// reference tracking, a non-generic abstract type between the base and the subtypes, and whether the subtypes are in
    /// the base's assembly or in another one; with every kind of
    /// subtype (plain, generic, with a private member, built by a constructor, [NinoPooled], a struct and a record struct
    /// for interfaces), each read
    /// through the base, the type between, itself and object, by the boxed API, alone and as a list element, plus null and data of another
    /// type. A combination nobody thought of is still in the matrix: add a dimension here when a feature adds one.
    /// </summary>
    [TestClass]
    public class ShapeMatrixTests
    {
        private enum Kind { Abstract, Class, Interface, Record }

        private enum Place { Namespace, Global, Nested, GenericNested }

        private static readonly string[] Formats = { "Positional", "VersionTolerant", "Keyed" };

        // Rows 0 to 3 and 8 to 11: each kind of base, its subtypes in its assembly; rows 4 to 7 and 12 to 15: in another
        // assembly. Rows 0 to 7 declare the shapes in a namespace or the global namespace, rows 8 to 15 nested in a class
        // or a generic class: a row compiles some 70 shapes at once, where every place at once took gigabytes.
        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        [DataRow(4)]
        [DataRow(5)]
        [DataRow(6)]
        [DataRow(7)]
        [DataRow(8)]
        [DataRow(9)]
        [DataRow(10)]
        [DataRow(11)]
        [DataRow(12)]
        [DataRow(13)]
        [DataRow(14)]
        [DataRow(15)]
        public void EveryShapeCompilesAndRoundTrips(int row)
        {
            int kind = row % 4;
            bool otherAssembly = row % 8 >= 4;
            var places = row < 8 ? new[] { Place.Namespace, Place.Global } : new[] { Place.Nested, Place.GenericNested };
            // otherAssembly: the bases in one assembly, the subtypes in another that references it as metadata (a
            // package or a project reference on the command line), both loaded into one context.
            var bases = new StringBuilder("using Nino.Core;\n");
            var source = new StringBuilder(Prelude);
            var probes = new List<string>();
            foreach (int arity in new[] { 0, 1, 2 })
            foreach (Place place in places)
            foreach (string format in Formats)
            foreach (bool track in new[] { false, true })
            foreach (bool middle in new[] { false, true })
            {
                // Each run its own names: the same full name loaded twice in one process is a type id collision.
                string ns = $"{(otherAssembly ? "Split" : "")}{(Kind)kind}{arity}{place}{format}{(track ? "Tracked" : "")}{(middle ? "Middle" : "")}";
                source.Append(new Shape((Kind)kind, arity, place, format, track, middle).Emit(ns, bases, otherAssembly));
                probes.Add(ns);
            }

            source.Append("public static class Probes\n{\n    public static List<string> Run()\n    {\n");
            source.Append("        var e = new List<string>();\n");
            foreach (string ns in probes)
                source.Append($"        {ns}.Probe.Run(e);\n");
            source.Append("        return e;\n    }\n}\n");

            Assembly probesAssembly;
            if (otherAssembly)
            {
                var library = CornerCompiler.Compile(bases.ToString(), load: false);
                Assert.AreEqual(0, library.Errors.Length, library.ErrorText);
                var compiled = CornerCompiler.CompileAgainst(source.ToString(), library);
                Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
                var context = new TwoAssemblies(library.Image);
                probesAssembly = context.LoadFromStream(new MemoryStream(compiled.Image));
            }
            else
            {
                var compiled = CornerCompiler.Compile(source.Insert(Prelude.Length, bases.ToString().Substring("using Nino.Core;\n".Length)).ToString());
                Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
                probesAssembly = compiled.Assembly;
            }

            var errors = (List<string>)probesAssembly.GetType("Probes")!.GetMethod("Run")!.Invoke(null, null);
            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
        }

        /// <summary>Resolves the probes' reference to the bases' assembly; everything else from the default context.</summary>
        private sealed class TwoAssemblies : AssemblyLoadContext
        {
            private readonly Assembly _library;

            public TwoAssemblies(byte[] library) : base("nino-shape-matrix") =>
                _library = LoadFromStream(new MemoryStream(library));

            protected override Assembly Load(AssemblyName name) => name.Name == _library.GetName().Name ? _library : null;
        }

        private const string Prelude = @"using System;
using System.Collections.Generic;
using Nino.Core;

public static class P
{
    public static T Init<T>(T value, Action<T> init)
    {
        init(value);
        return value;
    }

    public static void Check<TB>(List<string> e, string name, TB v, Func<TB, byte[]> write, Func<byte[], TB> read,
        Func<List<TB>, byte[]> writeList, Func<byte[], List<TB>> readList, Func<TB, bool> ok)
    {
        try
        {
            byte[] a = write(v);
            TB r = read(a);
            if (r == null || r.GetType() != v.GetType()) { e.Add(name + "": read as "" + r?.GetType()); return; }
            if (!ok(r)) { e.Add(name + "": values""); return; }
            if (!a.AsSpan().SequenceEqual(write(r))) { e.Add(name + "": bytes of a second write""); return; }
            if (!typeof(TB).IsValueType && read(write(default)) != null) { e.Add(name + "": null""); return; }
            var list = readList(writeList(new List<TB> { v, default }));
            if (list.Count != 2 || list[0]?.GetType() != v.GetType() || !ok(list[0]) ||
                !typeof(TB).IsValueType && list[1] != null)
                e.Add(name + "": list"");
        }
        catch (Exception x) { e.Add(name + "": "" + x.GetType().Name + "": "" + x.Message); }
    }

    public static void Rejects(List<string> e, string name, Action read)
    {
        try { read(); e.Add(name + "": data of another type was read""); }
        catch (InvalidOperationException) { }
        catch (Exception x) { e.Add(name + "": "" + x.GetType().Name + "": "" + x.Message); }
    }
}

namespace Other
{
    [NinoType] public abstract class OtherBase { public int A; }
    [NinoType] public sealed class OtherLeaf : OtherBase { }
}
";

        private sealed class Shape
        {
            private readonly Kind _kind;
            private readonly int _arity;
            private readonly Place _place;
            private readonly string _format;
            private readonly bool _track;
            private readonly bool _middle;
            private string _ns = "";

            public Shape(Kind kind, int arity, Place place, string format, bool track, bool middle)
            {
                _kind = kind;
                _arity = arity;
                _place = place;
                _format = format;
                _track = track;
                _middle = middle;
            }

            private bool Fields => _kind != Kind.Interface;
            private string Keyword => _kind == Kind.Record ? "record" : "class";
            private string Prefix => _place switch { Place.Nested => "Holder.", Place.GenericNested => "Holder<long>.", _ => "" };

            /// <summary>Whether the types are declared in a class (Holder) rather than in a namespace.</summary>
            private bool InHolder => _place is Place.Nested or Place.GenericNested;

            /// <summary>
            /// The name of a declared type: in the global namespace (<see cref="Place.Global"/>) every shape's types
            /// are there, so each is prefixed with its shape's namespace name.
            /// </summary>
            private string N(string name) => _place == Place.Global ? _ns + "_" + name : name;

            private static string Parameters(int n) => n == 0 ? "" : "<" + string.Join(", ", new[] { "T1", "T2" }.Take(n)) + ">";
            private static string Arguments(int n) => n == 0 ? "" : "<" + string.Join(", ", new[] { "int", "string" }.Take(n)) + ">";

            // More closed instances of a generic base, each with subtypes of its own (a dispatch per instance): type
            // arguments that are tuples, a class with subtypes and a sealed class.
            private string[] OtherArguments(int n) => n switch
            {
                0 => new string[0],
                1 => new[] { "<(int, long)>", $"<{N("OpenArg")}>" },
                _ => new[] { $"<(int, string), {N("OpenArg")}>", $"<{N("SealedArg")}, (int, long)>" },
            };

            private string Attribute(bool nonPublic = false) =>
                $"[NinoType(format: NinoFormat.{_format}, trackReferences: {(_track ? "true" : "false")}" +
                $"{(nonPublic ? ", containNonPublicMembers: true" : "")})]";

            // (name, value) of the members every subtype inherits.
            private List<(string Name, string Type, string Value)> Inherited()
            {
                var list = new List<(string, string, string)>();
                if (!Fields)
                    return list;
                if (_arity == 0)
                    list.Add(("V0", "int", "1"));
                if (_arity >= 1)
                    list.Add(("V1", "int", "1"));
                if (_arity >= 2)
                    list.Add(("V2", "string", "\"s\""));
                if (_place == Place.GenericNested)
                    list.Add(("O", "X", "2"));
                if (_middle)
                    list.Add(("M", "int", "3"));
                return list;
            }

            /// <summary>
            /// The source of the shape, in namespace <paramref name="ns"/>: the base (and the type between) in
            /// <paramref name="bases"/>, the subtypes and their checks in the return value. With
            /// <paramref name="split"/> the two go to different assemblies, and the subtypes are declared next to the
            /// containing class instead of in it (a class cannot be partial across assemblies).
            /// </summary>
            public string Emit(string ns, StringBuilder bases, bool split)
            {
                _ns = ns;
                bool global = _place == Place.Global;
                var s = new StringBuilder();
                if (!global)
                    bases.Append($"namespace {ns}\n{{\n");
                // Type arguments of the other closed instances, in each namespace: a type loaded twice is an id collision.
                if (_arity > 0)
                    bases.Append($"{Attribute()} public sealed class {N("SealedArg")} {{ public int A; }}\n" +
                                 $"{Attribute()} public class {N("OpenArg")} {{ public int A; }}\n" +
                                 $"{Attribute()} public sealed class {N("OpenArgChild")} : {N("OpenArg")} {{ }}\n");
                if (InHolder)
                    bases.Append(_place == Place.Nested ? "public partial class Holder\n{\n" : "public partial class Holder<X>\n{\n");

                string baseFields = !Fields ? ""
                    : (_arity == 0 ? "public int V0; " : _arity == 1 ? "public T1 V1; " : "public T1 V1; public T2 V2; ") +
                      (_place == Place.GenericNested ? "public X O; " : "");
                string baseKeyword = _kind switch
                {
                    Kind.Abstract => "abstract class", Kind.Class => "class", Kind.Interface => "interface", _ => "abstract record"
                };
                bases.Append($"{Attribute()} public {baseKeyword} {N("Base")}{Parameters(_arity)} {{ {baseFields}}}\n");
                string closedBase = N("Base") + Arguments(_arity);
                if (_middle)
                    bases.Append(_kind == Kind.Interface
                        ? $"{Attribute()} public interface {N("Mid")} : {closedBase} {{ }}\n"
                        : $"{Attribute()} public abstract {Keyword} {N("Mid")} : {closedBase} {{ public int M; }}\n");

                // Where the subtypes are declared: in the container, or (split) in the namespace, naming it.
                var leaves = split ? s : bases;
                string near = split ? Prefix : "";
                string leafPrefix = split ? "" : Prefix;
                string outer = split ? "long" : "X";
                // The probes are in the shape's namespace; with the global namespace, after the subtypes.
                bool probesOpen = false;
                if (split)
                {
                    if (InHolder)
                        bases.Append("}\n");
                    if (!global)
                    {
                        bases.Append("}\n");
                        s.Append($"namespace {ns}\n{{\n");
                        probesOpen = true;
                    }
                }

                string parent = near + (_middle ? N("Mid") : closedBase);
                leaves.Append($"{Attribute()} public sealed {Keyword} {N("PLeaf")} : {parent} {{ public int W; }}\n");
                if (!_middle && _arity > 0)
                    leaves.Append($"{Attribute()} public sealed {Keyword} {N("GLeaf")}{Parameters(_arity)} : {near}{N("Base")}{Parameters(_arity)} {{ public int W; }}\n");
                else
                    leaves.Append($"{Attribute()} public sealed {Keyword} {N("GLeaf")}<TL> : {parent} {{ public TL W; }}\n");
                string[] others = OtherArguments(_arity);
                for (int i = 0; i < others.Length; i++)
                    leaves.Append($"{Attribute()} public sealed {Keyword} {N("XLeaf" + i)} : {near}{N("Base")}{others[i]} {{ public int W; }}\n");
                leaves.Append($"{Attribute(nonPublic: true)} public sealed partial {Keyword} {N("QLeaf")} : {parent} " +
                              "{ private int _w; public int GetW() => _w; public void SetW(int w) => _w = w; }\n");
                // Pooled: read into instances its pool hands out, through the hooks of its generated partial.
                leaves.Append($"{Attribute()} [NinoPooled] public sealed partial {Keyword} {N("PoolLeaf")} : {parent} {{ public int W; " +
                              $"private static partial IEnumerable<{N("PoolLeaf")}> OnNinoPoolWarmup() => null; " +
                              "private partial void OnNinoPoolReturn() { } }\n");
                var all = Inherited().Select(m => m.Type == "X" ? (m.Name, outer, m.Value) : m)
                    .Concat(new[] { ("W", "int", "7") }).ToList();
                if (!_track)
                {
                    leaves.Append($"{Attribute()} public sealed {Keyword} {N("CLeaf")} : {parent} {{ public readonly int W; " +
                                  $"[NinoConstructor({string.Join(", ", all.Select(m => $"\"{m.Item1}\""))})] " +
                                  $"public {N("CLeaf")}({string.Join(", ", all.Select(m => $"{m.Item2} {m.Item1.ToLowerInvariant()}"))}) " +
                                  $"{{ {string.Join(" ", all.Select(m => $"this.{m.Item1} = {m.Item1.ToLowerInvariant()};"))} }} }}\n");
                    if (_kind == Kind.Interface)
                        leaves.Append($"{Attribute()} public struct {N("SLeaf")} : {parent} {{ public int W; }}\n" +
                                      $"{Attribute()} public record struct {N("RLeaf")} : {parent} {{ public int W; }}\n");
                }

                if (!split && InHolder)
                    bases.Append("}\n");
                if (!split && !global)
                    bases.Append("}\n");
                if (!probesOpen)
                    s.Append($"namespace {ns}\n{{\n");

                // The checks: every subtype through the base, the type between and itself.
                s.Append("public static class Probe\n{\n    public static void Run(List<string> e)\n    {\n");
                string inits = string.Join(", ", Inherited().Select(m => $"{m.Name} = {m.Value}"));
                string checks = string.Concat(Inherited().Select(m => $" && q.{m.Name} == {m.Value}"));
                string gleaf = N("GLeaf") + (!_middle && _arity > 0 ? Arguments(_arity) : "<int>");
                var types = new List<(string Type, string Create, string Check)>
                {
                    (N("PLeaf"), $"new {leafPrefix}{N("PLeaf")} {{ {Join(inits, "W = 7")} }}", $"q.W == 7{checks}"),
                    (gleaf, $"new {leafPrefix}{gleaf} {{ {Join(inits, "W = 7")} }}", $"q.W == 7{checks}"),
                    (N("QLeaf"), $"P.Init(new {leafPrefix}{N("QLeaf")} {{ {inits} }}, q => q.SetW(7))", $"q.GetW() == 7{checks}"),
                    (N("PoolLeaf"), $"new {leafPrefix}{N("PoolLeaf")} {{ {Join(inits, "W = 7")} }}", $"q.W == 7{checks}"),
                };
                if (!_track)
                {
                    types.Add((N("CLeaf"), $"new {leafPrefix}{N("CLeaf")}({string.Join(", ", all.Select(m => m.Item3))})", $"q.W == 7{checks}"));
                    if (_kind == Kind.Interface)
                    {
                        types.Add((N("SLeaf"), $"new {leafPrefix}{N("SLeaf")} {{ W = 7 }}", "q.W == 7"));
                        types.Add((N("RLeaf"), $"new {leafPrefix}{N("RLeaf")} {{ W = 7 }}", "q.W == 7"));
                    }
                }

                if (_kind == Kind.Class)
                {
                    var own = Inherited().Where(m => m.Name != "M").ToList();
                    Check(s, ns, Prefix + closedBase, Prefix + closedBase,
                        $"new {Prefix}{closedBase} {{ {string.Join(", ", own.Select(m => $"{m.Name} = {m.Value}"))} }}",
                        string.Join(" && ", own.Select(m => $"q.{m.Name} == {m.Value}")));
                }

                foreach (var leaf in types)
                {
                    var through = new List<string> { Prefix + closedBase, leafPrefix + leaf.Type };
                    if (_middle)
                        through.Insert(1, Prefix + N("Mid"));
                    foreach (string type in through)
                        Check(s, ns, type, leafPrefix + leaf.Type, leaf.Create, leaf.Check);
                    // By runtime type: as object, and through the boxed API naming the base.
                    Check(s, ns, "object", leafPrefix + leaf.Type, leaf.Create, leaf.Check);
                    Check(s, ns, "object", leafPrefix + leaf.Type, leaf.Create, leaf.Check, boxedAs: Prefix + closedBase);
                    if (!_track)
                        s.Append($"        P.Rejects(e, \"{ns} {leaf.Type} read as another type\", () => " +
                                 $"NinoDeserializer.Deserialize<Other.OtherBase>(NinoSerializer.Serialize<{Prefix}{closedBase}>({leaf.Create})));\n");
                }

                // The subtypes of the other closed instances: through their own base, and rejected through the first one.
                for (int i = 0; i < others.Length; i++)
                {
                    string leaf = N("XLeaf" + i), create = $"new {leafPrefix}{leaf} {{ W = 7 }}";
                    foreach (string type in new[] { Prefix + N("Base") + others[i], leafPrefix + leaf, "object" })
                        Check(s, ns, type, leafPrefix + leaf, create, "q.W == 7");
                    Check(s, ns, "object", leafPrefix + leaf, create, "q.W == 7", boxedAs: Prefix + N("Base") + others[i]);
                    if (!_track)
                        s.Append($"        P.Rejects(e, \"{ns} {leaf} read as {closedBase}\", () => " +
                                 $"NinoDeserializer.Deserialize<{Prefix}{closedBase}>(NinoSerializer.Serialize<{Prefix}{N("Base")}{others[i]}>({create})));\n");
                }

                s.Append("    }\n}\n}\n");
                return s.ToString();
            }

            private static string Join(string a, string b) => a.Length == 0 ? b : a + ", " + b;

            private static void Check(StringBuilder s, string ns, string through, string leaf, string create, string check,
                string boxedAs = null) =>
                s.Append($"        P.Check<{through}>(e, \"{ns} {leaf} as {boxedAs ?? through}{(boxedAs != null ? " (boxed)" : "")}\", {create}, " +
                         (boxedAs == null
                             ? $"x => NinoSerializer.Serialize<{through}>(x), b => NinoDeserializer.Deserialize<{through}>(b), "
                             : $"x => NinoSerializer.SerializeBoxed(x, typeof({boxedAs})), b => NinoDeserializer.DeserializeBoxed(b, typeof({boxedAs})), ") +
                         $"x => NinoSerializer.Serialize(x), b => NinoDeserializer.Deserialize<List<{through}>>(b), " +
                         $"r => r is {leaf} q && {check});\n");
        }
    }
}
#endif
