using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

// Measures the Nino source generator on a synthetic workload: generator time, size of the generated code,
// time to compile (emit) it, analyzer time, and re-generation after edits.
//
// Usage: Nino.GeneratorBenchmark [roots=30] [depth=8] [--generics] [--calls] [--layouts] [--runs <n>=5]
//                           [--warmup <seconds>=60] [--generator <Nino.Generator.dll>] [--core <Nino.Core.dll>]
//                           [--dump <dir>] [--no-analyzers] [--json <file>]
// --generics adds generic [NinoType]s per root: a polymorphic generic base with generic subtypes (one through a
// constructed argument), a type holding itself over a bigger argument and members closing them, which make the
// analyzer decide what each closed generic writes (WrittenParameters: the definitions and their subtypes).
// --calls adds, per root, top-level NinoSerializer/NinoDeserializer calls (bound, registered and intercepted) and as
// many calls of another serializer's static Serialize/Deserialize, as a project using both has, in a file of their
// own: binding calls is what an edit of their file or of a declaration costs the generator in such projects (an
// edit elsewhere that keeps every declaration binds none again).
// --layouts makes the classes version-tolerant and adds a layout baseline ([assembly: NinoLayouts], NinoLayouts.cs)
// holding every type's layout as this repository's generator publishes it, so the analyzer checks each type against
// its section (NINO034-NINO037); without it the check costs one attribute lookup per compilation.
// --runs sets the number of measured runs; the last line is their median. Warm-up runs come first and are not
// measured: at least one, then more until --warmup seconds have passed (0: none, the first run is measured). One
// warm-up run is not enough for a workload of seconds: the in-process compiler is still being tiered up by the JIT
// and its heap still growing, so emitting v4's code at 1,100 types took 4.3, 3.4, 3.3, 3.2, 2.8 and 2.1 s in runs 0-5
// and 1.75-1.85 s from run 6 on (Xeon 2.10 GHz, 4 cores; 1938da63's code: 1.8-2.0 s), and a median
// of runs 1-2 made it look twice as slow. A run of v3 at that size takes minutes and needs only the first.
// "analyzers" is the analyzers' own time as Roslyn reports it, "wall" the whole analysis with the binding it needs.
// --no-analyzers skips the analysis (v3's analyzers bind its generated code: at 330 types as long as emitting it).
// --json writes the medians, the sizes and the machine to a file (report.yml's generator job turns them into the
// release body's build section with benchmarks/reports/generator_report.py); the printed lines stay as they are
// (runtime-ab reads the last one).
// Without --generator/--core it uses the generator and runtime of this repository. To measure another
// version, build its Nino.Generator and Nino.Core and pass their paths. v3 3.3.13's are in the NuGet cache once this
// project is restored (PackageDownload): nino.generator/3.3.13/analyzers/dotnet/cs/Nino.Generator.dll and
// nino.serialization/3.3.13/lib/net8.0/Nino.Core.dll.

int roots = 30, depth = 8;
string generatorPath = null, corePath = null, dump = null, json = null;
bool generics = false, calls = false, layouts = false, noAnalyzers = false;
int runs = 5;
double warmupSeconds = 60;
var positional = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--generator": generatorPath = args[++i]; break;
        case "--core": corePath = args[++i]; break;
        case "--dump": dump = args[++i]; break;
        case "--generics": generics = true; break;
        case "--calls": calls = true; break;
        case "--layouts": layouts = true; break;
        case "--runs": runs = int.Parse(args[++i]); break;
        case "--warmup": warmupSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--no-analyzers": noAnalyzers = true; break;
        case "--json": json = args[++i]; break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count > 0) roots = int.Parse(positional[0]);
if (positional.Count > 1) depth = int.Parse(positional[1]);

