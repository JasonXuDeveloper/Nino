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
/// Sealed classes that nest nothing get a <c>__Top</c> twin of their formatter, which the interceptors of top-level
/// calls use: no depth check, and a small body is inlined into the caller. This assembly defines
/// WEAK_VERSION_TOLERANCE (no type here has one), so the types are compiled here, and their bytes and reads through
/// <c>__Top</c> compared with the regular formatter's.
/// </summary>
[TestClass]
public class TopLevelInlineTests
{
    private const string Source = @"
using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;
namespace Top
{
    [NinoType] public sealed class Ints { public int Id; public int[] A; }
    [NinoType] public sealed class Text { public string A; [NinoUtf8] public string B; }
    [NinoType] public sealed class Dates { public List<DateTime> A; }
    [NinoType] public sealed class Map { public Dictionary<int, string> A; }
    [NinoType] public struct Pair { public int Id; public string Name; }
    [NinoType] public sealed class Big { public string A, B, C; }

    public static class Api
    {
        static string Hex(byte[] b) => Convert.ToHexString(b);

        static string Check<T, TTop, TFormatter>(T value)
            where TTop : struct, INinoFormatter<T> where TFormatter : struct, INinoFormatter<T>
        {
            var regular = NinoDirect.Serialize<T, TFormatter>(value);
            var top = NinoDirect.Serialize<T, TTop>(value);
            var buffer = new NinoArrayBufferWriter(16);
            NinoDirect.Serialize<T, TTop>(value, buffer);
            var again = NinoDirect.Serialize<T, TFormatter>(NinoDirect.Deserialize<T, TTop>(top));
            T reused = NinoDirect.Deserialize<T, TFormatter>(regular);
            NinoDirect.Deserialize<T, TTop>(top, ref reused);
            var reread = NinoDirect.Serialize<T, TFormatter>(reused);
            return Hex(regular) == Hex(top) && Hex(top) == Hex(buffer.WrittenSpan.ToArray()) && Hex(again) == Hex(top) &&
                   Hex(reread) == Hex(top)
                ? ""ok""
                : typeof(T).Name + "": "" + Hex(regular) + "" / "" + Hex(top) + "" / "" + Hex(again) + "" / "" + Hex(reread);
        }

