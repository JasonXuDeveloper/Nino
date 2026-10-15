using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Et;

// Collection shapes from the ET framework (github.com/egametang/ET), reduced to what matters to a serializer:
// pooled BCL subclasses, subclasses that hide BCL members with side effects, a constructor with only optional
// parameters, collections implemented from the interfaces, and pooled factories with optional parameters.

public static class EtPool
{
    public static int Fetched;
    public static int Recycled;

    public static T Fetch<T>() where T : class, new()
    {
        Fetched++;
        return new T();
    }

    public static void Recycle(object o) => Recycled++;
}

/// <summary>
/// ET's pooled list. Nino creates it through the [NinoFactory] factory (from the pool) and hands dropped
/// instances back through <see cref="INinoPooled"/>; returning one twice throws.
/// </summary>
public class ListComponent<T> : List<T>, IDisposable, INinoPooled
{
    public static int Created;
    public bool Returned;

    [NinoFactory]
    public static ListComponent<T> Create()
    {
        Created++;
        var list = EtPool.Fetch<ListComponent<T>>();
        list.Returned = false;
        return list;
    }

    public void Dispose()
    {
        if (Returned)
            throw new InvalidOperationException("returned twice");
        Returned = true;
        EtPool.Recycle(this);
    }

    void INinoPooled.ReturnToPool() => Dispose();
}

public class HashSetComponent<T> : HashSet<T>, IDisposable, INinoPooled
{
    public bool Returned;

    public static HashSetComponent<T> Create() => EtPool.Fetch<HashSetComponent<T>>();

    public void Dispose()
    {
        if (Returned)
            throw new InvalidOperationException("returned twice");
        Returned = true;
        EtPool.Recycle(this);
    }

    void INinoPooled.ReturnToPool() => Dispose();
}

public class DictionaryComponent<TKey, TValue> : Dictionary<TKey, TValue>, IDisposable
{
    public static DictionaryComponent<TKey, TValue> Create() => EtPool.Fetch<DictionaryComponent<TKey, TValue>>();
    public void Dispose() => EtPool.Recycle(this);
}

/// <summary>ET's own red-black tree dictionary: implements the interfaces, derives from nothing.</summary>
public class SortedMap<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
{
    private readonly System.Collections.Generic.SortedDictionary<TKey, TValue> _inner = new();

