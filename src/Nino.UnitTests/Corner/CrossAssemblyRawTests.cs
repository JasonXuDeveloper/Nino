using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// A [NinoType] struct of another assembly reads the same whether that assembly is referenced as source (the IDE,
    /// a project reference) or as metadata (a package, the command line), where Roslyn links no auto-property to its
    /// backing field (issue #200). Otherwise an outer struct was a raw block in one build and member by member in the
    /// other, and the two wrote different bytes. [StructLayout] and [FieldOffset] are not attributes in metadata, so
    /// the generator reads them from the type and field tables (issue #202).
    /// </summary>
    [TestClass]
    public class CrossAssemblyRawTests
    {
        private const string Contracts = @"
using System.Runtime.InteropServices;
using Nino.Core;

namespace Contracts
{
    [NinoType] public readonly partial record struct ClientSession(ulong Node, long Id);
    [NinoType] public struct Position { public Position(int x) { X = x; } public int X { get; } }
    public readonly record struct PlayerId(long Value);
    [StructLayout(LayoutKind.Sequential, Pack = 2)] public struct Packed { public byte A; public long B; }
    [StructLayout(LayoutKind.Explicit, Size = 12)] public struct Overlay { [FieldOffset(4)] public int A; }
    [StructLayout(LayoutKind.Explicit)] public struct Shifted { [FieldOffset(4)] public long A; }
    [NinoType, StructLayout(LayoutKind.Explicit)] public struct Union { [FieldOffset(0)] public int A; [FieldOffset(0)] public float B; }
}";

        // No usings: it is also appended to Contracts as one source.
        private const string Messages = @"
namespace Messages
{
    [Nino.Core.NinoType]
    public partial struct Attach { public byte Flags; public Contracts.ClientSession Session; public Contracts.Position At; }

    [Nino.Core.NinoType]
    public sealed class Player
    {
        public Contracts.PlayerId Id; public Contracts.Packed P; public Contracts.Overlay O; public Contracts.Shifted S;
        public Contracts.Union U;
    }
}";

        private static string LayoutOf(CornerCompiler.Result result, string type)
        {
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            return result.GeneratedCode.Split('\n').Single(l => l.Contains("NinoTypeLayout(typeof(global::" + type + ")"));
        }

        private static (string Source, string Metadata) BothWays(string type)
        {
            var source = LayoutOf(CornerCompiler.Compile(Contracts + Messages, load: false), type);
            var contracts = CornerCompiler.Compile(Contracts, load: false);
            Assert.AreEqual(0, contracts.Errors.Length, contracts.ErrorText);
            return (source, LayoutOf(CornerCompiler.CompileAgainst(Messages, contracts), type));
        }

        [TestMethod]
        public void MetadataReferenceReadsLikeSource()
        {
            var (source, metadata) = BothWays("Messages.Attach");

            Assert.AreEqual(source, metadata);
            // trackReferences: false, raw: true; the whole struct one raw block, nested structs included.
            StringAssert.Contains(metadata, "NinoHeader.None, false, true, ");
            StringAssert.EndsWith(metadata, "\"raw(32){Flags:byte@0:raw(1)," +
                "Session:Contracts.ClientSession@8:raw(16){Node:ulong@0:raw(8),Id:long@8:raw(8)}," +
                "At:Contracts.Position@24:raw(4){X:int@0:raw(4)}}\")]");
        }

        [TestMethod]
        public void MetadataStructLayoutReadsLikeSource()
        {
            var (source, metadata) = BothWays("Messages.Player");

            // Encodings describe bytes, so a nested [NinoType] reads the same whether it is generated here or not.
            Assert.AreEqual(source, metadata);
            StringAssert.Contains(source, "\"Id:Contracts.PlayerId:raw(8){Value:long@0:raw(8)};" +
                "P:Contracts.Packed:raw(10){A:byte@0:raw(1),B:long@2:raw(8)};" +
                "O:Contracts.Overlay:raw(12){A:int@4:raw(4)};" +
                // 16 bytes on 64-bit runtimes, 12 where longs align to 4: not a raw block, its formatter converts
                // it to this canonical layout.
                "S:Contracts.Shifted:raw(16){A:long@4:raw(8)};" +
                // An explicit-layout [NinoType] is written member by member.
                "U:Contracts.Union:type(Contracts.Union)\"");
        }
    }
}
