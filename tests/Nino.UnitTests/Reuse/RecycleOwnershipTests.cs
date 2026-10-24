using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public class RcItem
{
    public int Id;
}

[NinoType]
public class RcItemB : RcItem
{
    public int Extra;
}

[NinoType]
public class RcHolder
{
    public IList<RcItem> List;
    public ISet<RcItem> Set;
    public IDictionary<int, RcItem> Map;
    public RcItem Item;
    public object Boxed;
    public Tuple<int, RcItem> Pair;
}

/// <summary>
/// Pins the ownership rules of reuse deserialization (see <see cref="NinoRecycler"/>): every instance that is dropped
/// is recycled exactly once, with the declared type of its slot, and nothing that is reused is recycled.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RecycleOwnershipTests
{
    private readonly List<(Type Declared, object Value)> _recycled = new();
    private readonly List<Action> _unwatch = new();

    private void Watch<T>()
    {
        NinoSettings.SetRecycler<T>(value => _recycled.Add((typeof(T), value)));
        _unwatch.Add(() => NinoSettings.SetRecycler<T>(null));
    }

    [TestInitialize]
    public void Init()
    {
        Watch<RcItem>();
        Watch<RcItem[]>();
        Watch<RcItem[,]>();
        Watch<List<RcItem>>();
        Watch<HashSet<RcItem>>();
        Watch<IList<RcItem>>();
        Watch<ISet<RcItem>>();
        Watch<IDictionary<int, RcItem>>();
        Watch<IReadOnlyDictionary<int, RcItem>>();
        Watch<Dictionary<int, RcItem>>();
        Watch<object>();
        Watch<Tuple<int, RcItem>>();
        Watch<int?>();
#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD
        Watch<PriorityQueue<RcItem, RcItem>>();
#endif
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var unwatch in _unwatch)
            unwatch();
    }

    /// <summary>Exactly these recycles (as a multiset): same declared type and same instance.</summary>
    private void AssertRecycled(params (Type Declared, object Value)[] expected)
    {
        var left = _recycled.ToList();
        foreach (var e in expected)
        {
            int i = left.FindIndex(r => r.Declared == e.Declared &&
                                        (ReferenceEquals(r.Value, e.Value) ||
                                         (e.Value is ValueType && Equals(r.Value, e.Value))));
            Assert.IsTrue(i >= 0, $"expected a recycle of {e.Value} as {e.Declared.Name}; got {Describe()}");
            left.RemoveAt(i);
        }

        Assert.AreEqual(0, left.Count, $"unexpected recycles: {Describe()}");
        _recycled.Clear();
    }

    private string Describe() => "[" + string.Join(", ", _recycled.Select(r => $"{r.Value} as {r.Declared.Name}")) + "]";

    private static (Type, object) As<T>(T value) => (typeof(T), value);

    private static void Read<T>(ref T target, T data) =>
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(data), ref target);

    private static RcItem[] Items(int count) =>
        Enumerable.Range(1, count).Select(i => new RcItem { Id = i }).ToArray();

    // ---- arrays ----

    [TestMethod]
    public void ArrayReplacedByNull()
    {
        var target = Items(2);
        var old = target;
        Read(ref target, null);
        Assert.IsNull(target);
        // The array is dropped with its elements: only the array is recycled.
        AssertRecycled(As(old));
    }

    [TestMethod]
    public void ArrayShrinks()
    {
        var items = Items(3);
        var target = (RcItem[])items.Clone();
        var old = target;
        Read(ref target, Items(1));
        Assert.AreSame(items[0], target[0], "the kept element is reused");
        AssertRecycled(As(old), As(items[1]), As(items[2]));
    }

    [TestMethod]
    public void ArrayGrowsAndSameLength()
    {
        var target = Items(1);
        var old = target;
        var first = old[0];
        Read(ref target, Items(3));
        Assert.AreSame(first, target[0]);
        AssertRecycled(As(old));

        var same = target;
        Read(ref target, Items(3));
        Assert.AreSame(same, target);
        AssertRecycled();
    }

    [TestMethod]
    public void MultiDimArrayReplaced()
    {
        var target = new RcItem[1, 2];
        var old = target;
        Read(ref target, new RcItem[2, 2]);
        AssertRecycled(As(old));

        old = target;
        Read(ref target, null);
        AssertRecycled(As(old));
    }

    // ---- lists ----

    [TestMethod]
    public void ListReplacedByNullAndShrinks()
    {
        var items = Items(3);
        var target = new List<RcItem>(items);
        Read(ref target, new List<RcItem> { new RcItem { Id = 9 } });
        Assert.AreSame(items[0], target[0]);
        AssertRecycled(As(items[1]), As(items[2]));

        var old = target;
        Read(ref target, null);
        AssertRecycled(As(old));
    }

    // ---- refilled collections: every old element is dropped once ----

    [TestMethod]
    public void HashSetRefilled()
    {
        var items = Items(2);
        var target = new HashSet<RcItem>(items);
        var set = target;
        Read(ref target, new HashSet<RcItem> { new RcItem() });
        Assert.AreSame(set, target);
        AssertRecycled(As(items[0]), As(items[1]));

        Read(ref target, null);
        AssertRecycled(As(set));
    }

    [TestMethod]
    public void CollectionQueueStackAndProducerConsumerRefilled()
    {
        var items = Items(2);
        var linked = new LinkedList<RcItem>(items);
        Read(ref linked, new LinkedList<RcItem>(Items(1)));
        AssertRecycled(As(items[0]), As(items[1]));

        var queue = new Queue<RcItem>(items);
        Read(ref queue, new Queue<RcItem>(Items(1)));
        AssertRecycled(As(items[0]), As(items[1]));

        var stack = new Stack<RcItem>(items);
        Read(ref stack, new Stack<RcItem>());
        AssertRecycled(As(items[0]), As(items[1]));

        var concurrent = new ConcurrentQueue<RcItem>(items);
        Read(ref concurrent, new ConcurrentQueue<RcItem>(Items(3)));
        Assert.AreEqual(3, concurrent.Count);
        AssertRecycled(As(items[0]), As(items[1]));
    }