    public TValue this[TKey key] { get => _inner[key]; set => _inner[key] = value; }
    public ICollection<TKey> Keys => _inner.Keys;
    public ICollection<TValue> Values => _inner.Values;
    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _inner.Keys;
    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _inner.Values;
    public int Count => _inner.Count;
    public bool IsReadOnly => false;
    public void Add(TKey key, TValue value) => _inner.Add(key, value);
    public void Add(KeyValuePair<TKey, TValue> item) => _inner.Add(item.Key, item.Value);
    public void Clear() => _inner.Clear();
    public bool Contains(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Contains(item);
    public bool ContainsKey(TKey key) => _inner.ContainsKey(key);
    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _inner.GetEnumerator();
    public bool Remove(TKey key) => _inner.Remove(key);
    public bool Remove(KeyValuePair<TKey, TValue> item) => ((ICollection<KeyValuePair<TKey, TValue>>)_inner).Remove(item);
    public bool TryGetValue(TKey key, out TValue value) => _inner.TryGetValue(key, out value);
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>ET's MultiMap: its only constructor has an optional parameter and initializes a readonly pool.</summary>
public class MultiMap<T, K> : SortedMap<T, List<K>>
{
    private readonly Queue<List<K>> _pool;

    public MultiMap(int maxPoolCount = 0) => _pool = new Queue<List<K>>(maxPoolCount);

    public bool HasPool => _pool != null;

    public void Add(T t, K k)
    {
        if (!TryGetValue(t, out var list))
            Add(t, list = new List<K>());
        list.Add(k);
    }
}

public class UnOrderMultiMap<T, K> : Dictionary<T, List<K>>
{
    public new List<K> this[T t] => TryGetValue(t, out var list) ? list : null;

    public void Add(T t, K k)
    {
        if (!TryGetValue(t, out var list))
            base[t] = list = new List<K>();
        list.Add(k);
    }
}

public class MultiMapSet<T, K> : Dictionary<T, HashSet<K>>
{
}

public class MultiDictionary<T, M, N> : Dictionary<T, Dictionary<M, N>>
{
}

/// <summary>Issue #166: a [NinoType] list whose hidden members publish changes.</summary>
[NinoType]
public partial class SyncList<T> : List<T>
{
    [NinoIgnore] public int Published;

    public new void Add(T value)
    {
        base.Add(value);
        Published++;
    }

    public new void Clear()
    {
        base.Clear();
        Published++;
    }
}

[NinoType(false, true)]
public abstract partial class Entity
{
    [NinoMember(0)] public long Id;

    // Serialized although protected (ET marks it [MemoryPackInclude]).
    [NinoMember(100)] protected ChildrenCollection children;

    public ChildrenCollection Children => children ??= new ChildrenCollection();
}

public class ChildrenCollection : SortedMap<long, Entity>
{
}

[NinoType(false, true)]
public partial class Unit : Entity
{
    [NinoMember(1)] public int ConfigId;
}

[NinoType(false, true)]
public partial class Bag : Entity
{
    [NinoMember(1)] public ListComponent<long> Items = new();
}

/// <summary>An ET message: created from the pool by a factory whose parameter is optional.</summary>
[NinoType]
public partial class UnitInfo
{
    public static int Created;

    [NinoFactory]
    public static UnitInfo Create(bool isFromPool = false)
    {
        Created++;
        return new UnitInfo();
    }

    public long UnitId;
    public Dictionary<int, long> KV = new();
}

[NinoType]
public partial class EtMessage
{
    public ListComponent<long> Ids;
    public ListComponent<UnitInfo> Units;
    public HashSetComponent<int> Tags;
    public DictionaryComponent<int, string> Names;
    public MultiMap<int, long> ByType;
    public UnOrderMultiMap<long, int> Unordered;
    public MultiMapSet<int, string> Sets;
    public MultiDictionary<int, string, long> Nested;
    public SortedMap<string, int> Sorted;
    public SyncList<long> Synced;
    public List<Entity> Entities;
}

/// <summary>Slots in which reuse deserialization drops pooled ET collections.</summary>
[NinoType]
public partial class EtPooledSlots
{
    public ListComponent<int> Single;
    public List<ListComponent<int>> Many;
    public Dictionary<int, ListComponent<int>> ByKey;
    public List<List<int>> BaseTyped;
    public ICollection<int> Bucket;
}

/// <summary>
/// Only reached through generic code (<see cref="EtCompatibilityTests.RoundTripAtRuntime{T}"/>), so the generator never
/// sees these types: the runtime resolver binds them and NinoActivator creates them by reflection.
/// </summary>
public class RuntimeOnlyMap : SortedDictionary<int, int>
{
    public readonly int Capacity;

    public RuntimeOnlyMap(int capacity = 3) => Capacity = capacity;
}

public class RuntimePooledList : List<int>
{
    public static int Created;
    public bool FromPool;

    [NinoFactory]
    public static RuntimePooledList Rent(bool fromPool = true)
    {
        Created++;
        return new RuntimePooledList { FromPool = fromPool };
    }
}

public abstract class RuntimeAbstractList : List<int>
{
}

public class RuntimeNoConstructorList : List<int>
{
    public RuntimeNoConstructorList(int required)
    {
    }
}

[TestClass]
[DoNotParallelize]
public class EtCompatibilityTests
{
    private static EtMessage Sample()
    {
        var message = new EtMessage
        {
            Ids = new ListComponent<long> { 1, 2, 3 },
            Units = new ListComponent<UnitInfo> { new UnitInfo { UnitId = 7, KV = { [1] = 2 } } },
            Tags = new HashSetComponent<int> { 4, 5 },
            Names = new DictionaryComponent<int, string> { [1] = "a" },
            ByType = new MultiMap<int, long>(4),
            Unordered = new UnOrderMultiMap<long, int>(),
            Sets = new MultiMapSet<int, string> { [1] = new HashSet<string> { "x" } },
            Nested = new MultiDictionary<int, string, long> { [1] = new Dictionary<string, long> { ["k"] = 9 } },
            Sorted = new SortedMap<string, int> { ["b"] = 2, ["a"] = 1 },
            Synced = new SyncList<long>(),
        };
        message.ByType.Add(1, 10);
        message.ByType.Add(1, 11);
        message.Unordered.Add(5, 6);
        message.Synced.Add(42);
        var unit = new Unit { Id = 1, ConfigId = 1001 };
        var bag = new Bag { Id = 2, Items = { 100, 200 } };
        unit.Children.Add(bag.Id, bag);
        message.Entities = new List<Entity> { unit };
        return message;
    }

    private static void AssertEqual(EtMessage expected, EtMessage actual)
    {
        CollectionAssert.AreEqual(expected.Ids, actual.Ids);
        Assert.IsInstanceOfType(actual.Ids, typeof(ListComponent<long>));
        Assert.AreEqual(7, actual.Units[0].UnitId);
        Assert.AreEqual(2, actual.Units[0].KV[1]);
        Assert.IsTrue(actual.Tags.SetEquals(expected.Tags));
        Assert.AreEqual("a", actual.Names[1]);
        Assert.IsTrue(actual.ByType.HasPool, "MultiMap must be constructed (readonly pool initialized)");
        CollectionAssert.AreEqual(new long[] { 10, 11 }, actual.ByType[1]);
        CollectionAssert.AreEqual(new[] { 6 }, actual.Unordered[5]);
        Assert.IsTrue(actual.Sets[1].Contains("x"));
        Assert.AreEqual(9, actual.Nested[1]["k"]);
        CollectionAssert.AreEqual(new[] { "a", "b" }, actual.Sorted.Keys.ToArray());
        CollectionAssert.AreEqual(new long[] { 42 }, actual.Synced);
        Assert.AreEqual(0, actual.Synced.Published, "deserialization must not call members hidden with 'new'");
        var unit = (Unit)actual.Entities[0];
        Assert.AreEqual(1001, unit.ConfigId);
        var bag = (Bag)unit.Children[2];
        CollectionAssert.AreEqual(new long[] { 100, 200 }, bag.Items);
    }

    [TestMethod]
    public void RoundTripsEtCollections()
    {
        var message = Sample();
        var result = NinoDeserializer.Deserialize<EtMessage>(NinoSerializer.Serialize(message));
        AssertEqual(message, result);
    }

    [TestMethod]
    public void RefDeserializesIntoEtCollections()
    {
        var message = Sample();
        var bytes = NinoSerializer.Serialize(message);
        var target = Sample();
        var ids = target.Ids;
        var byType = target.ByType;
        target.Ids.Add(99);
        target.Synced.Published = 0;
        NinoDeserializer.Deserialize(bytes, ref target);
        AssertEqual(message, target);
        Assert.AreSame(ids, target.Ids, "existing pooled list is reused");
        Assert.AreSame(byType, target.ByType, "existing MultiMap is reused");
    }

    [TestMethod]
    public void FactoryWithOptionalParametersIsUsed()
    {
        int before = UnitInfo.Created;
        var bytes = NinoSerializer.Serialize(new UnitInfo { UnitId = 3 });
        Assert.AreEqual(3, NinoDeserializer.Deserialize<UnitInfo>(bytes).UnitId);
        Assert.AreEqual(before + 1, UnitInfo.Created);
    }

    [TestMethod]
    public void TopLevelEtCollections()
    {
        var map = new MultiMap<int, long>(2);
        map.Add(3, 4);
        var back = NinoDeserializer.Deserialize<MultiMap<int, long>>(NinoSerializer.Serialize(map));
        Assert.IsTrue(back.HasPool);
        CollectionAssert.AreEqual(new long[] { 4 }, back[3]);

        var list = new ListComponent<int> { 1, 2 };
        CollectionAssert.AreEqual(list, NinoDeserializer.Deserialize<ListComponent<int>>(NinoSerializer.Serialize(list)));
    }

    [TestMethod]
    public void PooledFactoryCreatesListComponents()
    {
        var message = new EtMessage { Ids = ListComponent<long>.Create() };
        message.Ids.AddRange(new long[] { 1, 2 });
        var bytes = NinoSerializer.Serialize(message);

        int created = ListComponent<long>.Created;
        var result = NinoDeserializer.Deserialize<EtMessage>(bytes);
        Assert.IsInstanceOfType(result.Ids, typeof(ListComponent<long>));
        CollectionAssert.AreEqual(new long[] { 1, 2 }, result.Ids);
        Assert.AreEqual(created + 1, ListComponent<long>.Created, "created through the [NinoFactory] factory");

        // In place: the existing instance is reused, nothing is created.
        NinoDeserializer.Deserialize(bytes, ref result);
        Assert.AreEqual(created + 1, ListComponent<long>.Created);

        // Top level, and as elements of a generic [NinoType] collection (resolved at runtime).
        created = ListComponent<int>.Created;
        var list = NinoDeserializer.Deserialize<ListComponent<int>>(NinoSerializer.Serialize(new ListComponent<int> { 5 }));
        CollectionAssert.AreEqual(new[] { 5 }, list);
        var synced = new SyncList<ListComponent<int>> { new ListComponent<int> { 1 }, new ListComponent<int> { 2, 3 } };
        var syncedBack = NinoDeserializer.Deserialize<SyncList<ListComponent<int>>>(NinoSerializer.Serialize(synced));
        Assert.AreEqual(2, syncedBack.Count);
        CollectionAssert.AreEqual(new[] { 2, 3 }, syncedBack[1]);
        Assert.AreEqual(created + 3, ListComponent<int>.Created);
        Assert.IsTrue(NinoActivator<ListComponent<int>>.IsRegistered);
    }

    [TestMethod]
    public void DroppedListComponentsReturnToPoolOnce()
    {
        var full = new EtPooledSlots
        {
            Single = new ListComponent<int> { 1 },
            Many = new List<ListComponent<int>> { new() { 1 } },
            ByKey = new Dictionary<int, ListComponent<int>> { [1] = new() { 1 } },
            BaseTyped = new List<List<int>> { new() { 1 } },
            Bucket = new List<int> { 1, 2 },
        };
        var bytes = NinoSerializer.Serialize(full);

        // Pooled instances exist from here on (created through NinoActivator), so base-typed slots are walked too.
        NinoActivator<ListComponent<int>>.Create();
        Assert.IsTrue(NinoRecycler.PoolsInUse);

        var single = new ListComponent<int> { 9 };
        var kept = new ListComponent<int> { 7 };
        var shrunk = new[] { new ListComponent<int>(), new ListComponent<int>() };
        var keyed = new ListComponent<int>();
        var keptKey = new ListComponent<int>();
        var baseTyped = new ListComponent<int>();
        var bucket = new HashSetComponent<int> { 4 };
        var target = new EtPooledSlots
        {
            Single = single,
            Many = new List<ListComponent<int>> { kept, shrunk[0], shrunk[1] },
            ByKey = new Dictionary<int, ListComponent<int>> { [1] = keptKey, [2] = keyed },
            BaseTyped = new List<List<int>> { new(), baseTyped },
            Bucket = bucket,
        };

        int recycled = EtPool.Recycled;
        NinoDeserializer.Deserialize(bytes, ref target);

        Assert.AreSame(single, target.Single, "reused in place");
        Assert.IsFalse(single.Returned);
        Assert.AreSame(kept, target.Many[0]);
        Assert.IsFalse(kept.Returned);
        Assert.IsTrue(shrunk[0].Returned && shrunk[1].Returned, "a shrinking list returns its tail");
        Assert.AreSame(keptKey, target.ByKey[1]);
        Assert.IsTrue(keyed.Returned, "a removed key returns its value");
        Assert.IsTrue(baseTyped.Returned, "a pooled list in a List<int> slot");
        Assert.IsTrue(bucket.Returned, "replaced: an ICollection<int> slot is read into a List<int>");
        Assert.IsInstanceOfType(target.Bucket, typeof(List<int>));
        CollectionAssert.AreEqual(new[] { 1, 2 }, target.Bucket.ToArray());
        Assert.AreEqual(recycled + 5, EtPool.Recycled, "each dropped instance exactly once");

        // The data holds null: the instance in the slot is returned, once; a dropped List<ListComponent<int>> takes
        // its elements with it (they are not returned separately).
        bytes = NinoSerializer.Serialize(new EtPooledSlots());
        recycled = EtPool.Recycled;
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.IsNull(target.Single);
        Assert.IsTrue(single.Returned);
        Assert.IsFalse(kept.Returned);
        Assert.AreEqual(recycled + 1, EtPool.Recycled);
    }

    [TestMethod]
    public void MultiMapAsMemberAndTopLevel()
    {
        var message = new EtMessage { ByType = new MultiMap<int, long>(8) };
        message.ByType.Add(2, 20);
        var result = NinoDeserializer.Deserialize<EtMessage>(NinoSerializer.Serialize(message));
        Assert.IsTrue(result.ByType.HasPool, "created with its optional constructor parameter");
        CollectionAssert.AreEqual(new long[] { 20 }, result.ByType[2]);
        Assert.IsNull(result.Ids);

        MultiMap<int, long> top = null;
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(message.ByType), ref top);
        Assert.IsTrue(top.HasPool);
        Assert.IsTrue(NinoActivator<MultiMap<int, long>>.IsRegistered);
    }

    [TestMethod]
    public void GenericNinoTypeList()
    {
        var list = new SyncList<string>();
        list.Add("a");
        list.Add(null);
        var bytes = NinoSerializer.Serialize(list);
        var back = NinoDeserializer.Deserialize<SyncList<string>>(bytes);
        CollectionAssert.AreEqual(new[] { "a", null }, back);
        Assert.AreEqual(0, back.Published);

        var target = new SyncList<string>();
        target.Add("x");
        target.Add("y");
        target.Add("z");
        var same = target;
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(same, target);
        CollectionAssert.AreEqual(new[] { "a", null }, target);
    }

    internal static T RoundTripAtRuntime<T>(T value) =>
        NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

    [TestMethod]
    public void RuntimeResolvedCollectionsUseNinoFactory()
    {
        Assert.IsFalse(NinoActivator<RuntimeOnlyMap>.IsRegistered, "the generator never saw it");
        var map = new RuntimeOnlyMap(10) { [1] = 2 };
        var back = RoundTripAtRuntime(map);
        Assert.AreEqual(3, back.Capacity, "constructor called with its default argument");
        Assert.AreEqual(2, back[1]);

        int created = RuntimePooledList.Created;
        var list = RoundTripAtRuntime(new RuntimePooledList { 4, 5 });
        CollectionAssert.AreEqual(new[] { 4, 5 }, list);
        Assert.IsTrue(list.FromPool, "factory called with its default argument");
        Assert.AreEqual(created + 1, RuntimePooledList.Created);

        // [NinoCreated] rejects types Nino cannot create, like the generator: there is no formatter at all.
        Assert.ThrowsException<InvalidOperationException>(() => RoundTripAtRuntime(new RuntimeNoConstructorList(1)));
        Assert.ThrowsException<InvalidOperationException>(() => NinoActivator<RuntimeAbstractList>.Create());
    }
}