// N roots, each with a chain of `depth` subclasses, a sealed leaf and a struct.
const string header = "using System; using System.Collections.Generic; using Nino.Core;\nnamespace Syn {\n";
var src = new StringBuilder(header);
var callSrc = new StringBuilder(header);
string classAttribute = layouts ? "[NinoType(format: NinoFormat.VersionTolerant)]" : "[NinoType]";
for (int r = 0; r < roots; r++)
{
    src.AppendLine($"[NinoType] public partial struct S{r} {{ public int A; public float B; public long C; }}");
    src.AppendLine($"{classAttribute} public abstract partial class R{r} {{ public int Id; public string Name; public List<int> L; }}");
    string parent = $"R{r}";
    for (int d = 0; d < depth; d++)
    {
        var name = $"R{r}_{d}";
        src.AppendLine($"{classAttribute} public partial class {name} : {parent} {{ public int F{d}; public string P{d} {{ get; set; }} public List<S{r}> Ls{d}; public Dictionary<int,string> M{d} {{ get; set; }} public R{r} Child{d}; private int _priv{d}; }}");
        parent = name;
    }

    src.AppendLine($"{classAttribute} public sealed partial class Leaf{r} : {parent} {{ public int[] Arr; public List<R{r}> Kids {{ get; set; }} }}");
    if (calls)
        callSrc.AppendLine($"public static class Use{r} {{ public static byte[] W(Leaf{r} v) => NinoSerializer.Serialize(v); " +
                       $"public static Leaf{r} R(byte[] b) => NinoDeserializer.Deserialize<Leaf{r}>(b); " +
                       $"public static string T(Leaf{r} v) => Json.Serialize(v); public static Leaf{r} U(string s) => Json.Deserialize<Leaf{r}>(s); }}");
    if (!generics)
        continue;
    src.AppendLine($"{classAttribute} public abstract partial class Shelf{r}<T> {{ public int Id; }}");
    src.AppendLine($"{classAttribute} public partial class ListShelf{r}<T> : Shelf{r}<T> {{ public List<T> Items; }}");
    src.AppendLine($"{classAttribute} public partial class Tag{r}<T> {{ public int Id; }}");
    src.AppendLine($"{classAttribute} public partial class TagShelf{r}<U> : Shelf{r}<Tag{r}<U>> {{ public U Value; }}");
    src.AppendLine($"{classAttribute} public partial class Box{r}<T> {{ public T Value; public Box{r}<List<T>> Next; }}");
    src.AppendLine($"{classAttribute} public partial class GHolder{r} {{ public Shelf{r}<int> A; public Shelf{r}<Tag{r}<string>> B; public Box{r}<S{r}> C; public List<Shelf{r}<R{r}>> D; }}");
}

if (calls)
    callSrc.AppendLine("public static class Json { public static string Serialize<T>(T value) => null; public static T Deserialize<T>(string text) => default; }");
src.AppendLine("}");
callSrc.AppendLine("}");

var types = generatorPath == null ? typeof(Nino.Generator.Analysis.NinoAnalyzer).Assembly.GetTypes() : LoadTypes(generatorPath);
var generators = GeneratorsOf(types);
var analyzers = noAnalyzers ? ImmutableArray<DiagnosticAnalyzer>.Empty : types
    // Suppressors only run on compiler diagnostics, which emit already produces: not measured here.
    .Where(t => !t.IsAbstract && typeof(DiagnosticAnalyzer).IsAssignableFrom(t) &&
                !typeof(DiagnosticSuppressor).IsAssignableFrom(t) &&
                t.GetCustomAttributes(typeof(DiagnosticAnalyzerAttribute), false).Length != 0)
    .Select(t => (DiagnosticAnalyzer)Activator.CreateInstance(t))
    .ToImmutableArray();
corePath ??= typeof(Nino.Core.NinoTypeAttribute).Assembly.Location;

var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator);
var refs = tpa.Where(p => !Path.GetFileName(p).StartsWith("Nino.", StringComparison.Ordinal))
    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
refs.Add(MetadataReference.CreateFromFile(corePath));

var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
var tree = CSharpSyntaxTree.ParseText(src.ToString(), parseOptions, "User.cs");
var trees = new List<SyntaxTree> { tree };
if (calls)
    trees.Add(CSharpSyntaxTree.ParseText(callSrc.ToString(), parseOptions, "Calls.cs"));
