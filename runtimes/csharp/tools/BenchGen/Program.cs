// Writes the timed methods of the .NET benchmarks (runtimes/csharp/benchmarks/Nino.Benchmark) from their cases and the
// libraries' adapters:
//     dotnet run --project runtimes/csharp/tools/BenchGen [-- --check] [-- <.NET root>]
// For each class with [BenchCase] attributes (on any of its partial declarations) it writes <Class>.Cases.g.cs beside
// the file that declares <Class>: the buffers (each adapter's Setup), the bytes every case reads (ToBytes, and Prepare
// for a value made outside the timed region), their payload sizes, the [GlobalSetup], and per case and library one
// serialize and one deserialize method, each the body of the adapter's template with the case's names put in. The
// methods keep the names every script and the docs site read (<Library>Serialize<Case>, <Library>Deserialize<Case>),
// and their IL is what the same method written by hand compiles to: an adapter's generic call in a benchmark would
// take Nino's slower generic path, so the adapters are templates (Adapters/, not compiled) and this tool expands them as
// text, reading syntax only. A method the class writes itself is kept: BenchGen writes the others.
// The file holds the v4 build's class and, under NINO_BENCH_V3, the v3 build's (the adapters whose Builds name v3).
// --check writes nothing and fails while a file is out of date (ci.yml).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.BenchGen;

internal static class Program
{
    private const string Project = "benchmarks/Nino.Benchmark";
    private const string Suffix = ".Cases.g.cs";

    private static readonly (string Name, string Symbol)[] Builds = [("v4", ""), ("v3", "NINO_BENCH_V3")];

