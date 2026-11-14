using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Generator;
using Nino.Generator.Analysis;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The layout baseline in the IDE: NINO034-NINO037 reported where a change is made, the code fixes that accept or undo
/// it, and the refactoring that creates the baseline. Each test writes the baseline of an earlier version of the code
/// with the refactoring (from the layouts the generator publishes), then checks the changed code against it.
/// </summary>
[TestClass]
public class LayoutAnalyzerTests
{
    private const string Before = @"
using Nino.Core;

namespace Game
{
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Save
    {
        public int Health;
        public string Name;
        public Vec Pos;
    }

    [NinoType]
    public struct Vec
    {
        public float X;
        public float Y;
    }

    [NinoType]
    public struct Unrelated
    {
        public int A;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class Settings
    {
        public int Volume;
        public bool Muted;
    }
}";

    /// <summary>The baseline of <paramref name="source"/>, as the refactoring creates it.</summary>
    private static async Task<string> Baseline(string source)
    {
        var solution = await Refactor(source, null, "Create the layout baseline (NinoLayouts.cs)");
        var created = solution.Projects.Single().Documents.Single(d => d.Name == NinoLayoutsRefactoringProvider.FileName);
        return (await created.GetTextAsync()).ToString();
    }

    /// <summary>
    /// Runs the refactoring titled <paramref name="title"/> on the first [NinoType] of <paramref name="source"/>, in a
    /// project with the generator's output (as the IDE's compilation has it) and <paramref name="baseline"/>.
    /// </summary>
    private static async Task<Solution> Refactor(string source, string baseline, string title)
    {
        var compilation = TestCompilation.Create("Layouts", new[] { source });
        var generated = TestCompilation.Generate(compilation).GeneratedTrees;
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Game",
            "Game", LanguageNames.CSharp, compilationOptions: compilation.Options, parseOptions: TestCompilation.Parse,
            metadataReferences: TestCompilation.References()));
        var document = workspace.AddDocument(project.Id, "Source.cs", SourceText.From(source));
        foreach (var tree in generated)
            workspace.AddDocument(project.Id, Path.GetFileName(tree.FilePath), tree.GetText());
        if (baseline != null)
            workspace.AddDocument(project.Id, NinoLayoutsRefactoringProvider.FileName, SourceText.From(baseline));
        document = workspace.CurrentSolution.GetDocument(document.Id)!;
        var type = (await document.GetSyntaxRootAsync())!.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .First(t => t.AttributeLists.Count != 0);
        var actions = new List<CodeAction>();
        await new NinoLayoutsRefactoringProvider().ComputeRefactoringsAsync(
            new CodeRefactoringContext(document, type.Identifier.Span, actions.Add, CancellationToken.None));
        var action = actions.Single();
        Assert.AreEqual(title, action.Title);
        var operations = await action.GetOperationsAsync(CancellationToken.None);
        return operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
    }

    private static CSharpAnalyzerTest<NinoAnalyzer, DefaultVerifier> Test(string source, string baseline,
        params DiagnosticResult[] expected)
    {
        var test = new CSharpAnalyzerTest<NinoAnalyzer, DefaultVerifier>
        {
            TestState = { Sources = { ("Source.cs", source), (NinoLayoutsRefactoringProvider.FileName, baseline) } },
        };
        test.TestState.ExpectedDiagnostics.AddRange(expected);
        TestReferences.Apply(test.TestState);
        return test;
    }

    private static CSharpCodeFixTest<NinoAnalyzer, NinoLayoutFixProvider, DefaultVerifier> Fix(string source,
        string baseline, string fixedSource, string fixedBaseline, string key, DiagnosticResult[] expected,
        params DiagnosticResult[] remaining)
    {
        var test = new CSharpCodeFixTest<NinoAnalyzer, NinoLayoutFixProvider, DefaultVerifier>
        {
            TestState = { Sources = { ("Source.cs", source), (NinoLayoutsRefactoringProvider.FileName, baseline) } },
            FixedState = { Sources = { ("Source.cs", fixedSource), (NinoLayoutsRefactoringProvider.FileName, fixedBaseline) } },
            CodeActionEquivalenceKey = key,
        };
        test.TestState.ExpectedDiagnostics.AddRange(expected);
        test.FixedState.ExpectedDiagnostics.AddRange(remaining);
        TestReferences.Apply(test.TestState);
        TestReferences.Apply(test.FixedState);
        return test;
    }

    /// <summary>
    /// A diagnostic of the baseline, whose additional location (the baseline) is not compared, nor its message unless
    /// the test gives its arguments.
    /// </summary>
    private static DiagnosticResult D(string id) =>
        new DiagnosticResult(id, id == "NINO036" ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning)
            .WithOptions(DiagnosticOptions.IgnoreAdditionalLocations);

    private static string Edit(string source, string from, string to)
    {
        Assert.IsTrue(source.Contains(from), from);
        return source.Replace(from, to);
    }

    // The baseline and the refactoring

    [TestMethod]
    public async Task TheBaselineHoldsWhatStoredDataReaches()
    {
        string baseline = await Baseline(Before);
        StringAssert.StartsWith(baseline, "// The layout baseline of this assembly");
        StringAssert.Contains(baseline, "[assembly: Nino.Core.NinoLayouts(@\"\n['Game.Save']\nformat = 'Keyed'\nheader = 'Presence'\n");
        // The keyed and version-tolerant types, and the positional struct a keyed one holds; not the struct nothing
        // stored holds.
        CollectionAssert.AreEqual(new[] { "['Game.Save']", "['Game.Settings']", "['Game.Vec']" },
            baseline.Split('\n').Where(l => l.StartsWith("['")).ToArray());
        await Test(Before, baseline).RunAsync();
    }

    [TestMethod]
    public async Task NestedGenericAndPolymorphicTypesMatchTheirSections()
    {
        const string source = @"
using System.Collections.Generic;
using Nino.Core;

namespace Game
{
    public class Outer<T>
    {
        [NinoType(format: NinoFormat.VersionTolerant)]
        public sealed class Inner<U>
        {
            public T A;
            public List<U> B;
        }
    }

    [NinoType(format: NinoFormat.Keyed)]
    public sealed record Profile(
        [property: NinoFormerName(""Title"")] [property: NinoFormerName(""Label"")] string Name,
        Outer<int>.Inner<string> Data, Animal Pet);

    [NinoType]
    public abstract class Animal { public string Name; }

    [NinoType]
    public sealed class Dog : Animal { public int Bones; }
}";
        string baseline = await Baseline(source);
        CollectionAssert.AreEqual(new[] { "['Game.Animal']", "['Game.Dog']", "['Game.Outer+Inner<T,U>']", "['Game.Profile']" },
            baseline.Split('\n').Where(l => l.StartsWith("['")).ToArray());
        await Test(source, baseline).RunAsync();
        // A subtype reached through its base is checked too.
        await Test(Edit(source, "public int Bones;", "public long {|#0:Bones|};"), baseline,
            D("NINO034").WithLocation(0)).RunAsync();
    }

    [TestMethod]
    public async Task TheRefactoringWritesTheBaselineAgain()
    {
        string baseline = await Baseline(Before);
        string after = Edit(Before, "public string Name;", "[NinoUtf8] public string Name;");
        var solution = await Refactor(after, baseline, "Write the layout baseline again (accept every change)");
        string rewritten = (await solution.Projects.Single().Documents
            .Single(d => d.Name == NinoLayoutsRefactoringProvider.FileName).GetTextAsync()).ToString();
        Assert.AreEqual(await Baseline(after), rewritten);
        await Test(after, rewritten).RunAsync();
    }

    [TestMethod]
    public async Task NothingWithoutABaseline()
    {
        var test = new CSharpAnalyzerTest<NinoAnalyzer, DefaultVerifier>
        {
            TestState = { Sources = { Edit(Before, "public sealed class Save", "public class Save") } },
        };
        TestReferences.Apply(test.TestState);
        await test.RunAsync();
    }

    // Keyed

    [TestMethod]
    public async Task NINO034_KeyedEncodingChanged()
    {
        string baseline = await Baseline(Before);
        await Test(Edit(Before, "public string Name;", "[NinoUtf8] public string {|#0:Name|};"), baseline,
            D("NINO034").WithLocation(0)).RunAsync();
    }

    [TestMethod]
    public async Task KeyedMemberAddedOrReordered()
    {
        string baseline = await Baseline(Before);
        await Test(Edit(Before, "public Vec Pos;", "public Vec Pos; public long Gold;"), baseline).RunAsync();
        await Test(Edit(Before, "public int Health;\n        public string Name;", "public string Name;\n        public int Health;"),
            baseline).RunAsync();
    }

    [TestMethod]
    public async Task NINO035_KeyedRenamedWithoutFormerName()
    {
        string baseline = await Baseline(Before);
        await Test(Edit(Before, "public string Name;", "public string {|#0:Title|};"), baseline,
            Verify.Diagnostic("NINO035").WithOptions(DiagnosticOptions.IgnoreAdditionalLocations).WithLocation(0).WithArguments("Game.Save.Title",
                "looks like 'Name' renamed: its key is new, so this build reads its default value from data written before the change, and builds before it drop the value it writes, without an error",
                "add [NinoFormerName(\"Name\")] to it")).RunAsync();
    }

    [TestMethod]
    public async Task NINO036_KeyedRenamedWithFormerNameOrRemoved()
    {
        string baseline = await Baseline(Before);
        await Test(Edit(Before, "public string Name;", "[NinoFormerName(\"Name\")] public string {|#0:Title|};"), baseline,
            D("NINO036").WithLocation(0)).RunAsync();
        await Test(Edit(Edit(Before, "public string Name;", ""), "public sealed class Save", "public sealed class {|#0:Save|}"),
            baseline, D("NINO036").WithLocation(0)).RunAsync();
    }

    // The type

    [TestMethod]
    public async Task NINO034_SealedRemoved()
    {
        string baseline = await Baseline(Before);
        await Test(Edit(Before, "public sealed class Save", "public class {|#0:Save|}"), baseline,
            D("NINO034").WithLocation(0)).RunAsync();
    }

    [TestMethod]
    public async Task NINO034_ReachedPositionalStructChanged()
    {
        string baseline = await Baseline(Before);
        // Reported once, by the struct: the keyed member holding it is not reported for it.
        await Test(Edit(Before, "public struct Vec\n    {\n        public float X;", "public struct {|#0:Vec|}\n    {\n        public double X;"),
            baseline, D("NINO034").WithLocation(0)).RunAsync();
        // A positional type nothing stored reaches is not checked.
        await Test(Edit(Before, "public int A;", "public long A;"), baseline).RunAsync();
    }

    [TestMethod]
    public async Task NINO034_RemovedType()
    {
        string baseline = await Baseline(Before);
        string removed = Before.Substring(0, Before.IndexOf("    [NinoType(format: NinoFormat.VersionTolerant)]")) + "}";
        // The baseline is the additional location, which the code fixes rewrite.
        await Test(removed, baseline.Replace("['Game.Settings']", "{|#0:['Game.Settings']|}")
                .Replace("[assembly: ", "[assembly: {|#1:").Replace("\")]", "\")|}]"),
            Verify.Diagnostic("NINO034").WithLocation(0).WithLocation(1).WithArguments("Game.Settings",
                "is no longer a [NinoType] of this assembly, so this build cannot read data written before the change, and builds before it cannot read data written after it",
                "if it was renamed or moved, add [NinoFormerName(\"Game.Settings\")] to it; if it was removed, accept the change into the layout baseline (code fix, or nino-gen layouts)"))
            .RunAsync();
    }

    [TestMethod]
    public async Task NINO034_MovedTypeKeepsItsIdOnlyWithFormerName()
    {
        string baseline = await Baseline(Before);
        string moved = Edit(Before, "    [NinoType(format: NinoFormat.VersionTolerant)]\n    public class Settings",
            "}\nnamespace Game.Config\n{\n    [NinoType(format: NinoFormat.VersionTolerant)]\n    public class {|#0:Settings|}");
        await Test(moved, baseline, D("NINO034").WithLocation(0)).RunAsync();
        await Test(Edit(moved, "public class {|#0:Settings|}", "[NinoFormerName(\"Game.Settings\")] public class Settings"),
            baseline).RunAsync();
    }

    // NinoNullable<T>

    private const string Nullables = @"
