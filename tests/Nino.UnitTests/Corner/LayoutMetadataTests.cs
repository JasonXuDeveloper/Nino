using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests
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
    [NinoType] public sealed class Shapes
    {
        public List<string> Names; public Dictionary<string, int[]> Index; public int? Maybe; public long?[] Gaps;
        public (int, string) Pair; public System.Tuple<int> Boxed; public int[,] Grid; public Animal[] Animals;
        public object Any; public KeyValuePair<int, Box<int>> Entry; public System.DateTimeOffset At;
    }
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
            Assert.AreEqual(7, assembly.GetCustomAttributes<NinoTypeLayoutAttribute>().Count());

            var move = Layout(assembly, "Layout.Move");
            Assert.AreEqual(NinoFormat.Positional, move.Format);
            Assert.AreEqual(NinoHeader.None, move.Header);
            Assert.IsTrue(move.Raw);
            Assert.AreEqual(NinoTypeId.Of(assembly.GetType("Layout.Move")), move.Id);
            Assert.AreEqual("raw(8){VelocityX:float@0:raw(4),VelocityY:float@4:raw(4)}", move.Members);

            var player = Layout(assembly, "Layout.Player");
            Assert.AreEqual(NinoHeader.NullFlag, player.Header);
            Assert.IsFalse(player.Raw);
            Assert.AreEqual("Id:Layout.PlayerId:raw(8){Value:long@0:raw(8)};Name:string:utf8;" +
                            "Path:System.Collections.Generic.List<Layout.Move>:" +
                            "seq(raw(8){VelocityX:float@0:raw(4),VelocityY:float@4:raw(4)})", player.Members);

            Assert.AreEqual(NinoHeader.TypeId, Layout(assembly, "Layout.Animal").Header);
            Assert.IsTrue(Layout(assembly, "Layout.Node").TrackReferences);

            var saved = Layout(assembly, "Layout.Saved");
            Assert.AreEqual(NinoFormat.Keyed, saved.Format);
            Assert.AreEqual($"Health:int:raw(4):key=0x{NinoTypeId.Compute("Health"):X8}:former=0x{NinoTypeId.Compute("Hp"):X8}",
                saved.Members);

            var box = Layout(assembly, "Layout.Box`1");
            Assert.IsTrue(box.Type.IsGenericTypeDefinition);
            Assert.AreEqual(NinoFormat.VersionTolerant, box.Format);
            Assert.AreEqual(0, box.Id);
            Assert.AreEqual("Value:T:type(T)", box.Members);

            // Members are described by their bytes, not by the formatters that write them.
            Assert.AreEqual(string.Join(";",
                "Names:System.Collections.Generic.List<string>:seq(string)",
                "Index:System.Collections.Generic.Dictionary<string, int[]>:map(string,seq(raw(4)))",
                "Maybe:System.Nullable<int>:nullable(raw(4))",
                "Gaps:System.Nullable<long>[]:seq(raw(16){hasValue:bool@0:raw(1),value:long@8:raw(8)})",
                "Pair:System.ValueTuple<int, string>:tuple(raw(4),string)",
                "Boxed:System.Tuple<int>:nullable(tuple(raw(4)))",
                "Grid:int[,]:grid(2,raw(4))",
                "Animals:Layout.Animal[]:seq(type(Layout.Animal))",
                "Any:object:object",
                "Entry:System.Collections.Generic.KeyValuePair<int, Layout.Box<int>>:pair(raw(4),type(Layout.Box<int>))",
                "At:System.DateTimeOffset:datetimeoffset"), Layout(assembly, "Layout.Shapes").Members);
        }

        /// <summary>
        /// Whether a class body can be deferred (read after its instance is in its slot) is published as
        /// <c>Deferrable</c>, and <c>ToString</c> writes it before the members.
        /// </summary>
        [TestMethod]
        public void PublishesDeferrability()
        {
            var assembly = Compile(@"
using Nino.Core;
namespace Deferral
{
    [NinoType] public sealed class Node { public Node Next; }
    [NinoType] public sealed record Point(int X, int Y);
    [NinoType] public abstract class Shape { public int Id; }
    [NinoType] public struct Pair { public int A; public int B; }
}");
            var node = Layout(assembly, "Deferral.Node");
            Assert.IsTrue(node.Deferrable);
            StringAssert.Contains(node.ToString(), "; typeId=0x" + node.Id.ToString("X8") + "; deferrable=true; members=");
            // Re-created through its constructor, compared by value, never created, a struct: never deferred.
            foreach (string type in new[] { "Deferral.Point", "Deferral.Shape", "Deferral.Pair" })
            {
                Assert.IsFalse(Layout(assembly, type).Deferrable, type);
                StringAssert.Contains(Layout(assembly, type).ToString(), "; deferrable=false; members=", type);
            }

            // Set only by the generator: a layout without it says nothing.
            Assert.IsFalse(new NinoTypeLayoutAttribute(typeof(int), NinoFormat.Positional, NinoHeader.None, false,
                true, 0, "raw(4)").ToString().Contains("deferrable"));
        }

        /// <summary>
        /// A referenced Nino.Core whose attribute has no <c>Deferrable</c> (older than 4.0) gets layouts without it.
        /// </summary>
        [TestMethod]
        public void LeavesDeferrabilityOutWhereNinoCoreCannotHoldIt()
        {
            const string stand = @"
namespace Nino.Core
{
    public enum NinoFormat { Positional, VersionTolerant, Keyed }
    public enum NinoHeader { None, NullFlag, TypeId }
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Interface)]
    public sealed class NinoTypeAttribute : System.Attribute
    {
        public NinoTypeAttribute(bool autoCollect = true, bool containNonPublicMembers = false,
            bool allowInheritance = true, NinoFormat format = NinoFormat.Positional, bool trackReferences = false) { }
    }
    [System.AttributeUsage(System.AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class NinoTypeLayoutAttribute : System.Attribute
    {
        public NinoTypeLayoutAttribute(System.Type type, NinoFormat format, NinoHeader header, bool trackReferences,
            bool raw, int typeId, string members) { }
    }
}";
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
            var core = CSharpCompilation.Create("Nino.Core", new[] { CSharpSyntaxTree.ParseText(stand) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var image = new MemoryStream();
            Assert.IsTrue(core.Emit(image).Success);
            refs.Add(MetadataReference.CreateFromImage(image.ToArray()));
            var user = CSharpCompilation.Create("Deferral" + Guid.NewGuid().ToString("N"),
                new[]
                {
                    CSharpSyntaxTree.ParseText(@"
using Nino.Core;
namespace Deferral
{
    [NinoType] public sealed class Node { public Node Next; }
}"),
                }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var generated = CSharpGeneratorDriver.Create(new Generator.Generation.NinoGenerator())
                .RunGenerators(user).GetRunResult().GeneratedTrees.Select(t => t.ToString()).ToList();

            string layout = generated.SelectMany(t => t.Split('\n'))
                .Single(l => l.Contains("NinoTypeLayout(typeof(global::Deferral.Node)"));
            StringAssert.EndsWith(layout, "\")]");
            Assert.IsFalse(layout.Contains("Deferrable"));
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
