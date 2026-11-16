// Writes the timed methods of the .NET benchmarks (runtimes/csharp/benchmarks/Nino.Benchmark) from their cases and the
// libraries' adapters:
//     dotnet run --project runtimes/csharp/tools/BenchGen [-- --check] [-- <.NET root>]
// For each file that declares a class with [BenchCase] attributes it writes <file>.Cases.g.cs beside it: the buffers
// (each adapter's Setup), the bytes every case reads (ToBytes, and Prepare for a value made outside the timed region),
// their payload sizes, the [GlobalSetup], and per case and library one serialize and one deserialize method, each the
// body of the adapter's Serialize or Deserialize with the case's names put in. The methods keep the names every script
// and the docs site read (<Library>Serialize<Case>, <Library>Deserialize<Case>), and their IL is what the same method
// written by hand compiles to: an adapter's generic call in a benchmark would take Nino's slower generic path, so the
// adapters are templates (Adapters/, not compiled) and this tool expands them as text, reading syntax only.
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
        var sources = Directory.GetFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".g.cs", StringComparison.Ordinal) && !Ignored(project, f))
            .Order(StringComparer.Ordinal).ToList();

        var expected = new Dictionary<string, string>();
        foreach (string source in sources)
        {
            string? text = Generate(source, adapters);
            if (text != null) expected[source[..^3] + Suffix] = text;
        }

        int stale = 0;
        foreach (string file in Directory.GetFiles(project, "*" + Suffix, SearchOption.AllDirectories)
                     .Where(f => !Ignored(project, f) && !expected.ContainsKey(f)))
        {
            stale++;
            Console.WriteLine($"{Path.GetRelativePath(root, file)}: {(check ? "no cases declare it" : "deleted")}");
            if (!check) File.Delete(file);
        }

        foreach (var (path, content) in expected)
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

    /// <summary>The generated file of one source file, or null when it declares no cases.</summary>
    private static string? Generate(string source, List<Adapter> adapters)
    {
        string text = File.ReadAllText(source);
        var parts = new List<string>();
        string? className = null;
        foreach (var (build, symbol) in Builds)
        {
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)
                .WithPreprocessorSymbols(symbol.Length > 0 ? new[] { symbol } : Array.Empty<string>());
            var unit = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(text, options, source).GetRoot();
            var classes = unit.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => Attributes(c.AttributeLists, "BenchCase").Any()).ToList();
            if (classes.Count == 0)
            {
                parts.Add("");
                continue;
            }

            if (classes.Count > 1)
                throw new InvalidOperationException($"{source}: one class with [BenchCase] per file");
            var bench = new BenchClass(classes[0], unit, adapters.Where(a => a.Builds.Contains(build)).ToList());
            className = bench.Name;
            parts.Add(bench.Write());
        }

        if (className == null) return null;
        var sb = new StringBuilder();
        sb.Append("// <auto-generated>\n");
        sb.Append($"// Generated by runtimes/csharp/tools/BenchGen from {Path.GetFileName(source)}'s [BenchCase] attributes and the adapters in\n");
        sb.Append("// Adapters/. Do not edit by hand: change those and run\n");
        sb.Append("//     dotnet run --project runtimes/csharp/tools/BenchGen\n");
        sb.Append("// ci.yml fails while this file is out of date (BenchGen --check).\n");
        sb.Append("// </auto-generated>\n");
        sb.Append("#nullable disable\n");
        sb.Append("#if !NINO_BENCH_V3\n");
        sb.Append(parts[0]);
        sb.Append("#else\n");
        sb.Append(parts[1]);
        sb.Append("#endif\n");
        return sb.ToString();
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

/// <summary>A library's adapter: a class of Adapters/ with [BenchLibrary] whose methods are the templates.</summary>
internal sealed class Adapter
{
    public string Name = "";
    public int Order, Slot;
    public bool Baseline;
    public string[] Builds = [];
    public string? Prepared;
    public readonly Dictionary<string, string> Args = new();
    public readonly Dictionary<string, MethodDeclarationSyntax> Methods = new();
    public List<string> Usings = [];

