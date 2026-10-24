using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Generated;
using Nino.UnitTests.Corner.Elsewhere;

#nullable disable
namespace Nino.UnitTests.Corner
{
    [NinoType]
    public abstract class AbstractShape
    {
        public string Name;
        public abstract double Area { get; }
    }

    [NinoType]
    public class SquareShape : AbstractShape
    {
        public double Side;
        public override double Area => Side * Side;
    }

    [NinoType]
    public sealed class SealedCircle : AbstractShape
    {
        public double Radius;
        public override double Area => Math.PI * Radius * Radius;
    }

    [NinoType]
    public interface IEntity
    {
        long Uid { get; set; }
    }

    [NinoType]
    public class PlayerEntity : IEntity
    {
        public long Uid { get; set; }
        public string Nick;
    }

    [NinoType]
    public struct StructEntity : IEntity
    {
        public long Uid { get; set; }
        public int Level;
    }

    // 6 levels, each adds a member.
    [NinoType]
    public class Deep1
    {
        public int L1;
    }

    [NinoType]
    public class Deep2 : Deep1
    {
        public string L2;
    }

    [NinoType]
    public abstract class Deep3 : Deep2
    {
        public List<int> L3;
    }

    [NinoType]
    public class Deep4 : Deep3
    {
        public double L4;
    }

    [NinoType]
    public class Deep5 : Deep4
    {
        public Deep1 L5;
    }

    [NinoType]
    public sealed class Deep6 : Deep5
    {
        public long L6;
    }

    [NinoType]
    public class GenericBase<T>
    {
        public T BaseValue;
    }

    [NinoType]
    public class IntSub : GenericBase<int>
    {
        public string Extra;
    }

    [NinoType]
    public class GenericSubOfGeneric<T> : GenericBase<T>
    {
        public T Second;
    }

    [NinoType]
    public class NonGenericBase
    {
        public int Id;
    }

    [NinoType]
    public class GenericSub<T> : NonGenericBase
    {
        public T Payload;
    }

    [NinoType]
    public class NonGenericSubOfGenericSub : GenericSub<string>
    {
        public int More;
    }

    [NinoType(allowInheritance: false)]
    public class NoInheritance
    {
        public int A;
    }

    [NinoType]
    public class MarkedSubOfNoInheritance : NoInheritance
    {
        public int B;
    }

#pragma warning disable NINO003
    public class UnmarkedSubOfNoInheritance : NoInheritance
    {
        public int C;
    }
#pragma warning restore NINO003

    [NinoType]
    [NinoFormerName("Old.Namespace.LegacyItem")]
    public class RenamedItem
    {
        public int Value;
    }

    [NinoType]
    [NinoFormerName("Old.Namespace.LegacySub")]
    public class RenamedSub : RenamedItem
    {
        public string Text;
    }

    [NinoType]
    public class PolyContainer
    {
        public AbstractShape Shape;
        public List<AbstractShape> Shapes;
        public IEntity Entity;
        public IEntity[] Entities;
        public Deep1 Deep;
        public NonGenericBase Generic;
        public RenamedItem Renamed;
    }

    // Names GenericSub<DateTimeKind> as a member type and nowhere else: the generated registry maps its id.
    [NinoType]
    public class ClosedGenericHolder
    {
        public List<GenericSub<DateTimeKind>> Held;
    }
}

namespace Nino.UnitTests.Corner.Elsewhere
{
    [NinoType]
    public class TriangleShape : AbstractShape
    {
        public double Base, Height;
        public override double Area => Base * Height / 2;
    }

    public class Outer
    {
        [NinoType]
        public class NestedShape : AbstractShape
        {
            public int Sides;
            public override double Area => Sides;
        }
    }
}

namespace Nino.UnitTests.Corner
{
    [TestClass]
    public class CornerPolymorphismTests
    {
        [TestMethod]
        public void AbstractBaseWithSubtypesInOtherNamespacesAndNested()
        {
            var shapes = new List<AbstractShape>
            {
                new SquareShape { Name = "sq", Side = 2 }, new SealedCircle { Name = "c", Radius = 1 },
                new TriangleShape { Name = "t", Base = 2, Height = 3 }, new Outer.NestedShape { Name = "n", Sides = 5 },
                null,
            };
            var back = Rt.RoundTrip(shapes);
            Assert.AreEqual(5, back.Count);
            for (int i = 0; i < 4; i++)
            {
                Assert.AreEqual(shapes[i].GetType(), back[i].GetType());
                Assert.AreEqual(shapes[i].Name, back[i].Name);
                Assert.AreEqual(shapes[i].Area, back[i].Area);
            }

            Assert.IsNull(back[4]);

            // A sealed leaf as its own type: a 1-byte header instead of a type id.
            var circle = new SealedCircle { Radius = 2 };
            Assert.AreEqual(NinoSerializer.Serialize<AbstractShape>(circle).Length - 3, NinoSerializer.Serialize(circle).Length);
            Assert.AreEqual(2, Rt.RoundTrip(circle).Radius);
        }

