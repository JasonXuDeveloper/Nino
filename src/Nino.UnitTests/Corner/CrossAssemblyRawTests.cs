using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// A [NinoType] struct of another assembly reads the same whether that assembly is referenced as source (the IDE,
    /// a project reference) or as metadata (a package, the command line), where Roslyn links no auto-property to its
    /// backing field (issue #200). Otherwise an outer struct was a raw block in one build and member by member in the
    /// other, and the two wrote different bytes.
    /// </summary>
    [TestClass]
    public class CrossAssemblyRawTests
    {
        private const string Contracts = @"
using Nino.Core;

namespace Contracts
{
    [NinoType] public readonly partial record struct ClientSession(ulong Node, long Id);
    [NinoType] public struct Position { public Position(int x) { X = x; } public int X { get; } }
}";

        // No usings: it is also appended to Contracts as one source.
        private const string Messages = @"
namespace Messages
{
    [Nino.Core.NinoType]
    public partial struct Attach { public byte Flags; public Contracts.ClientSession Session; public Contracts.Position At; }
}";

        private static string LayoutOf(CornerCompiler.Result result, string type)
        {
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            return result.GeneratedCode.Split('\n').Single(l => l.Contains("NinoTypeLayout(typeof(global::" + type + ")"));
        }

        [TestMethod]
        public void MetadataReferenceReadsLikeSource()
        {
            var source = LayoutOf(CornerCompiler.Compile(Contracts + Messages, load: false), "Messages.Attach");
            var contracts = CornerCompiler.Compile(Contracts, load: false);
            Assert.AreEqual(0, contracts.Errors.Length, contracts.ErrorText);
            var metadata = LayoutOf(CornerCompiler.CompileAgainst(Messages, contracts), "Messages.Attach");

            Assert.AreEqual(source, metadata);
            // trackReferences: false, raw: true; each member a raw block.
            StringAssert.Contains(metadata, "NinoHeader.None, false, true, ");
            StringAssert.EndsWith(metadata,
                "\"Flags:byte:raw(1);Session:Contracts.ClientSession:raw;At:Contracts.Position:raw\")]");
        }
    }
}
