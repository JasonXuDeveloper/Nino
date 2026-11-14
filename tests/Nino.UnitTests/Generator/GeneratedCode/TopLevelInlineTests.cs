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
/// calls use: no depth check, and a small body is inlined into the caller. Positional types: they are compiled here
/// (this assembly's types are version tolerant, and no such type has one), and their bytes and reads through
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
using Nino.Core.CompilerServices;
namespace Top
{
    [NinoType] public sealed class Ints { public int Id; public int[] A; }
    [NinoType] public sealed class Text { public string A; [NinoUtf8] public string B; }
    [NinoType] public sealed class Dates { public List<DateTime> A; }
    [NinoType] public sealed class Map { public Dictionary<int, string> A; }
    [NinoType] public struct Pair { public int Id; public string Name; }
    [NinoType] public sealed class Big { public string A, B, C; }
    [NinoType] public struct Wide { public string A, B, C; }
    [NinoType] public struct Holder { public int Id; public Text T; }
    [NinoType] public sealed class Nullables { public int? A; public long? B; public Guid? C; public decimal? D; public string E; }

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

        // The twin's own top-level Write and Read (what NinoSerializer and NinoDeserializer reach), a null value
        // included: the presence byte alone, written and read without a writer or reader of its own.
        static string CheckTop<T, TTop, TFormatter>(T value)
            where TTop : struct, INinoTopFormatter<T> where TFormatter : struct, INinoFormatter<T>
        {
            var expected = NinoDirect.Serialize<T, TFormatter>(value);
            var buffer = new NinoArrayBufferWriter(1);
            buffer.GetSpan(1)[0] = 0xEE; // a byte before the payload, so a null is written after it
            buffer.Advance(1);
            default(TTop).Write(value, buffer);
            var written = buffer.WrittenSpan.Slice(1).ToArray();
            var read = default(TTop).Read(written);
            return Hex(written) == Hex(expected) && Hex(NinoDirect.Serialize<T, TFormatter>(read)) == Hex(expected)
                ? ""ok""
                : typeof(T).Name + "" top: "" + Hex(expected) + "" / "" + Hex(written);
        }

        static string Throws(Func<object> read)
        {
            try { read(); return ""no exception""; }
            catch (NinoDataException) { return ""ok""; }
        }

