using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Corner;

#nullable disable
namespace Nino.UnitTests.Corner
{
    [NinoType]
    public partial class CtorRenamedParams
    {
        public readonly int Id;
        public string Name { get; private set; }
        public readonly long Stamp;

        [NinoConstructor(nameof(Id), nameof(Name), nameof(Stamp))]
        public CtorRenamedParams(int a, string b, long c)
        {
            Id = a;
            Name = b;
            Stamp = c;
        }
    }

    [NinoType]
    public class CtorReorderedParams
    {
        public readonly int Id;
        public readonly string Name;
        public readonly double Score;

        // Parameters match members by name (any case), in another order than the members.
        public CtorReorderedParams(double score, string NAME, int id)
        {
            Id = id;
            Name = NAME;
            Score = score;
        }
    }

    [NinoType]
    public class CtorPlusSettable
    {
        public readonly int Id;
        public string Extra { get; set; }
        public List<int> Items;

        [NinoConstructor]
        public CtorPlusSettable(int id)
        {
            Id = id;
        }
    }

    [NinoType]
    public record PositionalRecord(int Id, string Name, List<int> Items);

    [NinoType]
    public record struct PositionalRecordStruct(int Id, string Name);

    [NinoType]
    public readonly record struct ReadonlyRecordStruct(int Id, long Value);

    [NinoType]
    public record RecordWithBody(int Id)
    {
        public string Note { get; set; }
        public List<int> Items { get; set; }
        public string Tag { get; init; }
    }

    [NinoType]
    public record DerivedRecord(int Id, string More) : RecordWithBody(Id);

    // ET-style pooled factory: an optional argument, and an instance that may carry stale state.
    [NinoType]
    public class EtStyle
    {
        public static readonly Stack<EtStyle> Pool = new Stack<EtStyle>();
        public static int Created;

        public bool IsFromPool;
        public long Id;
        public string Name;
        public List<int> Values;
        public Dictionary<int, string> Map;
        public EtStyleChild Child;

        [NinoFactory]
        public static EtStyle Create(bool isFromPool = false)
        {
            Created++;
            if (Pool.Count > 0)
                return Pool.Pop();
            return new EtStyle { IsFromPool = isFromPool, Values = new List<int>(), Map = new Dictionary<int, string>() };
        }
    }

    [NinoType]
    public class EtStyleChild
    {
        public static int Created;
        public int X;

        [NinoFactory]
        public static EtStyleChild Create(bool isFromPool = false, int capacity = 4)
        {
            Created++;
            return new EtStyleChild { X = -1 };
        }
    }

    [NinoType]
    public class FactoryBase
    {
        public static int BaseCreated;
        public int A;

        [NinoFactory]
        public static FactoryBase Create() => new FactoryBase { A = -1 };
    }

    [NinoType]
    public class FactorySubA : FactoryBase
    {
        public static int Created;
        public string B;

        [NinoFactory]
        public static new FactorySubA Create()
        {
            Created++;
            return new FactorySubA { B = "stale" };
        }
    }

    [NinoType]
    public class FactorySubB : FactoryBase
    {
        public static int Created;
        public double C;

        [NinoFactory]
        public static new FactorySubB Create()
        {
            Created++;
            return new FactorySubB { C = -1 };
        }
    }

    [NinoType]
    public class FactoryOwner
    {
        public FactoryBase Slot;
    }

    [TestClass]
    public class CornerConstructionTests
    {
        [TestMethod]
        public void NinoConstructorWithRenamedParameters()
        {
            var value = new CtorRenamedParams(3, "n", 99);
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(3, result.Id);
            Assert.AreEqual("n", result.Name);
            Assert.AreEqual(99, result.Stamp);

            var target = new CtorRenamedParams(1, "old", 1);
            Rt.Into(value, ref target);
            Assert.AreEqual(3, target.Id);
            Assert.AreEqual(99, target.Stamp);

            Assert.IsNull(Rt.RoundTrip<CtorRenamedParams>(null));
        }

        [TestMethod]
        public void ConstructorParametersInAnotherOrder()
        {
            var value = new CtorReorderedParams(2.5, "x", 7);
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(7, result.Id);
            Assert.AreEqual("x", result.Name);
            Assert.AreEqual(2.5, result.Score);
            // Members are written in declaration order, not constructor order.
            var expected = NinoSerializer.Serialize(7).Concat(NinoSerializer.Serialize("x"))
                .Concat(NinoSerializer.Serialize(2.5)).ToArray();
            var bytes = NinoSerializer.Serialize(value);
            CollectionAssert.AreEqual(expected, bytes.Skip(bytes.Length - expected.Length).ToArray());
        }

