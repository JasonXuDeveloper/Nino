using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// Types whose formatter names differ only by case: their generated files need hint names that differ ignoring
    /// case, or the generator adds no file at all (CS8785) and every type of the project is resolved at run time.
    /// </summary>
    [TestClass]
    public class CaseClashTests
    {
        [TestMethod]
        public void BothTypesRoundTrip()
        {
            var result = CornerCompiler.Compile(@"
using Nino.Core;
namespace Clash
{
    [NinoType] public struct RawS { public int A; }
    [NinoType] public sealed class Raws { public string B; }
    public static class Run
    {
        public static bool Check()
        {
            var s = NinoDeserializer.Deserialize<RawS>(NinoSerializer.Serialize(new RawS { A = 7 }));
            var c = NinoDeserializer.Deserialize<Raws>(NinoSerializer.Serialize(new Raws { B = ""b"" }));
            return s.A == 7 && c.B == ""b"";
        }
    }
}");
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            StringAssert.Contains(result.GeneratedCode, "struct Clash_RawSFormatter");
            StringAssert.Contains(result.GeneratedCode, "struct Clash_RawsFormatter");
            Assert.IsTrue((bool)result.Assembly.GetType("Clash.Run")!.GetMethod("Check")!.Invoke(null, null)!);
        }
    }
}
