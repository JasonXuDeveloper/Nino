using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.GenericPolymorphism
{
    // Every shape of a generic type that has subtypes: a generic abstract class or interface, an abstract class nested
    // in a generic type, generic subtypes of non-generic bases, in each format. A generic type lists no closed subtypes,
    // so its formatter reaches them through the registry; before that, the generator emitted a static dispatch with no
    // root for these types and the project did not compile. The types below compile with the generated code, so a
    // regression in any of them fails the build of this project.

    [NinoType(false)]
    public abstract class GenericPolyBase
    {
        [NinoMember(1)] public int Id;
    }

    [NinoType(false)]
    public abstract class GenericPolyBox<T> : GenericPolyBase
    {
        [NinoMember(2)] public T Value;
    }

    [NinoType(false)]
    public sealed class GenericPolyIntBox : GenericPolyBox<int>
    {
    }

    [NinoType(false)]
    public abstract class NonGenericPolyBase
    {
        [NinoMember(1)] public int Id;
    }

    [NinoType(false)]
    public sealed class NonGenericPolyLeaf : NonGenericPolyBase
    {
        [NinoMember(2)] public int Value;
    }

    [NinoType] public abstract class PureBox<T> { public T Value; }
    [NinoType] public sealed class PureIntBox : PureBox<int> { public int X; }

    [NinoType(format: NinoFormat.VersionTolerant)] public abstract class VtBox<T> { public T Value; }
    [NinoType(format: NinoFormat.VersionTolerant)] public sealed class VtIntBox : VtBox<int> { public int X; }

    [NinoType(format: NinoFormat.Keyed)] public abstract class KeyedBox<T> { public T Value; }
    [NinoType(format: NinoFormat.Keyed)] public sealed class KeyedIntBox : KeyedBox<int> { public int X; }

    [NinoType(trackReferences: true)] public abstract class TrackedBox<T> { public T Value; }
    [NinoType(trackReferences: true)] public sealed class TrackedIntBox : TrackedBox<int> { public TrackedBox<int> Next; }

    [NinoType(trackReferences: true)] public abstract class TrackedBase { public int A; }
    [NinoType(trackReferences: true)] public sealed class TrackedGenericLeaf<T> : TrackedBase { public T Value; }

    [NinoType] public class ClassBase { public int A; }
    [NinoType] public class ClassBox<T> : ClassBase { public T Value; }
    [NinoType] public sealed class ClassIntBox : ClassBox<int> { public int B; }

    [NinoType] public abstract class Chain1<T> { public T A; }
    [NinoType] public abstract class Chain2<T, U> : Chain1<T> { public U B; }
    [NinoType] public sealed class ChainLeaf : Chain2<int, string> { public int C; }

    [NinoType] public abstract class SealedGenericBase { public int A; }
    [NinoType] public sealed class SealedGenericLeaf<T> : SealedGenericBase { public T Value; }

    [NinoType] public interface IGenericA<T> { }
    [NinoType] public interface IGenericB<T> : IGenericA<T> { }
    [NinoType] public sealed class GenericInterfaceLeaf : IGenericB<int> { public int V; }

    [NinoType] public interface IGenericValue<T> { }
    [NinoType] public struct GenericValueStruct<T> : IGenericValue<T> { public T V; }
    [NinoType] public struct IntValueStruct : IGenericValue<int> { public int V; }

    [NinoType] public interface IVariant<out T> { }
    [NinoType] public sealed class VariantLeaf : IVariant<string> { public string S; }

    [NinoType] public interface IStructConstrained<T> where T : struct { }
    [NinoType] public sealed class StructConstrainedLeaf : IStructConstrained<DateTime> { public DateTime D; }

    [NinoType] public interface IShape { }
    [NinoType] public sealed class GenericShape<T> : IShape { public T V; }
    [NinoType] public sealed class Square : IShape { public int S; }

    public partial class Container<T>
    {
        [NinoType] public abstract partial class Inner { public int A; }
        [NinoType] public sealed partial class InnerLeaf : Inner { public int B; }
    }

    public partial class GenericNodeHolder<T>
    {
        [NinoType] public abstract partial class Node { public T V; }
        [NinoType] public sealed partial class Leaf : Node { public int W; }
    }

    public partial class Outer<T>
    {
        public partial class Middle<U>
        {
            [NinoType] public abstract partial class Deep { public T A; public U B; }
            [NinoType] public sealed partial class DeepLeaf : Deep { public int C; }
        }
    }

    [NinoType] public sealed class Payload { public int P; }
    [NinoType] public abstract class Constrained<T> where T : class, new() { public T Value; }
    [NinoType] public sealed class ConstrainedLeaf : Constrained<Payload> { }

    [NinoType(allowInheritance: false)] public abstract class NoInheritance<T> { public T V; }

    [NinoType] public abstract record RecordBase<T>(T V);
    [NinoType] public sealed record IntRecord(int V, int W) : RecordBase<int>(V);

    [NinoType] public abstract class Wrap<T> { public T V; }
    [NinoType] public sealed class WrapList<T> : Wrap<List<T>> { }

    [NinoType] public abstract class WithConstructor<T> { public T V; }
    [NinoType] public sealed class ConstructorLeaf : WithConstructor<int>
    {
        public readonly int W;
        [NinoConstructor(nameof(V), nameof(W))] public ConstructorLeaf(int v, int w) { V = v; W = w; }
    }
    [NinoType] public abstract class NonGenericMiddle : WithConstructor<string> { public int M; }
    [NinoType] public sealed class NonGenericMiddleLeaf : NonGenericMiddle { public int L; }

    [NinoType] public abstract class ThreeParameters<T1, T2, T3> { public T1 A; public T2 B; public T3 C; }
    [NinoType] public sealed class ThreeParametersLeaf : ThreeParameters<int, string, List<int>> { }

    [NinoType] public abstract class SelfReferencing<T> where T : SelfReferencing<T> { public T Next; }
    [NinoType] public sealed class SelfReferencingLeaf : SelfReferencing<SelfReferencingLeaf> { public int V; }

    [NinoType] public abstract class CollectionsOfT<T> { public T[] Items; public Dictionary<string, T> Map; }
    [NinoType] public sealed class CollectionsOfNullableInt : CollectionsOfT<int?> { }

    [NinoType] public sealed class Holder
    {
        public PureBox<int> Single;
        public List<PureBox<int>> Many;
        public IGenericA<int> ByInterface;
    }

    [TestClass]
    public class GenericPolymorphicFormatterTests
    {
        // Through TBase: the runtime type, the values and the bytes of a second write survive, and null stays null.
        private static TBase RoundTrip<TBase>(TBase value, Func<TBase, bool> check)
        {
            byte[] bytes = NinoSerializer.Serialize<TBase>(value);
            Assert.IsTrue(bytes.Length > 0);
            var result = NinoDeserializer.Deserialize<TBase>(bytes);
            Assert.IsNotNull(result);
            Assert.AreEqual(value.GetType(), result.GetType());
            Assert.IsTrue(check(result), $"values of {value.GetType().Name} through {typeof(TBase).Name}");
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize<TBase>(result));
            if (!typeof(TBase).IsValueType)
                Assert.IsNull(NinoDeserializer.Deserialize<TBase>(NinoSerializer.Serialize<TBase>(default)));
            return result;
        }

        [TestMethod]
        public void GenericAbstractUnderNonGenericBase()
        {
            RoundTrip<GenericPolyBase>(new GenericPolyIntBox { Id = 7, Value = 42 },
                r => r.Id == 7 && ((GenericPolyIntBox)r).Value == 42);
            RoundTrip<GenericPolyBox<int>>(new GenericPolyIntBox { Id = 7, Value = 42 }, r => r.Id == 7 && r.Value == 42);
            RoundTrip<NonGenericPolyBase>(new NonGenericPolyLeaf { Id = 3, Value = 4 },
                r => r.Id == 3 && ((NonGenericPolyLeaf)r).Value == 4);
        }

        [TestMethod]
        public void GenericAbstractInEachFormat()
        {
            RoundTrip<PureBox<int>>(new PureIntBox { Value = 1, X = 2 }, r => r.Value == 1 && ((PureIntBox)r).X == 2);
            RoundTrip<VtBox<int>>(new VtIntBox { Value = 1, X = 2 }, r => r.Value == 1 && ((VtIntBox)r).X == 2);
            RoundTrip<KeyedBox<int>>(new KeyedIntBox { Value = 1, X = 2 }, r => r.Value == 1 && ((KeyedIntBox)r).X == 2);
            var tracked = new TrackedIntBox { Value = 3 };
            tracked.Next = tracked;
            RoundTrip<TrackedBox<int>>(tracked, r => r.Value == 3 && ((TrackedIntBox)r).Next == r);
            RoundTrip<TrackedBase>(new TrackedGenericLeaf<int> { A = 1, Value = 2 },
                r => r.A == 1 && ((TrackedGenericLeaf<int>)r).Value == 2);
        }

        [TestMethod]
        public void GenericClassAndChains()
        {
            RoundTrip<ClassBase>(new ClassIntBox { A = 1, Value = 2, B = 3 }, r => ((ClassIntBox)r).B == 3);
            RoundTrip<ClassBox<int>>(new ClassIntBox { A = 1, Value = 2, B = 3 }, r => r.Value == 2 && ((ClassIntBox)r).B == 3);
            RoundTrip<ClassBox<int>>(new ClassBox<int> { A = 1, Value = 2 }, r => r.A == 1 && r.Value == 2);
            RoundTrip<Chain1<int>>(new ChainLeaf { A = 1, B = "b", C = 3 }, r => r.A == 1 && ((ChainLeaf)r).B == "b");
            RoundTrip<Chain2<int, string>>(new ChainLeaf { A = 1, B = "b", C = 3 }, r => r.B == "b" && ((ChainLeaf)r).C == 3);
            RoundTrip<SealedGenericBase>(new SealedGenericLeaf<int> { A = 1, Value = 2 },
                r => ((SealedGenericLeaf<int>)r).Value == 2);
            RoundTrip<WithConstructor<int>>(new ConstructorLeaf(1, 2), r => r.V == 1 && ((ConstructorLeaf)r).W == 2);
            RoundTrip<WithConstructor<string>>(new NonGenericMiddleLeaf { V = "v", M = 1, L = 2 },
                r => r.V == "v" && ((NonGenericMiddleLeaf)r).L == 2);
            RoundTrip<NonGenericMiddle>(new NonGenericMiddleLeaf { V = "v", M = 1, L = 2 }, r => r.M == 1);
        }

        [TestMethod]
        public void GenericInterfaces()
        {
            RoundTrip<IGenericA<int>>(new GenericInterfaceLeaf { V = 5 }, r => ((GenericInterfaceLeaf)r).V == 5);
            RoundTrip<IGenericB<int>>(new GenericInterfaceLeaf { V = 5 }, r => ((GenericInterfaceLeaf)r).V == 5);
            RoundTrip<IGenericValue<int>>(new IntValueStruct { V = 4 }, r => ((IntValueStruct)r).V == 4);
            RoundTrip<IGenericValue<int>>(new GenericValueStruct<int> { V = 4 }, r => ((GenericValueStruct<int>)r).V == 4);
            RoundTrip<IVariant<string>>(new VariantLeaf { S = "s" }, r => ((VariantLeaf)r).S == "s");
            RoundTrip<IVariant<object>>(new VariantLeaf { S = "s" }, r => ((VariantLeaf)r).S == "s");
            RoundTrip<IStructConstrained<DateTime>>(new StructConstrainedLeaf { D = new DateTime(2020, 1, 2) },
                r => ((StructConstrainedLeaf)r).D == new DateTime(2020, 1, 2));
            RoundTrip<IShape>(new GenericShape<int> { V = 6 }, r => ((GenericShape<int>)r).V == 6);
            RoundTrip<IShape>(new Square { S = 7 }, r => ((Square)r).S == 7);
        }

        [TestMethod]
        public void AbstractNestedInGeneric()
        {
            RoundTrip<Container<int>.Inner>(new Container<int>.InnerLeaf { A = 1, B = 2 },
                r => r.A == 1 && ((Container<int>.InnerLeaf)r).B == 2);
            RoundTrip<GenericNodeHolder<int>.Node>(new GenericNodeHolder<int>.Leaf { V = 1, W = 2 },
                r => r.V == 1 && ((GenericNodeHolder<int>.Leaf)r).W == 2);
            RoundTrip<Outer<int>.Middle<string>.Deep>(new Outer<int>.Middle<string>.DeepLeaf { A = 1, B = "b", C = 3 },
                r => r.B == "b" && ((Outer<int>.Middle<string>.DeepLeaf)r).C == 3);
        }

        [TestMethod]
        public void ConstraintsRecordsAndTypeArguments()
        {
            RoundTrip<Constrained<Payload>>(new ConstrainedLeaf { Value = new Payload { P = 9 } }, r => r.Value.P == 9);
            RoundTrip<RecordBase<int>>(new IntRecord(1, 2), r => r.V == 1 && ((IntRecord)r).W == 2);
            RoundTrip<Wrap<List<int>>>(new WrapList<int> { V = new List<int> { 1, 2 } }, r => r.V.SequenceEqual(new[] { 1, 2 }));
            RoundTrip<ThreeParameters<int, string, List<int>>>(new ThreeParametersLeaf { A = 1, B = "x", C = new List<int> { 3 } },
                r => r.A == 1 && r.B == "x" && r.C[0] == 3);
            RoundTrip<SelfReferencing<SelfReferencingLeaf>>(new SelfReferencingLeaf { V = 1, Next = new SelfReferencingLeaf { V = 2 } },
                r => r.Next.V == 2 && r.Next.Next == null);
            RoundTrip<CollectionsOfT<int?>>(new CollectionsOfNullableInt
                {
                    Items = new int?[] { 1, null },
                    Map = new Dictionary<string, int?> { ["a"] = 2, ["b"] = null }
                },
                r => r.Items[0] == 1 && r.Items[1] == null && r.Map["a"] == 2 && r.Map["b"] == null);
            // allowInheritance: false has no subtypes to write; its formatter still compiles and writes null.
            Assert.IsNull(NinoDeserializer.Deserialize<NoInheritance<int>>(NinoSerializer.Serialize<NoInheritance<int>>(null)));
        }

        [TestMethod]
        public void AsMembersAndElements()
        {
            var holder = new Holder
            {
                Single = new PureIntBox { Value = 1, X = 2 },
                Many = new List<PureBox<int>> { new PureIntBox { Value = 3, X = 4 }, null },
                ByInterface = new GenericInterfaceLeaf { V = 5 }
            };
            byte[] bytes = NinoSerializer.Serialize(holder);
            var result = NinoDeserializer.Deserialize<Holder>(bytes);
            Assert.AreEqual(2, ((PureIntBox)result.Single).X);
            Assert.AreEqual(2, result.Many.Count);
            Assert.AreEqual(4, ((PureIntBox)result.Many[0]).X);
            Assert.IsNull(result.Many[1]);
            Assert.AreEqual(5, ((GenericInterfaceLeaf)result.ByInterface).V);
            CollectionAssert.AreEqual(bytes, NinoSerializer.Serialize(result));

            var chain = new List<Chain1<int>> { new ChainLeaf { A = 1, B = "x", C = 2 }, null };
            var chainResult = NinoDeserializer.Deserialize<List<Chain1<int>>>(NinoSerializer.Serialize(chain));
            Assert.AreEqual(2, ((ChainLeaf)chainResult[0]).C);
            Assert.IsNull(chainResult[1]);
        }

        [TestMethod]
        public void DataOfAnotherTypeIsRejected()
        {
            // The id names a registered type that is not a subtype of the type being read.
            byte[] square = NinoSerializer.Serialize<IShape>(new Square { S = 1 });
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<IGenericA<int>>(square));
            byte[] leaf = NinoSerializer.Serialize<GenericPolyBase>(new GenericPolyIntBox { Id = 1, Value = 2 });
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<PureBox<int>>(leaf));
            byte[] keyed = NinoSerializer.Serialize<Chain1<int>>(new ChainLeaf { A = 1, B = "b", C = 2 });
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<Wrap<List<int>>>(keyed));
        }
    }
}
