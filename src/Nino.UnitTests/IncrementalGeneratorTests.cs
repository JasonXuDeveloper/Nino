using System;
using System.IO;
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

    private static CSharpCompilation Create(params (string Path, string Text)[] files)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(NinoTypeAttribute).Assembly.Location));
        // A unique assembly name per test keeps the generator's cache entries apart.
        return CSharpCompilation.Create("Inc" + Guid.NewGuid().ToString("N"),
            files.Select(f => CSharpSyntaxTree.ParseText(f.Text, parse, f.Path)), refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

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
            ("B.cs", Usings + "[NinoType(containNonPublicMembers: true)] public partial class B { private int _y; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        // Lines inserted above both types move every location; models carry none, so nothing is re-emitted.
        compilation = Edit(compilation, "A.cs", "[NinoType]", "\n\n// moved\n[NinoType]");
        compilation = Edit(compilation, "B.cs", "[NinoType", "\n\n[NinoType");
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult().Results[0];
        Assert.AreEqual(0, result.Diagnostics.Length, "the generator reports nothing; the analyzer does");
        foreach (var output in result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs))
            Assert.AreEqual(IncrementalStepRunReason.Cached, output.Reason);
        foreach (var output in result.TrackedSteps["NinoType"].SelectMany(s => s.Outputs))
            Assert.IsTrue(output.Reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached,
                output.Reason.ToString());
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
