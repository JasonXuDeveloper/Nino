using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

#nullable disable
namespace Nino.UnitTests
{
    /// <summary>
    /// The generator (RawRules.IsRawUnmanaged) and Nino.Core (NinoReflection.IsRawUnmanaged) decide which values are
    /// written as their raw bytes each on its own: one from symbols, the other by reflection, for the formatters it
    /// resolves at run time (a generic <c>[NinoType]</c>'s type arguments, <c>[NinoRaw]</c> formatter parameters,
    /// NativeAOT). A value one writes raw and the other member by member is written in two ways, so both must agree
    /// on every shape: BCL values with formats of their own, user structs, and <c>[NinoType]</c> structs raw or not
    /// for each reason the generator has.
    /// </summary>
    [TestClass]
    public class RawRuleAgreementTests
    {
        private const string Source = @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nino.Core;

namespace RawRules
{
    public enum Color : byte { Red, Green }
    public struct Plain { public int A; public float B; }
    public struct PlainWithOffset { public DateTimeOffset D; }
    public struct PlainWithNint { public nint P; }
    public struct PlainWithNullable { public int? N; }
    public struct PlainWithTuple { public (int, int) T; }
    public struct PlainGeneric<T> { public T Value; }
    public unsafe struct PlainFixed { public fixed byte Bytes[4]; }
    public struct PlainEmpty { }

    [NinoType] public struct Raw { public int A; public float B; }
    [NinoType] public struct RawNested { public Raw R; public long X; public Color C; }
    [NinoType] public struct RawOfPlain { public Plain P; }
    [NinoType] public struct RawGeneric<T> { public T Value; }
    [NinoType] public record struct RawRecord(int A, short B);
    [NinoType] public struct RawEmpty { }
    [NinoType, StructLayout(LayoutKind.Sequential, Pack = 1)] public struct RawPacked { public byte A; public long B; }
    [NinoType] public struct WithTuple { public (int, int) T; }
    [NinoType] public struct WithLongTuple { public (int, int, int, int, int, int, int, int) T; }
    [NinoType] public struct WithNullable { public int? N; }
    [NinoType] public struct WithNint { public nint P; }
    [NinoType] public struct WithOffset { public DateTimeOffset D; }
    [NinoType] public struct WithPair { public KeyValuePair<int, int> P; }
    [NinoType] public struct WithNotRaw { public WithTuple Inner; }
    [NinoType] public struct WithPlainOffset { public PlainWithOffset Inner; }
    [NinoType] public struct WithIgnored { public int A; [NinoIgnore] public int B; }
    [NinoType] public struct WithPrivate { public int A; private int _b; public int B => _b; }
    [NinoType(false)] public struct NotAutoCollected { [NinoMember(0)] public int A; public int B; }
    [NinoType(false)] public struct AllMarked { [NinoMember(0)] public int A; [NinoMember(1)] public int B; }
    [NinoType(containNonPublicMembers: true)] public struct WithPrivateCollected { public int A; private int _b; }
    [NinoType(format: NinoFormat.VersionTolerant)] public struct Tolerant { public int A; }
    [NinoType(format: NinoFormat.Keyed)] public struct Keyed { public int A; }
    [NinoType, StructLayout(LayoutKind.Explicit)] public struct Union { [FieldOffset(0)] public int A; [FieldOffset(0)] public float B; }
    [NinoType, StructLayout(LayoutKind.Auto)] public struct Auto { public int A; public long B; }
    [NinoType] public struct WithString { public int A; public string S; }

    [NinoType] public interface IShape { }
    public struct Inherited : IShape { public int A; }
    public struct InheritedWithTuple : IShape { public (int, int) T; }
    [NinoType(allowInheritance: false)] public interface INotInherited { }
    public struct NotInherited : INotInherited { public int A; }

