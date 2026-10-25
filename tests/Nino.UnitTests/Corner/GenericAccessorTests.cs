#if NET8_0_OR_GREATER
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests
{
    /// <summary>
    /// Generic types with non-public members: on .NET 9+ (the compilation defines <c>NET9_0_OR_GREATER</c>) generated
    /// code reaches them through generic <c>[UnsafeAccessor]</c> externs in the generic formatter, so the types need not
    /// be partial; before (.NET 8 and older, Unity) through an accessor class inside the type, which must be partial
    /// (NINO002). Each shape is compiled with the symbols of both and round-tripped where the runtime can run it.
    /// </summary>
    [TestClass]
    public class GenericAccessorTests
    {
        private static readonly string[] Net8 = { "NET8_0_OR_GREATER" };
        private static readonly string[] Net9 = { "NET8_0_OR_GREATER", "NET9_0_OR_GREATER" };

        // Every shape whose members the accessor class or [UnsafeAccessor] reaches; PARTIAL is "partial " or "".
        private const string Shapes = @"
using System.Collections.Generic;
using Nino.Core;

namespace NS
{
    [NinoType(containNonPublicMembers: true)]
    public PARTIALclass Box<T>
    {
        private T _value;
        private int _count;
        private T Prop { get; set; }
        public void Set(T v, int c, T p) { _value = v; _count = c; Prop = p; }
        public string Show() => _value + ""/"" + _count + ""/"" + Prop;
    }

    [NinoType(containNonPublicMembers: true)]
    public PARTIALstruct SBox<T>
    {
        private T _value;
        public void Set(T v) => _value = v;
        public string Show() => """" + _value;
    }

    [NinoType(containNonPublicMembers: true)]
    public PARTIALclass Constrained<T> where T : class, new()
    {
        private T _value;
        public void Set(T v) => _value = v;
        public T Get() => _value;
    }

    public PARTIALclass Outer<T>
    {
        [NinoType(containNonPublicMembers: true)]
        public PARTIALclass Inner<U>
        {
            private T _t;
            private U _u;
            public void Set(T t, U u) { _t = t; _u = u; }
            public string Show() => _t + ""/"" + _u;
        }

        [NinoType(containNonPublicMembers: true)]
        public PARTIALclass Plain
        {
            private List<T> _items;
            public void Set(List<T> items) => _items = items;
            public string Show() => string.Join("","", _items);
        }
    }

    [NinoType(format: NinoFormat.Keyed, containNonPublicMembers: true)]
    public PARTIALclass Keyed<T>
    {
        private T _value;
        public void Set(T v) => _value = v;
        public string Show() => """" + _value;
    }

    [NinoType(format: NinoFormat.VersionTolerant, containNonPublicMembers: true)]
    public PARTIALclass Tolerant<T>
    {
        private T _value;
        public void Set(T v) => _value = v;
        public string Show() => """" + _value;
    }

    // Members declared in a generic base: reached through the base, whose type arguments are the subtype's first
    // type parameters.
    [NinoType(containNonPublicMembers: true)]
    public PARTIALclass GBase<T>
    {
        protected T Value;
        public void SetBase(T v) => Value = v;
        public T GetBase() => Value;
    }

    [NinoType(containNonPublicMembers: true)]
    public sealed PARTIALclass GDerived<T> : GBase<T>
    {
        private int _own;
        public void SetOwn(int v) => _own = v;
        public int GetOwn() => _own;
    }

    [NinoType(containNonPublicMembers: true)]
    public sealed PARTIALclass GTwo<T, U> : GBase<T>
    {
        private U _u;
        public void SetU(U v) => _u = v;
        public U GetU() => _u;
    }

    // A base member hidden by `new`: only an accessor naming the base reaches it (NINO022 otherwise).
    [NinoType(containNonPublicMembers: true)]
    public sealed PARTIALclass GHiding<T> : GBase<T>
    {
        public new string Value;
    }

    // Read through a polymorphic generic base.
    [NinoType]
    public abstract class PBase<T>
    {
        public T A;
    }

    [NinoType(containNonPublicMembers: true)]
    public sealed PARTIALclass PLeaf<T> : PBase<T>
    {
        private int _w;
        public void SetW(int w) => _w = w;
        public int GetW() => _w;
    }

    public static class Api
    {
        static T Rt<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

        public static string Run()
        {
            var box = new Box<string>();
            box.Set(""v"", 3, ""p"");
            var sbox = new SBox<long>();
            sbox.Set(4);
            var con = new Constrained<List<int>>();
            con.Set(new List<int> { 5 });
            var inner = new Outer<int>.Inner<string>();
            inner.Set(6, ""u"");
            var plain = new Outer<string>.Plain();
            plain.Set(new List<string> { ""a"", ""b"" });
            var keyed = new Keyed<int>();
            keyed.Set(7);
            var tolerant = new Tolerant<string>();
            tolerant.Set(""t"");
            var derived = new GDerived<string>();
            derived.SetBase(""b"");
            derived.SetOwn(8);
            var two = new GTwo<int, string>();
            two.SetBase(9);
            two.SetU(""two"");
            var hiding = new GHiding<int> { Value = ""h"" };
            hiding.SetBase(10);
            var leaf = new PLeaf<int> { A = 11 };
            leaf.SetW(12);
            var asBase = (PLeaf<int>)Rt<PBase<int>>(leaf);

            return string.Join("" "", Rt(box).Show(), Rt(sbox).Show(), Rt(con).Get()[0], Rt(inner).Show(),
                Rt(plain).Show(), Rt(keyed).Show(), Rt(tolerant).Show(), Rt(derived).GetBase(), Rt(derived).GetOwn(),
                Rt(two).GetBase(), Rt(two).GetU(), Rt(hiding).Value, Rt(hiding).GetBase(), asBase.A, asBase.GetW());
        }
    }
}";

        private const string Expected = "v/3/p 4 5 6/u a,b 7 t b 8 9 two h 10 11 12";

        // Generic types that declare (or inherit) the non-public members above, as NINO002 names them.
        private static readonly string[] NeedPartial =
        {
            "G.Box<T>", "G.SBox<T>", "G.Constrained<T>", "G.Outer<T>.Inner<U>", "G.Outer<T>.Plain", "G.Keyed<T>",
            "G.Tolerant<T>", "G.GBase<T>", "G.GDerived<T>", "G.GTwo<T, U>", "G.PLeaf<T>",
        };

        private static int s_compilations;

        /// <summary>The source in a namespace of its own: one full name loaded twice in a process is an id collision.</summary>
        private static string Unique(string source, out string ns)
        {
            ns = "G" + System.Threading.Interlocked.Increment(ref s_compilations);
            return source.Replace("namespace NS\n", $"namespace {ns}\n");
        }

        private static string Source(bool partial, out string ns) =>
            Unique(Shapes.Replace("PARTIAL", partial ? "partial " : ""), out ns);

        private static object Run(CornerCompiler.Result compiled, string ns) =>
            compiled.Assembly.GetType(ns + ".Api")!.GetMethod("Run")!.Invoke(null, null);

        // The types NINO002 names, in namespace G.
        private static string[] NotPartial(CornerCompiler.Result compiled, string ns) => compiled.AnalyzerDiagnostics
            .Where(d => d.Id == "NINO002").Select(d => "G" + d.GetMessage().Split('\'')[1].Substring(ns.Length))
            .OrderBy(x => x, System.StringComparer.Ordinal).ToArray();

        [TestMethod]
        public void BeforeNet9NonPartialGenericTypesNeedPartial()
        {
            var compiled = CornerCompiler.Compile(Source(partial: false, out string ns), load: false, preprocessorSymbols: Net8);
            CollectionAssert.AreEqual(NeedPartial.OrderBy(x => x, System.StringComparer.Ordinal).ToArray(), NotPartial(compiled, ns));
        }

        [TestMethod]
        public void BeforeNet9PartialGenericTypesUseTheAccessorClass()
        {
            var compiled = CornerCompiler.Compile(Source(partial: true, out string ns), preprocessorSymbols: Net8);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            Assert.AreEqual(0, NotPartial(compiled, ns).Length);
            Assert.IsTrue(compiled.GeneratedCode.Contains($"__NinoAccess_{ns}_Box_1Formatter"));
            Assert.IsFalse(compiled.GeneratedCode.Contains("UnsafeAccessor("));
            // The base member hidden by `new` stays out of reach: skipped (NINO022), read as its default.
            Assert.IsTrue(compiled.HasAnalyzer("NINO022"), string.Join("\n", compiled.AnalyzerDiagnostics));
            Assert.AreEqual(Expected.Replace(" h 10 ", " h 0 "), Run(compiled, ns));
        }

#if NET9_0_OR_GREATER
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void Net9GenericTypesUseUnsafeAccessor(bool partial)
        {
            var compiled = CornerCompiler.Compile(Source(partial, out string ns), preprocessorSymbols: Net9);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            Assert.AreEqual(0, NotPartial(compiled, ns).Length, string.Join("\n", compiled.AnalyzerDiagnostics));
            Assert.IsFalse(compiled.HasAnalyzer("NINO022"), string.Join("\n", compiled.AnalyzerDiagnostics));
            Assert.IsFalse(compiled.GeneratedCode.Contains("__NinoAccess_"), "no accessor class");
            Assert.IsTrue(compiled.GeneratedCode.Contains("UnsafeAccessorKind.Field, Name = \"_value\")]"));
            Assert.AreEqual(Expected, Run(compiled, ns));
        }

        [TestMethod]
        public void Net9BaseOfOtherTypeArgumentsNeedsPartial()
        {
            // The runtime matches an accessor's signature against the member's own: a member of GBase<List<T>> or
            // GBase<int> is not reachable from a formatter over <T>, and keeps the accessor class.
            const string template = @"
using System.Collections.Generic;
using Nino.Core;

namespace NS
{
    [NinoType(containNonPublicMembers: true)]
    public class GBase<T>
    {
        protected T Value;
        public void SetBase(T v) => Value = v;
        public T GetBase() => Value;
    }

    [NinoType(containNonPublicMembers: true)] public sealed class OfList<T> : GBase<List<T>> { }
    [NinoType(containNonPublicMembers: true)] public sealed class OfInt<T> : GBase<int> { }
    [NinoType(containNonPublicMembers: true)] public sealed class Second<T, U> : GBase<U> { }
    [NinoType(containNonPublicMembers: true)] public sealed partial class Partial<T> : GBase<List<T>> { }

    public static class Api
    {
        static T Rt<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

        public static string Run()
        {
            var p = new Partial<int>();
            p.SetBase(new List<int> { 1, 2 });
            return string.Join("","", Rt(p).GetBase());
        }
    }
}";
            var compiled = CornerCompiler.Compile(Unique(template, out string ns), load: false, preprocessorSymbols: Net9);
            CollectionAssert.AreEqual(new[] { "G.OfInt<T>", "G.OfList<T>", "G.Second<T, U>" }, NotPartial(compiled, ns));
            var partial = CornerCompiler.Compile(
                Unique(template.Replace("public sealed class", "public sealed partial class"), out ns),
                preprocessorSymbols: Net9);
            Assert.AreEqual(0, partial.Errors.Length, partial.ErrorText);
            Assert.AreEqual("1,2", Run(partial, ns));
        }
#endif
    }
}
#endif