    public static IEnumerable<Adapter> Read(string file)
    {
        var unit = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(File.ReadAllText(file),
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), file).GetRoot();
        foreach (var c in unit.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var library = Program.Attributes(c.AttributeLists, "BenchLibrary").FirstOrDefault();
            if (library == null) continue;
            var adapter = new Adapter { Usings = unit.Usings.Select(u => u.ToString()).ToList() };
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
                    case var other: throw new InvalidOperationException($"{file}: unknown BenchLibrary argument {other}");
                }
            }

            foreach (var arg in Program.Attributes(c.AttributeLists, "BenchArg"))
                adapter.Args[Program.Value(arg.ArgumentList!.Arguments[0].Expression)] =
                    Program.Value(arg.ArgumentList.Arguments[1].Expression);
            foreach (var m in c.Members.OfType<MethodDeclarationSyntax>())
                adapter.Methods[m.Identifier.ValueText] = m;
            foreach (string required in new[] { "Setup", "ToBytes", "Serialize", "Deserialize" })
                if (!adapter.Methods.ContainsKey(required))
                    throw new InvalidOperationException($"{file}: {adapter.Name} has no {required}");
            if (adapter.Prepared != null != adapter.Methods.ContainsKey("Prepare"))
                throw new InvalidOperationException($"{file}: Prepared and Prepare go together");
            yield return adapter;
        }
    }

    /// <summary>The adapter's instance fields: one per out parameter of Setup, <c>_&lt;library&gt;&lt;Parameter&gt;</c>.</summary>
    public IEnumerable<(string Parameter, string Type, string Field)> State() =>
        Methods["Setup"].ParameterList.Parameters.Select(p =>
        {
            string name = p.Identifier.ValueText;
            return (name, p.Type!.ToString(),
                "_" + char.ToLowerInvariant(Name[0]) + Name[1..] + char.ToUpperInvariant(name[0]) + name[1..]);
        });

    /// <summary>The case's arguments ([BenchArg] with its properties put in), or null when it lacks one.</summary>
    public Dictionary<string, string>? Bind(BenchCase c)
    {
        var map = new Dictionary<string, string>();
        foreach (var (name, format) in Args)
        {
            string value = format;
            foreach (var (property, text) in c.Properties)
                value = value.Replace("{" + property + "}", text);
            if (value.Contains('{')) return null;
            map[name] = value;
        }

        return map;
    }
}

internal sealed record BenchCase(string Name, string Type, Dictionary<string, string> Properties);

/// <summary>One build's view of a class that declares cases, and its generated part.</summary>
internal sealed class BenchClass
{
    public readonly string Name;
    private readonly string _namespace;
    private readonly List<string> _usings;
    private readonly List<(BenchCase Case, List<(Adapter Adapter, Dictionary<string, string> Map)> Rows)> _cases = [];
    private readonly List<Adapter> _adapters;

    public BenchClass(ClassDeclarationSyntax c, CompilationUnitSyntax unit, List<Adapter> adapters)
    {
        Name = c.Identifier.ValueText;
        _namespace = c.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().First().Name.ToString();
        var fields = unit.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(k => k.Identifier.ValueText == Name)
            .SelectMany(k => k.Members.OfType<FieldDeclarationSyntax>())
            .SelectMany(f => f.Declaration.Variables.Select(v => (v.Identifier.ValueText, f.Declaration.Type.ToString())))
            .ToDictionary(v => v.Item1, v => v.Item2);
        foreach (var attribute in Program.Attributes(c.AttributeLists, "BenchCase"))
        {
            var arguments = attribute.ArgumentList!.Arguments;
            string name = Program.Value(arguments[0].Expression);
            if (!fields.TryGetValue(name, out string? type))
                throw new InvalidOperationException($"{Name}: [BenchCase(\"{name}\")] names no field of the class");
            var properties = arguments.Skip(1).ToDictionary(a => a.NameEquals!.Name.Identifier.ValueText,
                a => Program.Value(a.Expression));
            var benchCase = new BenchCase(name, type, properties);
            var rows = new List<(Adapter, Dictionary<string, string>)>();
            foreach (var adapter in adapters.OrderBy(a => a.Order))
                if (adapter.Bind(benchCase) is { } map)
                    rows.Add((adapter, map));
            _cases.Add((benchCase, rows));
        }

        _adapters = adapters.Where(a => _cases.Any(c => c.Rows.Any(r => r.Adapter == a))).OrderBy(a => a.Order).ToList();
        _usings = unit.Usings.Select(u => u.ToString()).Concat(_adapters.SelectMany(a => a.Usings)).Distinct()
            .OrderBy(u => u.Contains(" = ") ? 2 : Namespace(u) == "System" || Namespace(u).StartsWith("System.") ? 0 : 1)
            .ThenBy(Namespace, StringComparer.Ordinal).ToList();
    }

    /// <summary>What a using directive names: System.Buffers, or P = Nino.Benchmark.Proto.</summary>
    private static string Namespace(string directive) => directive["using ".Length..].TrimEnd(';');