        [TestMethod]
        public void InterfaceBaseWithClassAndStructImplementations()
        {
            var entities = new IEntity[] { new PlayerEntity { Uid = 1, Nick = "p" }, new StructEntity { Uid = 2, Level = 3 }, null };
            var back = Rt.RoundTrip(entities);
            Assert.AreEqual("p", ((PlayerEntity)back[0]).Nick);
            Assert.AreEqual(1, back[0].Uid);
            Assert.AreEqual(3, ((StructEntity)back[1]).Level);
            Assert.AreEqual(2, back[1].Uid);
            Assert.IsNull(back[2]);

            // Reuse deserialization: same class kept, struct replaced by value.
            var player = new PlayerEntity();
            var target = new IEntity[] { player, new StructEntity(), new PlayerEntity() };
            Rt.Into(entities, ref target);
            Assert.AreSame(player, target[0]);
            Assert.AreEqual("p", player.Nick);
            Assert.AreEqual(3, ((StructEntity)target[1]).Level);
            Assert.IsNull(target[2]);
        }

        [TestMethod]
        public void DeepHierarchyThroughEveryLevel()
        {
            var deep = new Deep6
            {
                L1 = 1, L2 = "2", L3 = new List<int> { 3 }, L4 = 4.5, L5 = new Deep4 { L1 = 51, L4 = 5.5 }, L6 = 6,
            };

            void Check(Deep1 result)
            {
                var d = (Deep6)result;
                Assert.AreEqual(1, d.L1);
                Assert.AreEqual("2", d.L2);
                CollectionAssert.AreEqual(deep.L3, d.L3);
                Assert.AreEqual(4.5, d.L4);
                Assert.AreEqual(typeof(Deep4), d.L5.GetType());
                Assert.AreEqual(5.5, ((Deep4)d.L5).L4);
                Assert.AreEqual(6, d.L6);
            }

            Check(Rt.RoundTrip<Deep1>(deep));
            Check(Rt.RoundTrip<Deep2>(deep));
            Check(Rt.RoundTrip<Deep3>(deep));
            Check(Rt.RoundTrip<Deep4>(deep));
            Check(Rt.RoundTrip<Deep5>(deep));
            Check(Rt.RoundTrip(deep));
            Check((Deep6)Rt.RoundTrip(new PolyContainer { Deep = deep }).Deep);

            // In place through the root type.
            Deep1 target = new Deep6 { L3 = new List<int> { 9, 9 } };
            var list = ((Deep6)target).L3;
            var before = target;
            Rt.Into<Deep1>(deep, ref target);
            Assert.AreSame(before, target);
            Assert.AreSame(list, ((Deep6)target).L3);
            Check(target);
        }

        [TestMethod]
        public void NonGenericSubtypesOfGenericTypes()
        {
            GenericBase<int> sub = new IntSub { BaseValue = 3, Extra = "x" };
            var back = (IntSub)Rt.RoundTrip(sub);
            Assert.AreEqual(3, back.BaseValue);
            Assert.AreEqual("x", back.Extra);

            NonGenericBase nonGeneric = new NonGenericSubOfGenericSub { Id = 1, Payload = "p", More = 2 };
            var ngBack = (NonGenericSubOfGenericSub)Rt.RoundTrip(nonGeneric);
            Assert.AreEqual("p", ngBack.Payload);
            Assert.AreEqual(2, ngBack.More);

            // The generic types as themselves.
            var gs = Rt.RoundTrip(new GenericSubOfGeneric<string> { BaseValue = "a", Second = "b" });
            Assert.AreEqual("a", gs.BaseValue);
            Assert.AreEqual("b", gs.Second);
            Assert.AreEqual(7, Rt.RoundTrip(new GenericSub<int> { Id = 1, Payload = 7 }).Payload);
        }

        [TestMethod]
        public void GenericSubtypeOfGenericBaseThroughTheBase()
        {
            // Naming the closed type in a Nino call first does not help.
            Rt.RoundTrip(new GenericSubOfGeneric<string> { Second = "warm" });
            GenericBase<string> genSub = new GenericSubOfGeneric<string> { BaseValue = "a", Second = "b" };
            var genBack = (GenericSubOfGeneric<string>)Rt.RoundTrip(genSub);
            Assert.AreEqual("a", genBack.BaseValue);
            Assert.AreEqual("b", genBack.Second);
        }

