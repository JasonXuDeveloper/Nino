#if NET8_0_OR_GREATER
using System;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests
{
    [NinoType]
    public sealed class BclStructs
    {
        public Half H;
        public Rune R;
        public DateOnly D;
        public TimeOnly T;
        public Index I;
        public Range Span;
        public Complex C;
        public Int128 Big;
        public int?[] Maybe;
        public TimeAfterInt Late;
        public WideAfterLong Wide;
    }

    public struct TimeAfterInt
    {
        public int A;
        public TimeOnly T;
    }

    public struct WideAfterLong
    {
        public long A;
        public Int128 B;
    }

    /// <summary>
    /// BCL structs whose fields are private: a project compiles against reference assemblies, which replace those
    /// fields with placeholders (<c>int _dummyPrimitive</c>), so their published layouts and whether they are laid out
    /// the same on every runtime come from what the generator knows of them, not from the fields it sees. This test
    /// project is compiled against reference assemblies; <see cref="CornerCompiler"/> compiles against the runtime's
    /// own assemblies, which show the real fields: both publish the same layouts.
    /// </summary>
    [TestClass]
    public class BclLayoutTests
    {
        private const string Source = @"
using System;
using System.Numerics;
using System.Text;
using Nino.Core;

namespace BclCorner
{
    [NinoType]
    public sealed class BclStructs
    {
        public Half H; public Rune R; public DateOnly D; public TimeOnly T; public Index I; public Range Span;
        public Complex C; public Int128 Big; public int?[] Maybe; public TimeAfterInt Late; public WideAfterLong Wide;
    }

    public struct TimeAfterInt { public int A; public TimeOnly T; }
    public struct WideAfterLong { public long A; public Int128 B; }
}";

        private static readonly string Expected = string.Join(";",
            "H:System.Half:raw(2)",
            "R:System.Text.Rune:raw(4)",
            "D:System.DateOnly:raw(4)",
            "T:System.TimeOnly:raw(8)",
            "I:System.Index:raw(4)",
            "Span:System.Range:raw(8){Start:System.Index@0:raw(4),End:System.Index@4:raw(4)}",
            "C:System.Numerics.Complex:raw(16){m_real:double@0:raw(8),m_imaginary:double@8:raw(8)}",
            "Big:System.Int128:raw(16){_lower:ulong@0:raw(8),_upper:ulong@8:raw(8)}",
            "Maybe:System.Nullable<int>[]:seq(raw(8){hasValue:bool@0:raw(1),value:int@4:raw(4)})",
            // Not laid out the same on x86 (TimeOnly at 4) and 64-bit runtimes: written through its formatter, in the
            // canonical layout.
            "Late:Nino.UnitTests.TimeAfterInt:raw(16){A:int@0:raw(4),T:System.TimeOnly@8:raw(8)}",
            // Int128 is 16-byte aligned on .NET 8 and later, 8-byte aligned before: no layout known at compile time.
            "Wide:Nino.UnitTests.WideAfterLong:raw");

        private static string Members(Assembly assembly, string ns) =>
            assembly.GetCustomAttributes<NinoTypeLayoutAttribute>()
                .Single(a => a.Type == assembly.GetType(ns + ".BclStructs")).Members;

        [TestMethod]
        public void ReferenceAssembliesPublishTheRealLayouts() =>
            Assert.AreEqual(Expected, Members(typeof(BclStructs).Assembly, "Nino.UnitTests"));

        [TestMethod]
        public void ImplementationAssembliesPublishTheSameLayouts()
        {
            var result = CornerCompiler.Compile(Source);
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            Assert.AreEqual(Expected.Replace("Nino.UnitTests.", "BclCorner."),
                Members(result.Assembly, "BclCorner"));
        }

        [TestMethod]
        public void OnlyStructsLaidOutTheSameEverywhereAreCopiedRaw()
        {
            var generated = CornerCompiler.Compile(Source).GeneratedCode;
            // Members of portable layouts join the raw block with constant sizes; the others go through their
            // formatter, which writes the canonical layout.
            StringAssert.Contains(generated, "UnmanagedFormatter<global::BclCorner.TimeAfterInt>");
            StringAssert.Contains(generated, "UnmanagedFormatter<global::BclCorner.WideAfterLong>");
            Assert.IsFalse(generated.Contains("SizeOf<global::System.Half>"), generated);
        }

        [TestMethod]
        public void RoundTrips()
        {
            var value = new BclStructs
            {
                H = (Half)1.5f, R = new Rune(0x1F600), D = new DateOnly(2024, 2, 29), T = new TimeOnly(23, 59, 58),
                I = ^3, Span = 1..^2, C = new Complex(1.25, -2.5), Big = Int128.MaxValue - 7,
                Maybe = new int?[] { 1, null, -3 }, Late = new TimeAfterInt { A = 9, T = new TimeOnly(1, 2, 3) },
                Wide = new WideAfterLong { A = -4, B = Int128.MinValue + 5 },
            };
            var copy = NinoDeserializer.Deserialize<BclStructs>(NinoSerializer.Serialize(value));
            Assert.AreEqual(value.H, copy.H);
            Assert.AreEqual(value.R, copy.R);
            Assert.AreEqual(value.D, copy.D);
            Assert.AreEqual(value.T, copy.T);
            Assert.AreEqual(value.I, copy.I);
            Assert.AreEqual(value.Span, copy.Span);
            Assert.AreEqual(value.C, copy.C);
            Assert.AreEqual(value.Big, copy.Big);
            CollectionAssert.AreEqual(value.Maybe, copy.Maybe);
            Assert.AreEqual(value.Late, copy.Late);
            Assert.AreEqual(value.Wide, copy.Wide);
        }
    }
}
#endif