    public string Write()
    {
        var sb = new StringBuilder();
        foreach (string u in _usings) sb.Append(u).Append('\n');
        sb.Append('\n').Append($"namespace {_namespace};\n\n");
        sb.Append($"partial class {Name}\n{{\n");

        foreach (var adapter in _adapters)
        foreach (var (_, type, field) in adapter.State())
            sb.Append($"    private {type} {field};\n");
        sb.Append('\n');

        var slots = string.Join(", ", _adapters.OrderBy(a => a.Slot).Select(a => $"{a.Name} {a.Slot}"));
        sb.Append($"    // Serialized<Case>[slot]: the bytes each library reads ({slots}).\n");
        foreach (var (c, rows) in _cases)
        {
            sb.Append($"    private static readonly byte[][] Serialized{c.Name};\n");
            foreach (var (adapter, map) in rows.Where(r => r.Adapter.Prepared != null))
                sb.Append($"    private static readonly {Template.Type(adapter.Methods["Prepare"], Map(adapter, c, map))} {c.Name}{adapter.Prepared};\n");
        }

        sb.Append($"\n    static {Name}()\n    {{\n");
        foreach (var (c, rows) in _cases)
        {
            sb.Append($"        Serialized{c.Name} = new byte[{rows.Max(r => r.Adapter.Slot) + 1}][];\n");
            foreach (var (adapter, map) in rows.Where(r => r.Adapter.Prepared != null))
                sb.Append($"        {c.Name}{adapter.Prepared} = {Template.Expression(adapter.Methods["Prepare"], Map(adapter, c, map))};\n");
            foreach (var (adapter, map) in rows.OrderBy(r => r.Adapter.Slot))
                sb.Append($"        Serialized{c.Name}[{adapter.Slot}] = {Template.Expression(adapter.Methods["ToBytes"], Map(adapter, c, map, value: Value(adapter, c)))};\n");
            sb.Append('\n');
        }

        foreach (var adapter in _adapters)
        {
            foreach (var (c, rows) in _cases.Where(c => c.Rows.Any(r => r.Adapter == adapter)))
                sb.Append($"        BenchmarkPayloadRegistry.Register(nameof({adapter.Name}Serialize{c.Name}), Serialized{c.Name}[{adapter.Slot}].Length);\n");
            if (adapter != _adapters[^1]) sb.Append('\n');
        }

        sb.Append("    }\n\n");
        sb.Append("    [GlobalSetup]\n    public void Setup()\n    {\n        OnSetup();\n");
        foreach (var adapter in _adapters)
            sb.Append(Template.Statements(adapter.Methods["Setup"], State(adapter)));
        sb.Append("    }\n\n");
        sb.Append("    /// <summary>The class's own setup, before the buffers'.</summary>\n");
        sb.Append("    partial void OnSetup();\n");

        foreach (string verb in new[] { "Serialize", "Deserialize" })
        foreach (var (c, rows) in _cases)
        foreach (var (adapter, map) in rows)
        {
            var method = adapter.Methods[verb];
            var bound = verb == "Serialize"
                ? Map(adapter, c, map, value: Value(adapter, c))
                : Map(adapter, c, map, bytes: $"Serialized{c.Name}[{adapter.Slot}]");
            sb.Append('\n');
            sb.Append($"    [Benchmark{(adapter.Baseline ? "(Baseline = true)" : "")}, BenchmarkCategory(\"{c.Name}{verb}\")]\n");
            sb.Append($"    public {Template.Type(method, bound)} {adapter.Name}{verb}{c.Name}()\n    {{\n");
            sb.Append(Template.Statements(method, bound));
            sb.Append("    }\n");
        }

        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>The value a library writes: the case's field, or the field its Prepare made.</summary>
    private static string Value(Adapter adapter, BenchCase c) => adapter.Prepared != null ? c.Name + adapter.Prepared : c.Name;

    private static Dictionary<string, string> State(Adapter adapter) =>
        adapter.State().ToDictionary(s => s.Parameter, s => s.Field);

    private static Dictionary<string, string> Map(Adapter adapter, BenchCase c, Dictionary<string, string> args,
        string? value = null, string? bytes = null)
    {
        var map = new Dictionary<string, string>(args) { ["T"] = c.Type, ["value"] = value ?? c.Name };
        if (bytes != null) map["bytes"] = bytes;
        foreach (var (parameter, field) in State(adapter)) map[parameter] = field;
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