        // A struct that is not raw, as a value and as T?: alone, in an array with nulls, and read into an existing
        // array (values kept where the data has them, dropped where it has null).
        static string CheckStruct<T, TFormatter>(T a, T b) where T : struct where TFormatter : struct, INinoFormatter<T>
        {
            byte[] body = NinoDirect.Serialize<T, TFormatter>(a), other = NinoDirect.Serialize<T, TFormatter>(b);
            var one = NinoDirect.Serialize<T?, NullableFormatter<T, TFormatter>>(a);
            var none = NinoDirect.Serialize<T?, NullableFormatter<T, TFormatter>>(null);
            var array = new T?[] { a, null, b };
            var many = NinoDirect.Serialize<T?[], ArrayFormatter<T?, NullableFormatter<T, TFormatter>>>(array);
            var expected = new byte[] { 3, 0, 0, 0, 1 }.Concat(body).Append((byte)0).Append((byte)1).Concat(other).ToArray();
            var read = NinoDirect.Deserialize<T?[], ArrayFormatter<T?, NullableFormatter<T, TFormatter>>>(many);
            var reused = new T?[] { null, b, a };
            NinoDirect.Deserialize<T?[], ArrayFormatter<T?, NullableFormatter<T, TFormatter>>>(many, ref reused);
            T? single = b;
            NinoDirect.Deserialize<T?, NullableFormatter<T, TFormatter>>(one, ref single);
            T? gone = a;
            NinoDirect.Deserialize<T?, NullableFormatter<T, TFormatter>>(none, ref gone);
            return Hex(one) == ""01"" + Hex(body) && Hex(none) == ""00"" && Hex(many) == Hex(expected) &&
                   Hex(NinoDirect.Serialize<T?[], ArrayFormatter<T?, NullableFormatter<T, TFormatter>>>(read)) == Hex(expected) &&
                   Hex(NinoDirect.Serialize<T?[], ArrayFormatter<T?, NullableFormatter<T, TFormatter>>>(reused)) == Hex(expected) &&
                   !reused[1].HasValue && !gone.HasValue &&
                   Hex(NinoDirect.Serialize<T, TFormatter>(single.Value)) == Hex(body)
                ? ""ok""
                : typeof(T).Name + ""?: "" + Hex(one) + "" / "" + Hex(many) + "" / "" + Hex(expected);
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
                Check<Nullables, NinoGen.Top_NullablesFormatter.__Top, NinoGen.Top_NullablesFormatter>(
                    new Nullables { A = 1, C = Guid.Empty, D = 2.5m, E = ""e"" }),
                Check<Nullables, NinoGen.Top_NullablesFormatter.__Top, NinoGen.Top_NullablesFormatter>(new Nullables()),
                Check<Nullables, NinoGen.Top_NullablesFormatter.__Top, NinoGen.Top_NullablesFormatter>(null),
                CheckTop<Nullables, NinoGen.Top_NullablesFormatter.__Top, NinoGen.Top_NullablesFormatter>(
                    new Nullables { B = -3, D = 1m, E = ""é"" }),
                CheckTop<Nullables, NinoGen.Top_NullablesFormatter.__Top, NinoGen.Top_NullablesFormatter>(null),
                CheckTop<Text, NinoGen.Top_TextFormatter.__Top, NinoGen.Top_TextFormatter>(null),
                Throws(() => default(NinoGen.Top_NullablesFormatter.__Top).Read(new byte[] { 2 })),
                Throws(() => default(NinoGen.Top_NullablesFormatter.__Top).Read(Array.Empty<byte>())),
                CheckStruct<Pair, NinoGen.Top_PairFormatter>(new Pair { Id = 7, Name = ""é"" }, new Pair { Id = -1 }),
                CheckStruct<Wide, NinoGen.Top_WideFormatter>(new Wide { A = ""a"", C = new string('ç', 300) }, default),
                CheckStruct<Holder, NinoGen.Top_HolderFormatter>(new Holder { Id = 3, T = new Text { A = ""a"" } }, new Holder { Id = 4 }),
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
        // A struct has no twin, but a body that opens no class body is a class's: inlined where it is small (Pair, into
        // the loop over its array or its nullable's), else out of line on a local copy of the reader and the writer
        // (Wide). One holding a class (Holder) passes the reader and writer on, and stays as it was.
        string pair = sources["NinoGen.Top_PairFormatter.g.cs"];
        StringAssert.Contains(pair, "public int Traits => 0;");
        StringAssert.Contains(pair, "=> __WriteBodyInline(ref value, ref writer);");
        StringAssert.Contains(pair, "=> __ReadBodyInline(ref reader, ref value);");
        Assert.IsFalse(pair.Contains("NoInline"), pair);
        string wide = sources["NinoGen.Top_WideFormatter.g.cs"];
        StringAssert.Contains(wide, "writer.CopyTo(ref __w);");
        StringAssert.Contains(wide, "reader.CopyTo(ref __r);");
        string holder = sources["NinoGen.Top_HolderFormatter.g.cs"];
        StringAssert.Contains(holder, "public int Traits => 1;");
        Assert.IsFalse(holder.Contains("CopyTo") || holder.Contains("Inline("), holder);
        // Nullable raw members write through the writer inline (Flat): the body runs on a local copy of the writer.
        // A null value is handled in the twin's inlined Write and Read, the rest out of line.
        string nullables = sources["NinoGen.Top_NullablesFormatter.g.cs"];
        StringAssert.Contains(nullables, "writer.CopyTo(ref __w);");
        StringAssert.Contains(nullables, "{ if (value == null) __CS.NinoDirect.WriteNull(buffer); else __WriteTop(value, buffer); }");
        StringAssert.Contains(nullables, "=> !data.IsEmpty && data[0] == 0 ? null : __ReadTop(data);");
        StringAssert.Contains(nullables, "private static __T __ReadTop(global::System.ReadOnlySpan<byte> data)");

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
