using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests
{
    [NinoType]
    public class Box<T>
    {
        public T Item;
        public List<T> Items;
    }

    [NinoType]
    public struct ValueBox<T>
    {
        public T Item;
        public int Count;
    }

    [NinoType]
    public class Pair<TKey, TValue>
    {
        public TKey Key;
        public TValue Value;
        public Dictionary<TKey, TValue> Map;
    }

    [NinoType]
    public class StructConstrained<T> where T : struct, IComparable<T>
    {
        public T Value;
        public T? Maybe;
        public T[] Values;
    }

    [NinoType]
    public class ClassConstrained<T> where T : class, new()
    {
        public T Value;
        public List<T> Values;
    }

    [NinoType]
    public class BaseConstrained<T> where T : SimpleNode
    {
        public T Node;
    }

    // A base closed over another parameter of the subtype, over a collection of it, over a tuple, and two levels deep:
    // each base formatter is named with the subtype's formatter parameters (__F_U, ListFormatter<..., __F_T>).
    [NinoType]
    public abstract class ArgBase<T>
    {
        public T A;
    }

    [NinoType]
    public class OtherArg<T, U> : ArgBase<U>
    {
        public T B;
    }

    [NinoType]
    public class ListArg<T> : ArgBase<List<T>>
    {
        public T C;
    }

    [NinoType]
    public class ListArgChild<T> : ListArg<T>
    {
        public T E;
    }

    [NinoType]
    public class TupleArg : ArgBase<(string, int)>
    {
        public int D;
    }

    // Holds itself over a bigger type argument: its formatter names its own over List<T> (an expanding cycle the
    // runtime instantiates only as deep as the data goes).
    [NinoType]
    public class Expanding<T>
    {
        public T Value;
        public Expanding<List<T>> Child;
    }

    public class GenericOuter<T>
    {
        [NinoType]
        public class Inner<U>
        {
            public T First;
            public U Second;
        }

        [NinoType]
        public class Plain
        {
            public T Only;
        }
    }

    [TestClass]
    public class CornerGenericTests
    {
        [TestMethod]
        public void OpenGenericsOverStructClassNullableArrayAndNestedArguments()
        {
            Assert.AreEqual(5, Rt.RoundTrip(new Box<int> { Item = 5 }).Item);
            Assert.AreEqual("s", Rt.RoundTrip(new Box<string> { Item = "s", Items = new List<string> { null } }).Item);
            Assert.AreEqual(3, Rt.RoundTrip(new Box<int?> { Item = 3, Items = new List<int?> { null, 1 } }).Item);
            Assert.IsNull(Rt.RoundTrip(new Box<int?> { Item = null }).Item);
            CollectionAssert.AreEqual(new[] { 1, 2 }, Rt.RoundTrip(new Box<int[]> { Item = new[] { 1, 2 } }).Item);
            Assert.AreEqual(2, Rt.RoundTrip(new Box<int[][]> { Item = new[] { new[] { 1 }, null, new int[0] } }).Item[0].Length + 1);
            Assert.AreEqual(4, Rt.RoundTrip(new Box<int[,]> { Item = new int[2, 2] }).Item.Length);

            var nested = new Box<Box<Box<string>>> { Item = new Box<Box<string>> { Item = new Box<string> { Item = "deep" } } };
            Assert.AreEqual("deep", Rt.RoundTrip(nested).Item.Item.Item);

            var listOfGen = new Box<List<Box<int>>> { Item = new List<Box<int>> { new Box<int> { Item = 1 }, null } };
            var back = Rt.RoundTrip(listOfGen);
            Assert.AreEqual(1, back.Item[0].Item);
            Assert.IsNull(back.Item[1]);

            var vb = Rt.RoundTrip(new ValueBox<Box<string>> { Item = new Box<string> { Item = "v" }, Count = 2 });
            Assert.AreEqual("v", vb.Item.Item);
            Assert.AreEqual(2, vb.Count);
            Assert.AreEqual(Guid.Empty, Rt.RoundTrip(new ValueBox<Guid?> { Item = Guid.Empty }).Item);
            Assert.AreEqual(7, Rt.RoundTrip<ValueBox<int>?>(new ValueBox<int> { Item = 7 }).Value.Item);

            var tuple = Rt.RoundTrip(new Box<(int, string)> { Item = (1, "t") });
            Assert.AreEqual((1, "t"), tuple.Item);

            var pair = Rt.RoundTrip(new Pair<string, Box<int>>
                { Key = "k", Value = new Box<int> { Item = 1 }, Map = new Dictionary<string, Box<int>> { ["a"] = null } });
            Assert.AreEqual("k", pair.Key);
            Assert.AreEqual(1, pair.Value.Item);
            Assert.IsNull(pair.Map["a"]);
        }

        [TestMethod]
        public void SameGenericDefinitionWithManyArgumentsHasDistinctIds()
        {
            var ids = new[]
            {
                NinoTypeId.Of(typeof(Box<int>)), NinoTypeId.Of(typeof(Box<long>)), NinoTypeId.Of(typeof(Box<int?>)),
                NinoTypeId.Of(typeof(Box<int[]>)), NinoTypeId.Of(typeof(Box<Box<int>>)), NinoTypeId.Of(typeof(Box<string>)),
            };
            Assert.AreEqual(ids.Length, ids.Distinct().Count());
            // The id is written in front of non-sealed generic classes and must match the reader's.
            var bytes = NinoSerializer.Serialize(new Box<long> { Item = 1 });
            Assert.AreEqual(NinoTypeId.Of(typeof(Box<long>)), BitConverter.ToInt32(bytes, 0));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Box<int>>(bytes));
        }

        [TestMethod]
        public void ConstrainedGenerics()
        {
            var s = Rt.RoundTrip(new StructConstrained<long> { Value = 1, Maybe = 2, Values = new long[] { 3 } });
            Assert.AreEqual(1, s.Value);
            Assert.AreEqual(2, s.Maybe);
            CollectionAssert.AreEqual(new long[] { 3 }, s.Values);
            Assert.IsNull(Rt.RoundTrip(new StructConstrained<DateTime>()).Maybe);

            var c = Rt.RoundTrip(new ClassConstrained<SimpleNode> { Value = new SimpleNode { Id = 2 }, Values = new List<SimpleNode> { null } });
            Assert.AreEqual(2, c.Value.Id);
            Assert.IsNull(c.Values[0]);

            var b = Rt.RoundTrip(new BaseConstrained<SimpleNode> { Node = new SimpleNode { Name = "n" } });
            Assert.AreEqual("n", b.Node.Name);
        }

        [TestMethod]
        public void GenericNestedInGeneric()
        {
            var inner = Rt.RoundTrip(new GenericOuter<string>.Inner<int> { First = "f", Second = 2 });
            Assert.AreEqual("f", inner.First);
            Assert.AreEqual(2, inner.Second);

            var nested = Rt.RoundTrip(new GenericOuter<int[]>.Inner<GenericOuter<long>.Plain>
                { First = new[] { 1 }, Second = new GenericOuter<long>.Plain { Only = 9 } });
            Assert.AreEqual(9, nested.Second.Only);
            CollectionAssert.AreEqual(new[] { 1 }, nested.First);

            Assert.AreEqual(3.5, Rt.RoundTrip(new GenericOuter<double>.Plain { Only = 3.5 }).Only);
        }

        [TestMethod]
        public void GenericSubtypesOfBasesClosedOverOtherArguments()
        {
            var other = (OtherArg<string, int>)Rt.RoundTrip<ArgBase<int>>(new OtherArg<string, int> { A = 1, B = "b" });
            Assert.AreEqual(1, other.A);
            Assert.AreEqual("b", other.B);

            var child = (ListArgChild<long>)Rt.RoundTrip<ArgBase<List<long>>>(
                new ListArgChild<long> { A = new List<long> { 5 }, C = 6, E = 7 });
            CollectionAssert.AreEqual(new[] { 5L }, child.A);
            Assert.AreEqual(6, child.C);
            Assert.AreEqual(7, child.E);

            var tuple = (TupleArg)Rt.RoundTrip<ArgBase<(string, int)>>(new TupleArg { A = ("q", 2), D = 3 });
            Assert.AreEqual(("q", 2), tuple.A);
            Assert.AreEqual(3, tuple.D);

            var list = Rt.RoundTrip(new List<ArgBase<int>> { other, new OtherArg<int, int> { A = 2, B = 3 }, null });
            Assert.AreEqual("b", ((OtherArg<string, int>)list[0]).B);
            Assert.AreEqual(3, ((OtherArg<int, int>)list[1]).B);
            Assert.IsNull(list[2]);
        }

        [TestMethod]
        public void GenericHoldingItselfOverABiggerArgument()
        {
            var read = Rt.RoundTrip(new Expanding<int>
            {
                Value = 1,
                Child = new Expanding<List<int>>
                {
                    Value = new List<int> { 2, 3 },
                    Child = new Expanding<List<List<int>>> { Value = new List<List<int>> { new List<int> { 4 } } },
                },
            });
            Assert.AreEqual(1, read.Value);
            CollectionAssert.AreEqual(new[] { 2, 3 }, read.Child.Value);
            Assert.AreEqual(4, read.Child.Child.Value[0][0]);
            Assert.IsNull(read.Child.Child.Child);
        }

        [TestMethod]
        public void GenericReuseDeserializationReusesInstances()
        {
            var target = new Box<Box<int>> { Item = new Box<int> { Items = new List<int> { 9, 9, 9 } } };
            var inner = target.Item;
            var list = inner.Items;
            Rt.Into(new Box<Box<int>> { Item = new Box<int> { Item = 1, Items = new List<int> { 1 } } }, ref target);
            Assert.AreSame(inner, target.Item);
            Assert.AreSame(list, target.Item.Items);
            CollectionAssert.AreEqual(new[] { 1 }, list);

            Rt.Into(new Box<Box<int>>(), ref target);
            Assert.IsNull(target.Item);
        }
    }
}