    private static int Main(string[] args)
    {
        bool check = args.Contains("--check");
        string? rootArg = args.FirstOrDefault(a => !a.StartsWith("--"));
        string? root = rootArg != null ? Path.GetFullPath(rootArg) : FindRoot(Directory.GetCurrentDirectory());
        if (root == null)
        {
            Console.Error.WriteLine($"Cannot find the .NET root (runtimes/csharp, the directory containing {Project}).");
            return 1;
        }

        string project = Path.Combine(root, Project);
        var adapters = Directory.GetFiles(Path.Combine(project, "Adapters"), "*.cs").Order(StringComparer.Ordinal)
            .SelectMany(Adapter.Read).ToList();
        foreach (var adapter in adapters.Where(a => a.Like != null))
            adapter.Inherit(adapters.Single(a => a.Like == null && a.Name == adapter.Like && a.Builds.Intersect(adapter.Builds).Any()));
        var sources = Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".g.cs", StringComparison.Ordinal) && !Ignored(project, f))
            .Order(StringComparer.Ordinal).ToList();

        var expected = new Dictionary<string, string>();
        foreach (var (path, text) in Generate(sources, adapters))
            expected[path] = text;

        int stale = 0;
        foreach (string file in Directory.GetFiles(project, "*" + Suffix, SearchOption.AllDirectories)
                     .Where(f => !Ignored(project, f) && !expected.ContainsKey(f)))
        {
            stale++;
            Console.WriteLine($"{Path.GetRelativePath(root, file)}: {(check ? "no cases declare it" : "deleted")}");
            if (!check) File.Delete(file);
        }

        foreach (var (path, content) in expected.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, path);
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                Console.WriteLine($"{relative}: up to date");
                continue;
            }

            stale++;
            Console.WriteLine($"{relative}: {(check ? "out of date" : "written")}");
            if (!check) File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        if (check && stale > 0)
        {
            Console.Error.WriteLine("Run `dotnet run --project runtimes/csharp/tools/BenchGen` and commit the result.");
            return 1;
        }

        return 0;
    }

    private static bool Ignored(string project, string file)
    {
        string first = Path.GetRelativePath(project, file).Split(Path.DirectorySeparatorChar)[0];
        return first is "bin" or "obj" or "Adapters";
    }

    private static string? FindRoot(string directory)
    {
        for (var dir = new DirectoryInfo(directory); dir != null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, Project)))
                return dir.FullName;
            string csharp = Path.Combine(dir.FullName, "runtimes", "csharp");
            if (Directory.Exists(Path.Combine(csharp, Project)))
                return csharp;
        }

        return null;
    }

    /// <summary>The generated file of each class that declares cases: its path and text.</summary>
    private static IEnumerable<(string Path, string Text)> Generate(List<string> sources, List<Adapter> adapters)
    {
        var texts = sources.ToDictionary(s => s, File.ReadAllText);
        // Per build, every partial declaration of every class, by its full name.
        var views = new List<Dictionary<string, List<Part>>>();
        foreach (var (_, symbol) in Builds)
        {
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
                .WithPreprocessorSymbols(symbol.Length > 0 ? new[] { symbol } : Array.Empty<string>());
            var parts = new Dictionary<string, List<Part>>();
            foreach (string source in sources)
            {
                var unit = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(texts[source], options, source).GetRoot();
                foreach (var c in unit.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    string ns = c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
                    string key = ns + "." + c.Identifier.ValueText;
                    if (!parts.TryGetValue(key, out var list)) parts[key] = list = [];
                    list.Add(new Part(source, c, unit));
                }
            }

            views.Add(parts);
        }

        var classes = views.SelectMany(v => v).Where(p => p.Value.Any(d => Attributes(d.Class.AttributeLists, "BenchCase").Any()))
            .Select(p => p.Key).Distinct().Order(StringComparer.Ordinal);
        foreach (string key in classes)
        {
            string name = key[(key.LastIndexOf('.') + 1)..];
            // The file named after the class holds it; the generated file goes beside it.
            var all = views.SelectMany(v => v.TryGetValue(key, out var p) ? p : []).ToList();
            string main = all.Select(p => p.File).FirstOrDefault(f => Path.GetFileName(f) == name + ".cs") ?? all[0].File;
            var sb = new StringBuilder();
            sb.Append("// <auto-generated>\n");
            sb.Append($"// Generated by runtimes/csharp/tools/BenchGen from {name}'s [BenchCase] attributes and the adapters in Adapters/.\n");
            sb.Append("// Do not edit by hand: change those and run\n");
            sb.Append("//     dotnet run --project runtimes/csharp/tools/BenchGen\n");
            sb.Append("// ci.yml fails while this file is out of date (BenchGen --check).\n");
            sb.Append("// </auto-generated>\n");
            sb.Append("#nullable disable\n");
            // A class built only under a condition ([BenchClass(Condition = ...)]): its generated part too.
            string? condition = all.SelectMany(p => Program.Attributes(p.Class.AttributeLists, "BenchClass"))
                .SelectMany(a => a.ArgumentList!.Arguments).Where(a => a.NameEquals?.Name.Identifier.ValueText == "Condition")
                .Select(a => Program.Value(a.Expression)).FirstOrDefault();
            if (condition != null) sb.Append($"#if {condition}\n");
            for (int b = 0; b < Builds.Length; b++)
            {
                sb.Append(b == 0 ? "#if !NINO_BENCH_V3\n" : "#else\n");
                if (views[b].TryGetValue(key, out var parts) && parts.Any(d => Attributes(d.Class.AttributeLists, "BenchCase").Any()))
                {
                    // The class's own file first, then its other parts in the order of their names.
                    parts = parts.OrderBy(p => p.File == main ? 0 : 1).ThenBy(p => p.File, StringComparer.Ordinal).ToList();
                    sb.Append(new BenchClass(parts, Builds[b].Name, adapters.Where(a => a.Builds.Contains(Builds[b].Name)).ToList()).Write());
                }
            }

            sb.Append("#endif\n");
            if (condition != null) sb.Append("#endif\n");
            yield return (Path.Combine(Path.GetDirectoryName(main)!, name + Suffix), sb.ToString());
        }
    }

    internal static IEnumerable<AttributeSyntax> Attributes(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Where(a =>
        {
            string n = a.Name is QualifiedNameSyntax q ? q.Right.Identifier.ValueText : a.Name.ToString();
            return n == name || n == name + "Attribute";
        });

    /// <summary>The value of a string literal or a <c>nameof</c>, or the text of any other constant.</summary>
    internal static string Value(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax l => l.Token.ValueText,
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } n =>
            n.ArgumentList.Arguments[0].Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                var other => other.ToString(),
            },
        _ => e.ToString(),
    };
}