        [TestMethod]
        public void ClosedGenericSubtypeReadThroughNonGenericBase()
        {
            // Read by type id only: the closed generic type must be resolvable without being named by the reader.
            NonGenericBase value = new GenericSub<Guid> { Id = 4, Payload = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e") };
            var back = (GenericSub<Guid>)Rt.RoundTrip(value);
            Assert.AreEqual(4, back.Id);
            Assert.AreEqual(((GenericSub<Guid>)value).Payload, back.Payload);

            var container = Rt.RoundTrip(new PolyContainer { Generic = new GenericSub<List<string>> { Payload = new List<string> { "a" } } });
            CollectionAssert.AreEqual(new[] { "a" }, ((GenericSub<List<string>>)container.Generic).Payload);
        }

        [TestMethod]
        public void ClosedGenericNamedByAMemberIsReadableFromItsIdAlone()
        {
            // Nothing has written or resolved GenericSub<DateTimeKind>: the id is mapped by the generated registry.
            int id = NinoTypeId.Of(typeof(GenericSub<DateTimeKind>));
            Assert.IsFalse(NinoFormatterCache<GenericSub<DateTimeKind>>.IsRegistered);
            Assert.AreEqual(typeof(GenericSub<DateTimeKind>), NinoPolymorphic<NonGenericBase>.Find(id)?.Type);

            // Bytes as another process writes them: type id, then the body (Id, then Payload).
            var bytes = NinoSerializer.Serialize(new GenericSub<DateTimeKind> { Id = 9, Payload = DateTimeKind.Utc });
            var back = (GenericSub<DateTimeKind>)NinoDeserializer.Deserialize<NonGenericBase>(bytes);
            Assert.AreEqual(9, back.Id);
            Assert.AreEqual(DateTimeKind.Utc, back.Payload);
        }

        [TestMethod]
        public void ClosedGenericNeverNamedIsRegisteredAtRuntime()
        {
            // An instantiation no code names: unknown until registered (or written).
            var type = typeof(GenericSub<>).MakeGenericType(typeof(ConsoleColor));
            int id = NinoTypeId.Of(type);
            Assert.IsNull(NinoPolymorphic<NonGenericBase>.Find(id));
            Assert.IsTrue(NinoPolymorphic.Register(type));
            Assert.AreEqual(type, NinoPolymorphic<NonGenericBase>.Find(id)?.Type);
            Assert.IsTrue(NinoPolymorphic.Register<GenericSub<ConsoleColor>>(), "idempotent");

            // Not polymorphic: abstract, open, or not a [NinoType].
            Assert.IsFalse(NinoPolymorphic.Register(typeof(AbstractShape)));
            Assert.IsFalse(NinoPolymorphic.Register(typeof(GenericSub<>)));
            Assert.IsFalse(NinoPolymorphic.Register(typeof(List<int>)));
        }

        [TestMethod]
        public void AllowInheritanceFalseOnlyAcceptsExplicitlyMarkedSubtypes()
        {
            // A subtype marked [NinoType] itself is serializable, also through the base (NINO003 says so).
            var sub = new MarkedSubOfNoInheritance { A = 1, B = 2 };
            Assert.AreEqual(2, Rt.RoundTrip(sub).B);
            Assert.AreEqual(2, ((MarkedSubOfNoInheritance)Rt.RoundTrip<NoInheritance>(sub)).B);

            // An unmarked subtype does not inherit the attribute: the runtime rejects it through the base too,
            // rather than silently writing it as the base.
            var unmarked = new UnmarkedSubOfNoInheritance { A = 1, C = 3 };
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => NinoSerializer.Serialize<NoInheritance>(unmarked));
            StringAssert.Contains(ex.Message, nameof(UnmarkedSubOfNoInheritance));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                NinoSerializer.Serialize(new List<NoInheritance> { new NoInheritance(), unmarked }));

            Assert.AreEqual(5, Rt.RoundTrip(new NoInheritance { A = 5 }).A);
        }

