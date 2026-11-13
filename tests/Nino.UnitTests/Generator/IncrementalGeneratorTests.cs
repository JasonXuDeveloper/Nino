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
    public void InheritedTypesBindInTreesWithTheSamePath()
    {
        var compilation = Create(
            ("Base.cs", Usings + "[NinoType] public class Base { public int X; }\n}"),
            ("Shared.cs", Usings + "public class Alpha : Base { public int A; }\n}"),
            ("Shared.cs", Usings + "public class Bravo : Base { public int B; }\n}"));
        var driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator()).RunGenerators(compilation);

        StringAssert.Contains(Output(driver, "Alpha"), "value.A");
        StringAssert.Contains(Output(driver, "Bravo"), "value.B");
    }

    [TestMethod]
    public void ShiftedModelsReuseTheirFormatterText()
    {
        // A type inserted ahead of another shifts it to a new slot of the model step; its file must be the same
        // SourceText instance, found by model value (EmittedFormatters), so the compiler keeps its parsed tree.
        var compilation = Create(("Types.cs", Usings + "[NinoType] public class Kept { public int A; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        var before = Text(driver);

        compilation = Edit(compilation, "Types.cs", "[NinoType] public class Kept",
            "[NinoType] public class Inserted { public long B; }\n[NinoType] public class Kept");
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Inc_InsertedFormatter.g.cs").SourceText.ToString(), "value.B");
        Assert.AreSame(before, Text(driver));

        static SourceText Text(GeneratorDriver driver) => driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Inc_KeptFormatter.g.cs").SourceText;
    }

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
        StringAssert.Contains(holder, "WriteValueIn(ref destination, offset, in value.I);");

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
        holder = Output(driver, "Holder");
        Assert.IsFalse(holder.Contains("WriteValueIn("));
        StringAssert.Contains(holder, ".Serialize(value.I, ref writer);");
    }

    [TestMethod]
    public void CollectionsThatHoldThemselvesNameTheirNestingFormatter()
    {
        // Each level of a type that holds itself calls a generated struct directly (it counts the level), where
        // CachedFormatter<T> made a call through the shared generic code's dictionary and a virtual one. Every struct
        // a formatter, a top-level call or a closed generic names is declared once, whichever model named it first.
        var compilation = Create(
            ("Types.cs", Usings + """
                public sealed class Tree : System.Collections.Generic.List<Tree> { }
                public sealed class Map : System.Collections.Generic.Dictionary<string, Map> { }
                public sealed class A : System.Collections.Generic.List<B> { }
                public sealed class B : System.Collections.Generic.List<A> { }
                [NinoType] public class Box<T> { public T Value; }
                }
                """),
            ("Holder.cs", Usings + "[NinoType] public class Holder { public Tree T; [NinoUtf8] public Map M; public Box<Tree> X; }\n}"),
            ("Other.cs", Usings + "[NinoType] public class Other { public B B; public Map M; }\n}"),
            ("Calls.cs", Usings + """
                public static class Calls
                {
                    public static byte[] Write(A a) => NinoSerializer.Serialize(a);
                }
                }
                """));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        string nesting = driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Nesting.g.cs").SourceText.ToString();
        // Tree, Map and Map with UTF-8 keys, and one of A and B (the cycle closes there).
        Assert.AreEqual(4, nesting.Split("internal struct ").Length - 1, nesting);
        // Named after the type's formatter, the hash of the closed type, then NestingFormatter.
        StringAssert.Matches(nesting,
            new System.Text.RegularExpressions.Regex(@"internal struct Inc_Tree_[0-9A-F]{8}NestingFormatter :"));
        StringAssert.Contains(nesting, "writer.EnterStruct(typeof(global::Inc.Tree));");
        StringAssert.Contains(nesting, "reader.EnterStruct(typeof(global::Inc.Map));");
        foreach (var file in driver.GetRunResult().Results[0].GeneratedSources)
            Assert.IsFalse(file.SourceText.ToString().Contains("CachedFormatter"), file.HintName);
        StringAssert.Contains(Output(driver, "Holder"),
            "ListFormatter<global::Inc.Tree, global::Inc.Tree, Inc_Tree_");

        // A model rebuilt alone after an edit still finds the structs the others declared.
        compilation = Edit(compilation, "Other.cs", "public Map M;", "public Map M; public int I;");
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out output, out _);
        errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
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
        string box = Output(driver, "Box_1").Replace("__C.", "global::Nino.Core.");
        StringAssert.Contains(box, "where __F_T : struct, global::Nino.Core.INinoFormatter<T>");
        Assert.IsFalse(box.Contains("CachedFormatter"), box);
        StringAssert.Contains(Output(driver, "Holder").Replace("__C.", "global::Nino.Core."),
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
                         "public void Deserialize(ref Reader reader, ref Money value) { value ??= new Money(); reader.Read(out value.Cents); }\n}\n}"),
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
    public void RegistryRegistersTheMarksOfRegisteredFormatters()
    {
        // IL2CPP keeps no attributes on generic parameters: the registry registers [NinoRaw] (1), unmanaged (2) and
        // [NinoCreated] (4) per parameter, up to the last marked one, before handing the assembly's registrations to
        // the resolver. Any number of parameters: a bit per parameter in an int had wrapped at 32.
        string wide = string.Join(", ", Enumerable.Range(0, 32).Select(i => $"T{i}"));
        var compilation = Create(
            ("Rules.cs", "using Nino.Core;\n[assembly: NinoFormatter(typeof(Inc.CellFormatter<>))]\n" +
                         $"[assembly: NinoFormatter(typeof(Inc.WideFormatter<{new string(',', 32)}>))]\n" +
                         "[assembly: NinoFormatter(typeof(Inc.BagFormatter<,>))]\nnamespace Inc {\n" +
                         "public class Cell<T> { public T V; }\npublic class Bag<T> : System.Collections.Generic.List<T> { }\n" +
                         "public struct CellFormatter<[NinoRaw] T> : INinoFormatter<Cell<T>> where T : unmanaged {\n" +
                         "public void Serialize(Cell<T> value, ref Writer writer) { }\n" +
                         "public void Deserialize(ref Reader reader, ref Cell<T> value) { }\n}\n" +
                         "public struct BagFormatter<[NinoCreated] TBag, T> : INinoFormatter<TBag> where TBag : Bag<T>, new() {\n" +
                         "public void Serialize(TBag value, ref Writer writer) { }\n" +
                         "public void Deserialize(ref Reader reader, ref TBag value) { }\n}\n" +
                         $"public class Wide<{wide}, T32> : System.Collections.Generic.List<T32> {{ }}\n" +
                         $"public struct WideFormatter<{wide}, [NinoCreated] T32> : INinoFormatter<Wide<{wide}, T32>>\n" +
                         $"where T32 : Wide<{wide}, T32>, new() {{\n" +
                         $"public void Serialize(Wide<{wide}, T32> value, ref Writer writer) {{ }}\n" +
                         $"public void Deserialize(ref Reader reader, ref Wide<{wide}, T32> value) {{ }}\n}}\n}}"),
            ("Holder.cs", Usings + "[NinoType] public class Holder { public int X; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGenerators(compilation);
        string registry = driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Registry.g.cs").SourceText.ToString();
        StringAssert.Contains(registry,
            "NinoParameterMarks.Register(typeof(global::Inc.CellFormatter<>), 3);");
        StringAssert.Contains(registry,
            "NinoParameterMarks.Register(typeof(global::Inc.BagFormatter<,>), 4);");
        StringAssert.Contains(registry, $"NinoParameterMarks.Register(typeof(global::Inc.WideFormatter<{new string(',', 32)}>), " +
                                        $"{string.Concat(Enumerable.Repeat("0, ", 32))}4);");
        Assert.IsTrue(registry.IndexOf("NinoParameterMarks.Register", StringComparison.Ordinal) <
                      registry.IndexOf("NinoRegistry.Add", StringComparison.Ordinal), registry);
    }

    private const string RegisterPointers = "NinoFunctionPointerFields.Register(";

    /// <summary>The registry's NinoFunctionPointerFields.Register arguments for structs of <paramref name="assembly"/>.</summary>
    private static string[] PointerRegistrations(GeneratorDriver driver, string assembly)
    {
        string registry = driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Registry.g.cs").SourceText.ToString();
        Assert.IsTrue(registry.IndexOf(RegisterPointers, StringComparison.Ordinal) <
                      registry.IndexOf("NinoRegistry.Add", StringComparison.Ordinal), registry);
        return registry.Split('\n').Select(l => l.Trim())
            .Where(l => l.Contains(RegisterPointers + "\"" + assembly + "\", "))
            .Select(l => l.Substring(l.IndexOf(RegisterPointers, StringComparison.Ordinal) + RegisterPointers.Length))
            .ToArray();
    }

    [TestMethod]
    public void RegistryRegistersFunctionPointerFields()
    {
        // Mono and IL2CPP report a delegate* field as an IntPtr (sign-extended on 32-bit), .NET 8 and later as a
        // function pointer type: the registry names the fields of every struct the compilation sees (by assembly and
        // runtime name, loading no type) for Nino.Core to lay out as pointers, zero-extended. A struct of another
        // assembly that a member names, ones declared here that only generic code reaches, generic and nested ones,
        // auto-properties and positional record structs; an nint is no function pointer.
        var library = CreateNamed("FnLib", ("Lib.cs", "namespace Lib { public unsafe struct Remote { public int A; public delegate*<int> Call; } }"));
        var compilation = CreateNamed("FnApp",
                ("Types.cs", Usings + "public unsafe struct Local { public byte A; public delegate*<int> F; public nint N; }\n" +
                             "public unsafe struct Generic<T> where T : unmanaged { public T V; public delegate*<T, void> F; }\n" +
                             "public unsafe class Outer { private struct Hidden { public delegate* unmanaged<void> G { get; set; } } }\n" +
                             "public unsafe record struct Positional(delegate*<int> P, int Q);\n" +
                             "public struct Plain { public nint N; }\n" +
                             "[NinoType] public class Holder { public Lib.Remote R; }\n}"))
            .AddReferences(library.ToMetadataReference());
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        CollectionAssert.AreEqual(new[] { "\"FnLib\", \"Lib.Remote\", \"Call\");" }, PointerRegistrations(driver, "FnLib"));
        CollectionAssert.AreEquivalent(new[]
            {
                "\"FnApp\", \"Inc.Local\", \"F\");",
                "\"FnApp\", \"Inc.Generic<T>\", \"F\");",
                "\"FnApp\", \"Inc.Outer+Hidden\", \"<G>k__BackingField\");",
                "\"FnApp\", \"Inc.Positional\", \"<P>k__BackingField\");",
            }, PointerRegistrations(driver, "FnApp"));
    }

    [TestMethod]
    public void RegistryRegistersFunctionPointerFieldsOfEveryReferencedStruct()
    {
        // A struct of an assembly built without Nino's generator, which only generic code reaches (Save<T> calling
        // Serialize<T>): nothing here names it, yet the registry of every assembly referencing it registers its
        // fields, from the reference's metadata. Private fields (Roslyn's symbols of a metadata reference omit them),
        // internal and private nested structs, a volatile field (a custom modifier), a nested generic, an
        // auto-property and a struct outside any namespace; no class, no static field; each named as the runtime
        // names its type. (The field a primary
        // constructor captures, C# 12, is a field like these; Nino.RuntimeTests declares one.)
        const string source = @"
namespace Lib
{
    public unsafe struct PrivateField { private delegate*<int> _call; public int A; public int Call() => _call(); }
    internal unsafe struct Internal { public volatile delegate*<int> V; }
    public unsafe class Outer<T> { private struct Hidden<U> { public delegate*<T, U> F; } }
    public unsafe struct Property { public delegate* unmanaged<void> P { get; set; } }
    public unsafe class NotAStruct { public delegate*<int> F; }
    public unsafe struct Static { public static delegate*<int> S; public nint N; }
}
public unsafe struct Global { public delegate*<void> G; }";
        var library = CreateNamed("FnRef", ("Lib.cs", source));
        var expected = new[]
        {
            "\"FnRef\", \"Global\", \"G\");",
            "\"FnRef\", \"Lib.Internal\", \"V\");",
            "\"FnRef\", \"Lib.Outer+Hidden<T,U>\", \"F\");",
            "\"FnRef\", \"Lib.PrivateField\", \"_call\");",
            "\"FnRef\", \"Lib.Property\", \"<P>k__BackingField\");",
        };
        using var image = new System.IO.MemoryStream();
        var emitted = library.Emit(image);
        Assert.IsTrue(emitted.Success, string.Join("\n", emitted.Diagnostics));
        // The names are the runtime's: Nino.Core matches them against NinoTypeId.GetRuntimeName of the loaded type,
        // so the two views below (metadata and symbols) and the runtime must spell every struct alike.
        var runtimeNames = System.Reflection.Assembly.Load(image.ToArray()).GetTypes()
            .Select(NinoTypeId.GetRuntimeName).ToArray();
        foreach (var registration in expected)
            CollectionAssert.Contains(runtimeNames, registration.Split(new[] { ", " }, StringSplitOptions.None)[1].Trim('"'), registration);
        // The built library (a file reference) and the library's compilation (a project reference in the IDE).
        foreach (var reference in new MetadataReference[] { MetadataReference.CreateFromImage(image.ToArray()), library.ToMetadataReference() })
        {
            var compilation = Create(("Save.cs", Usings +
                    "public static class Saver { public static byte[] Save<T>(T value) where T : unmanaged => NinoSerializer.Serialize(value); }\n}"))
                .AddReferences(reference);
            GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
            var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
            CollectionAssert.AreEqual(expected, PointerRegistrations(driver, "FnRef"), reference.GetType().Name);
        }
    }

    [TestMethod]
    public void CallsThroughAliasesAndUsingStaticAreRegistered()
    {
        // The receiver is matched by symbol: an alias (here or global, in another file, or in a namespace naming the
        // file's alias) or using static names it too.
        var compilation = Create(
            ("Global.cs", "global using G = Nino.Core.NinoDeserializer;"),
            ("Calls.cs", @"using S = Nino.Core.NinoSerializer;
using static Nino.Core.NinoDeserializer;
using System.Collections.Generic;
namespace Inc {
using N = S;
public static class Calls
{
    public static byte[] Write(List<int> x) => S.Serialize(x);
    public static byte[] WriteNested(List<uint> x) => N.Serialize(x);
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
        StringAssert.Contains(usages, "using __SCG = global::System.Collections.Generic;");
        StringAssert.Contains(usages, "NinoFormatterCache<__SCG.List<int>>");
        StringAssert.Contains(usages, "NinoFormatterCache<__SCG.List<long>>");
        StringAssert.Contains(usages, "NinoFormatterCache<__SCG.List<short>>");
        StringAssert.Contains(usages, "NinoFormatterCache<__SCG.List<uint>>");
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
        [NinoType(includeNonPublicMembers: true)]
        public partial class Inner { private int _secret; public string Name; }

        [NinoType(includeNonPublicMembers: true)]
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
    public void OpenMethodArgumentsDoNotHideQualifiedClosedTypes()
    {
        var compilation = Create(("Calls.cs", Usings + """
            using System.Collections.Generic;
            [NinoType] public class T { public int Value; }
            public static class Calls
            {
                public static byte[] Open<T>(List<T> value) => NinoSerializer.Serialize<List<T>>(value);
                public static byte[] Closed<T>(List<global::Inc.T> value) =>
                    NinoSerializer.Serialize<List<global::Inc.T>>(value);
            }
            }
            """));
        var result = RunTracked(compilation);
        var usages = result.GeneratedSources.Single(source => source.HintName == "NinoGen.Usages.g.cs");
        StringAssert.Contains(usages.SourceText.ToString(), "List<global::Inc.T>");
        Assert.IsFalse(usages.SourceText.ToString().Contains("List<T>"));
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
    [DataRow(LanguageVersion.CSharp9, false)]
    [DataRow(LanguageVersion.CSharp10, true)]
    [DataRow(LanguageVersion.Latest, true)]
    public void FilesDeclareTheirNamespaceAsTheLanguageAllows(LanguageVersion version, bool fileScoped)
    {
        // C# 10 and later: a file-scoped namespace, except where the file declares another namespace (the partial
        // accessors of private members, the interceptors' attribute); C# 9 (Unity): blocks.
        var parse = new CSharpParseOptions(version).WithFeatures(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string>("InterceptorsNamespaces", "NinoGen"),
        });
        var compilation = Create(("Shapes.cs", Usings + @"
[NinoType] public class Shape { public int Id; }
[NinoType] public sealed class Circle : Shape { public float R; }
[NinoType] public class Box<__IL> { public __IL Value; }
[NinoType] public class AliasBox<__MI> { public __MI Value; }
[NinoType] public class DispatchAlias<__CS> { public __CS Value; }
[NinoType] public sealed class DispatchAliasLeaf : DispatchAlias<int> { public short Tail; }
[NinoType(includeNonPublicMembers: true)] public partial class Hidden { private int _secret; public int Get() => _secret; }
public class Tree : System.Collections.Generic.List<Tree> { }
[NinoType] public class Forest { public Tree Root; }
public static class Calls
{
    public static byte[] Write(Shape s) => NinoSerializer.Serialize(s);
    public static Box<int> Read(byte[] d) => NinoDeserializer.Deserialize<Box<int>>(d);
    public static byte[] WriteMany(System.Collections.Generic.List<Circle> c) => NinoSerializer.Serialize(c);
}
}"));
        compilation = compilation.RemoveAllSyntaxTrees()
            .AddSyntaxTrees(compilation.SyntaxTrees.Select(t => t.WithRootAndOptions(t.GetRoot(), parse)));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
            parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));

        var sources = driver.GetRunResult().Results[0].GeneratedSources;
        Assert.IsTrue(sources.Length > 6);
        Assert.AreEqual(fileScoped, Output(driver, "Circle").Contains("Create() => new();"));
        foreach (var source in sources)
        {
            string text = source.SourceText.ToString();
            bool own = !source.HintName.Contains("Interceptors") && !source.HintName.Contains("Inc_Hidden");
            Assert.AreEqual(fileScoped && own, text.Contains("namespace NinoGen;\n"), source.HintName);
            Assert.AreEqual(!(fileScoped && own), text.Contains("namespace NinoGen\n{\n"), source.HintName);
        }
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
            ("B.cs", Usings + "[NinoType(includeNonPublicMembers: true)] public partial class B { private int _y; }\n}"),
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
        foreach (var (_, reason) in result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs))
            Assert.AreEqual(IncrementalStepRunReason.Cached, reason);
        // The models, the calls and every step that collects them or reads the whole compilation.
        foreach (var step in new[]
                 {
                     "NinoType", "TopLevelCall", "ProjectFacts", "ClashingNames", "RegistryEntries",
                     "Usages", "FormatterRegistrations", "FactoryRegistrations", "GenericTypeRegistrations", "LayoutTypes",
                     "ReferencedFunctionPointerFields", "FunctionPointerFields", "Dispatch",
                 })
        {
            // Every step runs the first time; a step whose inputs are all cached is not run (nor listed) again.
            Assert.IsTrue(first.TrackedSteps.ContainsKey(step), step + " not in " + string.Join(", ", first.TrackedSteps.Keys));
            if (!result.TrackedSteps.TryGetValue(step, out var runs))
                continue;
            foreach (var (_, reason) in runs.SelectMany(s => s.Outputs))
                Assert.IsTrue(reason is IncrementalStepRunReason.Unchanged or IncrementalStepRunReason.Cached,
                    $"{step}: {reason}");
        }
    }

    [TestMethod]
    public void ReparsedSourcesReuseCallBindings()
    {
        var compilation = Create(
            ("Alias.cs", "global using Saver = Nino.Core.NinoSerializer;"),
            ("Calls.cs", Usings + """
                public static class Calls
                {
                    public static byte[] Write(System.Collections.Generic.List<int> value) => Saver.Serialize(value);
                }
                public static class Other { public static byte[] Serialize<T>(T value) => null; }
                }
                """));

        static object? Call(GeneratorRunResult result) =>
            result.TrackedSteps.TryGetValue("TopLevelCall", out var steps)
                ? steps.SelectMany(step => step.Outputs).Single().Value
                : null;

        var first = RunTracked(compilation);
        Assert.IsNotNull(Call(first));
        var reparsed = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Select(tree =>
        {
            var text = tree.GetText();
            return CSharpSyntaxTree.ParseText(SourceText.From(text.ToString(), text.Encoding, text.ChecksumAlgorithm),
                (CSharpParseOptions)tree.Options, tree.FilePath);
        }));
        var second = RunTracked(reparsed);
        GC.KeepAlive(compilation);
        Assert.AreSame(Call(first), Call(second));
        CollectionAssert.AreEqual(first.GeneratedSources.Select(source => source.SourceText.ToString()).ToArray(),
            second.GeneratedSources.Select(source => source.SourceText.ToString()).ToArray());

        var changed = Edit(reparsed, "Alias.cs", "Nino.Core.NinoSerializer", "Inc.Other");
        Assert.IsNull(Call(RunTracked(changed)));
    }

    [TestMethod]
    public void MethodBodyEditRebindsOnlyItsOwnCalls()
    {
        var compilation = Create(("Calls.cs", Usings + """
            using System.Collections.Generic;
            public static class Calls
            {
                public static byte[] First() { return NinoSerializer.Serialize(new List<int>()); }
                public static byte[] Second() { return NinoSerializer.Serialize(new List<byte>()); }
            }
            }
            """));
        var first = CallBindings(RunTracked(compilation));
        var edited = Edit(compilation, "Calls.cs", "new List<int>()", "new List<long>()");
        var result = RunTracked(edited);
        GC.KeepAlive(compilation);
        var second = CallBindings(result);
        Assert.AreNotSame(first[0], second[0]);
        Assert.AreSame(first[1], second[1]);
        var usages = result.GeneratedSources.Single(s => s.HintName == "NinoGen.Usages.g.cs").SourceText.ToString();
        StringAssert.Contains(usages, "List<long>");
        Assert.IsFalse(usages.Contains("List<int>"));

        // A directive may change binding outside the method's braces.
        compilation = Edit(edited, "Calls.cs", "return NinoSerializer.Serialize(new List<long>());",
            "\n#nullable enable\nreturn NinoSerializer.Serialize(new List<long>());");
        Assert.AreNotSame(second[1], CallBindings(RunTracked(compilation))[1]);

        static object?[] CallBindings(GeneratorRunResult run) =>
            run.TrackedSteps["TopLevelCall"].SelectMany(step => step.Outputs).Select(o => o.Value).ToArray();
    }

    private static GeneratorRunResult RunTracked(CSharpCompilation compilation) =>
        CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
                driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None,
                    trackIncrementalGeneratorSteps: true))
            .RunGenerators(compilation).GetRunResult().Results[0];

    [TestMethod]
    public void ReparsedAndMethodEditedModelsKeepTheirDependencies()
    {
        var compilation = Create(
            ("Box.cs", Usings + """
                [NinoType] public class Box
                {
                    public string Name;
                    public Box() { }
                    public void Work() { _ = 1; }
                }
                }
                """),
            ("Holder.cs", Usings + "[NinoType] public class Holder { public Box Value; } }"));
        var first = RunTracked(compilation);
        var reparsed = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(compilation.SyntaxTrees.Select(tree =>
            CSharpSyntaxTree.ParseText(tree.GetText(), (CSharpParseOptions)tree.Options, tree.FilePath)));
        var second = RunTracked(reparsed);
        GC.KeepAlive(compilation);
        Assert.AreSame(Model(first, "Box"), Model(second, "Box"));
        Assert.AreSame(Model(first, "Holder"), Model(second, "Holder"));

        var edited = Edit(reparsed, "Box.cs", "_ = 1;", "_ = 2;");
        var third = RunTracked(edited);
        GC.KeepAlive(reparsed);
        Assert.AreSame(Model(second, "Box"), Model(third, "Box"));
        Assert.AreSame(Model(second, "Holder"), Model(third, "Holder"));

        // The constructor affects fresh reads, so even a body-only constructor edit invalidates both models.
        edited = Edit(edited, "Box.cs", "public Box() { }", "public Box() { Name = \"seed\"; }");
        var fourth = RunTracked(edited);
        Assert.AreNotSame(Model(third, "Box"), Model(fourth, "Box"));
        Assert.AreNotSame(Model(third, "Holder"), Model(fourth, "Holder"));

        static object? Model(GeneratorRunResult result, string name) => result.TrackedSteps["NinoType"]
            .SelectMany(step => step.Outputs)
            .Select(output => (System.Runtime.CompilerServices.ITuple)output.Value!)
            .Single(tuple => (string)tuple[0]! == name)[1];
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
        const string registered = "NinoTypes.Register<global::Inc.Box<int>";
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
    public void CallsFollowDeclarationsInOtherFiles()
    {
        // A call is not bound again while its file is the same and no other file's declarations changed
        // (ModelCache.GetOrBuildCall): an edit in a method body elsewhere keeps it, a declaration elsewhere that changes
        // what it binds to (a global using alias) is seen.
        var compilation = Create(
            ("Box.cs", Usings + "[NinoType] public class Box<T> { public T V; }\n}"),
            ("Alias.cs", "global using Saver = Inc.Other;\n"),
            ("Calls.cs", Usings + "public static class Calls { public static byte[] W() => Saver.Serialize<Box<int>>(null); }\n}"),
            ("Other.cs", Usings + "public static class Other { public static byte[] Serialize<T>(T v) { return null; } }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        string Usages() => driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Usages.g.cs").SourceText.ToString();
        const string registered = "NinoTypes.Register<global::Inc.Box<int>";
        driver = driver.RunGenerators(compilation);
        Assert.IsFalse(Usages().Contains(registered), Usages());

        // Only a method body changes: the call still binds to Other.Serialize.
        compilation = Edit(compilation, "Other.cs", "{ return null; }", "{ return new byte[0]; }");
        driver = driver.RunGenerators(compilation);
        Assert.IsFalse(Usages().Contains(registered), Usages());

        // The alias in another file now names NinoSerializer: the call is bound again and registers its type.
        compilation = Edit(compilation, "Alias.cs", "Inc.Other", "Nino.Core.NinoSerializer");
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Usages(), registered);

        // And back.
        compilation = Edit(compilation, "Alias.cs", "Nino.Core.NinoSerializer", "Inc.Other");
        driver = driver.RunGenerators(compilation);
        Assert.IsFalse(Usages().Contains(registered), Usages());
    }

    [TestMethod]
    public void EveryCallThatNamesATypeRegistersIt()
    {
        // Native AOT cannot close a generic formatter at run time: every call that names a closed type, by type
        // argument or by typeof, registers it, not only Serialize and Deserialize. A Type known only at run time
        // names nothing, and a method of another class is not Nino's.
        var compilation = Create(
            ("Pair.cs", Usings + "[NinoType] public record struct Pair<TKey, TValue>(TKey Key, TValue Value);\n}"),
            ("Calls.cs", Usings + @"using System;
using System.Collections.Generic;
using static Nino.Core.NinoSerializer;
public static class Calls
{
    public static int Fixed() => NinoSerializer.GetFixedSize<Pair<int, short>>();
    public static byte[] Boxed(object o) => NinoSerializer.SerializeBoxed(o, typeof(Pair<int, long>));
    public static object Read(byte[] b) => NinoDeserializer.DeserializeBoxed(b, typeof(Pair<byte, int>));
    public static void ReadInto(byte[] b, ref object o) => NinoDeserializer.DeserializeBoxed(b, ref o, type: typeof(List<Pair<int, int>>));
    public static bool Supported() => IsSupported(typeof(Pair<short, short>));
    public static byte[] Runtime(object o) => NinoSerializer.SerializeBoxed(o, o.GetType());
    public static bool Open() => NinoSerializer.IsSupported(typeof(Pair<,>));
    public static int NotNino() => Other.GetFixedSize<Pair<long, long>>();
}
public static class Other { public static int GetFixedSize<T>() => 0; }
}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.AreEqual(0, errors.Length, string.Join("\n", errors.Select(e => e.ToString())));
        string Usages() => driver.GetRunResult().Results[0].GeneratedSources
            .Single(s => s.HintName == "NinoGen.Usages.g.cs").SourceText.ToString();
        foreach (var registered in new[] { "Pair<int, short>", "Pair<int, long>", "Pair<byte, int>", "Pair<int, int>", "Pair<short, short>" })
            StringAssert.Contains(Usages(), "NinoTypes.Register<global::Inc." + registered);
        StringAssert.Contains(Usages().Replace("__SCG.", "global::System.Collections.Generic."),
            "NinoFormatterCache<global::System.Collections.Generic.List<global::Inc.Pair<int, int>>>");
        Assert.IsFalse(Usages().Contains("Pair<long, long>"), Usages());

        // The call is cached by position, method and the type it names: an edit of the typeof argument is seen.
        compilation = Edit(compilation, "Calls.cs", "typeof(Pair<int, long>)", "typeof(Pair<long, int>)");
        driver = driver.RunGenerators(compilation);
        StringAssert.Contains(Usages(), "NinoTypes.Register<global::Inc.Pair<long, int>");
        Assert.IsFalse(Usages().Contains("Pair<int, long>"), Usages());
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
    public void ClosedProjectReleasesGeneratorInputsAndOutputs()
    {
        var (tree, text) = GenerateAndReleaseProject();
        for (int i = 0; i < 5 && (tree.IsAlive || text.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.IsFalse(tree.IsAlive, "A closed project retained its syntax tree.");
        Assert.IsFalse(text.IsAlive, "A closed project retained its generated formatter text.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ActiveProjectReleasesThePreviousTree(bool editBody)
    {
        var (compilation, driver, previous) = GenerateAndReplaceTree(editBody);
        for (int i = 0; i < 5 && previous.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.IsFalse(previous.IsAlive, "The active compilation retained its previous tree.");
        GC.KeepAlive(compilation);
        GC.KeepAlive(driver);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (Compilation Compilation, GeneratorDriver Driver, WeakReference Previous) GenerateAndReplaceTree(
        bool editBody)
    {
        var compilation = Create(("Reparsed.cs", Usings + """
            [NinoType] public class Box { public int X; }
            public static class Calls
            {
                public static byte[] Write(Box value) => NinoSerializer.Serialize(value);
                public static void Work() { _ = 1; }
            }
            }
            """));
        var before = compilation.SyntaxTrees.Single();
        var first = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator()).RunGenerators(compilation);
        var next = CSharpSyntaxTree.ParseText(before.GetText(), (CSharpParseOptions)before.Options, before.FilePath);
        var edited = editBody ? Edit(compilation, "Reparsed.cs", "_ = 1;", "_ = 2;")
            : compilation.ReplaceSyntaxTree(before, next);
        var second = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator()).RunGenerators(edited);
        GC.KeepAlive(first);
        GC.KeepAlive(compilation);
        return (edited, second, new WeakReference(before));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Tree, WeakReference Text) GenerateAndReleaseProject()
    {
        var compilation = Create(("Closed.cs", Usings + "[NinoType] public class Closed { public int X; } }"));
        var driver = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator()).RunGenerators(compilation);
        var text = driver.GetRunResult().Results[0].GeneratedSources
            .Single(source => source.HintName == "NinoGen.Inc_ClosedFormatter.g.cs").SourceText;
        return (new WeakReference(compilation.SyntaxTrees.Single()), new WeakReference(text));
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

    [TestMethod]
    public void AddingAMemberReEmitsOnlyTheTypesThatHoldIt()
    {
        // GeneratorBenchmark's member edit (#262): a member added to a base rebuilds every model of its file (the tree
        // changed), but only the base's and its subtype's models differ, so only their formatter files are emitted
        // again; the collected stages (registry, dispatch, usages, names) keep their values and emit nothing.
        var compilation = Create(
            ("A.cs", Usings + "[NinoType] public class Base { public int X; }\n" +
                     "[NinoType] public class Derived : Base { public string Y; }\n" +
                     "[NinoType] public class Other { public long Z; }\n" +
                     "[NinoType] public struct Value { public int V; }\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new Generator.Generation.NinoGenerator().AsSourceGenerator() },
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        compilation = Edit(compilation, "A.cs", "public int X;", "public int X; public int Extra;");
        driver = driver.RunGenerators(compilation);
        var result = driver.GetRunResult().Results[0];
        Assert.AreEqual(0, result.Diagnostics.Length, string.Join("\n", result.Diagnostics));
        var models = result.TrackedSteps["NinoType"].SelectMany(s => s.Outputs).ToList();
        Assert.AreEqual(2, models.Count(o => o.Reason == IncrementalStepRunReason.Modified),
            string.Join(", ", models.Select(o => o.Reason)));
        foreach (var name in new[] { "ClashingNames", "Dispatch", "RegistryEntries", "FormatterRegistrations", "LayoutTypes" })
        foreach (var output in result.TrackedSteps[name].SelectMany(s => s.Outputs))
            Assert.IsTrue(output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"{name}: {output.Reason}");
        var emitted = result.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs)
            .Count(o => o.Reason != IncrementalStepRunReason.Cached);
        Assert.AreEqual(2, emitted);
        StringAssert.Contains(Output(driver, "Base"), "value.Extra");
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
[NinoType(includeNonPublicMembers: true)] public class S { private int _never; private string _assigned = """"; }
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