/// <summary>One partial declaration of a class and the file it is in.</summary>
internal sealed record Part(string File, ClassDeclarationSyntax Class, CompilationUnitSyntax Unit);

/// <summary>A library's adapter: a class of Adapters/ with [BenchLibrary] whose methods are the templates.</summary>
internal sealed class Adapter
{
    public string Name = "";
    public int Order, Slot;
    public bool Baseline;
    public string[] Builds = [];
    public string? Prepared, PrepareFrom, When, Unless, Like, Reads;
    public readonly Dictionary<string, string> Args = new();
    public readonly Dictionary<string, MethodDeclarationSyntax> Methods = new();
    public List<string> Usings = [];
    private string _file = "";

    public static IEnumerable<Adapter> Read(string file)
    {
        var unit = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(file),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), file).GetRoot();
        foreach (var c in unit.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var library = Program.Attributes(c.AttributeLists, "BenchLibrary").FirstOrDefault();
            if (library == null) continue;
            var adapter = new Adapter { Usings = unit.Usings.Select(u => u.ToString()).ToList(), _file = file };
            foreach (var arg in library.ArgumentList!.Arguments)
            {
                string value = Program.Value(arg.Expression);
                switch (arg.NameEquals?.Name.Identifier.ValueText)
                {
                    case null: adapter.Name = value; break;
                    case "Order": adapter.Order = int.Parse(value); break;
                    case "Slot": adapter.Slot = int.Parse(value); break;
                    case "Baseline": adapter.Baseline = value == "true"; break;
                    case "Builds": adapter.Builds = value.Split(' ', StringSplitOptions.RemoveEmptyEntries); break;
                    case "Prepared": adapter.Prepared = value; break;
                    case "PrepareFrom": adapter.PrepareFrom = value; break;
                    case "When": adapter.When = value; break;
                    case "Unless": adapter.Unless = value; break;
                    case "Like": adapter.Like = value; break;
                    case "Reads": adapter.Reads = value; break;
                    case var other: throw new InvalidOperationException($"{file}: unknown BenchLibrary argument {other}");
                }
            }

            foreach (var arg in Program.Attributes(c.AttributeLists, "BenchArg"))
                adapter.Args[Program.Value(arg.ArgumentList!.Arguments[0].Expression)] =
                    Program.Value(arg.ArgumentList.Arguments[1].Expression);
            foreach (var m in c.Members.OfType<MethodDeclarationSyntax>())
                adapter.Methods[m.Identifier.ValueText] = m;
            if (adapter.Like == null) adapter.Validate();
            yield return adapter;
        }
    }

    /// <summary>An adapter <c>Like</c> another takes the templates it does not write from it.</summary>
    public void Inherit(Adapter from)
    {
        foreach (var (name, method) in from.Methods)
            Methods.TryAdd(name, method);
        foreach (var (name, format) in from.Args)
            Args.TryAdd(name, format);
        Usings = Usings.Concat(from.Usings).Distinct().ToList();
        Validate();
    }

    private void Validate()
    {
        foreach (string required in new[] { "Setup", "Serialize", "Deserialize" })
            if (!Methods.ContainsKey(required))
                throw new InvalidOperationException($"{_file}: {Name} has no {required}");
        if (Reads == null && !Methods.ContainsKey("ToBytes"))
            throw new InvalidOperationException($"{_file}: {Name} has no ToBytes and reads no other library's bytes");
        if (Prepared != null != Methods.ContainsKey("Prepare"))
            throw new InvalidOperationException($"{_file}: Prepared and Prepare go together");
    }

    /// <summary>The adapter's instance fields: one per out parameter of Setup, <c>_&lt;library&gt;&lt;Parameter&gt;</c>.</summary>
    public IEnumerable<(string Parameter, string Type, string Field)> State() =>
        Methods["Setup"].ParameterList.Parameters.Where(p => p.Modifiers.Any(SyntaxKind.OutKeyword)).Select(p =>
        {
            string name = p.Identifier.ValueText;
            return (name, p.Type!.ToString(), StateField(char.ToUpperInvariant(name[0]) + name[1..]));
        });

    /// <summary>
    /// The buffers belong to the library whose Setup makes them: an adapter <c>Like</c> another writes to that one's.
    /// </summary>
    private string StateField(string suffix) => Field(Like ?? Name, suffix);

    /// <summary>An instance field of this library: <c>_&lt;library&gt;&lt;suffix&gt;</c>.</summary>
    public string Field(string suffix) => Field(Name, suffix);

    private static string Field(string library, string suffix) =>
        "_" + char.ToLowerInvariant(library[0]) + library[1..] + suffix;
}

