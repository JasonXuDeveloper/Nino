using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>Models reused across edits must be rebuilt when anything they depend on changes.</summary>
[TestClass]
public class IncrementalGeneratorTests
{
    private const string Usings = "using Nino.Core;\nnamespace Inc {\n";

    private static CSharpCompilation Create(params (string Path, string Text)[] files) =>
        CreateNamed("Inc" + Guid.NewGuid().ToString("N"), files);

    // A unique assembly name per test keeps the generator's cache entries apart.
    private static CSharpCompilation CreateNamed(string name, params (string Path, string Text)[] files) =>
        TestCompilation.Create(name, files.Select(f => CSharpSyntaxTree.ParseText(f.Text, TestCompilation.Parse, f.Path)));

    private static CSharpCompilation Edit(CSharpCompilation compilation, string path, string find, string replace)
    {
        var tree = compilation.SyntaxTrees.Single(t => t.FilePath == path);
        var text = tree.GetText();
        int at = text.ToString().IndexOf(find, StringComparison.Ordinal);
        Assert.IsTrue(at >= 0, find);
        var edited = tree.WithChangedText(text.WithChanges(new TextChange(new TextSpan(at, find.Length), replace)));
        return (CSharpCompilation)compilation.ReplaceSyntaxTree(tree, edited);
    }

