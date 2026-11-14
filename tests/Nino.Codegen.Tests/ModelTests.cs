using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Codegen.Model;
using Nino.Core;
using Nino.Core.Internal;

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
            Assert.AreEqual(HeaderKind.Presence, integers.Header);
            Assert.AreEqual(NinoHash.Compute("Conformance.Integers"), integers.TypeId);
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
            Assert.AreEqual(NinoHash.Compute("Hp"), hp.Key);
            Assert.AreSequenceEqual(new[] { NinoHash.Compute("Health") }, hp.FormerKeys);
        }

        [TestMethod]
        public void PolymorphicTypesListWhatTheyAllow()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            var allowed = Type(model, "Conformance.Animal").Assignable.Select(t => t.FullName).ToList();
            CollectionAssert.IsSubsetOf(new[] { "Conformance.Cat", "Conformance.Dog", "Conformance.Puppy" }, allowed);
            Assert.DoesNotContain("Conformance.Animal", allowed);
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
            Assert.AreSequenceEqual(new[] { 0, 4 }, gappy.Fields.Select(f => f.Offset).ToList());
        }

        [TestMethod]
        public void NullableStridesAreThoseNinoCoreWrites()
        {
            using var model = Load(GeneratedFilesTests.Shapes);
            var strides = Type(model, "Shapes.Strides");
            ValueModel Member(string name) => strides.Members.Single(m => m.Name == name).Value;
            ValueModel Element(string name) =>
                (name == "Boxed" ? Member(name).Arguments.Single() : Member(name)).Element!;
            // One element after the four-byte count: the stride .NET writes, the value after the flag's padding.
            void Check(string member, byte[] bytes, int valueSize)
            {
                var element = Element(member);
                Assert.AreEqual(ValueKind.RawNullable, element.Kind, member);
                Assert.AreEqual(bytes.Length - 4, element.Size, member);
                Assert.AreEqual(bytes.Length - 4 - valueSize, element.ValueOffset, member);
            }

            var cells = NinoSerializer.Serialize(new Shapes.TupleCell?[] { default(Shapes.TupleCell) });
            Check("Cells", cells, 12);
            Check("Boxed", cells, 12);
        }

        /// <summary>
        /// A <c>NinoNullable&lt;T&gt;</c> is the value a <c>T?</c> is, wherever it is declared: a member, an array or list
        /// element (the corpus's types) and inside type arguments (Shapes.cs), where no layout says so and nino-gen
        /// works the encoding out. .NET writes both declarations to the same bytes.
        /// </summary>
        [TestMethod]
        [DataRow("Shapes.NinoNullableArgs", "Shapes.NullableArgs")]
        public void NinoNullableIsTheValueOfNullable(string ninoNullable, string nullable)
        {
            using var model = Load(ninoNullable.StartsWith("Shapes.", StringComparison.Ordinal)
                ? GeneratedFilesTests.Shapes
                : GeneratedFilesTests.Conformance);
            var (actual, expected) = (Type(model, ninoNullable), Type(model, nullable));
            Assert.AreEqual(expected.Format, actual.Format);
            Assert.AreEqual(expected.Header, actual.Header);
            Assert.AreSequenceEqual(expected.Members.Select(m => m.Name).ToList(),
                actual.Members.Select(m => m.Name).ToList());
            foreach (var (a, e) in actual.Members.Zip(expected.Members))
            {
                Assert.AreNotEqual(ValueKind.Unsupported, a.Value.Kind, a.Name);
                Assert.AreEqual(Shape(e.Value), Shape(a.Value), a.Name);
            }
        }

        /// <summary>What a value is on the wire, without the .NET types it was declared with.</summary>
        private static string Shape(ValueModel v)
        {
            string text = $"{v.Kind} size={v.Size} primitive={v.Primitive} collection={v.Collection} " +
                          $"flag@{v.FlagOffset} value@{v.ValueOffset} packed={v.Packed} pair={v.IsPair} " +
                          $"enum={v.Enum?.Underlying} type={v.Type?.FullName} struct={v.Struct?.Size}" +
                          $"@[{string.Join(",", v.Struct?.Fields.Select(f => f.Offset) ?? Enumerable.Empty<int>())}] " +
                          $"offsets=[{string.Join(",", v.Offsets)}]";
            return $"{text} {{{string.Join("; ", v.Children().Select(Shape))}}}";
        }

        [TestMethod]
        public void NinoNullableTypeArgumentsAreWrittenAsNullableOnes()
        {
            var cell = new Shapes.TupleCell { A = 1 };
            var ninoNullable = new Shapes.NinoNullableArgs
            {
                Enum = Shapes.Flags.B,
                Enums = new NinoNullable<Shapes.Flags>[] { Shapes.Flags.A, default },
                One = new() { Value = 7 },
                Array = new() { Value = new NinoNullable<Shapes.TupleCell>[] { default, cell } },
                List = new() { Value = new List<NinoNullable<long>> { -1, default } },
                Set = new HashSet<NinoNullable<int>> { 4, default },
                Map = new Dictionary<int, NinoNullable<double>> { [1] = 0.5, [2] = default },
            };
            var nullable = new Shapes.NullableArgs
            {
                Enum = Shapes.Flags.B,
                Enums = new Shapes.Flags?[] { Shapes.Flags.A, null },
                One = new() { Value = 7 },
                Array = new() { Value = new Shapes.TupleCell?[] { null, cell } },
                List = new() { Value = new List<long?> { -1, null } },
                Set = new HashSet<int?> { 4, null },
                Map = new Dictionary<int, double?> { [1] = 0.5, [2] = null },
            };
            var bytes = NinoSerializer.Serialize(ninoNullable);
            Assert.AreSequenceEqual(NinoSerializer.Serialize(nullable), bytes);
            // The bytes the runtimes' tests of the generated shapes read and write (NinoNullableArgs).
            Assert.AreEqual(NinoNullableArgsHex, Convert.ToHexString(bytes).ToLowerInvariant());
            Assert.AreSequenceEqual(bytes, NinoSerializer.Serialize(NinoDeserializer.Deserialize<Shapes.NinoNullableArgs>(bytes)));
        }

        /// <summary>
        /// Inside a plain struct a <c>NinoNullable&lt;T&gt;</c> is its memory: a presence byte at the start of a whole
        /// <c>T</c>, then the value. The model has the flag and the value where Nino.Core writes them, for a
        /// <c>T</c> as large as its alignment (the bytes of <c>T?</c> there) and for a larger one (not those bytes).
        /// </summary>
        [TestMethod]
        public void NinoNullableInsideRawStructsIsWhereNinoCoreWritesIt()
        {
            using var model = Load(GeneratedFilesTests.Shapes);
            ValueModel Field(string type) =>
                Type(model, type).Members.Single(m => m.Name == "One").Value.Struct!.Fields.Single(f => f.Name == "B").Value;

            var narrow = Field("Shapes.NinoNullableRaw");
            Assert.AreEqual(ValueKind.RawNullable, narrow.Kind);
            Assert.AreEqual((0, 4, 8), (narrow.FlagOffset, narrow.ValueOffset, narrow.Size));
            var bytes = NinoSerializer.Serialize(new Shapes.NinoNullableRaw
            {
                One = new Shapes.HoldsNinoNullable { A = 1, B = 7 },
                Many = new[] { new Shapes.HoldsNinoNullable { A = 2 }, new Shapes.HoldsNinoNullable { A = 3, B = -1 } },
            });
            Assert.AreEqual(NinoNullableRawHex, Convert.ToHexString(bytes).ToLowerInvariant());
            // The flag of One.B at 4 of the struct (after the null flag), its value at 8.
            Assert.AreEqual(1, bytes[1 + 4 + narrow.FlagOffset]);
            Assert.AreEqual(7, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1 + 4 + narrow.ValueOffset)));

            var wide = Field("Shapes.NinoNullableRawWide");
            Assert.AreEqual(ValueKind.RawNullable, wide.Kind);
            Assert.AreEqual((0, 16, 32), (wide.FlagOffset, wide.ValueOffset, wide.Size));
            bytes = NinoSerializer.Serialize(new Shapes.NinoNullableRawWide
            {
                One = new Shapes.HoldsWideNinoNullable { A = 1, B = 1.5m },
                Many = new[] { new Shapes.HoldsWideNinoNullable { A = 2 } },
            });
            Assert.AreEqual(NinoNullableRawWideHex, Convert.ToHexString(bytes).ToLowerInvariant());
            Assert.AreEqual(1, bytes[1 + 8 + wide.FlagOffset]);
            Assert.AreEqual(15, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(1 + 8 + wide.ValueOffset + 8)));
        }

        /// <summary>What .NET writes for the values above, which the runtimes' tests of the generated shapes hold too.</summary>
        internal const string NinoNullableRawHex = "01010000000100000007000000020000000200000000000000000000000300000001000000ffffffff";

        /// <summary>Rust leaves the type out (TargetTests.RustLeavesOutANinoNullableWiderThanItsAlignmentInRawMemory).</summary>
        internal const string NinoNullableRawWideHex = 
            "0101000000000000000100000000000000000000000000000000000100000000000f000000000000000100" +
            "000002000000000000000000000000000000000000000000000000000000000000000000000000000000";

        /// <summary>
        /// What .NET writes for the value above, which runtimes/typescript/test/shapes.test.ts,
        /// runtimes/go/internal/generated/shapes/shapes_test.go and runtimes/rust/tests/generated.rs hold too.
        /// </summary>
        internal const string NinoNullableArgsHex =
            "0101000000000001000002000000010000000000000001000000000000000000000000000000000000000000000001010700000001020000000000" +
            "00000000000000000000000000000100000001000000000000000000000001020000000100000000000000ffffffffffffffff0000000000000000" +
            "000000000000000002000000010400000000020000000100000001000000000000e03f0200000000";

        /// <summary>
        /// The leaves whose layout differs by runtime (Int128, nint, nuint, pointers, delegate*, DateTimeOffset) are where
        /// Nino.Core writes them: each field of the model, read at its offset from .NET's bytes, is the value written.
        /// </summary>
        [TestMethod]
        public unsafe void HostDependentLeavesAreWhereNinoCoreWritesThem()
        {
            using var model = Load(GeneratedFilesTests.Shapes);
            ValueModel Member(string type, string name) =>
                Type(model, type).Members.Single(m => m.Name == name).Value;
            RawFieldModel Field(RawStructModel s, string name) => s.Fields.Single(f => f.Name == name);

            var leaves = Member("Shapes.HostHolder", "Leaves").Struct!;
            var bytes = NinoSerializer.Serialize(new Shapes.HostLeaves
            {
                A = 0xA5, Wide = (System.Int128)long.MinValue * 3, Signed = -3, Unsigned = 0xFFFFFFFE,
                Pointer = (int*)0x12345678, Callback = (delegate*<int>)(nuint)0x87654321u,
            });
            Assert.AreEqual(bytes.Length, leaves.Size);
            Assert.AreEqual(0xA5, bytes[Field(leaves, "A").Offset]);
            var wide = Field(leaves, "Wide");
            Assert.AreEqual(PrimitiveKind.I128, wide.Value.Primitive);
            Assert.AreEqual((System.Int128)long.MinValue * 3,
                BinaryPrimitives.ReadInt128LittleEndian(bytes.AsSpan(wide.Offset)));
            void Check(string name, PrimitiveKind kind, ulong expected)
            {
                var field = Field(leaves, name);
                Assert.AreEqual(kind, field.Value.Primitive, name);
                Assert.AreEqual(8, field.Value.Size, name);
                Assert.AreEqual(expected, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(field.Offset)), name);
            }

            Check("Signed", PrimitiveKind.NInt, unchecked((ulong)-3L));
            Check("Unsigned", PrimitiveKind.NUInt, 0xFFFFFFFE);
            Check("Pointer", PrimitiveKind.NUInt, 0x12345678);
            Check("Callback", PrimitiveKind.NUInt, 0x87654321);

            // Int128? at its stride: the flag, then the value at 16.
            var gap = Member("Shapes.HostHolder", "Gaps").Element!;
            var gaps = NinoSerializer.Serialize(new System.Int128?[] { -5 });
            Assert.AreEqual(ValueKind.RawNullable, gap.Kind);
            Assert.AreEqual(gaps.Length - 4, gap.Size);
            Assert.AreEqual((System.Int128)(-5), BinaryPrimitives.ReadInt128LittleEndian(gaps.AsSpan(4 + gap.ValueOffset)));

            // DateTimeOffset as its canonical fields: the minutes an int, the UTC ticks a DateTime of kind Unspecified.
            var leaf = Member("Shapes.OffsetHolder", "Leaf").Struct!;
            var at = new System.DateTimeOffset(2024, 2, 29, 23, 59, 58, System.TimeSpan.FromMinutes(-570)).AddTicks(7);
            var offsetBytes = NinoSerializer.Serialize(new Shapes.OffsetLeaf { A = -1, At = at });
            Assert.AreEqual(offsetBytes.Length, leaf.Size);
            var field = Field(leaf, "At");
            var dto = field.Value.Struct!;
            var minutes = Field(dto, "offsetMinutes");
            var ticks = Field(dto, "dateTime");
            Assert.AreEqual(PrimitiveKind.I32, minutes.Value.Primitive);
            Assert.AreEqual(PrimitiveKind.DateTime, ticks.Value.Primitive);
            Assert.AreEqual(-570, BinaryPrimitives.ReadInt32LittleEndian(offsetBytes.AsSpan(field.Offset + minutes.Offset)));
            Assert.AreEqual(at.UtcTicks, BinaryPrimitives.ReadInt64LittleEndian(offsetBytes.AsSpan(field.Offset + ticks.Offset)));
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
            Assert.AreSequenceEqual(new[] { "T" }, bag.TypeParameters.ToList());
            Assert.AreSequenceEqual(new[] { "TKey", "TValue" },
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
            Assert.HasCount(2, complex.Arguments);
            Assert.AreEqual(ValueKind.Map, complex.Arguments[1].Kind);
            // A closed generic [NinoType] struct as a member is written by its formatter, member by member, even when
            // its arguments are unmanaged: its definition has fields of a type parameter, so it is never raw.
            Assert.AreEqual(ValueKind.Generic, Member("Raw").Kind);

            Assert.IsNull(CodegenModel.GenericProblem(bag));
            Assert.IsNull(CodegenModel.GenericProblem(node));
            // Written behind a type id, the hash of its closed .NET name: each closed type is a slot of its own.
            var open = Type(model, "Shapes.Generic.Open`1");
            Assert.IsNull(CodegenModel.GenericProblem(open));
            var openUse = Type(model, "Shapes.Generic.OpenUse").Members.Single().Value;
            Assert.AreEqual(ValueKind.Generic, openUse.Kind);
            Assert.AreEqual("Shapes.Generic.Open<System.Int32>", openUse.Slot!.RuntimeName);
        }

        [TestMethod]
        public void SlotsOfGenericTypesAllowTheClosedTypesOfTheirSubtypes()
        {
            using var model = Load(GeneratedFilesTests.Shapes);
            var names = model.ClosedTypes.Select(c => c.RuntimeName).ToList();
            // Reached through OpenHolder<long>'s slots of Open<T>, though the assembly names neither.
            CollectionAssert.IsSubsetOf(new[]
            {
                "Shapes.Generic.Open<System.Int64>", "Shapes.Generic.MoreOpen<System.Int64>",
                "Shapes.Generic.MoreOpen<System.Int32>",
            }, names);
            var slot = model.Slots.Single(s => s.RuntimeName == "Shapes.Generic.Open<System.Int64>");
            Assert.AreSequenceEqual(new[] { "Shapes.Generic.MoreOpen<System.Int64>", "Shapes.Generic.Open<System.Int64>" },
                slot.ClosedAssignable.Select(c => c.RuntimeName).ToList(), SequenceOrder.InAnyOrder);
            // OpenHolder<T>'s codec is given the slot of Open<T>; OpenHolder<long> gives it Open<long>'s.
            var holder = Type(model, "Shapes.Generic.OpenHolder`1");
            Assert.AreEqual("Shapes.Generic.Open`1[!0]", holder.SlotParameters.Single().TypeKey);
            var use = Type(model, "Shapes.Generic.OpenHolderUse").Members.Single().Value;
            Assert.AreEqual("Shapes.Generic.Open<System.Int64>", use.SlotArguments.Single().Slot!.RuntimeName);
            // MoreOpen<T> sits in the slots of itself and of Open<T>, with its own parameter.
            var more = Type(model, "Shapes.Generic.MoreOpen`1");
            Assert.AreSequenceEqual(new[] { "Shapes.Generic.MoreOpen`1[!0]", "Shapes.Generic.Open`1[!0]" },
                more.SlotBases.Select(b => b.TypeKey).ToList(), SequenceOrder.InAnyOrder);
        }

        [TestMethod]
        public void ClosedGenericTypesAreThoseTheAssemblyNames()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            var names = model.ClosedTypes.Select(c => c.RuntimeName).ToList();
            // Registered (NinoPolymorphic.TryRegister<Box<int>>()) and created (new Box<int>) by the corpus's code.
            CollectionAssert.IsSubsetOf(new[]
            {
                "Conformance.Box<System.Int32>", "Conformance.Box<System.String>", "Conformance.Box<Conformance.Vec2>",
                "Conformance.Box<System.Collections.Generic.List<Conformance.Point>>",
            }, names);
            // A closed type no code names is not one (the corpus's malicious case generic-not-in-use).
            Assert.DoesNotContain("Conformance.Box<System.Guid>", names);
            var box = model.ClosedTypes.Single(c => c.RuntimeName == "Conformance.Box<System.Int32>");
            Assert.AreEqual(NinoHash.Compute("Conformance.Box<System.Int32>"), box.TypeId);
            Assert.AreEqual("Conformance.Box`1", box.Definition.FullName);
            Assert.Contains(box, Type(model, "Conformance.Animal").ClosedAssignable);
            Assert.DoesNotContain(box, Type(model, "Conformance.IShape").ClosedAssignable);
        }

        [TestMethod]
        public void RecordsAreNotDeferrable()
        {
            using var model = Load(GeneratedFilesTests.Conformance);
            Assert.IsFalse(Type(model, "Conformance.RecordChain").Deferrable);
            Assert.IsTrue(Type(model, "Conformance.Chain").Deferrable);
        }
    }
}