#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD // PriorityQueue: .NET 6+ builds of Nino.Core only
    [TestMethod]
    public void PriorityQueueRefilled()
    {
        var items = Items(2);
        var priorities = Items(2);
        var queue = new PriorityQueue<RcItem, RcItem>(Comparer<RcItem>.Create((x, y) => x.Id.CompareTo(y.Id)));
        queue.Enqueue(items[0], priorities[0]);
        queue.Enqueue(items[1], priorities[1]);
        var data = new PriorityQueue<RcItem, RcItem>(Comparer<RcItem>.Create((x, y) => x.Id.CompareTo(y.Id)));
        data.Enqueue(new RcItem { Id = 9 }, new RcItem { Id = 1 });
        Read(ref queue, data);
        Assert.AreEqual(1, queue.Count);
        // Both halves of every dropped pair, once each.
        AssertRecycled(As(items[0]), As(items[1]), As(priorities[0]), As(priorities[1]));

        var old = queue;
        Read(ref queue, null);
        Assert.IsNull(queue);
        AssertRecycled(As(old));
    }
#endif

    // ---- dictionaries ----

    [TestMethod]
    public void DictionaryMergedByKey()
    {
        var a = new RcItem { Id = 1 };
        var b = new RcItem { Id = 2 };
        var target = new Dictionary<int, RcItem> { [1] = a, [2] = b };
        Read(ref target, new Dictionary<int, RcItem> { [1] = new RcItem { Id = 5 } });
        Assert.AreSame(a, target[1], "the value of a kept key is reused");
        AssertRecycled(As(b));

        var old = target;
        Read(ref target, null);
        AssertRecycled(As(old));
    }

    [TestMethod]
    public void DictionaryLikeRefilled()
    {
        var items = Items(2);
        var target = new SortedDictionary<int, RcItem> { [1] = items[0], [2] = items[1] };
        Read(ref target, new SortedDictionary<int, RcItem> { [1] = new RcItem() });
        AssertRecycled(As(items[0]), As(items[1]));
    }

    // ---- interfaces: the concrete instance is handed on, another implementation is dropped once ----

    [TestMethod]
    public void InterfaceListHoldingTheConcreteType()
    {
        var items = Items(3);
        IList<RcItem> target = new List<RcItem>(items);
        var list = target;
        Read(ref target, new List<RcItem> { new RcItem() });
        Assert.AreSame(list, target, "a List<T> is reused");
        Assert.AreSame(items[0], target[0]);
        AssertRecycled(As(items[1]), As(items[2]));

        Read(ref target, null);
        // Dropped by the list formatter, so with the List<T> declared type: once.
        AssertRecycled(As((List<RcItem>)list));
    }

    [TestMethod]
    public void InterfaceListHoldingAnotherImplementation()
    {
        var items = Items(2);
        IList<RcItem> target = items;
        Read(ref target, new List<RcItem> { new RcItem() });
        Assert.IsInstanceOfType(target, typeof(List<RcItem>));
        // The array is dropped as a whole (with its elements), as the interface.
        AssertRecycled(As<IList<RcItem>>(items));

        target = items;
        Read(ref target, null);
        AssertRecycled(As<IList<RcItem>>(items));
    }

    [TestMethod]
    public void InterfaceSetAndDictionaries()
    {
        var items = Items(2);
        ISet<RcItem> set = new HashSet<RcItem>(items);
        var hashSet = set;
        Read(ref set, new HashSet<RcItem>());
        Assert.AreSame(hashSet, set);
        AssertRecycled(As(items[0]), As(items[1]));

        ISet<RcItem> sorted = new SortedSet<RcItem>(Comparer<RcItem>.Create((x, y) => x.Id.CompareTo(y.Id)));
        var oldSorted = sorted;
        Read(ref sorted, new HashSet<RcItem>());
        AssertRecycled(As(oldSorted));

        IDictionary<int, RcItem> map = new SortedDictionary<int, RcItem> { [1] = items[0] };
        var oldMap = map;
        Read(ref map, new Dictionary<int, RcItem> { [1] = new RcItem() });
        Assert.IsInstanceOfType(map, typeof(Dictionary<int, RcItem>));
        AssertRecycled(As(oldMap));

        var dictionary = (Dictionary<int, RcItem>)map;
        var value = dictionary[1];
        Read(ref map, null);
        AssertRecycled(As(dictionary));
        Assert.IsNotNull(value);

        IReadOnlyDictionary<int, RcItem> readOnly =
            new ReadOnlyDictionary<int, RcItem>(new Dictionary<int, RcItem> { [1] = items[0] });
        var oldReadOnly = readOnly;
        Read(ref readOnly, new Dictionary<int, RcItem> { [2] = new RcItem() });
        AssertRecycled(As(oldReadOnly));
    }

    // ---- objects and polymorphism ----

    [TestMethod]
    public void PolymorphicTypeChange()
    {
        RcItem target = new RcItemB { Id = 1 };
        var old = target;
        Read(ref target, new RcItem { Id = 2 });
        Assert.AreEqual(typeof(RcItem), target.GetType());
        AssertRecycled(As<RcItem>(old));

        old = target;
        Read(ref target, new RcItemB { Id = 3 });
        Assert.AreEqual(typeof(RcItemB), target.GetType());
        AssertRecycled(As<RcItem>(old));

        old = target;
        Read(ref target, new RcItemB { Id = 4 });
        Assert.AreSame(old, target, "same type: reused");
        AssertRecycled();

        Read(ref target, null);
        AssertRecycled(As<RcItem>(old));
    }

    [TestMethod]
    public void ObjectSlot()
    {
        object target = new RcItem { Id = 1 };
        var old = target;
        Read(ref target, new RcItemB { Id = 2 });
        AssertRecycled(As(old));

        old = target;
        Read<object>(ref target, new RcItemB { Id = 3 });
        Assert.AreSame(old, target);
        AssertRecycled();

        Read(ref target, null);
        AssertRecycled(As(old));
    }

    [TestMethod]
    public void MembersOfAGeneratedType()
    {
        var items = Items(3);
        var list = new List<RcItem> { items[0] };
        var holder = new RcHolder
        {
            List = list,
            Set = new SortedSet<RcItem>(),
            Map = new Dictionary<int, RcItem> { [1] = items[1] },
            Item = new RcItemB(),
            Boxed = items[2],
            Pair = Tuple.Create(1, new RcItem()),
        };
        var oldSet = holder.Set;
        var oldMap = holder.Map;
        var oldItem = holder.Item;
        var oldPair = holder.Pair;

        Read(ref holder, new RcHolder
        {
            List = new List<RcItem> { new RcItem { Id = 7 } },
            Set = new HashSet<RcItem>(),
            Map = null,
            Item = new RcItem(),
            Boxed = items[2],
            Pair = Tuple.Create(2, new RcItem()),
        });

        Assert.AreSame(list, holder.List);
        Assert.AreSame(items[0], holder.List[0]);
        Assert.AreSame(items[2], holder.Boxed);
        AssertRecycled(As(oldSet), As((Dictionary<int, RcItem>)oldMap), As(oldItem), As(oldPair));
    }

    // ---- value types ----

    [TestMethod]
    public void NullableAndValueTuple()
    {
        int? number = 5;
        Read(ref number, null);
        Assert.IsNull(number);
        AssertRecycled(As<int?>(5));

        var item = new RcItem();
        var tuple = (item, 1);
        Read(ref tuple, (null, 2));
        Assert.IsNull(tuple.item);
        AssertRecycled(As(item));
    }

    [TestMethod]
    public void TupleReplaced()
    {
        var target = Tuple.Create(1, new RcItem());
        var old = target;
        Read(ref target, Tuple.Create(2, new RcItem()));
        Assert.AreNotSame(old, target);
        // The immutable tuple is replaced; the tuple, not its items, is recycled.
        AssertRecycled(As(old));

        old = target;
        Read(ref target, null);
        AssertRecycled(As(old));
    }
}