        public static string Run()
        {
            var results = new[]
            {
                Check<Ints, NinoGen.Top_IntsFormatter.__Top, NinoGen.Top_IntsFormatter>(new Ints { Id = 7, A = new[] { 1, 2, 3 } }),
                Check<Ints, NinoGen.Top_IntsFormatter.__Top, NinoGen.Top_IntsFormatter>(new Ints { A = new int[0] }),
                Check<Ints, NinoGen.Top_IntsFormatter.__Top, NinoGen.Top_IntsFormatter>(new Ints()),
                Check<Ints, NinoGen.Top_IntsFormatter.__Top, NinoGen.Top_IntsFormatter>(null),
                Check<Text, NinoGen.Top_TextFormatter.__Top, NinoGen.Top_TextFormatter>(new Text { A = ""héllo"", B = ""wörld 你好"" }),
                Check<Text, NinoGen.Top_TextFormatter.__Top, NinoGen.Top_TextFormatter>(new Text { A = """", B = null }),
                Check<Text, NinoGen.Top_TextFormatter.__Top, NinoGen.Top_TextFormatter>(new Text { B = new string('é', 3000) }),
                Check<Dates, NinoGen.Top_DatesFormatter.__Top, NinoGen.Top_DatesFormatter>(new Dates { A = new List<DateTime> { new DateTime(2020, 1, 2) } }),
                Check<Map, NinoGen.Top_MapFormatter.__Top, NinoGen.Top_MapFormatter>(new Map { A = new Dictionary<int, string> { [1] = ""a"", [2] = null } }),
                Check<Big, NinoGen.Top_BigFormatter.__Top, NinoGen.Top_BigFormatter>(new Big { A = ""a"", C = ""ç"" }),
                Check<Big, NinoGen.Top_BigFormatter.__Top, NinoGen.Top_BigFormatter>(null),
                // Top-level collections: the interceptors use the __Top twins of their elements' formatters.
                Check<Text[], ArrayFormatter<Text, NinoGen.Top_TextFormatter.__Top>, ArrayFormatter<Text, NinoGen.Top_TextFormatter>>(
                    new[] { new Text { A = ""a"", B = ""é"" }, null, new Text() }),
                Check<Text[], ArrayFormatter<Text, NinoGen.Top_TextFormatter.__Top>, ArrayFormatter<Text, NinoGen.Top_TextFormatter>>(null),
                Check<List<Ints>, ListFormatter<List<Ints>, Ints, NinoGen.Top_IntsFormatter.__Top>, ListFormatter<List<Ints>, Ints, NinoGen.Top_IntsFormatter>>(
                    Enumerable.Range(0, 50).Select(i => i % 7 == 0 ? null : new Ints { Id = i, A = new[] { i } }).ToList()),
                Check<Dictionary<int, Map>, DictionaryFormatter<int, Map, UnmanagedFormatter<int>, NinoGen.Top_MapFormatter.__Top>,
                    DictionaryFormatter<int, Map, UnmanagedFormatter<int>, NinoGen.Top_MapFormatter>>(
                    new Dictionary<int, Map> { [1] = new Map { A = new Dictionary<int, string> { [3] = ""c"" } }, [2] = null }),
            };
            return string.Join(""; "", results.Where(r => r != ""ok""));
        }
    }
}";

    private static Assembly Compile()
    {
        var compilation = TestCompilation.Create("Top", new[] { Source });
        var run = TestCompilation.Generate(compilation);
        var sources = run.GeneratedTrees.ToDictionary(t => Path.GetFileName(t.FilePath), t => t.ToString());
        // Sealed classes only. Small bodies are inlined into the twin; Big has three formatter members, so its twin
        // calls the regular body (skipping only the depth check).
        StringAssert.Contains(sources["NinoGen.Top_IntsFormatter.g.cs"], "struct __Top");
        StringAssert.Contains(sources["NinoGen.Top_IntsFormatter.g.cs"], "=> __WriteBodyInline(value, ref writer);");
        StringAssert.Contains(sources["NinoGen.Top_BigFormatter.g.cs"], "struct __Top");
        StringAssert.Contains(sources["NinoGen.Top_BigFormatter.g.cs"],
            "=> default(Top_BigFormatter).WriteBody(value, ref writer);");
        Assert.IsFalse(sources["NinoGen.Top_PairFormatter.g.cs"].Contains("__Top"));

        return TestCompilation.EmitAndLoad(compilation.AddSyntaxTrees(run.GeneratedTrees));
    }

    [TestMethod]
    public void TopFormatterWritesAndReadsTheSameBytes()
    {
        var api = Compile().GetType("Top.Api")!;
        Assert.AreEqual("", api.GetMethod("Run")!.Invoke(null, null));
    }

    [TestMethod]
    public void TopLevelCallsOfThisAssemblyAreIntercepted()
    {
        // The interceptors name their (file-local) [InterceptsLocation] through an alias: the calls of this assembly
        // must still be redirected to them, which shows as calls in the IL.
        var assembly = typeof(TopLevelInlineTests).Assembly;
        var interceptors = assembly.GetTypes().FirstOrDefault(t => t.Name.EndsWith("NinoInterceptors"));
        if (interceptors == null)
            return; // built with -p:NinoRegularCalls=true
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                 BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int calls = 0;
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(all))
            {
                var il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null)
                    continue;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    // call <MethodDef token>
                    if (il[i] != 0x28 || il[i + 4] != 0x06)
                        continue;
                    try
                    {
                        if (method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1))?.DeclaringType == interceptors)
                            calls++;
                    }
                    catch (ArgumentException)
                    {
                        // not an instruction boundary
                    }
                }
            }
        }

        Assert.IsTrue(calls > 100, $"{calls} intercepted calls");
    }
}