using System;
using System.Collections.Generic;
using Nino.Core;

namespace Game
{
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class Save
    {
        public long? Gold;
        public long?[] Scores;
        public List<Vec?> Path;
        public Row Row;
    }

    [NinoType]
    public struct Vec
    {
        public float X;
        public float Y;
    }

    [NinoType]
    public struct Row
    {
        public string Name;
        public decimal? Price;
        public Guid?[] Ids;
    }

    [NinoType(format: NinoFormat.VersionTolerant)]
    public class Settings
    {
        public int? Volume;
        public List<long?> Marks;
    }
}";

    /// <summary>
    /// Every <c>T?</c> member, array and list of keyed, positional and version-tolerant types changed to
    /// <c>NinoNullable&lt;T&gt;</c>, and back: the layouts' encodings are the same, so the baseline reports nothing.
    /// </summary>
    [TestMethod]
    public async Task NinoNullableInPlaceOfNullableIsNoChange()
    {
        string changed = System.Text.RegularExpressions.Regex.Replace(Nullables, @"(\w+)\?", "NinoNullable<$1>");
        StringAssert.Contains(changed, "public List<NinoNullable<Vec>> Path;");
        StringAssert.Contains(changed, "public NinoNullable<Guid>[] Ids;");
        Assert.IsFalse(changed.Contains('?'));
        await Test(changed, await Baseline(Nullables)).RunAsync();
        await Test(Nullables, await Baseline(changed)).RunAsync();
        // The same edit with another T changes the bytes, and is reported.
        await Test(Edit(changed, "public NinoNullable<int> Volume;", "public NinoNullable<long> {|#0:Volume|};"),
            await Baseline(Nullables), D("NINO034").WithLocation(0)).RunAsync();
    }

    // Version tolerant

    [TestMethod]
    public async Task NINO034_VersionTolerantInsertedNotAppended()
    {
        string baseline = await Baseline(Before);
        await Test(Edit(Before, "public bool Muted;", "public bool Muted;\n        public float Brightness;"), baseline).RunAsync();
        await Test(Edit(Before, "public int Volume;", "public int Volume;\n        public float {|#0:Brightness|};"), baseline,
            D("NINO034").WithLocation(0)).RunAsync();
    }

    [TestMethod]
    public async Task NINO037_UnreadableBaseline()
    {
        string baseline = (await Baseline(Before)).Replace("format = 'Keyed'", "{|#0:format = Keyed|}");
        await Test(Before, baseline, Verify.Diagnostic("NINO037").WithLocation(0)
            .WithArguments("'Keyed' is not a value (strings are in single quotes)", 3)).RunAsync();
    }

    // The code fixes

    [TestMethod]
    public async Task AcceptRewritesTheTypesSection()
    {
        string baseline = await Baseline(Before);
        string after = Edit(Before, "public string Name;", "[NinoUtf8] public string {|#0:Name|};");
        string clean = after.Replace("{|#0:", "").Replace("|}", "");
        await Fix(after, baseline, clean, await Baseline(clean), "NinoLayoutAccept",
            new[] { D("NINO034").WithLocation(0) }).RunAsync();
    }

    [TestMethod]
    public async Task AcceptRemovesTheSectionOfARemovedType()
    {
        string baseline = await Baseline(Before);
        string removed = Before.Substring(0, Before.IndexOf("    [NinoType(format: NinoFormat.VersionTolerant)]")) + "}";
        await Fix(removed, baseline.Replace("['Game.Settings']", "{|#0:['Game.Settings']|}"), removed,
            await Baseline(removed), "NinoLayoutAccept", new[] { D("NINO034").WithLocation(0) }).RunAsync();
    }

    [TestMethod]
    public async Task AcceptAllRewritesEverySection()
    {
        string baseline = await Baseline(Before);
        string after = Edit(Edit(Before, "public string Name;", "[NinoUtf8] public string {|#0:Name|};"),
            "public int Volume;", "public int Volume;\n        public float {|#1:Brightness|};");
        string clean = after.Replace("{|#0:", "").Replace("{|#1:", "").Replace("|}", "");
        var test = Fix(after, baseline, clean, await Baseline(clean), "NinoLayoutAccept",
            new[] { D("NINO034").WithLocation(0), D("NINO034").WithLocation(1) });
        test.NumberOfIncrementalIterations = 2;
        test.NumberOfFixAllIterations = 1;
        await test.RunAsync();
    }

    [TestMethod]
    public async Task AddFormerNameToARenamedMember()
    {
        string baseline = await Baseline(Before);
        await Fix(Edit(Before, "public string Name;", "public string {|#0:Title|};"), baseline,
            Edit(Before, "public string Name;", "[NinoFormerName(\"Name\")]\n        public string {|#1:Title|};"), baseline,
            "NinoLayoutFormerName", new[] { D("NINO035").WithLocation(0) },
            D("NINO036").WithLocation(1)).RunAsync();
    }

    [TestMethod]
    public async Task AddFormerNameToAMovedType()
    {
        string baseline = await Baseline(Before);
        string moved = Edit(Before, "    [NinoType(format: NinoFormat.VersionTolerant)]\n    public class Settings",
            "}\nnamespace Game.Config\n{\n    [NinoType(format: NinoFormat.VersionTolerant)]\n    public class {|#0:Settings|}");
        string fixedSource = Edit(moved, "    [NinoType(format: NinoFormat.VersionTolerant)]\n    public class {|#0:Settings|}",
            "    [NinoType(format: NinoFormat.VersionTolerant)]\n    [NinoFormerName(\"Game.Settings\")]\n    public class Settings");
        await Fix(moved, baseline, fixedSource, baseline, "NinoLayoutFormerName",
            new[] { D("NINO034").WithLocation(0) }).RunAsync();
    }

    [TestMethod]
    public async Task MakeTheClassSealedAgain()
    {
        string baseline = await Baseline(Before);
        await Fix(Edit(Before, "public sealed class Save", "public class {|#0:Save|}"), baseline, Before, baseline,
            "NinoLayoutAddSealed", new[] { D("NINO034").WithLocation(0) }).RunAsync();
    }

    [TestMethod]
    public async Task MoveAnInsertedMemberToTheEnd()
    {
        string baseline = await Baseline(Before);
        await Fix(Edit(Before, "public int Volume;", "public int Volume;\n        public float {|#0:Brightness|};"), baseline,
            Edit(Before, "public bool Muted;", "public bool Muted;\n        public float Brightness;"), baseline,
            "NinoLayoutMoveToEnd", new[] { D("NINO034").WithLocation(0) }).RunAsync();
    }
}
