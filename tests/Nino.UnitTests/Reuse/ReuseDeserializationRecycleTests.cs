using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public class PooledHolder
{
    public static readonly Stack<List<int>> ListPool = new Stack<List<int>>();
    public static int Created;

    public List<int> Items { get; set; }
    public List<int> Field;
    public string Name;

    [NinoFactory]
    public static PooledHolder Create()
    {
        Created++;
        return new PooledHolder
        {
            Items = ListPool.Count > 0 ? ListPool.Pop() : new List<int>(),
            Field = ListPool.Count > 0 ? ListPool.Pop() : new List<int>(),
        };
    }
}

[NinoType]
public class InitOnlyHolder
{
    public int Id { get; init; }
    public string Name { get; init; }
}

[NinoType]
public class Owner
{
    public InitOnlyHolder Child;
    public PooledHolder Pooled;
}

[TestClass]
public class ReuseDeserializationRecycleTests
{
    [TestCleanup]
    public void Cleanup()
    {
        NinoSettings.SetRecycler<List<int>>(null);
        NinoSettings.SetRecycler<InitOnlyHolder>(null);
        NinoSettings.SetRecycler<PooledHolder>(null);
        PooledHolder.ListPool.Clear();
    }

    [TestMethod]
    public void NullTargetIsCreatedWithFactory()
    {
        var source = new PooledHolder { Items = new List<int> { 1, 2 }, Field = new List<int> { 3 }, Name = "a" };
        var bytes = NinoSerializer.Serialize(source);

        PooledHolder target = null;
        int before = PooledHolder.Created;
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.IsNotNull(target);
        Assert.AreEqual(before + 1, PooledHolder.Created);
        CollectionAssert.AreEqual(source.Items, target.Items);
        CollectionAssert.AreEqual(source.Field, target.Field);
    }

    [TestMethod]
    public void PooledListsAreRecycledWhenDataIsNull()
    {
        var recycled = new List<List<int>>();
        NinoSettings.SetRecycler<List<int>>(list =>
        {
            list.Clear();
            recycled.Add(list);
        });

        // the pooled instance holds lists from the factory; the data has nulls
        var target = PooledHolder.Create();
        var items = target.Items;
        var field = target.Field;
        var bytes = NinoSerializer.Serialize(new PooledHolder { Items = null, Field = null, Name = "n" });

        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.IsNull(target.Items);
        Assert.IsNull(target.Field);
        Assert.AreEqual(2, recycled.Count);
        Assert.IsTrue(recycled.Contains(items));
        Assert.IsTrue(recycled.Contains(field));
    }

    [TestMethod]
    public void PropertyCollectionsAreReusedInPlace()
    {
        var target = new PooledHolder { Items = new List<int> { 9, 9, 9, 9 }, Field = new List<int>() };
        var items = target.Items;
        var bytes = NinoSerializer.Serialize(new PooledHolder { Items = new List<int> { 1, 2 }, Field = new List<int> { 5 } });

        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(items, target.Items);
        CollectionAssert.AreEqual(new List<int> { 1, 2 }, target.Items);
        CollectionAssert.AreEqual(new List<int> { 5 }, target.Field);
    }

    [TestMethod]
    public void InitOnlyTypesAreRecreatedAndOldInstanceRecycled()
    {
        var recycled = new List<InitOnlyHolder>();
        NinoSettings.SetRecycler<InitOnlyHolder>(recycled.Add);

        var old = new InitOnlyHolder { Id = 1, Name = "old" };
        var owner = new Owner { Child = old };
        var bytes = NinoSerializer.Serialize(new Owner { Child = new InitOnlyHolder { Id = 2, Name = "new" } });

        NinoDeserializer.Deserialize(bytes, ref owner);
        Assert.AreEqual(2, owner.Child.Id);
        Assert.AreEqual("new", owner.Child.Name);
        Assert.AreNotSame(old, owner.Child);
        CollectionAssert.AreEqual(new[] { old }, recycled);
    }

    [TestMethod]
    public void NestedObjectsAreRecycledWhenDataIsNull()
    {
        var recycled = new List<PooledHolder>();
        NinoSettings.SetRecycler<PooledHolder>(recycled.Add);

        var pooled = PooledHolder.Create();
        var owner = new Owner { Pooled = pooled };
        var bytes = NinoSerializer.Serialize(new Owner());

        NinoDeserializer.Deserialize(bytes, ref owner);
        Assert.IsNull(owner.Pooled);
        CollectionAssert.AreEqual(new[] { pooled }, recycled);
    }