/// <summary>
/// A case: its name and properties ([BenchCase]), over the class's ([BenchClass]) and BenchGen's defaults. A property
/// is a format: <c>{Case}</c>, <c>{Library}</c>, <c>{Slot}</c>, other properties' names and <c>{Type:Property}</c>
/// (the type of the field a property names) are put in.
/// </summary>
internal sealed class BenchCase(string name, Dictionary<string, string> properties, IReadOnlyDictionary<string, string> fields)
{
    public readonly string Name = name;
    public readonly Dictionary<string, string> Properties = properties;
    private readonly IReadOnlyDictionary<string, string> _fields = fields;

    /// <summary>The value of a property, or null when neither the case, its class nor the defaults set it.</summary>
    public string? Get(string property, Adapter? library = null) =>
        Properties.TryGetValue(property, out string? format) ? Expand(format, library) : null;

    public bool Has(string property) => Properties.ContainsKey(property);

    /// <summary>The type of a field of the class.</summary>
    public string TypeOf(string field) => _fields.TryGetValue(field, out string? type)
        ? type
        : throw new InvalidOperationException($"case {Name} reads {field}, no field of the class");

    public string Expand(string format, Adapter? library = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < format.Length; i++)
        {
            int end;
            if (format[i] != '{' || (end = format.IndexOf('}', i)) < 0)
            {
                sb.Append(format[i]);
                continue;
            }

            string key = format[(i + 1)..end];
            sb.Append(key switch
            {
                "Case" => Name,
                "Library" => library?.Name ?? throw new InvalidOperationException($"{Name}: {{Library}} outside a library"),
                "Slot" => library?.Slot.ToString() ?? throw new InvalidOperationException($"{Name}: {{Slot}} outside a library"),
                _ when key.StartsWith("Type:") => TypeOf(Get(key[5..], library) ?? throw new MissingProperty(key[5..])),
                _ => Get(key, library) ?? throw new MissingProperty(key),
            });
            i = end;
        }

        return sb.ToString();
    }

    /// <summary>A copy that reads the previous version's data: Old* properties in place of their own.</summary>
    public BenchCase Old()
    {
        var properties = new Dictionary<string, string>(Properties);
        foreach (var (key, value) in Properties)
            if (key.StartsWith("Old") && key is not ("Old" or "OldBytes"))
                properties[key[3..]] = value;
        return new BenchCase(Name, properties, _fields);
    }
}

internal sealed class MissingProperty(string property) : Exception(property);

/// <summary>One build's view of a class that declares cases, and its generated part.</summary>
internal sealed class BenchClass
{
    /// <summary>What a case reads when neither it nor its class ([BenchClass]) says otherwise.</summary>
    private static readonly Dictionary<string, string> Defaults = new()
    {
        ["Buffer"] = "320 * 1024",
        ["Value"] = "{Case}",
        ["Bytes"] = "Serialized{Case}[{Slot}]",
        ["OldBytes"] = "{Library}{Case}OldBytes",
        ["Order"] = "verb",
        ["Serialize"] = "buffer",
        ["JsonContext"] = "CaseJson",
        ["NinoGen"] = "NinoGen",
    };

    private readonly string _name;
    private readonly string _namespace;
    private readonly List<string> _usings;
    private readonly List<Adapter> _adapters;
    private readonly HashSet<string> _fields, _methods;
    private readonly List<(BenchCase Case, List<Row> Rows)> _cases = [];
    private readonly bool _arrays, _byCase, _toArray;
    private readonly string _buffer;

