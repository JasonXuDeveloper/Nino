using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        StringAssert.Contains(holder, "WriteValue<global::Inc.Inner>");

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
}