    [TestMethod]
    public void ShrinkingListRecyclesDroppedElements()
    {
        var recycled = new List<InitOnlyHolder>();
        NinoSettings.SetRecycler<InitOnlyHolder>(recycled.Add);

        var a = new InitOnlyHolder { Id = 1 };
        var b = new InitOnlyHolder { Id = 2 };
        var c = new InitOnlyHolder { Id = 3 };
        var target = new List<InitOnlyHolder> { a, b, c };
        var bytes = NinoSerializer.Serialize(new List<InitOnlyHolder> { new InitOnlyHolder { Id = 7 } });

        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreEqual(1, target.Count);
        Assert.AreEqual(7, target[0].Id);
        // b and c are dropped by the shrink; a is replaced (init-only type) and recycled by its formatter
        CollectionAssert.AreEquivalent(new[] { a, b, c }, recycled);
    }

    [TestMethod]
    public void InterfaceCollectionsRecycleDroppedInstancesOnce()
    {
        var recycled = new List<object>();
        NinoSettings.SetRecycler<List<int>>(recycled.Add);
        NinoSettings.SetRecycler<IList<int>>(recycled.Add);
        NinoSettings.SetRecycler<Dictionary<int, int>>(recycled.Add);
        NinoSettings.SetRecycler<IDictionary<int, int>>(recycled.Add);
        try
        {
            // The data is null: the existing instance is dropped once, by the concrete formatter.
            IList<int> list = new List<int> { 1 };
            var oldList = list;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<IList<int>>(null), ref list);
            Assert.IsNull(list);
            CollectionAssert.AreEqual(new object[] { oldList }, recycled);

            IDictionary<int, int> dictionary = new Dictionary<int, int> { [1] = 2 };
            var oldDictionary = dictionary;
            recycled.Clear();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<IDictionary<int, int>>(null), ref dictionary);
            Assert.IsNull(dictionary);
            CollectionAssert.AreEqual(new object[] { oldDictionary }, recycled);

            // An instance of another collection type cannot be reused: it is dropped once, as the interface.
            IList<int> array = new[] { 1, 2 };
            var oldArray = array;
            recycled.Clear();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize<IList<int>>(new List<int> { 3 }), ref array);
            CollectionAssert.AreEqual(new[] { 3 }, (List<int>)array);
            CollectionAssert.AreEqual(new object[] { oldArray }, recycled);
        }
        finally
        {
            NinoSettings.SetRecycler<IList<int>>(null);
            NinoSettings.SetRecycler<Dictionary<int, int>>(null);
            NinoSettings.SetRecycler<IDictionary<int, int>>(null);
        }
    }

    [TestMethod]
    public void BoxedNullRoundTrips()
    {
        object value = new GoldenPoint { X = 1 };
        var bytes = NinoSerializer.Serialize<object>(null);
        NinoDeserializer.Deserialize(bytes, ref value);
        Assert.IsNull(value);
    }

    [TestMethod]
    public void ConcurrentSerializationIsSafe()
    {
        var data = new List<PooledHolder>();
        for (int i = 0; i < 64; i++)
            data.Add(new PooledHolder { Items = new List<int> { i, i + 1 }, Field = new List<int> { i }, Name = "n" + i });
        var expected = data.ConvertAll(d => System.Convert.ToBase64String(NinoSerializer.Serialize(d)));

        System.Threading.Tasks.Parallel.For(0, 2000, i =>
        {
            var d = data[i % data.Count];
            var bytes = NinoSerializer.Serialize(d);
            Assert.AreEqual(expected[i % data.Count], System.Convert.ToBase64String(bytes));
            var back = NinoDeserializer.Deserialize<PooledHolder>(bytes);
            Assert.AreEqual(d.Name, back.Name);
            // polymorphic path through the registry
            var shapes = new List<GoldenShape> { new GoldenCircle { Name = "c" + i, Radius = i } };
            var shapesBack = NinoDeserializer.Deserialize<List<GoldenShape>>(NinoSerializer.Serialize(shapes));
            Assert.AreEqual(i, ((GoldenCircle)shapesBack[0]).Radius);
        });
    }
}