var compilation = CSharpCompilation.Create("Syn.Test", trees, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
string baseline = null;
if (layouts)
{
    // The baseline of this code as the refactoring writes it: the layouts the generator publishes.
    CSharpGeneratorDriver.Create(GeneratorsOf(typeof(Nino.Generator.Analysis.NinoAnalyzer).Assembly.GetTypes()),
            parseOptions: parseOptions)
        .RunGeneratorsAndUpdateCompilation(compilation, out var published, out _);
    var entries = new List<Nino.Generator.Common.LayoutEntry>();
    foreach (var attribute in published.Assembly.GetAttributes())
    {
        if (attribute.AttributeClass?.Name != "NinoTypeLayoutAttribute" ||
            attribute.ConstructorArguments[0].Value is not INamedTypeSymbol type)
            continue;
        var a = attribute.ConstructorArguments;
        var deferrable = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Deferrable").Value.Value as bool?;
        type = type.OriginalDefinition;
        string key = type.ContainingNamespace.ToDisplayString() + "." + type.Name +
                     (type.TypeParameters.IsEmpty ? "" : "<" + string.Join(",", type.TypeParameters.Select(p => p.Name)) + ">");
        entries.Add(Nino.Generator.Common.LayoutEntry.FromLayout(key, (int)a[1].Value, (int)a[2].Value, (bool)a[3].Value,
            (bool)a[4].Value, (int)a[5].Value, deferrable, (string)a[6].Value));
    }

    baseline = Nino.Generator.Common.LayoutBaseline.File(entries);
    var baselineTree = CSharpSyntaxTree.ParseText(baseline, parseOptions, "NinoLayouts.cs");
    trees.Add(baselineTree);
    compilation = compilation.AddSyntaxTrees(baselineTree);
    Console.WriteLine($"layout baseline: {entries.Count} types, {baseline.Length} chars");
}

if (dump != null)
{
    Directory.CreateDirectory(dump);
    File.WriteAllText(Path.Combine(dump, "User.cs"), src.ToString());
    if (calls)
        File.WriteAllText(Path.Combine(dump, "Calls.cs"), callSrc.ToString());
    if (baseline != null)
        File.WriteAllText(Path.Combine(dump, "NinoLayouts.cs"), baseline);
}

Console.WriteLine($"types: {roots * (depth + 3 + (generics ? 6 : 0))} ({roots} roots x depth {depth}" +
                  (generics ? ", generics)" : ")"));
var generateRuns = new List<double>();
var analyzeRuns = new List<double>();
var wallRuns = new List<double>();
// For --json: the measured runs (after the warm-up), and the sizes, the same in every run.
var measured = new Dictionary<string, List<double>>
{
    ["generate_ms"] = new(), ["emit_ms"] = new(), ["member_edit_ms"] = new(), ["comment_edit_ms"] = new(),
};
var sizes = new Dictionary<string, long>();
var warmupClock = Stopwatch.StartNew();
int warmupRuns = 0;
for (int iteration = 0; iteration < warmupRuns + runs; iteration++)
{
    bool warmup = warmupSeconds > 0 && (iteration == 0 || warmupClock.Elapsed.TotalSeconds < warmupSeconds) &&
                  iteration == warmupRuns;
    if (warmup)
        warmupRuns++;
    GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, parseOptions: parseOptions);
    // A collection before each timed step: else the garbage of the step before it lands in its time (the analyzers
    // took 80 or 330 ms run by run on the same code).
    Collect();
    var sw = Stopwatch.StartNew();
    driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
    double generate = sw.Elapsed.TotalMilliseconds;

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
    double emitTime = sw.Elapsed.TotalMilliseconds;
    int errors = emit.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    // Analyzers on the compilation a build sees. Their own execution time is reported by Roslyn; the rest of
    // the wall time is binding that the build shares with emit. Binding an analyzer triggers first lands in its
    // time, though the compiler would do it anyway: compare the wall time too when a change moves such work.
    var analyzerDiagnostics = ImmutableArray<Diagnostic>.Empty;
    double analyzeMs = 0, wallMs = 0;
    if (!analyzers.IsEmpty)
    {
        Collect();
        sw.Restart();
        var withAnalyzers = output.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(
            new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty), null, concurrentAnalysis: false,
            logAnalyzerExecutionTime: true, reportSuppressedDiagnostics: false));
        analyzerDiagnostics = withAnalyzers.GetAnalyzerDiagnosticsAsync().Result;
        wallMs = sw.Elapsed.TotalMilliseconds;
        foreach (var analyzer in analyzers)
            analyzeMs += withAnalyzers.GetAnalyzerTelemetryInfoAsync(analyzer, default).Result.ExecutionTime.TotalMilliseconds;
    }

    // Edits: add a member to one type, then add a comment.
    var text0 = tree.GetText();
    int at = text0.ToString().IndexOf("public int F3;", StringComparison.Ordinal);
    var memberEdit = tree.WithChangedText(text0.WithChanges(new TextChange(new TextSpan(at, 0), "public int Extra; ")));
    var edited = compilation.ReplaceSyntaxTree(tree, memberEdit);
    // Collected before each edit too: emit and the analysis leave their garbage, and a collection landing in one edit
    // or the other doubled its time (1,100 types: main's (52bcc223) member and comment edits took 0.28-0.34 and
    // 0.56-0.67 s, 1938da63's 0.29-0.72 s either, run by run; collected first, 0.22-0.34 s and 0.25-0.31 s; #262).
    Collect();
    sw.Restart();
    driver = driver.RunGenerators(edited);
    long memberMs = sw.ElapsedMilliseconds;
    double memberTime = sw.Elapsed.TotalMilliseconds;

    var text1 = memberEdit.GetText();
    var commentEdit = memberEdit.WithChangedText(text1.WithChanges(new TextChange(new TextSpan(text1.Length, 0), "\n// comment\n")));
    Collect();
    sw.Restart();
    driver.RunGenerators(edited.ReplaceSyntaxTree(memberEdit, commentEdit));
    long commentMs = sw.ElapsedMilliseconds;
    double commentTime = sw.Elapsed.TotalMilliseconds;

    if (!warmup)
    {
        measured["generate_ms"].Add(generate);
        measured["emit_ms"].Add(emitTime);
        measured["member_edit_ms"].Add(memberTime);
        measured["comment_edit_ms"].Add(commentTime);
    }

    sizes["lines"] = lines;
    sizes["chars"] = chars;
    sizes["il_bytes"] = il.Length;
    sizes["errors"] = errors;
    if (!warmup)
    {
        generateRuns.Add(generate);
        analyzeRuns.Add(analyzeMs);
        wallRuns.Add(wallMs);
    }

    Console.WriteLine($"run {iteration}{(warmup ? " (warm-up)" : "")}: generate {generate:F1} ms, emit {emitMs} ms, " +
                      $"analyzers {analyzeMs:F1} ms " +
                      $"({analyzerDiagnostics.Length} diagnostics, wall {wallMs:F0} ms), {lines} lines, {chars} chars, IL {il.Length} bytes, " +
                      $"{errors} errors; member edit {memberMs} ms, comment edit {commentMs} ms");
}

