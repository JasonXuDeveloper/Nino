using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>
/// Hot-update runtimes (HybridCLR) load assemblies with <c>Assembly.Load(bytes)</c> and run neither module
/// initializers nor Unity's load hooks. Registration must then happen on its own, from the
/// <c>[assembly: NinoGenerated]</c> attribute, when Nino first meets a type of the assembly or an unknown type id.
/// </summary>
[TestClass]
public class HotUpdateAssemblyTests
{
    private const string Source = @"
using System.Collections.Generic;
using Nino.Core;
namespace Hot
{
    [NinoType] public class Item { public int X; public List<Part> Parts; }
    [NinoType] public class Part { public string S; }
    [NinoType] public class Shape { public int Id; }
    [NinoType] public class Circle : Shape { public float R; }

    public static class Api
    {
        public static byte[] SerializeCircle() => NinoSerializer.Serialize<object>(new Circle { Id = 1, R = 2 });
        public static string RoundTripItem()
        {
            var bytes = NinoSerializer.Serialize(new Item { X = 3, Parts = new List<Part> { new Part { S = ""p"" } } });
            var item = NinoDeserializer.Deserialize<Item>(bytes);
            return item.X + item.Parts[0].S;
        }
    }
}";

    private static Assembly CompileWithoutInitializers()
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(NinoTypeAttribute).Assembly.Location));
        var compilation = CSharpCompilation.Create("Hot" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(Source, parse) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var run = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator()).RunGenerators(compilation)
            .GetRunResult();
        // What a hot-update runtime skips: drop the entry points that would register eagerly.
        var generated = run.GeneratedTrees.Select(t => CSharpSyntaxTree.ParseText(t.GetText().ToString()
            .Replace("[global::System.Runtime.CompilerServices.ModuleInitializer]", ""), parse, t.FilePath));
        var output = compilation.AddSyntaxTrees(generated);

        using var stream = new MemoryStream();
        var result = output.Emit(stream);
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        StringAssert.Contains(string.Concat(run.GeneratedTrees.Select(t => t.ToString())), "[assembly: global::Nino.Core.NinoGenerated(");
        return Assembly.Load(stream.ToArray());
    }

    [TestMethod]
    public void LoadedAssemblyRegistersWithoutInitializers()
    {
        var api = CompileWithoutInitializers().GetType("Hot.Api")!;

        // An object of the loaded assembly read through a type Nino.Core knows: found by its unknown type id.
        var circle = NinoDeserializer.Deserialize<object>((byte[])api.GetMethod("SerializeCircle")!.Invoke(null, null)!);
        Assert.AreEqual("Hot.Circle", circle.GetType().FullName);
        Assert.AreEqual(2f, circle.GetType().GetField("R")!.GetValue(circle));

        // Types first met by the generated code of the loaded assembly itself.
        Assert.AreEqual("3p", api.GetMethod("RoundTripItem")!.Invoke(null, null));
    }
}