        [TestMethod]
        public void ConstructorPlusSettableMembers()
        {
            var value = new CtorPlusSettable(5) { Extra = "e", Items = new List<int> { 1 } };
            var result = Rt.RoundTrip(value);
            Assert.AreEqual(5, result.Id);
            Assert.AreEqual("e", result.Extra);
            CollectionAssert.AreEqual(value.Items, result.Items);

            var target = new CtorPlusSettable(1) { Extra = "old", Items = new List<int> { 9, 9 } };
            Rt.Into(value, ref target);
            Assert.AreEqual(5, target.Id);
            Assert.AreEqual("e", target.Extra);
            CollectionAssert.AreEqual(value.Items, target.Items);
        }

        [TestMethod]
        public void PositionalRecordsClassAndStruct()
        {
            var record = new PositionalRecord(1, "a", new List<int> { 1, 2 });
            var back = Rt.RoundTrip(record);
            Assert.AreEqual(1, back.Id);
            Assert.AreEqual("a", back.Name);
            CollectionAssert.AreEqual(record.Items, back.Items);
            Assert.IsNull(Rt.RoundTrip<PositionalRecord>(null));

            var target = new PositionalRecord(9, "z", null);
            Rt.Into(record, ref target);
            Assert.AreEqual(1, target.Id);

            var rs = new PositionalRecordStruct(2, "b");
            Assert.AreEqual(rs, Rt.RoundTrip(rs));
            var rsTarget = new PositionalRecordStruct(0, "old");
            Rt.Into(rs, ref rsTarget);
            Assert.AreEqual(rs, rsTarget);
            Assert.AreEqual(rs, Rt.RoundTrip<PositionalRecordStruct?>(rs));
            Assert.IsNull(Rt.RoundTrip<PositionalRecordStruct?>(null));

            var ro = new ReadonlyRecordStruct(3, long.MinValue);
            Assert.AreEqual(ro, Rt.RoundTrip(ro));
            CollectionAssert.AreEqual(new[] { ro, default }, Rt.RoundTrip(new[] { ro, default }));
        }

        [TestMethod]
        public void RecordWithBodyMembersAndInheritance()
        {
            var value = new RecordWithBody(4) { Note = "n", Items = new List<int> { 3 }, Tag = "t" };
            var back = Rt.RoundTrip(value);
            Assert.AreEqual(4, back.Id);
            Assert.AreEqual("n", back.Note);
            Assert.AreEqual("t", back.Tag);
            CollectionAssert.AreEqual(value.Items, back.Items);

            var derived = new DerivedRecord(5, "m") { Note = "dn", Tag = "dt" };
            var poly = (DerivedRecord)Rt.RoundTrip<RecordWithBody>(derived);
            Assert.AreEqual(5, poly.Id);
            Assert.AreEqual("m", poly.More);
            Assert.AreEqual("dn", poly.Note);
            Assert.AreEqual("dt", poly.Tag);

            RecordWithBody slot = new RecordWithBody(1);
            Rt.Into<RecordWithBody>(derived, ref slot);
            Assert.IsInstanceOfType(slot, typeof(DerivedRecord));
            Assert.AreEqual("m", ((DerivedRecord)slot).More);
        }

        [TestMethod]
        public void PrimaryConstructorsFromNewerCSharp()
        {
            var cls = CornerFactory.Primary(1, "p", 7);
            var back = Rt.RoundTrip(cls);
            Assert.AreEqual(1, back.Id);
            Assert.AreEqual("p", back.Name);
            Assert.AreEqual(7, back.Extra);

            var target = CornerFactory.Primary(0, "old", 0);
            Rt.Into(cls, ref target);
            Assert.AreEqual(1, target.Id);
            Assert.AreEqual(7, target.Extra);

            var st = CornerFactory.PrimaryStruct(3, long.MaxValue);
            var stBack = Rt.RoundTrip(st);
            Assert.AreEqual(3, stBack.X);
            Assert.AreEqual(long.MaxValue, stBack.Y);

            var reordered = Rt.RoundTrip(CornerFactory.PrimaryReordered(5, "r"));
            Assert.AreEqual(5, reordered.Id);
            Assert.AreEqual("r", reordered.Name);

            // A parameter only captured by the primary constructor is not a member: it is not restored.
            var captured = Rt.RoundTrip(CornerFactory.Captured(11, 12));
            Assert.AreEqual(12, captured.Value);
            Assert.AreEqual(0, captured.Seed);
        }