    /// <summary>A library's methods for a case: the names its templates get.</summary>
    private sealed record Row(Adapter Adapter, Dictionary<string, string> Args, Dictionary<string, string>? OldArgs);

    public BenchClass(List<Part> parts, string build, List<Adapter> adapters)
    {
        _name = parts[0].Class.Identifier.ValueText;
        _namespace = parts[0].Class.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();
        var fields = parts.SelectMany(p => p.Class.Members.OfType<FieldDeclarationSyntax>())
            .SelectMany(f => f.Declaration.Variables.Select(v => (v.Identifier.ValueText, f.Declaration.Type.ToString())))
            .ToDictionary(v => v.Item1, v => v.Item2);
        _fields = fields.Keys.ToHashSet();
        _methods = parts.SelectMany(p => p.Class.Members.OfType<MethodDeclarationSyntax>())
            .Where(m => m.Body != null || m.ExpressionBody != null).Select(m => m.Identifier.ValueText).ToHashSet();

        var classProperties = new Dictionary<string, string>(Defaults);
        foreach (var attribute in parts.SelectMany(p => Program.Attributes(p.Class.AttributeLists, "BenchClass")))
        foreach (var a in attribute.ArgumentList!.Arguments)
            classProperties[a.NameEquals!.Name.Identifier.ValueText] = Program.Value(a.Expression);
        _arrays = classProperties["Bytes"].Contains('[');
        _byCase = classProperties["Order"] == "case";
        _toArray = classProperties["Serialize"] == "array";

        foreach (var attribute in parts.SelectMany(p => Program.Attributes(p.Class.AttributeLists, "BenchCase")))
        {
            var arguments = attribute.ArgumentList!.Arguments;
            var properties = new Dictionary<string, string>(classProperties);
            foreach (var a in arguments.Skip(1))
                properties[a.NameEquals!.Name.Identifier.ValueText] = Program.Value(a.Expression);
            var benchCase = new BenchCase(Program.Value(arguments[0].Expression), properties, fields);
            if (benchCase.Get("Builds") is { } builds && !builds.Split(' ').Contains(build))
                continue;
            benchCase.TypeOf(benchCase.Get("Value")!);
            if (benchCase.Has("Old"))
            {
                benchCase.TypeOf(benchCase.Get("Old")!);
                // The previous version's data has a message and type info of its own.
                foreach (string own in new[] { "Proto", "Json" })
                    if (benchCase.Has(own) && !benchCase.Has("Old" + own))
                        throw new InvalidOperationException($"{_name}: case {benchCase.Name} reads Old data and needs Old{own}");
            }

            var libraries = benchCase.Get("Libraries")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var without = benchCase.Get("Without")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            var rows = new List<Row>();
            foreach (var adapter in adapters.OrderBy(a => a.Order))
            {
                if (libraries != null && !libraries.Contains(adapter.Name)) continue;
                if (without.Contains(adapter.Name)) continue;
                if (adapter.When != null && benchCase.Get(adapter.When) is null or "false") continue;
                if (adapter.Unless != null && benchCase.Get(adapter.Unless) is not (null or "false")) continue;
                if (_toArray && !adapter.Methods.ContainsKey("SerializeToArray")) continue;
                var args = Bind(adapter, benchCase);
                var oldArgs = benchCase.Has("Old") ? Bind(adapter, benchCase.Old()) : null;
                if (args != null && (oldArgs != null || !benchCase.Has("Old")))
                    rows.Add(new Row(adapter, args, oldArgs));
            }

            _cases.Add((benchCase, rows));
        }

        _buffer = classProperties["Buffer"];
        _adapters = adapters.Where(a => _cases.Any(c => c.Rows.Any(r => r.Adapter == a))).OrderBy(a => a.Order).ToList();
        _usings = parts.SelectMany(p => p.Unit.Usings).Select(u => u.ToString())
            .Concat(_adapters.SelectMany(a => a.Usings)).Distinct()
            .OrderBy(u => u.Contains(" = ") ? 2 : Namespace(u) == "System" || Namespace(u).StartsWith("System.") ? 0 : 1)
            .ThenBy(Namespace, StringComparer.Ordinal).ToList();
    }