    public unsafe class Probe
    {
        public bool F0; public char F1; public decimal F2; public Guid F3; public DateTime F4; public TimeSpan F5;
        public DateTimeOffset F6; public nint F7; public nuint F8; public int? F9; public Raw? F10;
        public KeyValuePair<int, int> F11; public ValueTuple<int> F12; public (int, long) F13;
        public (int, int, int, int, int, int, int, int) F14; public Color F15; public Color? F16;
        public Plain F17; public PlainWithOffset F18; public PlainWithNint F19; public PlainWithNullable F20;
        public PlainWithTuple F21; public PlainGeneric<int> F22; public PlainGeneric<nint> F23;
        public PlainGeneric<DateTimeOffset> F24; public PlainFixed F25; public PlainEmpty F26; public ValueTuple F27;
        public Raw F28; public RawNested F29; public RawOfPlain F30; public RawGeneric<int> F31;
        public RawGeneric<(int, int)> F32; public RawGeneric<Raw> F33; public RawRecord F34; public RawEmpty F35;
        public RawPacked F36; public WithTuple F37; public WithLongTuple F38; public WithNullable F39; public WithNint F40;
        public WithOffset F41; public WithPair F42; public WithNotRaw F43; public WithPlainOffset F44;
        public WithIgnored F45; public WithPrivate F46; public NotAutoCollected F47; public AllMarked F48;
        public WithPrivateCollected F49; public Tolerant F50; public Keyed F51; public Union F52; public Auto F53;
        public WithString F54; public Inherited F55; public InheritedWithTuple F56; public NotInherited F57;
        public RawGeneric<nint> F58; public RawGeneric<int?> F59; public KeyValuePair<Raw, int> F60;
        public PlainGeneric<Raw> F61; public PlainGeneric<WithTuple> F62;
#if NET8_0_OR_GREATER
        public Half F63; public Int128 F64; public UInt128 F65; public System.Text.Rune F66; public DateOnly F67;
        public TimeOnly F68; public System.Numerics.Complex F69; public System.Numerics.Vector2 F70;
#endif
    }
}";

        [TestMethod]
        public void GeneratorAndRuntimeAgreeOnRawValues()
        {
#if NET8_0_OR_GREATER
            var symbols = new[] { "NET8_0_OR_GREATER" };
#else
            string[] symbols = null;
#endif
            var result = CornerCompiler.Compile(Source, allowUnsafe: true, preprocessorSymbols: symbols);
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            var compilation = result.Compilation;
            var probe = compilation.GetTypeByMetadataName("RawRules.Probe");
            Assert.IsNotNull(probe);

            // TypeFacts.For(compilation, options).Raw: the generator's rules for this compilation.
            var generator = typeof(Generator.Generation.NinoGenerator).Assembly;
            var factsType = generator.GetType("Nino.Generator.Generation.TypeFacts", true);
            var facts = factsType.GetMethod("For")!.Invoke(null,
                new object[] { compilation, compilation.SyntaxTrees.First().Options });
            var raw = factsType.GetProperty("Raw")!.GetValue(facts);
            var isRaw = raw.GetType().GetMethod("IsRawUnmanaged")!;

            var runtimeProbe = result.Assembly.GetType("RawRules.Probe", true);
            var disagreements = new List<string>();
            int rawCount = 0, fields = 0;
            foreach (var field in probe.GetMembers().OfType<IFieldSymbol>())
            {
                bool generated = (bool)isRaw.Invoke(raw, new object[] { field.Type });
                var type = runtimeProbe.GetField(field.Name)!.FieldType;
                bool runtime = Nino.Core.Internal.NinoReflection.IsRawUnmanaged(type);
                fields++;
                if (generated)
                    rawCount++;
                if (generated != runtime)
                    disagreements.Add($"{field.Name} {field.Type.ToDisplayString()}: generator {generated}, runtime {runtime}");
            }

            Assert.AreEqual(0, disagreements.Count, string.Join("\n", disagreements));
            // Both kinds are covered.
            Assert.IsTrue(rawCount > 10 && fields - rawCount > 10, $"{rawCount} raw of {fields}");
        }
    
        [TestMethod]
        public void GenericStructsAreWrittenByTheirFormatterEverywhere()
        {
            // Pad<int> as a member was copied raw (8 bytes, with the padding), while its formatter, which a Box<T> of
            // it and its own registration use, writes it member by member (5 bytes).
            var result = CornerCompiler.Compile(@"
using Nino.Core;
using Nino.Core.Internal;
[NinoType] public struct Pad<T> { public byte A; public T B; }
[NinoType] public sealed class Box<T> { public T V; }
[NinoType] public sealed class Holder { public Pad<int> P; }
public static class Api
{
    private static byte[] Boxed<T>(T v) => NinoSerializer.Serialize(new Box<T> { V = v });
    public static byte[] InBox() => Boxed(new Pad<int> { A = 1, B = 2 });
    public static byte[] InHolder() => NinoSerializer.Serialize(new Holder { P = new Pad<int> { A = 1, B = 2 } });
    public static byte[] Alone() => NinoSerializer.Serialize(new Pad<int> { A = 1, B = 2 });
}");
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            var api = result.Assembly.GetType("Api", true);
            byte[] Call(string name) => (byte[])api.GetMethod(name)!.Invoke(null, null);
            CollectionAssert.AreEqual(new byte[] { 1, 1, 2, 0, 0, 0 }, Call("InHolder"));
            CollectionAssert.AreEqual(Call("InHolder"), Call("InBox"));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 0, 0, 0 }, Call("Alone"));
        }
    }
}
