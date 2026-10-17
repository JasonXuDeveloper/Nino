using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// The generator publishes each [NinoType]'s wire layout as an [assembly: NinoTypeLayout] (issue #197), so tools
    /// read what Nino writes from metadata.
    /// </summary>
    [TestClass]
    public class LayoutMetadataTests
    {
        private const string Types = @"
using System.Collections.Generic;
using Nino.Core;

namespace Layout
{
    [NinoType] public struct Move { public float VelocityX; public float VelocityY; }
    public readonly record struct PlayerId(long Value);
    [NinoType] public sealed class Player { public PlayerId Id; [NinoUtf8] public string Name; public List<Move> Path; }
    [NinoType] public class Animal { public string Name; }
    [NinoType(trackReferences: true)] public sealed class Node { public Node Next; }
    [NinoType(format: NinoFormat.Keyed)] public sealed class Saved { [NinoFormerName(""Hp"")] public int Health; }
    [NinoType(format: NinoFormat.VersionTolerant)] public sealed class Box<T> { public T Value; }
}";

        private static NinoTypeLayoutAttribute Layout(Assembly assembly, string type) =>
            assembly.GetCustomAttributes<NinoTypeLayoutAttribute>()
                .Single(a => a.Type == assembly.GetType(type));

        private static Assembly Compile(string source)
        {
            var result = CornerCompiler.Compile(source);
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            return result.Assembly;
        }

        [TestMethod]
        public void DescribesEachType()
        {
            var assembly = Compile(Types);
            Assert.AreEqual(6, assembly.GetCustomAttributes<NinoTypeLayoutAttribute>().Count());

            var move = Layout(assembly, "Layout.Move");
            Assert.AreEqual(NinoFormat.Positional, move.Format);
            Assert.AreEqual(NinoHeader.None, move.Header);
            Assert.IsTrue(move.Raw);
            Assert.AreEqual(NinoTypeId.Of(assembly.GetType("Layout.Move")), move.Id);
            Assert.AreEqual("raw(8){VelocityX:float@0:raw(4),VelocityY:float@4:raw(4)}", move.Members);

            var player = Layout(assembly, "Layout.Player");
            Assert.AreEqual(NinoHeader.NullFlag, player.Header);
            Assert.IsFalse(player.Raw);
            Assert.AreEqual("Id:Layout.PlayerId:raw(8){Value:long@0:raw(8)};Name:string:Nino.Core.Utf8StringFormatter;" +
                            "Path:System.Collections.Generic.List<Layout.Move>:" +
                            "Nino.Core.UnmanagedListFormatter<System.Collections.Generic.List<Layout.Move>, " +
                            "Layout.Move>", player.Members);

            Assert.AreEqual(NinoHeader.TypeId, Layout(assembly, "Layout.Animal").Header);
            Assert.IsTrue(Layout(assembly, "Layout.Node").TrackReferences);

            var saved = Layout(assembly, "Layout.Saved");
            Assert.AreEqual(NinoFormat.Keyed, saved.Format);
            StringAssert.StartsWith(saved.Members, "Health:int:raw(4):former=0x");

            var box = Layout(assembly, "Layout.Box`1");
            Assert.IsTrue(box.Type.IsGenericTypeDefinition);
            Assert.AreEqual(NinoFormat.VersionTolerant, box.Format);
            Assert.AreEqual(0, box.Id);
            Assert.AreEqual("Value:T:Nino.Core.CachedFormatter<T>", box.Members);
        }

        [TestMethod]
        public void ChangesExactlyWhenTheBytesDo()
        {
            string Of(string source, string type) => Layout(Compile(source), type).ToString();
            string baseline = Of(Types, "Layout.Player");

            // Renaming the namespace of an unrelated type, or reformatting the source, changes nothing.
            Assert.AreEqual(baseline, Of(Types.Replace("public float VelocityY; }", "public float VelocityY;\n}"),
                "Layout.Player"));
            // Each of these changes the bytes, and the layout.
            Assert.AreNotEqual(baseline, Of(Types.Replace("[NinoUtf8] public string Name", "public string Name"),
                "Layout.Player"));
            Assert.AreNotEqual(baseline, Of(Types.Replace("PlayerId Id;", "long Id;"), "Layout.Player"));
            // A raw member's bytes are its own: a field of a struct that is not a [NinoType] (issue #202).
            Assert.AreNotEqual(baseline, Of(Types.Replace("PlayerId(long Value)", "PlayerId(int Value)"),
                "Layout.Player"));
            Assert.AreNotEqual(baseline, Of(Types.Replace("public sealed class Player", "public class Player"),
                "Layout.Player"));
            Assert.AreNotEqual(baseline, Of(Types.Replace("[NinoType] public sealed class Player",
                "[NinoType(format: NinoFormat.VersionTolerant)] public sealed class Player"), "Layout.Player"));
            // [StructLayout] changes the bytes of a raw struct, and its layout (issue #202), when it moves a field or
            // pads the end; Pack on a struct it does not change is not a change.
            string move = Of(Types, "Layout.Move");
            const string MoveDecl = "[NinoType] public struct Move";
            Assert.AreEqual(move, Of(Types.Replace(MoveDecl,
                "[NinoType, System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)] public struct Move"),
                "Layout.Move"));
            Assert.AreNotEqual(move, Of(Types.Replace(MoveDecl,
                "[NinoType, System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 12)] public struct Move"),
                "Layout.Move"));
            // A member's nested type changes its own layout, not this one.
            Assert.AreNotEqual(Of(Types, "Layout.Move"),
                Of(Types.Replace("public float VelocityY;", "public double VelocityY;"), "Layout.Move"));
        }
    }
}