    /// <summary>The case's arguments ([BenchArg] with its properties put in), or null when it lacks one.</summary>
    private static Dictionary<string, string>? Bind(Adapter adapter, BenchCase c)
    {
        var map = new Dictionary<string, string>();
        try
        {
            foreach (var (name, format) in adapter.Args)
                map[name] = c.Expand(format, adapter);
        }
        catch (MissingProperty)
        {
            return null;
        }

        return map;
    }

    /// <summary>What a using directive names: System.Buffers, or P = Nino.Benchmark.Proto.</summary>
    private static string Namespace(string directive) => directive["using ".Length..].TrimEnd(';');

    private static bool InPlace(BenchCase c, Adapter a) => c.Get("InPlace") == "true" && a.Methods.ContainsKey("DeserializeInto");
    private static string Target(BenchCase c, Adapter a) => a.Field(c.Name);

    /// <summary>The library whose bytes a library reads: its own, or the one it <c>Reads</c>.</summary>
    private Adapter Source(Adapter a) => a.Reads == null ? a : _adapters.Single(s => s.Name == a.Reads);

    private string Bytes(BenchCase c, Adapter a) => c.Get("Bytes", Source(a))!;
    private string ReadBytes(BenchCase c, Adapter a) => c.Has("Old") ? c.Get("OldBytes", Source(a))! : Bytes(c, a);

    /// <summary>The field a library writes: its own value of the case (&lt;Library&gt;Value), or the case's.</summary>
    private static string Field(BenchCase c, Adapter a) => c.Get(a.Name + "Value") ?? c.Get("Value")!;

    /// <summary>The value a library writes: its field, or the field its Prepare makes.</summary>
    private static string Value(BenchCase c, Adapter a) => a.Prepared != null ? c.Name + a.Prepared : Field(c, a);

    /// <summary>The field a library's Prepare reads: its own, or that of the library it prepares from.</summary>
    private string PrepareSource(BenchCase c, Adapter a) =>
        a.PrepareFrom == null ? Field(c, a) : Field(c, _adapters.Single(s => s.Name == a.PrepareFrom));

    /// <summary>
    /// Whether the class holds the adapter's state (Setup's buffers): not for an adapter <c>Like</c> another, which
    /// writes to that one's, nor for one whose serialize methods all write to a new array without it.
    /// </summary>
    private bool Owns(Adapter a) => (a.Like == null || !_adapters.Any(o => o.Like == null && o.Name == a.Like)) &&
        (!_toArray || a.Methods["SerializeToArray"].ParameterList.Parameters.Count > 1);

    private IEnumerable<Row> Owning(List<Row> rows) => rows.Where(r => r.Adapter.Reads == null);

