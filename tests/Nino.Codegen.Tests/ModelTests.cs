using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Codegen.Model;

namespace Nino.Codegen.Tests
{
    /// <summary>The layout model (the IR every target reads), from the corpus's types and Shapes.cs.</summary>
    [TestClass]
    public class ModelTests
    {
        private static CodegenModel Load(string path) => CodegenModel.Load(path, new List<string>());

        private static TypeModel Type(CodegenModel model, string name) =>
            model.Find(name) ?? throw new AssertFailedException("no " + name);

        [TestMethod]
        public void ReadsFormatsHeadersAndIds()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            var integers = Type(model, "Conformance.Integers");
            Assert.AreEqual(BodyFormat.Positional, integers.Format);
            Assert.AreEqual(HeaderKind.NullFlag, integers.Header);
            Assert.AreEqual(TypeNames.Hash("Conformance.Integers"), integers.TypeId);
            Assert.AreEqual(BodyFormat.VersionTolerant, Type(model, "Conformance.TolerantV2").Format);
            Assert.AreEqual(BodyFormat.Keyed, Type(model, "Conformance.KeyedV2").Format);
            Assert.AreEqual(HeaderKind.TypeId, Type(model, "Conformance.Animal").Header);
            Assert.AreEqual(HeaderKind.None, Type(model, "Conformance.KeyedStruct").Header);
            Assert.IsTrue(Type(model, "Conformance.Vec2").Raw);
            Assert.IsTrue(Type(model, "Conformance.Box`1").IsGenericDefinition);
            Assert.IsTrue(Type(model, "Conformance.Tracked").TrackReferences);
        }

