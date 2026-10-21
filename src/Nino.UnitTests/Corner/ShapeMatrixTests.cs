using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// Every combination of the ways a type hierarchy can be declared, generated, compiled with the generator and
    /// round-tripped: the kind of the base (abstract class, class, interface, abstract record), its type parameters
    /// (none, one, two), where it is declared (namespace, nested in a class, nested in a generic class), the format,
    /// reference tracking and a non-generic abstract type between the base and the subtypes; with every kind of
    /// subtype (plain, generic, with a private member, built by a constructor, a struct for interfaces), each read
    /// through the base, the type between and itself, alone and as a list element, plus null and data of another
    /// type. A combination nobody thought of is still in the matrix: add a dimension here when a feature adds one.
    /// </summary>
    [TestClass]
    public class ShapeMatrixTests
    {
        private enum Kind { Abstract, Class, Interface, Record }

        private enum Place { Namespace, Nested, GenericNested }

        private static readonly string[] Formats = { "Positional", "VersionTolerant", "Keyed" };

        [TestMethod]
        [DataRow(0)]
        [DataRow(1)]
        [DataRow(2)]
        [DataRow(3)]
        public void EveryShapeCompilesAndRoundTrips(int kind)
        {
            var source = new StringBuilder(Prelude);
            var probes = new List<string>();
            foreach (int arity in new[] { 0, 1, 2 })
            foreach (Place place in Enum.GetValues(typeof(Place)))
            foreach (string format in Formats)
            foreach (bool track in new[] { false, true })
            foreach (bool middle in new[] { false, true })
            {
                string ns = $"{(Kind)kind}{arity}{place}{format}{(track ? "Tracked" : "")}{(middle ? "Middle" : "")}";
                source.Append(new Shape((Kind)kind, arity, place, format, track, middle).Emit(ns));
                probes.Add(ns);
            }

            source.Append("public static class Probes\n{\n    public static List<string> Run()\n    {\n");
            source.Append("        var e = new List<string>();\n");
            foreach (string ns in probes)
                source.Append($"        {ns}.Probe.Run(e);\n");
            source.Append("        return e;\n    }\n}\n");

            var compiled = CornerCompiler.Compile(source.ToString());
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            var errors = (List<string>)compiled.Assembly.GetType("Probes")!.GetMethod("Run")!.Invoke(null, null);
            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
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

            private static string Parameters(int n) => n == 0 ? "" : "<" + string.Join(", ", new[] { "T1", "T2" }.Take(n)) + ">";
            private static string Arguments(int n) => n == 0 ? "" : "<" + string.Join(", ", new[] { "int", "string" }.Take(n)) + ">";

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

            public string Emit(string ns)
            {
                var s = new StringBuilder();
                s.Append($"namespace {ns}\n{{\n");
                if (_place != Place.Namespace)
                    s.Append(_place == Place.Nested ? "public partial class Holder\n{\n" : "public partial class Holder<X>\n{\n");

                string baseFields = !Fields ? ""
                    : (_arity == 0 ? "public int V0; " : _arity == 1 ? "public T1 V1; " : "public T1 V1; public T2 V2; ") +
                      (_place == Place.GenericNested ? "public X O; " : "");
                string baseKeyword = _kind switch
                {
                    Kind.Abstract => "abstract class", Kind.Class => "class", Kind.Interface => "interface", _ => "abstract record"
                };
                s.Append($"{Attribute()} public {baseKeyword} Base{Parameters(_arity)} {{ {baseFields}}}\n");
                string closedBase = "Base" + Arguments(_arity);
                if (_middle)
                    s.Append(_kind == Kind.Interface
                        ? $"{Attribute()} public interface Mid : {closedBase} {{ }}\n"
                        : $"{Attribute()} public abstract {Keyword} Mid : {closedBase} {{ public int M; }}\n");
                string parent = _middle ? "Mid" : closedBase;

                s.Append($"{Attribute()} public sealed {Keyword} PLeaf : {parent} {{ public int W; }}\n");
                if (!_middle && _arity > 0)
                    s.Append($"{Attribute()} public sealed {Keyword} GLeaf{Parameters(_arity)} : Base{Parameters(_arity)} {{ public int W; }}\n");
                else
                    s.Append($"{Attribute()} public sealed {Keyword} GLeaf<TL> : {parent} {{ public TL W; }}\n");
                s.Append($"{Attribute(nonPublic: true)} public sealed partial {Keyword} QLeaf : {parent} " +
                         "{ private int _w; public int GetW() => _w; public void SetW(int w) => _w = w; }\n");
                var all = Inherited().Concat(new[] { ("W", "int", "7") }).ToList();
                if (!_track)
                {
                    s.Append($"{Attribute()} public sealed {Keyword} CLeaf : {parent} {{ public readonly int W; " +
                             $"[NinoConstructor({string.Join(", ", all.Select(m => $"\"{m.Item1}\""))})] " +
                             $"public CLeaf({string.Join(", ", all.Select(m => $"{m.Item2} {m.Item1.ToLowerInvariant()}"))}) " +
                             $"{{ {string.Join(" ", all.Select(m => $"this.{m.Item1} = {m.Item1.ToLowerInvariant()};"))} }} }}\n");
                    if (_kind == Kind.Interface)
                        s.Append($"{Attribute()} public struct SLeaf : {parent} {{ public int W; }}\n");
                }

                if (_place != Place.Namespace)
                    s.Append("}\n");

                // The checks: every subtype through the base, the type between and itself.
                s.Append("public static class Probe\n{\n    public static void Run(List<string> e)\n    {\n");
                string inits = string.Join(", ", Inherited().Select(m => $"{m.Name} = {m.Value}"));
                string checks = string.Concat(Inherited().Select(m => $" && q.{m.Name} == {m.Value}"));
                string gleaf = !_middle && _arity > 0 ? "GLeaf" + Arguments(_arity) : "GLeaf<int>";
                var leaves = new List<(string Type, string Create, string Check)>
                {
                    ("PLeaf", $"new {Prefix}PLeaf {{ {Join(inits, "W = 7")} }}", $"q.W == 7{checks}"),
                    (gleaf, $"new {Prefix}{gleaf} {{ {Join(inits, "W = 7")} }}", $"q.W == 7{checks}"),
                    ("QLeaf", $"P.Init(new {Prefix}QLeaf {{ {inits} }}, q => q.SetW(7))", $"q.GetW() == 7{checks}"),
                };
                if (!_track)
                {
                    leaves.Add(("CLeaf", $"new {Prefix}CLeaf({string.Join(", ", all.Select(m => m.Item3))})", $"q.W == 7{checks}"));
                    if (_kind == Kind.Interface)
                        leaves.Add(("SLeaf", $"new {Prefix}SLeaf {{ W = 7 }}", "q.W == 7"));
                }

                if (_kind == Kind.Class)
                {
                    var own = Inherited().Where(m => m.Name != "M").ToList();
                    Check(s, ns, Prefix + closedBase, Prefix + closedBase,
                        $"new {Prefix}{closedBase} {{ {string.Join(", ", own.Select(m => $"{m.Name} = {m.Value}"))} }}",
                        string.Join(" && ", own.Select(m => $"q.{m.Name} == {m.Value}")));
                }
                foreach (var leaf in leaves)
                {
                    var through = new List<string> { Prefix + closedBase, Prefix + leaf.Type };
                    if (_middle)
                        through.Insert(1, Prefix + "Mid");
                    foreach (string type in through)
                        Check(s, ns, type, Prefix + leaf.Type, leaf.Create, leaf.Check);
                    if (!_track)
                        s.Append($"        P.Rejects(e, \"{ns} {leaf.Type} read as another type\", () => " +
                                 $"NinoDeserializer.Deserialize<Other.OtherBase>(NinoSerializer.Serialize<{Prefix}{closedBase}>({leaf.Create})));\n");
                }

                s.Append("    }\n}\n}\n");
                return s.ToString();
            }

            private static string Join(string a, string b) => a.Length == 0 ? b : a + ", " + b;

            private static void Check(StringBuilder s, string ns, string through, string leaf, string create, string check) =>
                s.Append($"        P.Check<{through}>(e, \"{ns} {leaf} as {through}\", {create}, " +
                         $"x => NinoSerializer.Serialize<{through}>(x), b => NinoDeserializer.Deserialize<{through}>(b), " +
                         $"x => NinoSerializer.Serialize(x), b => NinoDeserializer.Deserialize<List<{through}>>(b), " +
                         $"r => r is {leaf} q && {check});\n");
        }
    }
}
