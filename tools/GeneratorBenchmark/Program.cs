using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

// Measures the Nino source generator on a synthetic workload: generator time, size of the generated code,
// time to compile (emit) it, and re-generation after edits.
//
// Usage: GeneratorBenchmark [roots=30] [depth=8] [--generator <Nino.Generator.dll>] [--core <Nino.Core.dll>]
//                           [--dump <dir>]
// Without --generator/--core it uses the generator and runtime of this repository. To measure another
// version (e.g. v3), build its Nino.Generator and Nino.Core and pass their paths.

int roots = 30, depth = 8;
string generatorPath = null, corePath = null, dump = null;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--generator": generatorPath = args[++i]; break;
        case "--core": corePath = args[++i]; break;
        case "--dump": dump = args[++i]; break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count > 0) roots = int.Parse(positional[0]);
if (positional.Count > 1) depth = int.Parse(positional[1]);

// N roots, each with a chain of `depth` subclasses, a sealed leaf and a struct.
var src = new StringBuilder("using System; using System.Collections.Generic; using Nino.Core;\nnamespace Syn {\n");
for (int r = 0; r < roots; r++)
{
    src.AppendLine($"[NinoType] public partial struct S{r} {{ public int A; public float B; public long C; }}");
    src.AppendLine($"[NinoType] public abstract partial class R{r} {{ public int Id; public string Name; public List<int> L; }}");
    string parent = $"R{r}";
    for (int d = 0; d < depth; d++)
    {
        var name = $"R{r}_{d}";
        src.AppendLine($"[NinoType] public partial class {name} : {parent} {{ public int F{d}; public string P{d} {{ get; set; }} public List<S{r}> Ls{d}; public Dictionary<int,string> M{d} {{ get; set; }} public R{r} Child{d}; private int _priv{d}; }}");
        parent = name;
    }

    src.AppendLine($"[NinoType] public sealed partial class Leaf{r} : {parent} {{ public int[] Arr; public List<R{r}> Kids {{ get; set; }} }}");
}

src.AppendLine("}");

var generators = generatorPath == null
    ? new[] { new Nino.Generator.Generation.NinoGenerator().AsSourceGenerator() }
    : LoadGenerators(generatorPath);
corePath ??= typeof(Nino.Core.NinoTypeAttribute).Assembly.Location;

var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);
var refs = tpa.Where(p => !Path.GetFileName(p).StartsWith("Nino.", StringComparison.Ordinal))
    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
refs.Add(MetadataReference.CreateFromFile(corePath));

var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
var tree = CSharpSyntaxTree.ParseText(src.ToString(), parseOptions);
var compilation = CSharpCompilation.Create("Syn.Test", new[] { tree }, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
if (dump != null)
{
    Directory.CreateDirectory(dump);
    File.WriteAllText(Path.Combine(dump, "User.cs"), src.ToString());
}

Console.WriteLine($"types: {roots * (depth + 3)} ({roots} roots x depth {depth})");
for (int iteration = 0; iteration < 3; iteration++)
{
    GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions);
    var sw = Stopwatch.StartNew();
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
    long generate = sw.ElapsedMilliseconds;

    long lines = 0, chars = 0;
    foreach (var result in driver.GetRunResult().Results)
    {
        foreach (var source in result.GeneratedSources)
        {
            var text = source.SourceText.ToString();
            chars += text.Length;
            lines += text.Count(c => c == '\n');
            if (dump != null && iteration == 0)
                File.WriteAllText(Path.Combine(dump, source.HintName), text);
        }
    }

    sw.Restart();
    using var il = new MemoryStream();
    var emit = output.Emit(il);
    long emitMs = sw.ElapsedMilliseconds;
    int errors = emit.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    // Edits: add a member to one type, then add a comment.
    var text0 = tree.GetText();
    int at = text0.ToString().IndexOf("public int F3;", StringComparison.Ordinal);
    var memberEdit = tree.WithChangedText(text0.WithChanges(new TextChange(new TextSpan(at, 0), "public int Extra; ")));
    var edited = compilation.ReplaceSyntaxTree(tree, memberEdit);
    sw.Restart();
    driver = driver.RunGenerators(edited);
    long memberMs = sw.ElapsedMilliseconds;

    var text1 = memberEdit.GetText();
    var commentEdit = memberEdit.WithChangedText(text1.WithChanges(new TextChange(new TextSpan(text1.Length, 0), "\n// comment\n")));
    sw.Restart();
    driver.RunGenerators(edited.ReplaceSyntaxTree(memberEdit, commentEdit));
    long commentMs = sw.ElapsedMilliseconds;

    Console.WriteLine($"run {iteration}: generate {generate} ms, emit {emitMs} ms, {lines} lines, {chars} chars, " +
                      $"IL {il.Length} bytes, {errors} errors; member edit {memberMs} ms, comment edit {commentMs} ms");
}

static ISourceGenerator[] LoadGenerators(string path)
{
    // Its own load context: the assembly has the same name as the generator this tool references. Roslyn
    // itself falls back to the default context, so the generator interfaces are shared.
    var assembly = new System.Runtime.Loader.AssemblyLoadContext("generator").LoadFromAssemblyPath(Path.GetFullPath(path));
    Type[] types;
    try
    {
        types = assembly.GetTypes();
    }
    catch (System.Reflection.ReflectionTypeLoadException e)
    {
        types = e.Types.Where(t => t != null).ToArray(); // e.g. code fixes referencing Roslyn workspaces
    }

    return types
        .Where(t => !t.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(t) &&
                    t.GetCustomAttributes(typeof(GeneratorAttribute), false).Length != 0)
        .Select(t => ((IIncrementalGenerator)Activator.CreateInstance(t)).AsSourceGenerator())
        .ToArray();
}