        [TestMethod]
        public void KeyedMembersCarryTheirKeysAndFormerKeys()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            var hp = Type(model, "Conformance.KeyedV2").Members.Single(m => m.Name == "Hp");
            Assert.AreEqual(TypeNames.Hash("Hp"), hp.Key);
            CollectionAssert.AreEqual(new[] { TypeNames.Hash("Health") }, hp.FormerKeys);
        }

        [TestMethod]
        public void PolymorphicTypesListWhatTheyAllow()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            var allowed = Type(model, "Conformance.Animal").Assignable.Select(t => t.FullName).ToList();
            CollectionAssert.IsSubsetOf(new[] { "Conformance.Cat", "Conformance.Dog", "Conformance.Puppy" }, allowed);
            CollectionAssert.DoesNotContain(allowed, "Conformance.Animal");
            Assert.AreEqual(BodyFormat.Positional, Type(model, "Conformance.Puppy").BaseType!.Format);
            Assert.AreEqual("Conformance.Dog", Type(model, "Conformance.Puppy").BaseType!.FullName);
        }

        [TestMethod]
        public void RawStructsHaveTheirOffsetsAndPadding()
        {
            using var model = Load(GeneratedFilesTests.Shapes);
            var gappy = Type(model, "Shapes.Gappy").RawValue!.Struct!;
            Assert.AreEqual(8, gappy.Size);
            Assert.IsFalse(gappy.Packed);
            CollectionAssert.AreEqual(new[] { 0, 4 }, gappy.Fields.Select(f => f.Offset).ToList());
        }

        [TestMethod]
        public void ValuesResolveAgainstTheirDotNetTypes()
        {
            using var model = Load(GeneratedFilesTests.Shapes);
            var holder = Type(model, "Shapes.Holder");
            ValueModel Member(string name) => holder.Members.Single(m => m.Name == name).Value;
            Assert.AreEqual(ValueKind.Type, Member("Thing").Kind);
            Assert.AreEqual(ValueKind.Sequence, Member("Things").Kind);
            Assert.AreEqual(CollectionKind.Dictionary, Member("Names").Collection);
            Assert.AreEqual(ValueKind.Map, Member("Names").Kind);
            Assert.AreEqual(CollectionKind.HashSet, Member("Tags").Collection);
            Assert.AreEqual(ValueKind.Enum, Member("Flags").Kind);
            Assert.AreEqual(PrimitiveKind.I64, Member("Flags").Enum!.Underlying);
            Assert.IsTrue(Member("Flags").Enum!.Flags);
            Assert.AreEqual(ValueKind.Nullable, Member("Maybe").Kind);
            Assert.IsTrue(Member("Entry").IsPair);
            // List<Forest> where Forest : List<Forest>: a sequence of a collection of itself.
            var forest = Type(model, "Shapes.ForestHolder").Members[0].Value;
            Assert.AreEqual(ValueKind.Sequence, forest.Kind);
            Assert.AreEqual(ValueKind.Named, forest.Element!.Kind);
            Assert.IsTrue(model.NamedCollections.ContainsKey("Shapes.Forest"));
        }

        [TestMethod]
        public void GenericTypesKeepTheirParametersAndArguments()
        {
            using var model = Load(GeneratedFilesTests.Shapes);
            var bag = Type(model, "Shapes.Generic.Bag`1");
            CollectionAssert.AreEqual(new[] { "T" }, bag.TypeParameters.ToList());
            CollectionAssert.AreEqual(new[] { "TKey", "TValue" },
                Type(model, "Shapes.Generic.Pair`2").TypeParameters.ToList());
            ValueModel BagMember(string name) => bag.Members.Single(m => m.Name == name).Value;
            Assert.AreEqual(ValueKind.Parameter, BagMember("Items").Element!.Kind);
            Assert.AreEqual(0, BagMember("Items").Element!.Parameter);
            // Box<T> in Bag<T>: open (it holds T), and not Bag<T> itself.
            var inner = BagMember("Inner");
            Assert.AreEqual(ValueKind.Generic, inner.Kind);
            Assert.IsTrue(inner.IsOpen);
            Assert.IsFalse(inner.IsSelf(bag));
            var node = Type(model, "Shapes.Generic.Node`1");
            Assert.IsTrue(node.Members.Single(m => m.Name == "Next").Value.IsSelf(node));

            var uses = Type(model, "Shapes.Generic.Uses");
            ValueModel Member(string name) => uses.Members.Single(m => m.Name == name).Value;
            var nested = Member("Nested");
            Assert.AreEqual(ValueKind.Generic, nested.Kind);
            Assert.AreEqual("Shapes.Generic.Box`1", nested.Type!.FullName);
            Assert.IsFalse(nested.IsOpen);
            Assert.AreEqual(ValueKind.Generic, nested.Arguments.Single().Kind);
            Assert.AreEqual(ValueKind.Primitive, nested.Arguments.Single().Arguments.Single().Kind);
            var complex = Member("Complex");
            Assert.AreEqual(2, complex.Arguments.Count);
            Assert.AreEqual(ValueKind.Map, complex.Arguments[1].Kind);
            // A closed unmanaged generic struct as a member is raw, as before.
            Assert.AreEqual(ValueKind.RawStruct, Member("Raw").Kind);

            Assert.IsNull(CodegenModel.GenericProblem(bag));
            Assert.IsNull(CodegenModel.GenericProblem(node));
            // Written behind a type id, the hash of its closed .NET name: no other language can name it.
            Assert.IsNotNull(CodegenModel.GenericProblem(Type(model, "Shapes.Generic.Open`1")));
        }

        [TestMethod]
        public void ClosedGenericTypesAreThoseTheAssemblyNames()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            var names = model.ClosedTypes.Select(c => c.RuntimeName).ToList();
            // Registered (NinoPolymorphic.Register<Box<int>>()) and created (new Box<int>) by the corpus's code.
            CollectionAssert.IsSubsetOf(new[]
            {
                "Conformance.Box<System.Int32>", "Conformance.Box<System.String>", "Conformance.Box<Conformance.Vec2>",
                "Conformance.Box<System.Collections.Generic.List<Conformance.Point>>",
            }, names);
            // A closed type no code names is not one (the corpus's malicious case generic-not-in-use).
            CollectionAssert.DoesNotContain(names, "Conformance.Box<System.Guid>");
            var box = model.ClosedTypes.Single(c => c.RuntimeName == "Conformance.Box<System.Int32>");
            Assert.AreEqual(TypeNames.Hash("Conformance.Box<System.Int32>"), box.TypeId);
            Assert.AreEqual("Conformance.Box`1", box.Definition.FullName);
            CollectionAssert.Contains(Type(model, "Conformance.Animal").ClosedAssignable, box);
            CollectionAssert.DoesNotContain(Type(model, "Conformance.IShape").ClosedAssignable, box);
        }

        [TestMethod]
        public void RecordsAreNotDeferrable()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            Assert.AreEqual(false, Type(model, "Conformance.RecordChain").Deferrable);
            Assert.AreEqual(true, Type(model, "Conformance.Chain").Deferrable);
        }
    }
}