if (runs > 0)
    Console.WriteLine($"median of runs {warmupRuns}-{warmupRuns + runs - 1}: generate {Median(generateRuns):F2} ms, " +
                      $"analyzers {Median(analyzeRuns):F2} ms, " +
                      $"analysis wall {Median(wallRuns):F2} ms");

if (json != null)
{
    var generatorName = generatorPath == null
        ? typeof(Nino.Generator.Analysis.NinoAnalyzer).Assembly.GetName()
        : System.Reflection.AssemblyName.GetAssemblyName(generatorPath);
    var report = new Dictionary<string, object>
    {
        ["types"] = roots * (depth + 3 + (generics ? 6 : 0)), ["roots"] = roots, ["depth"] = depth,
        ["runs"] = warmupRuns + runs, ["warmup"] = warmupRuns, ["measured"] = measured["emit_ms"].Count,
        ["generator"] = generatorName.Version?.ToString(),
        ["cpu"] = CpuName(), ["cores"] = Environment.ProcessorCount,
        ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        ["roslyn"] = typeof(CSharpCompilation).Assembly.GetName().Version?.ToString(),
        ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    };
    foreach (var (key, values) in measured)
        report[key] = Math.Round(Median(values), 3);
    foreach (var (key, value) in sizes)
        report[key] = value;
    File.WriteAllText(json, System.Text.Json.JsonSerializer.Serialize(report,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
}

// The CPU's model name: /proc/cpuinfo on Linux, else what the OS gives.
static string CpuName()
{
    if (File.Exists("/proc/cpuinfo"))
        foreach (var line in File.ReadLines("/proc/cpuinfo"))
            if (line.StartsWith("model name", StringComparison.Ordinal))
                return line.Substring(line.IndexOf(':') + 1).Trim();
    return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ??
           System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
}

static ISourceGenerator[] GeneratorsOf(Type[] types) => types
    .Where(t => !t.IsAbstract && typeof(IIncrementalGenerator).IsAssignableFrom(t) &&
                t.GetCustomAttributes(typeof(GeneratorAttribute), false).Length != 0)
    .Select(t => ((IIncrementalGenerator)Activator.CreateInstance(t)).AsSourceGenerator())
    .ToArray();

static void Collect()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

static double Median(List<double> values)
{
    var sorted = values.OrderBy(v => v).ToList();
    return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
}

static Type[] LoadTypes(string path)
{
    // Its own load context: the assembly has the same name as the generator this tool references. Roslyn
    // itself falls back to the default context, so the generator interfaces are shared.
    var assembly = new System.Runtime.Loader.AssemblyLoadContext("generator").LoadFromAssemblyPath(Path.GetFullPath(path));
    try
    {
        return assembly.GetTypes();
    }
    catch (System.Reflection.ReflectionTypeLoadException e)
    {
        return e.Types.Where(t => t != null).ToArray(); // e.g. code fixes referencing Roslyn workspaces
    }
}
