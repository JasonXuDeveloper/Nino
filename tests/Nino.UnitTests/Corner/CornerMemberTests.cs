using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    // ---- `new` hiding ----

    [NinoType]
    public class HideBase
    {
        public int Value;
        public string Name;
    }

    [NinoType]
    public class HideSameType : HideBase
    {
        public new int Value;
    }

    [NinoType]
    public class HideOtherType : HideBase
    {
        public new string Value;
        public new int Name { get; set; }
    }

    // ---- same-named private fields at several levels ----

    [NinoType(containNonPublicMembers: true)]
    public partial class PrivLevel1
    {
        private int _x;
        public int GetX1() => _x;
        public void SetX1(int v) => _x = v;
    }

    [NinoType(containNonPublicMembers: true)]
    public partial class PrivLevel2 : PrivLevel1
    {
        private int _x;
        public int GetX2() => _x;
        public void SetX2(int v) => _x = v;
    }

    [NinoType(containNonPublicMembers: true)]
    public partial class PrivLevel3 : PrivLevel2
    {
        private string _x;
        public string GetX3() => _x;
        public void SetX3(string v) => _x = v;
    }

    // ---- explicit interface implementations ----

    public interface IHasId
    {
        int Id { get; set; }
    }

    [NinoType]
    public class ExplicitImpl : IHasId
    {
        int IHasId.Id { get; set; }
        public int Other;
    }

    [NinoType]
    public interface INinoIdentified
    {
        int Id { get; set; }
    }

    [NinoType]
    public class ImplicitNinoInterfaceImpl : INinoIdentified
    {
        public int Id { get; set; }
        public string Extra;
    }

    // ---- getter-only / computed ----

    [NinoType(containNonPublicMembers: true)]
    public partial class GetterBacked
    {
        private int _count;
        public int Count => _count;
        public void SetCount(int c) => _count = c;
    }

    [NinoType]
    public class ComputedProperty
    {
        public int A;
        public int Twice => A * 2;
        public string Label => "x" + A;
    }

    // ---- init / readonly ----

    [NinoType]
    public class InitMixed
    {
        public int A { get; init; }
        public List<int> L { get; set; }
        public string S;
    }

    [NinoType]
    public class ReadonlyFields
    {
        public readonly int A;
        public readonly string B;
        public int C;

        public ReadonlyFields(int a, string b)
        {
            A = a;
            B = b;
        }
    }

    // ---- static, const, indexer ----

    [NinoType]
    public sealed class WithStatics
    {
        public static int StaticField = 5;
        public const int Const = 7;
        public static string StaticProp { get; set; } = "s";
        public int V;
        private readonly int[] _store = new int[4];

        public int this[int i]
        {
            get => _store[i];
            set => _store[i] = value;
        }
    }

    [NinoType]
    public sealed class OnlyV
    {
        public int V;
    }

    // ---- generic parameter members ----

    [NinoType]
    public class GenericMembers<T>
    {
        public T Value;
        public T[] Array;
        public List<T> List;
        public Dictionary<int, T> Map;
    }

    [NinoType]
    public class SimpleNode
    {
        public int Id;
        public string Name;
    }

    [NinoType]
    public struct SimpleStructNode
    {
        public int A;
        public string B;
    }

    // ---- object members ----

    [NinoType]
    public class ObjectHolder
    {
        public object Value;
        public object[] Values;
        public List<object> List;
    }

    [NinoType]
    public class ObjectNodeA
    {
        public int A;
    }

    [NinoType]
    public class ObjectNodeB : ObjectNodeA
    {
        public string B;
    }

    // ---- delegates are skipped ----

    [NinoType]
    public class WithDelegate
    {
        [NinoIgnore] public Action Ignored;
        public int Id;
        public string Name;
    }

    // ---- restricted setters ----

    [NinoType]
    public partial class RestrictedSetters
    {
        public int PrivateSet { get; private set; }
        public int ProtectedSet { get; protected set; }
        public int InternalSet { get; internal set; }
        public List<int> PrivateSetList { get; private set; }

        public void Fill(int v)
        {
            PrivateSet = v;
            ProtectedSet = v + 1;
            InternalSet = v + 2;
            PrivateSetList = new List<int> { v };
        }
    }

    [NinoType]
    public readonly struct ReadonlyStruct
    {
        public readonly int A;
        public readonly string B;

        public ReadonlyStruct(int a, string b)
        {
            A = a;
            B = b;
        }
    }

    [NinoType]
    public struct StructWithReferences
    {
        public List<int> List;
        public SimpleNode Node;
        public int Value;
    }

    // Member names that collide with names generated code uses.
    [NinoType]
    public class AwkwardNames
    {
        public int value;
        public string writer;
        public string reader;
        public int @class;
        public int __length;
        public int __reader;
        public int __end;
        public int Id;
        public int TypeId;
        public List<int> global;
        public string Serialize;
        public string Deserialize;
    }

    [NinoType(containNonPublicMembers: true)]
    public partial class AwkwardPrivateNames
    {
        private int value;
        private string @this;
        private int __G_x;

        public void Set(int v, string t, int g)
        {
            value = v;
            @this = t;
            __G_x = g;
        }

        public string Get() => value + @this + __G_x;
    }

    [TestClass]
    public class CornerMemberTests
    {
        [TestMethod]
        public void PrivateMembersThroughPartialAccessorsWithoutUnsafeAccessor()
        {
            // The in-memory compilation does not define NET8_0_OR_GREATER, as on Unity: generated partial accessors.
            var compiled = CornerCompiler.Compile(@"
using System.Collections.Generic;
using Nino.Core;
namespace C
{
    [NinoType(containNonPublicMembers: true)]
    public partial class L1 { private int _x; protected string Shared; public void Set1(int v) => _x = v; public int Get1() => _x; }

    [NinoType(containNonPublicMembers: true)]
    public partial class L2 : L1 { private int _x; private List<int> value; public void Set2(int v) { _x = v; value = new List<int> { v }; Shared = ""s""; } public string Get2() => _x + "","" + value[0] + Shared; }

    public partial class Outer
    {
        [NinoType(containNonPublicMembers: true)]
        internal partial class Inner { private int __G_x; private string @this; public void Set(int a, string b) { __G_x = a; @this = b; } public string Get() => __G_x + @this; }
        public static string RunInner()
        {
            var i = new Inner();
            i.Set(4, ""t"");
            return NinoDeserializer.Deserialize<Inner>(NinoSerializer.Serialize(i)).Get();
        }
    }

    public static class Api
    {
        public static string Run()
        {
            var v = new L2();
            v.Set1(1);
            v.Set2(2);
            var back = (L2)NinoDeserializer.Deserialize<L1>(NinoSerializer.Serialize<L1>(v));
            return back.Get1() + ""|"" + back.Get2() + ""|"" + Outer.RunInner();
        }
    }
}");
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            // Base-private _x is not part of L2's layout (as documented by InheritanceDelegationTests); protected is.
            Assert.AreEqual("0|2,2s|4t", compiled.Assembly.GetType("C.Api")!.GetMethod("Run")!.Invoke(null, null));
        }

        [TestMethod]
        public void MemberNamesCollidingWithGeneratedNames()
        {
            var v = new AwkwardNames
            {
                value = 1, writer = "w", reader = "r", @class = 2, __length = 3, __reader = 4, __end = 5, Id = 6,
                TypeId = 7, global = new List<int> { 8 }, Serialize = "s", Deserialize = "d",
            };
            var back = Rt.RoundTrip(v);
            Assert.AreEqual(1, back.value);
            Assert.AreEqual("w", back.writer);
            Assert.AreEqual("r", back.reader);
            Assert.AreEqual(2, back.@class);
            Assert.AreEqual(3, back.__length);
            Assert.AreEqual(4, back.__reader);
            Assert.AreEqual(5, back.__end);
            Assert.AreEqual(6, back.Id);
            Assert.AreEqual(7, back.TypeId);
            CollectionAssert.AreEqual(new[] { 8 }, back.global);
            Assert.AreEqual("s", back.Serialize);
            Assert.AreEqual("d", back.Deserialize);

            var p = new AwkwardPrivateNames();
            p.Set(1, "t", 3);
            Assert.AreEqual("1t3", Rt.RoundTrip(p).Get());
        }

        [TestMethod]
        public void PropertiesWithRestrictedSetters()
        {
            var value = new RestrictedSetters();
            value.Fill(10);
            var back = Rt.RoundTrip(value);
            Assert.AreEqual(10, back.PrivateSet);
            Assert.AreEqual(11, back.ProtectedSet);
            Assert.AreEqual(12, back.InternalSet);
            CollectionAssert.AreEqual(new[] { 10 }, back.PrivateSetList);

            var target = new RestrictedSetters();
            target.Fill(1);
            var list = target.PrivateSetList;
            Rt.Into(value, ref target);
            Assert.AreEqual(10, target.PrivateSet);
            Assert.AreSame(list, target.PrivateSetList);
        }

        [TestMethod]
        public void ReadonlyStructAndStructsHoldingReferences()
        {
            var ro = new ReadonlyStruct(3, "b");
            var roBack = Rt.RoundTrip(ro);
            Assert.AreEqual(3, roBack.A);
            Assert.AreEqual("b", roBack.B);
            Assert.AreEqual("b", Rt.RoundTrip(new[] { ro })[0].B);

            // In place: the struct is assigned by ref, its list and node are reused.
            var target = new StructWithReferences { List = new List<int> { 9, 9 }, Node = new SimpleNode() };
            var list = target.List;
            var node = target.Node;
            Rt.Into(new StructWithReferences { List = new List<int> { 1 }, Node = new SimpleNode { Id = 2 }, Value = 3 }, ref target);
            Assert.AreSame(list, target.List);
            Assert.AreSame(node, target.Node);
            Assert.AreEqual(2, node.Id);
            Assert.AreEqual(3, target.Value);
            CollectionAssert.AreEqual(new[] { 1 }, list);

            // Elements of an array of such structs too.
            var array = new[] { new StructWithReferences { List = new List<int>() } };
            var inner = array[0].List;
            Rt.Into(new[] { new StructWithReferences { List = new List<int> { 5 } } }, ref array);
            Assert.AreSame(inner, array[0].List);
        }

        [TestMethod]
        public void NewHidingSameTypeKeepsBothMembers()
        {
            var value = new HideSameType { Value = 2, Name = "n" };
            ((HideBase)value).Value = 1;

            var result = Rt.RoundTrip(value);
            Assert.AreEqual(2, result.Value);
            Assert.AreEqual(1, ((HideBase)result).Value);
            Assert.AreEqual("n", result.Name);

            // Through the base type, and in place.
            var poly = (HideSameType)Rt.RoundTrip<HideBase>(value);
            Assert.AreEqual(2, poly.Value);
            Assert.AreEqual(1, ((HideBase)poly).Value);

            var target = new HideSameType { Value = 9 };
            ((HideBase)target).Value = 9;
            var before = target;
            Rt.Into(value, ref target);
            Assert.AreSame(before, target);
            Assert.AreEqual(2, target.Value);
            Assert.AreEqual(1, ((HideBase)target).Value);
        }

        [TestMethod]
        public void NewHidingOtherTypeKeepsBothMembers()
        {
            var value = new HideOtherType { Value = "derived", Name = 42 };
            ((HideBase)value).Value = 7;
            ((HideBase)value).Name = "base";

            var result = Rt.RoundTrip(value);
            Assert.AreEqual("derived", result.Value);
            Assert.AreEqual(42, result.Name);
            Assert.AreEqual(7, ((HideBase)result).Value);
            Assert.AreEqual("base", ((HideBase)result).Name);

            var poly = (HideOtherType)Rt.RoundTrip<HideBase>(value);
            Assert.AreEqual("derived", poly.Value);
            Assert.AreEqual("base", ((HideBase)poly).Name);
        }

        [TestMethod]
        public void NewHidingWithoutBaseFormatter()
        {
            // The hidden base member is written by the subtype's own formatter: through a cast when it is public,
            // [UnsafeAccessor] when it is not; a generic type has neither (NINO022).
            var compiled = CornerCompiler.Compile(@"
using Nino.Core;
namespace C
{
    [NinoType]
    public class Base { public int Value; public string Name; }

    // Re-created from its constructor: the hidden member is assigned after construction.
    [NinoType]
    public class Ctor : Base
    {
        public new readonly string Value;
        public Ctor(string value) { Value = value; }
    }

    // Keyed bodies never delegate to the base formatter; the hidden member has a key of its own.
    [NinoType(format: NinoFormat.Keyed)]
    public class KeyedBase { public int Value { get; set; } }

    [NinoType(format: NinoFormat.Keyed)]
    public class Keyed : KeyedBase { public new string Value { get; set; } }

    // Hidden by a member that is not serialized.
    [NinoType]
    public class IgnoredHiding : Base { [NinoIgnore] public new long Value; }

    [NinoType(containNonPublicMembers: true)]
    public partial class PBase { protected int Value; public int Get() => Value; public void Set(int v) => Value = v; }

    [NinoType(containNonPublicMembers: true)]
    public partial class PDerived : PBase { public new string Value; }

    [NinoType(containNonPublicMembers: true)]
    public partial class GBase<T> { protected int Value; public int Get() => Value; public void Set(int v) => Value = v; }

    [NinoType(containNonPublicMembers: true)]
    public partial class GDerived<T> : GBase<T> { public new T Value; }

    public static class Api
    {
        static T Rt<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

        public static string Run()
        {
            var ctor = new Ctor(""c"") { Name = ""n"" };
            ((Base)ctor).Value = 1;
            ctor = Rt(ctor);

            var keyed = new Keyed { Value = ""k"" };
            ((KeyedBase)keyed).Value = 2;
            keyed = Rt(keyed);

            var ignored = new IgnoredHiding { Value = 9 };
            ((Base)ignored).Value = 3;
            ignored = Rt(ignored);

            var p = new PDerived { Value = ""p"" };
            p.Set(4);
            p = Rt(p);

            var g = new GDerived<string> { Value = ""g"" };
            g.Set(5);
            g = Rt(g);

            return string.Join("","", ctor.Value, ((Base)ctor).Value, ctor.Name, keyed.Value, ((KeyedBase)keyed).Value,
                ignored.Value, ((Base)ignored).Value, p.Value, p.Get(), g.Value, g.Get());
        }
    }
}", preprocessorSymbols: new[] { "NET8_0_OR_GREATER" });
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            Assert.IsTrue(compiled.HasAnalyzer("NINO022"), string.Join("\n", compiled.AnalyzerDiagnostics));
            Assert.AreEqual("c,1,n,k,2,0,3,p,4,g,0", compiled.Assembly.GetType("C.Api")!.GetMethod("Run")!.Invoke(null, null));
        }

        [TestMethod]
        public void SameNamedPrivateFieldsAtEachLevel()
        {
            var l1 = new PrivLevel1();
            l1.SetX1(1);
            Assert.AreEqual(1, Rt.RoundTrip(l1).GetX1());

            var l2 = new PrivLevel2();
            l2.SetX2(2);
            Assert.AreEqual(2, Rt.RoundTrip(l2).GetX2());

            var l3 = new PrivLevel3();
            l3.SetX3("three");
            var r3 = Rt.RoundTrip(l3);
            Assert.AreEqual("three", r3.GetX3());

            // Polymorphic, through each base: the most derived level's own field survives.
            var p3 = (PrivLevel3)Rt.RoundTrip<PrivLevel1>(l3);
            Assert.AreEqual("three", p3.GetX3());
            var p2 = (PrivLevel2)Rt.RoundTrip<PrivLevel1>(l2);
            Assert.AreEqual(2, p2.GetX2());
        }

        [TestMethod]
        public void SameNamedPrivateFieldsOfBasesAreNotPartOfDerivedLayout()
        {
            // Documented by InheritanceDelegationTests: private base members are not serialized with a subtype.
            var l3 = new PrivLevel3();
            l3.SetX1(1);
            l3.SetX2(2);
            l3.SetX3("3");
            var r3 = Rt.RoundTrip(l3);
            Assert.AreEqual("3", r3.GetX3());
            Assert.AreEqual(0, r3.GetX1());
            Assert.AreEqual(0, r3.GetX2());
        }

        [TestMethod]
        public void ExplicitInterfacePropertyIsNotPublicAndSkipped()
        {
            var value = new ExplicitImpl { Other = 3 };
            ((IHasId)value).Id = 5;
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(3, result.Other);
            Assert.AreEqual(0, ((IHasId)result).Id);
        }

        [TestMethod]
        public void ExplicitInterfacePropertyWithNonPublicMembersIsSkipped()
        {
            var compiled = CornerCompiler.Compile(@"
using Nino.Core;
namespace C
{
    public interface IHasId { int Id { get; set; } }

    [NinoType(containNonPublicMembers: true)]
    public partial class Impl : IHasId
    {
        int IHasId.Id { get; set; }
        public int Other;
    }

    public static class Api
    {
        public static int Run()
        {
            var value = new Impl { Other = 3 };
            ((IHasId)value).Id = 5;
            var back = NinoDeserializer.Deserialize<Impl>(NinoSerializer.Serialize(value));
            return ((IHasId)back).Id * 10 + back.Other;
        }
    }
}");
            // Explicit interface implementations are never members of the implementing type, even with
            // containNonPublicMembers: they belong to the interface (serialized when it is a [NinoType]).
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            var result = (int)compiled.Assembly.GetType("C.Api")!.GetMethod("Run")!.Invoke(null, null)!;
            Assert.AreEqual(3, result);
        }

        [TestMethod]
        public void NinoInterfaceMembersThroughImplementation()
        {
            INinoIdentified value = new ImplicitNinoInterfaceImpl { Id = 4, Extra = "e" };
            var result = (ImplicitNinoInterfaceImpl)Rt.RoundTrip(value);
            Assert.AreEqual(4, result.Id);
            Assert.AreEqual("e", result.Extra);

            INinoIdentified target = new ImplicitNinoInterfaceImpl { Id = 9 };
            var before = target;
            Rt.Into(value, ref target);
            Assert.AreSame(before, target);
            Assert.AreEqual(4, target.Id);

            INinoIdentified nothing = null;
            Assert.IsNull(Rt.RoundTrip(nothing));
        }

        [TestMethod]
        public void GetterOnlyPropertyBackedByPrivateField()
        {
            var value = new GetterBacked();
            value.SetCount(12);
            Assert.AreEqual(12, Rt.RoundTrip(value).Count);
        }

        [TestMethod]
        public void ComputedPropertiesAreIgnored()
        {
            var value = new ComputedProperty { A = 21 };
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(21, result.A);
            Assert.AreEqual(42, result.Twice);
            // Only A is written: same size as a lone int member.
            Assert.AreEqual(NinoSerializer.Serialize(new ObjectNodeA { A = 21 }).Length,
                NinoSerializer.Serialize(value).Length);
        }

        [TestMethod]
        public void InitOnlyMixedWithSettable()
        {
            var value = new InitMixed { A = 1, L = new List<int> { 1, 2 }, S = "s" };
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(1, result.A);
            CollectionAssert.AreEqual(value.L, result.L);
            Assert.AreEqual("s", result.S);

            var target = new InitMixed { A = 9, L = new List<int> { 9 } };
            Rt.Into(value, ref target);
            Assert.AreEqual(1, target.A);
            CollectionAssert.AreEqual(value.L, target.L);
        }

        [TestMethod]
        public void ReadonlyFieldsAreRestoredThroughConstructor()
        {
            var value = new ReadonlyFields(4, "b") { C = 6 };
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(4, result.A);
            Assert.AreEqual("b", result.B);
            Assert.AreEqual(6, result.C);

            var recycled = new List<ReadonlyFields>();
            NinoSettings.SetRecycler<ReadonlyFields>(recycled.Add);
            try
            {
                var target = new ReadonlyFields(1, "old");
                var old = target;
                Rt.Into(value, ref target);
                Assert.AreEqual(4, target.A);
                Assert.AreEqual(6, target.C);
                // Re-created: the old instance is dropped exactly once.
                Assert.AreNotSame(old, target);
                CollectionAssert.AreEqual(new[] { old }, recycled);
            }
            finally
            {
                NinoSettings.SetRecycler<ReadonlyFields>(null);
            }
        }

        [TestMethod]
        public void StaticConstAndIndexerAreIgnored()
        {
            var value = new WithStatics { V = 3 };
            value[1] = 99;
            CollectionAssert.AreEqual(NinoSerializer.Serialize(new OnlyV { V = 3 }), NinoSerializer.Serialize(value));
            WithStatics.StaticField = 6;
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(3, result.V);
            Assert.AreEqual(0, result[1]);
            Assert.AreEqual(6, WithStatics.StaticField);
        }

        private static void CheckGeneric<T>(T a, T b, Func<T, T, bool> eq = null)
        {
            eq ??= (x, y) => EqualityComparer<T>.Default.Equals(x, y);
            var value = new GenericMembers<T>
            {
                Value = a, Array = new[] { a, b, default }, List = new List<T> { b, a },
                Map = new Dictionary<int, T> { [1] = a, [2] = default },
            };
            var result = Rt.RoundTrip(value);
            Assert.IsTrue(eq(a, result.Value), typeof(T).Name);
            Assert.AreEqual(3, result.Array.Length);
            Assert.IsTrue(eq(b, result.Array[1]));
            Assert.IsTrue(eq(default, result.Array[2]));
            Assert.IsTrue(eq(a, result.List[1]));
            Assert.IsTrue(eq(a, result.Map[1]));
            Assert.IsTrue(eq(default, result.Map[2]));

            var target = new GenericMembers<T> { Value = b, List = new List<T> { a, a, a, a } };
            var list = target.List;
            Rt.Into(value, ref target);
            Assert.AreSame(list, target.List);
            Assert.AreEqual(2, target.List.Count);
            Assert.IsTrue(eq(a, target.Value));

            var empty = Rt.RoundTrip(new GenericMembers<T>());
            Assert.IsNull(empty.Array);
            Assert.IsNull(empty.List);
            Assert.IsNull(empty.Map);
        }

        [TestMethod]
        public void GenericParameterTypedMembers()
        {
            CheckGeneric(1, 2);
            CheckGeneric("a", "b");
            CheckGeneric<int?>(1, null);
            CheckGeneric(Guid.NewGuid(), Guid.NewGuid());
            CheckGeneric(new[] { 1, 2 }, new int[0], (x, y) => x == null ? y == null : y != null && x.SequenceEqual(y));
            CheckGeneric(new List<string> { "x", null }, new List<string>(),
                (x, y) => x == null ? y == null : y != null && x.SequenceEqual(y));
            CheckGeneric(new SimpleNode { Id = 1, Name = "a" }, new SimpleNode { Id = 2 },
                (x, y) => x == null ? y == null : y != null && x.Id == y.Id && x.Name == y.Name);
            CheckGeneric(new SimpleStructNode { A = 1, B = "x" }, new SimpleStructNode { A = 2 });
            CheckGeneric(new GenericMembers<int> { Value = 3 }, null, (x, y) => x == null ? y == null : y != null && x.Value == y.Value);
        }

        [TestMethod]
        public void ObjectMemberHoldingNinoTypesAndNull()
        {
            var value = new ObjectHolder
            {
                Value = new ObjectNodeB { A = 1, B = "b" },
                Values = new object[] { new ObjectNodeA { A = 2 }, null, new SimpleNode { Id = 3 } },
                List = new List<object> { new SimpleStructNode { A = 4, B = "s" }, null },
            };
            var result = Rt.RoundTrip(value);
            Assert.AreEqual("b", ((ObjectNodeB)result.Value).B);
            Assert.AreEqual(2, ((ObjectNodeA)result.Values[0]).A);
            Assert.IsNull(result.Values[1]);
            Assert.AreEqual(3, ((SimpleNode)result.Values[2]).Id);
            Assert.AreEqual(4, ((SimpleStructNode)result.List[0]).A);
            Assert.AreEqual("s", ((SimpleStructNode)result.List[0]).B);
            Assert.IsNull(result.List[1]);

            Assert.IsNull(Rt.RoundTrip(new ObjectHolder()).Value);
        }

        [TestMethod]
        public void ObjectMemberReuseDeserialization()
        {
            var recycled = new List<object>();
            NinoSettings.SetRecycler<object>(recycled.Add);
            try
            {
                // Same runtime type: reused in place.
                var existing = new ObjectNodeA { A = 9 };
                var target = new ObjectHolder { Value = existing };
                Rt.Into(new ObjectHolder { Value = new ObjectNodeA { A = 1 } }, ref target);
                Assert.AreSame(existing, target.Value);
                Assert.AreEqual(1, existing.A);
                Assert.AreEqual(0, recycled.Count);

                // Another runtime type: replaced, old one recycled once as object.
                Rt.Into(new ObjectHolder { Value = new ObjectNodeB { A = 2, B = "b" } }, ref target);
                Assert.IsInstanceOfType(target.Value, typeof(ObjectNodeB));
                CollectionAssert.AreEqual(new object[] { existing }, recycled);

                // Null: dropped once.
                var b = target.Value;
                recycled.Clear();
                Rt.Into(new ObjectHolder(), ref target);
                Assert.IsNull(target.Value);
                CollectionAssert.AreEqual(new[] { b }, recycled);
            }
            finally
            {
                NinoSettings.SetRecycler<object>(null);
            }
        }

        [TestMethod]
        public void ObjectMemberHoldingNonNinoValuesIsRejected()
        {
            // Documented limitation (EdgeCaseAndFailureTests): an object slot only holds [NinoType] instances.
            foreach (var bad in new object[]
                     {
                         1, "s", 1.5, true, Guid.Empty, new int[] { 1 }, new List<int> { 1 },
                         new Dictionary<string, int>(), new SimpleNode[] { new SimpleNode() },
                     })
            {
                Assert.ThrowsExactly<InvalidOperationException>(
                    () => NinoSerializer.Serialize(new ObjectHolder { Value = bad }), bad.GetType().Name);
            }
        }

        [TestMethod]
        public void IgnoredDelegateMemberDoesNotBreakOtherMembers()
        {
            var value = new WithDelegate { Ignored = () => { }, Id = 1, Name = "n" };
            var result = Rt.RoundTrip(value);
            Assert.IsNull(result.Ignored);
            Assert.AreEqual(1, result.Id);
            Assert.AreEqual("n", result.Name);
        }

        [TestMethod]
        public void DelegateAndPointerMembersReportNino007AndAreSkipped()
        {
            var compiled = CornerCompiler.Compile(@"
using System;
using Nino.Core;
namespace C
{
    [NinoType]
    public unsafe class Holder
    {
        public Action Callback;
        public Func<int, int> Map;
        public int* Pointer;
        public delegate*<void> FunctionPointer;
        public int Id;
        public string Name;
    }

    public static unsafe class Api
    {
        public static string Run()
        {
            int x = 1;
            var bytes = NinoSerializer.Serialize(new Holder { Callback = () => { }, Map = i => i, Pointer = &x, Id = 4, Name = ""n"" });
            var back = NinoDeserializer.Deserialize<Holder>(bytes);
            return (back.Callback == null) + "","" + (back.Map == null) + "","" + (back.Pointer == null) + "","" + back.Id + back.Name;
        }
    }
}", allowUnsafe: true);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            var nino007 = compiled.AnalyzerDiagnostics.Where(d => d.Id == "NINO007").Select(d => d.GetMessage()).ToList();
            Assert.AreEqual(4, nino007.Count, string.Join("\n", nino007));
            foreach (var name in new[] { "Callback", "Map", "Pointer", "FunctionPointer" })
                Assert.IsTrue(nino007.Any(m => m.Contains("'" + name + "'")), name);
            var result = compiled.Assembly.GetType("C.Api")!.GetMethod("Run")!.Invoke(null, null);
            Assert.AreEqual("True,True,True,4n", result);
        }
    }
}