        [TestMethod]
        public void RequiredInitMembersFromNewerCSharp()
        {
            var value = CornerFactory.RequiredInit(4, "req", 8);
            var back = Rt.RoundTrip(value);
            Assert.AreEqual(4, back.Id);
            Assert.AreEqual("req", back.Name);
            Assert.AreEqual(8, back.Settable);

            var target = CornerFactory.RequiredInit(0, "old", 0);
            Rt.Into(value, ref target);
            Assert.AreEqual(4, target.Id);
            Assert.AreEqual("req", target.Name);
        }

        [TestMethod]
        public void RequiredSettableMembers()
        {
            // `new T()` must set required members in its object initializer: the type is re-created.
            var back = Rt.RoundTrip(CornerFactory.RequiredSettable(4, "req", 8));
            Assert.AreEqual(4, back.Id);
            Assert.AreEqual("req", back.Name);
            Assert.AreEqual(8, back.Extra);

            var target = CornerFactory.RequiredSettable(0, "old", 0);
            Rt.Into(back, ref target);
            Assert.AreEqual(4, target.Id);
            Assert.AreEqual("req", target.Name);
            Assert.AreEqual(8, target.Extra);

            // A [SetsRequiredMembers] constructor is preferred to the parameterless one.
            var withCtor = Rt.RoundTrip(CornerFactory.RequiredWithCtor(5, "c"));
            Assert.AreEqual(5, withCtor.Id);
            Assert.AreEqual("c", withCtor.Name);
        }

        [TestMethod]
        public void GetOnlyAutoPropertiesWithMatchingConstructor()
        {
            var compiled = CornerCompiler.Compile(@"
using Nino.Core;
namespace C
{
    [NinoType]
    public class Immutable
    {
        public int Id { get; }
        public string Name { get; }
        public Immutable(int id, string name) { Id = id; Name = name; }
    }

    // A get-only auto-property no constructor takes is not a member (no NINO006): only Id is serialized.
    [NinoType]
    public class Partly
    {
        public int Id { get; }
        public int Fixed { get; } = 5;
        public int Twice => Id * 2;
        public Partly(int id) { Id = id; }
    }

    public static class Api
    {
        public static string Run()
        {
            var back = NinoDeserializer.Deserialize<Immutable>(NinoSerializer.Serialize(new Immutable(3, ""x"")));
            var partly = NinoDeserializer.Deserialize<Partly>(NinoSerializer.Serialize(new Partly(4)));
            return back.Id + back.Name + partly.Twice + NinoSerializer.Serialize(new Partly(4)).Length;
        }
    }
}");
            Assert.IsFalse(compiled.HasAnalyzer("NINO004"), string.Join("\n", compiled.AnalyzerDiagnostics));
            Assert.IsFalse(compiled.HasAnalyzer("NINO006"), string.Join("\n", compiled.AnalyzerDiagnostics));
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);
            // Partly: type id (4 bytes) + Id.
            Assert.AreEqual("3x88", compiled.Assembly.GetType("C.Api")!.GetMethod("Run")!.Invoke(null, null));
        }

        [TestMethod]
        public void EtFactoryWithOptionalParameterAndStaleInstance()
        {
            EtStyle.Pool.Clear();
            var stale = new EtStyle
            {
                Id = 99, Name = "stale", Values = new List<int> { 9, 9, 9 },
                Map = new Dictionary<int, string> { [9] = "nine", [1] = "old" },
                Child = new EtStyleChild { X = 99 },
            };
            var staleValues = stale.Values;
            var staleMap = stale.Map;
            var staleChild = stale.Child;
            EtStyle.Pool.Push(stale);

            var source = new EtStyle
            {
                Id = 1, Name = null, Values = new List<int> { 1 }, Map = new Dictionary<int, string> { [1] = "one" },
                Child = new EtStyleChild { X = 5 },
            };
            int created = EtStyle.Created;
            var result = NinoDeserializer.Deserialize<EtStyle>(NinoSerializer.Serialize(source));

            Assert.AreEqual(created + 1, EtStyle.Created);
            Assert.AreSame(stale, result);
            // Every member overwritten, collections and nested objects reused.
            Assert.AreEqual(1, result.Id);
            Assert.IsNull(result.Name);
            Assert.AreSame(staleValues, result.Values);
            CollectionAssert.AreEqual(new List<int> { 1 }, result.Values);
            Assert.AreSame(staleMap, result.Map);
            Assert.AreEqual(1, result.Map.Count);
            Assert.AreEqual("one", result.Map[1]);
            Assert.AreSame(staleChild, result.Child);
            Assert.AreEqual(5, result.Child.X);

            // Fresh factory instance with the default argument.
            var fresh = NinoDeserializer.Deserialize<EtStyle>(NinoSerializer.Serialize(source));
            Assert.IsFalse(fresh.IsFromPool);
            Assert.AreEqual(5, fresh.Child.X);
        }