        [TestMethod]
        public void FormerNameReadsOldData()
        {
            // What the old version (Old.Namespace.LegacyItem / LegacySub) wrote: the id of the old name, then the body.
            var current = NinoSerializer.Serialize<RenamedItem>(new RenamedSub { Value = 3, Text = "t" });
            var expectedId = NinoTypeId.Compute("Old.Namespace.LegacySub");
            Assert.AreEqual(expectedId, BitConverter.ToInt32(current, 0));
            Assert.AreEqual(NinoTypeId.Compute("Old.Namespace.LegacyItem"), NinoTypeId.Of(typeof(RenamedItem)));

            var back = (RenamedSub)NinoDeserializer.Deserialize<RenamedItem>(current);
            Assert.AreEqual(3, back.Value);
            Assert.AreEqual("t", back.Text);

            var viaObject = NinoDeserializer.Deserialize<object>(NinoSerializer.Serialize<object>(new RenamedItem { Value = 8 }));
            Assert.AreEqual(8, ((RenamedItem)viaObject).Value);
            Assert.AreEqual(8, ((RenamedItem)Rt.RoundTrip(new PolyContainer { Renamed = new RenamedItem { Value = 8 } }).Renamed).Value);
        }

        [TestMethod]
        public void UnknownTypeIdThrowsClearly()
        {
            var bytes = NinoSerializer.Serialize<AbstractShape>(new SquareShape { Side = 1 });
            BitConverter.GetBytes(NinoTypeId.Compute("No.Such.Type")).CopyTo(bytes, 0);
            var ex = Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<AbstractShape>(bytes));
            StringAssert.Contains(ex.Message, "type id");

            // An id of a real but unrelated type.
            BitConverter.GetBytes(NinoTypeId.Of(typeof(PlayerEntity))).CopyTo(bytes, 0);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<AbstractShape>(bytes));
        }

        [TestMethod]
        public void IdsOfAbstractTypesAndInterfacesAreRejected()
        {
            // Crafted data naming the abstract base or the interface itself: nothing can be created.
            var bytes = NinoSerializer.Serialize<AbstractShape>(new SquareShape { Side = 1 });
            BitConverter.GetBytes(NinoTypeId.Of(typeof(AbstractShape))).CopyTo(bytes, 0);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<AbstractShape>(bytes));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<object>(bytes));

            var entity = NinoSerializer.Serialize<IEntity>(new PlayerEntity { Uid = 1 });
            BitConverter.GetBytes(NinoTypeId.Of(typeof(IEntity))).CopyTo(entity, 0);
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<IEntity>(entity));
        }

        [TestMethod]
        public void SubtypeOnlyInAHotUpdateAssembly()
        {
            var compiled = CornerCompiler.Compile(@"
using System.Collections.Generic;
using Nino.Core;
using Nino.UnitTests.Corner;
namespace HotCorner
{
    [NinoType] public class HotShape : AbstractShape { public int Hot; public List<AbstractShape> Children; public override double Area => Hot; }
    [NinoType] public sealed class HotEntity : IEntity { public long Uid { get; set; } public string Tag; }
    [NinoType] public class HotDeep : Deep6Alike { public int Extra; }
    [NinoType] public class Deep6Alike : Deep4 { public string Mid; }

    public static class Api
    {
        public static byte[] Shapes() => NinoSerializer.Serialize(new List<AbstractShape>
        {
            new HotShape { Name = ""hot"", Hot = 7, Children = new List<AbstractShape> { new SquareShape { Side = 2 } } },
            new SquareShape { Name = ""sq"" },
        });
        public static byte[] Container() => NinoSerializer.Serialize(new PolyContainer
        {
            Entity = new HotEntity { Uid = 9, Tag = ""tag"" },
            Deep = new HotDeep { L1 = 1, L4 = 4, Mid = ""m"", Extra = 5 },
        });
    }
}", load: true, extraReferences: typeof(AbstractShape).Assembly);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            var api = compiled.Assembly.GetType("HotCorner.Api")!;

            // The main assembly reads subtypes it has never seen, found by their type ids.
            var shapes = NinoDeserializer.Deserialize<List<AbstractShape>>((byte[])api.GetMethod("Shapes")!.Invoke(null, null)!);
            Assert.AreEqual("HotCorner.HotShape", shapes[0].GetType().FullName);
            Assert.AreEqual(7.0, shapes[0].Area);
            Assert.AreEqual("sq", shapes[1].Name);

            var container = NinoDeserializer.Deserialize<PolyContainer>((byte[])api.GetMethod("Container")!.Invoke(null, null)!);
            Assert.AreEqual(9, container.Entity.Uid);
            Assert.AreEqual("HotCorner.HotEntity", container.Entity.GetType().FullName);
            Assert.AreEqual(4.0, ((Deep4)container.Deep).L4);
            Assert.AreEqual(5, container.Deep.GetType().GetField("Extra")!.GetValue(container.Deep));

            // And writes them back identically.
            var again = NinoSerializer.Serialize(shapes);
            CollectionAssert.AreEqual((byte[])api.GetMethod("Shapes")!.Invoke(null, null)!, again);
        }
    }
}