    public string Write()
    {
        var sb = new StringBuilder();
        foreach (string u in _usings) sb.Append(u).Append('\n');
        sb.Append('\n').Append($"namespace {_namespace};\n\n");
        sb.Append($"partial class {_name}\n{{\n");

        foreach (var adapter in _adapters.Where(Owns))
        foreach (var (_, type, field) in adapter.State())
            sb.Append($"    private {type} {field};\n");
        foreach (var (c, rows) in _cases)
        foreach (var row in rows.Where(r => InPlace(c, r.Adapter)))
            sb.Append($"    private {c.TypeOf(Field(c, row.Adapter))} {Target(c, row.Adapter)};\n");
        sb.Append('\n');

        if (_arrays)
        {
            var slots = string.Join(", ", _adapters.Where(a => a.Reads == null).OrderBy(a => a.Slot).Select(a => $"{a.Name} {a.Slot}"));
            sb.Append($"    // Serialized<Case>[slot]: the bytes each library reads ({slots}).\n");
        }

        foreach (var (c, rows) in _cases)
        {
            if (rows.Count == 0) continue;
            if (_arrays)
                sb.Append($"    private static readonly byte[][] {Bytes(c, rows[0].Adapter).Split('[')[0]};\n");
            foreach (var row in rows.Where(r => r.Adapter.Prepared != null && !_fields.Contains(Value(c, r.Adapter))))
                sb.Append($"    private static readonly {Template.Type(row.Adapter.Methods["Prepare"], Map(row, c))} {Value(c, row.Adapter)};\n");
            if (_arrays) continue;
            foreach (var row in Owning(rows).Where(r => !_fields.Contains(Bytes(c, r.Adapter))))
                sb.Append($"    private static readonly byte[] {Bytes(c, row.Adapter)};\n");
            if (c.Has("Old"))
                foreach (var row in Owning(rows))
                    sb.Append($"    private static readonly byte[] {c.Get("OldBytes", row.Adapter)};\n");
        }

        sb.Append($"\n    static {_name}()\n    {{\n");
        if (_methods.Contains("OnStaticInit"))
            sb.Append("        OnStaticInit();\n");
        foreach (var (c, rows) in _cases)
        {
            if (rows.Count == 0) continue;
            if (_arrays)
                sb.Append($"        {Bytes(c, rows[0].Adapter).Split('[')[0]} = new byte[{Owning(rows).Max(r => r.Adapter.Slot) + 1}][];\n");
            foreach (var row in rows.Where(r => r.Adapter.Prepared != null && !_fields.Contains(Value(c, r.Adapter))))
                sb.Append($"        {Value(c, row.Adapter)} = {Template.Expression(row.Adapter.Methods["Prepare"], Map(row, c, value: PrepareSource(c, row.Adapter)))};\n");
            foreach (var row in (_arrays ? Owning(rows).OrderBy(r => r.Adapter.Slot) : Owning(rows))
                         .Where(r => !_fields.Contains(Bytes(c, r.Adapter))))
                sb.Append($"        {Bytes(c, row.Adapter)} = {Template.Expression(row.Adapter.Methods["ToBytes"], Map(row, c, value: Value(c, row.Adapter)))};\n");
            if (c.Has("Old"))
            {
                var old = c.Old();
                foreach (var row in Owning(rows))
                {
                    // The previous version's value, prepared as the current one is (a message parsed from it).
                    string value = old.Get("Old")!;
                    if (row.Adapter.Prepared != null)
                        value = Template.Expression(row.Adapter.Methods["Prepare"], Map(row, old, value: value, old: true));
                    sb.Append($"        {old.Get("OldBytes", row.Adapter)} = {Template.Expression(row.Adapter.Methods["ToBytes"], Map(row, old, value: value, old: true))};\n");
                }
            }

            sb.Append('\n');
        }

        foreach (var adapter in _adapters)
        {
            foreach (var (c, _) in _cases.Where(c => c.Rows.Any(r => r.Adapter == adapter)))
                sb.Append($"        BenchmarkPayloadRegistry.Register(nameof({adapter.Name}Serialize{c.Name}), {Bytes(c, adapter)}.Length);\n");
            if (adapter != _adapters[^1]) sb.Append('\n');
        }

        sb.Append("    }\n\n");
        sb.Append("    [GlobalSetup]\n    public void Setup()\n    {\n        OnSetup();\n");
        foreach (var adapter in _adapters.Where(Owns))
            sb.Append(Template.Statements(adapter.Methods["Setup"], State(adapter)));
        foreach (var (c, rows) in _cases)
        foreach (var row in rows.Where(r => InPlace(c, r.Adapter)))
            sb.Append($"        {Target(c, row.Adapter)} = {Template.Expression(row.Adapter.Methods["Deserialize"], Map(row, c, bytes: ReadBytes(c, row.Adapter)))};\n");
        sb.Append("    }\n\n");
        sb.Append("    /// <summary>The class's own setup, before the buffers'.</summary>\n");
        sb.Append("    partial void OnSetup();\n");
        if (_methods.Contains("OnStaticInit"))
        {
            sb.Append("\n    /// <summary>The class's own values, made before the bytes.</summary>\n");
            sb.Append("    static partial void OnStaticInit();\n");
        }

        var methods = _byCase
            ? _cases.SelectMany(c => new[] { "Serialize", "Deserialize" }.Select(verb => (verb, c)))
            : new[] { "Serialize", "Deserialize" }.SelectMany(verb => _cases.Select(c => (verb, c)));
        foreach (var (verb, (c, rows)) in methods)
        foreach (var row in rows)
        {
            var adapter = row.Adapter;
            string name = $"{adapter.Name}{verb}{c.Name}";
            if (_methods.Contains(name)) continue; // the class writes it
            MethodDeclarationSyntax method;
            Dictionary<string, string> bound;
            if (verb == "Serialize")
                (method, bound) = (adapter.Methods[_toArray ? "SerializeToArray" : verb], Map(row, c, value: Value(c, adapter)));
            else if (InPlace(c, adapter))
                (method, bound) = (adapter.Methods["DeserializeInto"], Map(row, c, bytes: ReadBytes(c, adapter), target: Target(c, adapter)));
            else
                (method, bound) = (adapter.Methods[verb], Map(row, c, bytes: ReadBytes(c, adapter)));
            string returns = verb == "Deserialize" && c.Get("Returns") is { } r ? r : Template.Type(method, bound);
            sb.Append('\n');
            sb.Append($"    [Benchmark{(adapter.Baseline ? "(Baseline = true)" : "")}, BenchmarkCategory(\"{c.Name}{verb}\")]\n");
            if (verb == "Deserialize" && adapter.Baseline && c.Get("ReadsNull") == "true")
                sb.Append("    [ReadsNull]\n");
            sb.Append($"    public {returns} {name}()\n    {{\n");
            sb.Append(Template.Statements(method, bound));
            sb.Append("    }\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    private Dictionary<string, string> State(Adapter adapter)
    {
        var map = adapter.State().ToDictionary(s => s.Parameter, s => s.Field);
        map["capacity"] = _buffer;
        return map;
    }

    private Dictionary<string, string> Map(Row row, BenchCase c, string? value = null, string? bytes = null,
        string? target = null, bool old = false)
    {
        var map = new Dictionary<string, string>(old ? row.OldArgs! : row.Args)
        {
            ["T"] = c.TypeOf(Field(c, row.Adapter)), ["value"] = value ?? Field(c, row.Adapter),
        };
        if (old) map["T"] = c.TypeOf(c.Get("Value")!);
        if (bytes != null) map["bytes"] = bytes;
        if (target != null) map["target"] = target;
        foreach (var (parameter, field) in State(row.Adapter)) map[parameter] = field;
        return map;
    }
}

/// <summary>An adapter method's text with the case's names put in for its type parameter and parameters.</summary>
internal static class Template
{
    public static string Type(MethodDeclarationSyntax m, Dictionary<string, string> map) =>
        new Substitute(map).Visit(m.ReturnType).ToString();

    /// <summary>The expression of an expression-bodied method.</summary>
    public static string Expression(MethodDeclarationSyntax m, Dictionary<string, string> map) =>
        m.ExpressionBody is { } body
            ? new Substitute(map).Visit(body.Expression).ToString()
            : throw new InvalidOperationException($"{m.Identifier}: an expression body is needed here");

    /// <summary>The body as statements at the indentation of a method's (eight spaces), each ending its line.</summary>
    public static string Statements(MethodDeclarationSyntax m, Dictionary<string, string> map)
    {
        if (m.ExpressionBody is { } body)
        {
            string e = new Substitute(map).Visit(body.Expression).ToString();
            bool returns = m.ReturnType is not PredefinedTypeSyntax { Keyword.ValueText: "void" };
            return $"        {(returns ? "return " : "")}{e};\n";
        }

        var block = (BlockSyntax)new Substitute(map).Visit(m.Body!);
        return string.Concat(block.Statements.Select(s => s.ToFullString()));
    }

    private sealed class Substitute(Dictionary<string, string> map) : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            // A member's name (x.value) is not the parameter.
            if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node ||
                node.Parent is QualifiedNameSyntax qualified && qualified.Right == node)
                return node;
            return map.TryGetValue(node.Identifier.ValueText, out string? text)
                ? node.WithIdentifier(SyntaxFactory.Identifier(node.Identifier.LeadingTrivia, text,
                    node.Identifier.TrailingTrivia))
                : node;
        }
    }
}