    private static string Output(GeneratorDriver driver, string type) =>
        driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == $"NinoGen.Inc_{type}Formatter.g.cs").SourceText.ToString();

    [TestMethod]
    public void DependentModelsAreRebuiltOnEdit()
    {
        var compilation = Create(
            ("Base.cs", Usings + "[NinoType] public class Base { public int X; }\n}"),
            ("Derived.cs", Usings + "[NinoType] public class Derived : Base { public int Y; }\n}"),
            ("Inner.cs", Usings + "[NinoType] public struct Inner { public int A; }\n}"),
            ("Holder.cs", Usings + "[NinoType] public class Holder { public Inner I; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        var derived = Output(driver, "Derived");
        var holder = Output(driver, "Holder");
        StringAssert.Contains(holder, "WriteValueIn<global::Inc.Inner>");

        // Unrelated edit: same outputs.
        compilation = Edit(compilation, "Inner.cs", "}\n}", "}\n// comment\n}");
        driver = driver.RunGenerators(compilation);
        Assert.AreEqual(derived, Output(driver, "Derived"));
        Assert.AreEqual(holder, Output(driver, "Holder"));

        // A base member added in another file reaches the derived formatter.
        compilation = Edit(compilation, "Base.cs", "public int X;", "public int X; public int Z;");
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Output(driver, "Derived"), "value.Z");

        // A member type that stops being raw changes how the holder writes it.
        compilation = Edit(compilation, "Inner.cs", "public int A;", "public int A; public string S;");
        driver = driver.RunGenerators(compilation);
        Assert.IsFalse(Output(driver, "Holder").Contains("WriteValue<global::Inc.Inner>"));
    }

    [TestMethod]
    public void GenericTypesTakeTheFormattersOfTheirTypeArguments()
    {
        // A member of type T calls the formatter the closed type names: a direct call on every runtime, where
        // CachedFormatter<T> is a virtual call on IL2CPP, Mono and NativeAOT.
        var compilation = Create(
            ("Box.cs", Usings + "[NinoType] public class Box<T> { public T Value; public System.Collections.Generic.List<T> Items; }\n}"),
            ("Holder.cs", Usings + "[NinoType] public class Holder { public Box<int> Ints; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        string box = Output(driver, "Box_1");
        StringAssert.Contains(box, "where __F_T : struct, global::Nino.Core.INinoFormatter<T>");
        Assert.IsFalse(box.Contains("CachedFormatter"), box);
        StringAssert.Contains(Output(driver, "Holder"),
            "Inc_Box_1Formatter<int, global::Nino.Core.UnmanagedFormatter<int>>");
    }

    [TestMethod]
    public void EditedFormatterRegistrationsReachCachedModels()
    {
        // The registration is in a file no model depends on: models must still see it change.
        var compilation = Create(
            ("Rules.cs", "using Nino.Core;\n[assembly: NinoFormatter(typeof(Inc.MoneyFormatter))]\nnamespace Inc {\n" +
                         "public struct MoneyFormatter : INinoFormatter<Money> {\n" +
                         "public void Serialize(Money value, ref Writer writer) => writer.Write(value.Cents);\n" +
                         "public void Deserialize(ref Money value, ref Reader reader) { value ??= new Money(); reader.Read(out value.Cents); }\n}\n}"),
            ("Money.cs", "namespace Inc { public class Money { public long Cents; } }"),
            ("Holder.cs", Usings + "[NinoType] public class Holder { public Money M; public int X; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Output(driver, "Holder"), "MoneyFormatter");

        compilation = Edit(compilation, "Rules.cs", "[assembly: NinoFormatter(typeof(Inc.MoneyFormatter))]", "");
        driver = driver.RunGenerators(compilation);
        Assert.IsFalse(Output(driver, "Holder").Contains("MoneyFormatter"), Output(driver, "Holder"));

        compilation = Edit(compilation, "Rules.cs", "using Nino.Core;\n",
            "using Nino.Core;\n[assembly: NinoFormatter(typeof(Inc.MoneyFormatter))]\n");
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Output(driver, "Holder"), "MoneyFormatter");
    }

    [TestMethod]
    public void CallsThroughAliasesAndUsingStaticAreRegistered()
    {
        // The receiver is matched by symbol: an alias (here or global, in another file) or using static names it too.
        var compilation = Create(
            ("Global.cs", "global using G = Nino.Core.NinoDeserializer;"),
            ("Calls.cs", @"using S = Nino.Core.NinoSerializer;
using static Nino.Core.NinoDeserializer;
using System.Collections.Generic;
namespace Inc {
public static class Calls
{
    public static byte[] Write(List<int> x) => S.Serialize(x);
    public static List<long> Read(byte[] b) => Deserialize<List<long>>(b);
    public static List<short> ReadGlobal(byte[] b) => G.Deserialize<List<short>>(b);
    public static List<byte> NotNino(byte[] b) => Other.Deserialize<List<byte>>(b);
}
public static class Other { public static T Deserialize<T>(byte[] b) => default; }
}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        var usages = driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Usages.g.cs").SourceText.ToString();
        StringAssert.Contains(usages, "NinoFormatterCache<global::System.Collections.Generic.List<int>>");
        StringAssert.Contains(usages, "NinoFormatterCache<global::System.Collections.Generic.List<long>>");
        StringAssert.Contains(usages, "NinoFormatterCache<global::System.Collections.Generic.List<short>>");
        Assert.IsFalse(usages.Contains("List<byte>"), usages);
    }

    /// <summary>An internal static method of the generator (<c>Nino.Generator.Generation.</c><paramref name="type"/>).</summary>
    private static System.Reflection.MethodInfo Internal(string type, string method) =>
        typeof(Generator.Generation.NinoGenerator).Assembly.GetType("Nino.Generator.Generation." + type, true)!
            .GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                               System.Reflection.BindingFlags.Static)!;

    [TestMethod]
    public void FilesWithOtherSymbolsShareTheirFacts()
    {
        // TypeFacts.For: files whose preprocessor symbols differ from the project's share one set of facts per symbols.
        var compilation = Create(("A.cs", Usings + "[NinoType] public class A { public int X; }\n}"));
        var facts = Internal("TypeFacts", "For");
        var project = facts.Invoke(null, new object[] { compilation, compilation.SyntaxTrees.First().Options });
        object Other(params string[] symbols) => facts.Invoke(null, new object[]
        {
            compilation, new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols),
        });
        var first = Other("OTHER");
        Assert.AreNotSame(project, first);
        Assert.AreSame(first, Other("OTHER"));
        Assert.AreNotSame(first, Other("OTHER", "MORE"));
        Assert.AreSame(project, facts.Invoke(null, new object[] { compilation, new CSharpParseOptions(LanguageVersion.Latest) }));
    }

    [TestMethod]
    public void ClosedProjectsAreNotKept()
    {
        // ModelCache keeps the latest state of each project (its models and the syntax trees they depend on) only
        // while a compilation of the project is alive, or among the few used last.
        string prefix = "IncClosed" + Guid.NewGuid().ToString("N");
        RunProjects(prefix, 50);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // Another project prunes the states collected since.
        RunProjects(prefix + "Last", 1);
        var cache = typeof(Generator.Generation.NinoGenerator).Assembly
            .GetType("Nino.Generator.Generation.ModelCache", true)!;
        object Field(string name) => cache
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var latest = (System.Collections.IDictionary)Field("LatestState");
        int alive = 0;
        lock (Field("Lock"))
        {
            foreach (System.Collections.DictionaryEntry entry in latest)
            {
                var type = entry.Value!.GetType();
                bool weak = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(WeakReference<>);
                if (((string)entry.Key).StartsWith(prefix, StringComparison.Ordinal) && (!weak || IsAlive(entry.Value)))
                    alive++;
            }
        }

        Assert.IsTrue(alive <= 33, $"{alive} states of closed projects are kept");
    }

    private static bool IsAlive(object weak) =>
        (bool)weak.GetType().GetMethod("TryGetTarget")!.Invoke(weak, new object[] { null })!;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunProjects(string prefix, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var compilation = CreateNamed(prefix + i, ("A.cs", Usings + "[NinoType] public class A { public int X; }\n}"));
            CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator()).RunGenerators(compilation);
        }
    }

    [TestMethod]
    public void SubtypesOfAliasedNinoTypesAreGenerated()
    {
        var compilation = Create(
            ("Base.cs", "using NT = Nino.Core.NinoTypeAttribute;\nnamespace Inc {\n[NT] public class AliasedBase { public int X; }\n}"),
            ("Derived.cs", Usings + "public class AliasedDerived : AliasedBase { public int Y; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Output(driver, "AliasedDerived"), "value.Y");
    }

    [TestMethod]
    public void NewPartialDeclarationsAreSeen()
    {
        var compilation = Create(
            ("A.cs", Usings + "[NinoType] public partial class Part { public int X; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        Assert.IsFalse(Output(driver, "Part").Contains("value.Y"));

        var parse = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            Usings + "public partial class Part { public int Y; }\n}", parse, "B.cs"));
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Output(driver, "Part"), "value.Y");
    }

    [TestMethod]
    public void NestedTypesWithPrivateMembersCompileWithoutUnsafeAccessor()
    {
        // No NET8_0_OR_GREATER symbol: private members go through an accessor declared inside the partial chain.
        var compilation = Create(("Nested.cs", Usings + @"
public partial class Outer
{
    internal partial class Middle
    {
        [NinoType(containNonPublicMembers: true)]
        public partial class Inner { private int _secret; public string Name; }

        [NinoType(containNonPublicMembers: true)]
        public partial struct InnerStruct { private long _value; }
    }
}
}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        Assert.AreEqual(0, generatorDiagnostics.Length, string.Join("\n", generatorDiagnostics));
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        StringAssert.Contains(Output(driver, "Outer_Middle_Inner"), "_secret");
    }

    [TestMethod]
    public void InaccessibleNinoConstructorStillCompiles()
    {
        // NINO021: the generator must not call the private constructor; the formatter compiles and throws instead.
        var compilation = Create(("Ctor.cs", Usings + @"
[NinoType]
public class Hidden
{
    public int A;
    public Hidden() { }
    [NinoConstructor] private Hidden(int a) { A = a; }
}
}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        StringAssert.Contains(Output(driver, "Hidden"), "NotSupportedException");
        // The message names the type as users write it, not as the file's alias of it.
        StringAssert.Contains(Output(driver, "Hidden"), "\"Nino: cannot construct Inc.Hidden\"");
    }

    [TestMethod]
    public void TopLevelCallsOnTypesNestedInOpenGenericsCompile()
    {
        // Outer<T>.Items names T through its containing type only: the registry and the interceptors cannot name it.
        var parse = new CSharpParseOptions(LanguageVersion.Latest).WithFeatures(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("InterceptorsNamespaces", "NinoGen"),
        });
        var compilation = Create(("Open.cs", Usings + @"
public class Outer<T>
{
    public class Items : System.Collections.Generic.List<int> { }
    public static byte[] Write(Items items) => NinoSerializer.Serialize(items);
    public static Items Read(byte[] data) => NinoDeserializer.Deserialize<Items>(data);
}
}"));
        compilation = compilation.RemoveAllSyntaxTrees()
            .AddSyntaxTrees(compilation.SyntaxTrees.Select(t => t.WithRootAndOptions(t.GetRoot(), parse)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
            parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
    }

    [TestMethod]
    public void GenericFactoriesAreNotCalled()
    {
        // NINO017: a generic [NinoFactory] cannot be called as T.Create(); the formatter creates `new T()` instead,
        // as the formatters of collections holding it do.
        var compilation = Create(("Factory.cs", Usings + @"
[NinoType]
public class Made
{
    public int A;
    [NinoFactory] public static Made Create<T>() => new Made();
}
public class MadeList : System.Collections.Generic.List<Made> { }
[NinoType] public class Holder { public MadeList Items; }
}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        Assert.IsFalse(Output(driver, "Made").Contains("Inc.Made.Create"));
    }

    [TestMethod]
    public void MovingCodeReusesEveryOutput()
    {
        var compilation = Create(
            ("A.cs", Usings + "[NinoType] public class A { public int X; public System.Action Bad; }\n}"),
            ("B.cs", Usings + "[NinoType(containNonPublicMembers: true)] public partial class B { private int _y; }\n}"),
            ("C.cs", Usings + "public static class C { public static byte[] W(System.Collections.Generic.List<A> a) => NinoSerializer.Serialize(a); }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        var first = driver.GetRunResult().Results[0];

        // Lines inserted above both types move every location; models carry none, so nothing is re-emitted.
        compilation = Edit(compilation, "A.cs", "[NinoType]", "\n\n// moved\n[NinoType]");
        compilation = Edit(compilation, "B.cs", "[NinoType", "\n\n[NinoType");
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult().Results[0];
        Assert.AreEqual(0, result.Diagnostics.Length, "the generator reports nothing; the analyzer does");
        foreach (var output in result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs))
            Assert.AreEqual(IncrementalStepRunReason.Cached, output.Reason);
        // The models, the calls and every step that collects them or reads the whole compilation.
        foreach (var step in new[]
                 {
                     "NinoType", "TopLevelCall", "ProjectFacts", "ClashingNames", "RegistryEntries",
                     "Usages", "FormatterRegistrations", "FactoryRegistrations", "GenericTypeRegistrations", "LayoutTypes",
                     "Dispatch",
                 })
        {
            // Every step runs the first time; a step whose inputs are all cached is not run (nor listed) again.
            Assert.IsTrue(first.TrackedSteps.ContainsKey(step), step + " not in " + string.Join(", ", first.TrackedSteps.Keys));
            if (!result.TrackedSteps.TryGetValue(step, out var runs))
                continue;
            foreach (var output in runs.SelectMany(s => s.Outputs))
                Assert.IsTrue(output.Reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached,
                    $"{step}: {output.Reason}");
        }
    }

    [TestMethod]
    public void CallsFollowEditsToTheirTypes()
    {
        // A top-level call is reused while its file and its type are unchanged (ModelCache.GetOrBuildCall): an edit to
        // the type in another file changes what it registers.
        var compilation = Create(
            ("Box.cs", Usings + "[NinoType] public class Box<T> { public T V; }\n}"),
            ("Calls.cs", Usings + "public static class Calls { public static byte[] W() => NinoSerializer.Serialize<Box<int>>(null); }\n}"),
            ("Other.cs", Usings + "public static class Other { }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        string Usages() => driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Usages.g.cs").SourceText.ToString();
        const string registered = "NinoPolymorphic.Register<global::Inc.Box<int>";
        StringAssert.Contains(Usages(), registered);

        // An unrelated edit: the call is reused, with the same result.
        compilation = Edit(compilation, "Other.cs", "{ }", "{ public static int N; }");
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Usages(), registered);

        // The type becomes abstract: it has no instances to register under its id.
        compilation = Edit(compilation, "Box.cs", "public class Box", "public abstract class Box");
        driver = driver.RunGenerators(compilation);
        Assert.IsFalse(Usages().Contains(registered), Usages());

        // The call now names another type, in the same file.
        compilation = Edit(compilation, "Calls.cs", "Box<int>", "Box<long>");
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Usages(), "global::Inc.Box<long>");
    }

    [TestMethod]
    public void InsertingATypeEmitsOnlyItsFormatter()
    {
        // Roslyn matches the models to the previous run's by position: the types after the inserted one (in its file
        // and, on Roslyn 4.3, in every file) reach the output step again. Their models are equal to before, so their
        // files are the same texts, not emitted again. (Step tracking stays off: Roslyn 4.3 throws recording a step
        // whose cached input gets new outputs, which is this case.)
        var compilation = Create(
            ("A.cs", Usings + "[NinoType] public class A1 { public int X; }\n[NinoType] public class A2 { public string Y; }\n}"),
            ("B.cs", Usings + "[NinoType] public class B1 { public long Z; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        var before = driver.GetRunResult().Results[0].GeneratedSources
            .Where(s => s.HintName.EndsWith("Formatter.g.cs", StringComparison.Ordinal))
            .ToDictionary(s => s.HintName, s => s.SourceText);
        Assert.AreEqual(3, before.Count);

        compilation = Edit(compilation, "A.cs", "[NinoType] public class A1",
            "[NinoType] public class A0 { public int W; }\n[NinoType] public class A1");
        driver = driver.RunGenerators(compilation);
        var after = driver.GetRunResult().Results[0].GeneratedSources.ToDictionary(s => s.HintName, s => s.SourceText);
        foreach (var pair in before)
            Assert.AreSame(pair.Value, after[pair.Key], pair.Key);
        StringAssert.Contains(after["NinoGen.Inc_A0Formatter.g.cs"].ToString(), "Inc.A0");
        StringAssert.Contains(after["NinoGen.Registry.g.cs"].ToString(), "Inc_A0Formatter");
    }

    [TestMethod]
    public void AddingATypeLeavesTheOtherOutputsCached()
    {
        // A type added after the others (in a file of its own, or at the end of one) moves none of them: with the
        // steps tracked, every model but its own is Cached or Unchanged, and so is every formatter file but its own.
        // (A type inserted before others moves them, InsertingATypeEmitsOnlyItsFormatter. In this order: Roslyn 4.3
        // throws recording the steps when the first run after the first one adds a file.)
        var compilation = Create(
            ("A.cs", Usings + "[NinoType] public class A1 { public int X; }\n[NinoType] public class A2 { public string Y; }\n}"),
            ("B.cs", Usings + "[NinoType] public class B1 { public long Z; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        compilation = Edit(compilation, "B.cs", "public long Z; }", "public long Z; }\n[NinoType] public class B2 { public int V; }");
        driver = driver.RunGenerators(compilation);
        AssertOnlyAddedOutputs(driver.GetRunResult().Results[0], "B2");

        var parse = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            Usings + "[NinoType] public class C1 { public int W; }\n}", parse, "C.cs"));
        driver = driver.RunGenerators(compilation);
        AssertOnlyAddedOutputs(driver.GetRunResult().Results[0], "C1");
    }

    private static void AssertOnlyAddedOutputs(GeneratorRunResult result, string added)
    {
        Assert.AreEqual(0, result.Diagnostics.Length, string.Join("\n", result.Diagnostics));
        var models = result.TrackedSteps["NinoType"].SelectMany(s => s.Outputs).ToList();
        Assert.AreEqual(1, models.Count(o => o.Reason is IncrementalStepRunReason.New or IncrementalStepRunReason.Modified),
            string.Join(", ", models.Select(o => o.Reason)));
        Assert.IsTrue(models.Count(o => o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged) >= 2);
        // The formatter file of the added type is new; the registry and the dispatch list it. Nothing else is emitted.
        var emitted = result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs)
            .Where(o => o.Reason != IncrementalStepRunReason.Cached).ToList();
        Assert.IsTrue(emitted.Count <= 3, $"{emitted.Count} outputs emitted: {string.Join(", ", emitted.Select(o => o.Reason))}");
        Assert.IsTrue(result.GeneratedSources.Any(s => s.HintName == $"NinoGen.Inc_{added}Formatter.g.cs"), added);
    }

    [TestMethod]
    public void NamesDifferingOnlyByCaseGetTheirOwnFiles()
    {
        // Hint names are compared ignoring case: two of them that differ only by case fail the whole generator run
        // (CS8785), which then adds no file at all.
        var compilation = Create(
            ("A.cs", Usings + @"
[NinoType] public struct RawS { public int A; }
[NinoType] public sealed class Raws { public string B; }
[NinoType] public class Box<T> { public T V; }
[NinoType] public class box<T> { public T V; }
public class Holder { [NinoType] public class Item { public int I; } }
public class holder { [NinoType] public class item { public int I; } }
[NinoType] public class Plain { public int P; }
}"),
            ("B.cs", Usings + "[NinoType] public class Other { public int O; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult().Results[0];
        Assert.AreEqual(0, result.Diagnostics.Length, string.Join("\n", result.Diagnostics));
        var names = result.GeneratedSources.Select(s => s.HintName).ToList();
        Assert.AreEqual(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), string.Join("\n", names));
        foreach (var formatter in new[]
                 {
                     "Inc_RawSFormatter", "Inc_RawsFormatter", "Inc_Box_1Formatter", "Inc_box_1Formatter",
                     "Inc_Holder_ItemFormatter", "Inc_holder_itemFormatter",
                 })
            Assert.AreEqual(1, names.Count(n => n.StartsWith($"NinoGen.{formatter}.", StringComparison.Ordinal) &&
                                                n.Length > $"NinoGen.{formatter}.g.cs".Length), formatter);
        // Types whose names clash with none keep their plain hint name.
        CollectionAssert.Contains(names, "NinoGen.Inc_PlainFormatter.g.cs");

        // The same names in the next run, and nothing re-emitted for an edit elsewhere.
        compilation = Edit(compilation, "B.cs", "public int O;", "public int O; public int O2;");
        driver = driver.RunGenerators(compilation);
        result = driver.GetRunResult().Results[0];
        CollectionAssert.AreEquivalent(names, result.GeneratedSources.Select(s => s.HintName).ToList());
        var reasons = result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs)
            .Count(o => o.Reason != IncrementalStepRunReason.Cached);
        Assert.IsTrue(reasons <= 4, $"{reasons} outputs re-emitted"); // Other's formatter, registry, usages, dispatch
    }

    [TestMethod]
    public void SerializedPrivateFieldsAreNotReportedAsUnused()
    {
        // .NET 8+: private members are reached through [UnsafeAccessor], which the compiler cannot see.
        var parse = new CSharpParseOptions(LanguageVersion.Latest).WithPreprocessorSymbols("NET8_0_OR_GREATER");
        var compilation = Create(("S.cs", Usings + @"
[NinoType(containNonPublicMembers: true)] public class S { private int _never; private string _assigned = """"; }
public class Plain { private int _unused; }
}"));
        compilation = compilation.RemoveAllSyntaxTrees()
            .AddSyntaxTrees(compilation.SyntaxTrees.Select(t => t.WithRootAndOptions(t.GetRoot(), parse)));
        var analyzers = System.Collections.Immutable.ImmutableArray.Create<Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer>(
            new Generator.Analysis.NinoSuppressor());
        var diagnostics = compilation.WithAnalyzers(analyzers, new Microsoft.CodeAnalysis.Diagnostics.CompilationWithAnalyzersOptions(
                new Microsoft.CodeAnalysis.Diagnostics.AnalyzerOptions(System.Collections.Immutable.ImmutableArray<AdditionalText>.Empty),
                null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true))
            .GetAllDiagnosticsAsync().Result
            .Where(d => d.Id is "CS0169" or "CS0414")
            .ToArray();
        Assert.IsTrue(diagnostics.Where(d => d.GetMessage().Contains("S._")).All(d => d.IsSuppressed),
            string.Join("\n", diagnostics.Select(d => d.ToString())));
        Assert.AreEqual(2, diagnostics.Count(d => d.GetMessage().Contains("S._")));
        Assert.IsFalse(diagnostics.Single(d => d.GetMessage().Contains("Plain._unused")).IsSuppressed);
    }
}
