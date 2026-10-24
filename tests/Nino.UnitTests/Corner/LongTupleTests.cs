using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    [NinoType]
    public sealed class LongTuples
    {
        public (int, int, int, int, int, int, int, int, int) Nine;
        public (byte, string, long, int, int, int, int, string, int, int, int, int, int, int, LongTupleItem) Fifteen;
        public List<(int, int, int, int, int, int, int, string)> Eights;
    }

    [NinoType]
    public sealed class LongTupleItem
    {
        public string Name;
    }

    /// <summary>
    /// Tuples of eight items or more are a <c>ValueTuple</c> whose eighth argument is a tuple of the rest. Nino.Core's
    /// formatter of eight type arguments writes every item in order, as for the shorter ones (they were skipped, with a
    /// NINO007 warning), in generated code and where Nino.Core resolves the formatter at run time.
    /// </summary>
    [TestClass]
    public class LongTupleTests
    {
        [TestMethod]
        public void RoundTrips()
        {
            var value = new LongTuples
            {
                Nine = (1, 2, 3, 4, 5, 6, 7, 8, 9),
                Fifteen = (1, "two", 3, 4, 5, 6, 7, "eight", 9, 10, 11, 12, 13, 14, new LongTupleItem { Name = "fifteen" }),
                Eights = new List<(int, int, int, int, int, int, int, string)> { (1, 2, 3, 4, 5, 6, 7, "a"), default },
            };
            var copy = NinoDeserializer.Deserialize<LongTuples>(NinoSerializer.Serialize(value));
            Assert.AreEqual(value.Nine, copy.Nine);
            Assert.AreEqual(value.Fifteen.Item14, copy.Fifteen.Item14);
            Assert.AreEqual("eight", copy.Fifteen.Item8);
            Assert.AreEqual("fifteen", copy.Fifteen.Item15.Name);
            Assert.AreEqual(value.Fifteen with { Item15 = null }, copy.Fifteen with { Item15 = null });
            CollectionAssert.AreEqual(value.Eights, copy.Eights);
        }

        [TestMethod]
        public void WritesEveryItemInOrder()
        {
            var tuple = new LongTuples { Nine = (1, 2, 3, 4, 5, 6, 7, 8, 9) };
            var bytes = NinoSerializer.Serialize(tuple.Nine);
            // The first seven items as Nino.Core's tuple of seven writes them, then the rest as its tuple of two.
            CollectionAssert.AreEqual(NinoSerializer.Serialize((1, 2, 3, 4, 5, 6, 7))
                .Concat(NinoSerializer.Serialize((8, 9))).ToArray(), bytes);
            Assert.AreEqual(tuple.Nine, NinoDeserializer.Deserialize<(int, int, int, int, int, int, int, int, int)>(bytes));
        }

        [TestMethod]
        public void PublishesEveryItem()
        {
            var layout = typeof(LongTuples).Assembly.GetCustomAttributes<NinoTypeLayoutAttribute>()
                .Single(a => a.Type == typeof(LongTuples));
            Assert.AreEqual(string.Join(";",
                "Nine:System.ValueTuple<int, int, int, int, int, int, int, System.ValueTuple<int, int>>:" +
                "tuple(raw(4),raw(4),raw(4),raw(4),raw(4),raw(4),raw(4),raw(4),raw(4))",
                "Fifteen:System.ValueTuple<byte, string, long, int, int, int, int, System.ValueTuple<string, int, int, " +
                "int, int, int, int, System.ValueTuple<Nino.UnitTests.Corner.LongTupleItem>>>:" +
                "tuple(raw(1),string,raw(8),raw(4),raw(4),raw(4),raw(4),string,raw(4),raw(4),raw(4),raw(4),raw(4)," +
                "raw(4),type(Nino.UnitTests.Corner.LongTupleItem))",
                "Eights:System.Collections.Generic.List<System.ValueTuple<int, int, int, int, int, int, int, " +
                "System.ValueTuple<string>>>:seq(tuple(raw(4),raw(4),raw(4),raw(4),raw(4),raw(4),raw(4),string))"),
                layout.Members);
        }

        [TestMethod]
        public void ResolvesAtRunTime()
        {
            // Box<T> of a tuple the generator never sees closed: Nino.Core resolves its formatter (it had none).
            var result = CornerCompiler.Compile(@"
using Nino.Core;
namespace LongTuple
{
    [NinoType] public sealed class Box<T> { public T V; }
    public static class Api
    {
        private static byte[] Write<T>(T v) => NinoSerializer.Serialize(new Box<T> { V = v });
        private static T Read<T>(byte[] b) => NinoDeserializer.Deserialize<Box<T>>(b).V;
        public static byte[] Run() => Write((1, 2, 3, 4, 5, 6, 7, ""eight"", 9));
        public static bool RoundTrips(byte[] b) => Read<(int, int, int, int, int, int, int, string, int)>(b) == (1, 2, 3, 4, 5, 6, 7, ""eight"", 9);
    }
}");
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            var api = result.Assembly.GetType("LongTuple.Api", true);
            var bytes = (byte[])api.GetMethod("Run")!.Invoke(null, null);
            // The null flag of the box, then the items as generated code writes them.
            CollectionAssert.AreEqual(new byte[] { 1 }.Concat(NinoSerializer.Serialize((1, 2, 3, 4, 5, 6, 7, "eight", 9)))
                .ToArray(), bytes);
            Assert.IsTrue((bool)api.GetMethod("RoundTrips")!.Invoke(null, new object[] { bytes }));
        }

        [TestMethod]
        public void IsNoLongerSkipped()
        {
            var result = CornerCompiler.Compile(@"
using Nino.Core;
namespace LongTuple { [NinoType] public sealed class Nine { public (int, int, int, int, int, int, int, int, int) T; } }");
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            Assert.IsFalse(result.HasAnalyzer("NINO007"), string.Join("\n", result.AnalyzerDiagnostics));
        }
    }
}