        [TestMethod]
        public void NestedFactoryIsUsedOnlyWhenTheSlotIsEmpty()
        {
            var source = new EtStyle { Child = new EtStyleChild { X = 3 } };
            var bytes = NinoSerializer.Serialize(source);

            int created = EtStyleChild.Created;
            var target = new EtStyle { Child = null };
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreEqual(created + 1, EtStyleChild.Created);
            Assert.AreEqual(3, target.Child.X);

            var child = target.Child;
            NinoDeserializer.Deserialize(bytes, ref target);
            Assert.AreEqual(created + 1, EtStyleChild.Created);
            Assert.AreSame(child, target.Child);
        }

        [TestMethod]
        public void ReuseDeserializationIntoNullAnotherSubtypeAndSameSubtype()
        {
            var recycled = new List<FactoryBase>();
            NinoSettings.SetRecycler<FactoryBase>(recycled.Add);
            try
            {
                var bytes = NinoSerializer.Serialize(new FactoryOwner { Slot = new FactorySubB { A = 1, C = 2.5 } });

                // Null slot: the subtype's own factory.
                var owner = new FactoryOwner();
                int b = FactorySubB.Created;
                NinoDeserializer.Deserialize(bytes, ref owner);
                Assert.AreEqual(b + 1, FactorySubB.Created);
                Assert.AreEqual(2.5, ((FactorySubB)owner.Slot).C);
                Assert.AreEqual(1, owner.Slot.A);
                Assert.AreEqual(0, recycled.Count);

                // Same subtype: reused, no factory call, nothing recycled.
                var same = owner.Slot;
                NinoDeserializer.Deserialize(bytes, ref owner);
                Assert.AreSame(same, owner.Slot);
                Assert.AreEqual(b + 1, FactorySubB.Created);
                Assert.AreEqual(0, recycled.Count);

                // Another subtype: the old one recycled once, the new one from its factory.
                var subA = new FactorySubA { A = 7, B = "a" };
                owner.Slot = subA;
                NinoDeserializer.Deserialize(bytes, ref owner);
                Assert.IsInstanceOfType(owner.Slot, typeof(FactorySubB));
                Assert.AreEqual(b + 2, FactorySubB.Created);
                CollectionAssert.AreEqual(new FactoryBase[] { subA }, recycled);

                // Base-typed instance into a subtype payload.
                recycled.Clear();
                var baseInstance = new FactoryBase { A = 3 };
                owner.Slot = baseInstance;
                NinoDeserializer.Deserialize(bytes, ref owner);
                Assert.IsInstanceOfType(owner.Slot, typeof(FactorySubB));
                CollectionAssert.AreEqual(new[] { baseInstance }, recycled);

                // Subtype instance into a base payload: base factory's instance, fields overwritten.
                recycled.Clear();
                var subB = owner.Slot;
                NinoDeserializer.Deserialize(
                    NinoSerializer.Serialize(new FactoryOwner { Slot = new FactoryBase { A = 4 } }), ref owner);
                Assert.AreEqual(typeof(FactoryBase), owner.Slot.GetType());
                Assert.AreEqual(4, owner.Slot.A);
                CollectionAssert.AreEqual(new[] { subB }, recycled);

                // Factory instances carry stale values: every member is overwritten.
                FactoryBase top = null;
                NinoDeserializer.Deserialize(NinoSerializer.Serialize<FactoryBase>(new FactorySubA { A = 0, B = null }), ref top);
                Assert.IsNull(((FactorySubA)top).B);
                Assert.AreEqual(0, top.A);
            }
            finally
            {
                NinoSettings.SetRecycler<FactoryBase>(null);
            }
        }
    }
}
