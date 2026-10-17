using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Verify = Microsoft.CodeAnalysis.CSharp.Testing.CSharpAnalyzerVerifier<
    Nino.Generator.Analysis.NinoAnalyzer,
    Microsoft.CodeAnalysis.Testing.DefaultVerifier>;

#nullable disable
namespace Nino.UnitTests.UserCollections;

// User collection types in unusual shapes. Each is checked through a generated holder ([NinoType] sealed class with one
// member) and through RuntimeBox<T>, whose member the runtime resolver binds: both must write the same bytes.

#region Construction

/// <summary>Its only constructor has optional parameters (which a <c>new()</c> constraint rejects).</summary>
public class OptionalCtorList : List<int>
{
    public readonly int Seed;
    public readonly string Name;

    public OptionalCtorList(int seed = 42, string name = "default")
    {
        Seed = seed;
        Name = name;
    }
}

/// <summary>Private constructor: created only through its factory.</summary>
public class FactoryOnlyList : List<int>
{
    public static int Created;

    private FactoryOnlyList()
    {
    }

    [NinoFactory]
    public static FactoryOnlyList Create()
    {
        Created++;
        return new FactoryOnlyList();
    }
}

/// <summary>Both a factory and a constructor: the factory wins.</summary>
public class FactoryAndCtorList : List<int>
{
    public bool ViaFactory;

    [NinoFactory]
    public static FactoryAndCtorList Create() => new FactoryAndCtorList { ViaFactory = true };
}

/// <summary>A factory that needs an argument is not a creator (NINO017 reports it): the constructor is used.</summary>
public class ArgumentFactoryList : List<int>
{
    public bool ViaFactory;

#pragma warning disable NINO017
    [NinoFactory]
    public static ArgumentFactoryList Create(int capacity) => new ArgumentFactoryList { ViaFactory = true };
#pragma warning restore NINO017
}

public class NullFactoryList : List<int>
{
    [NinoFactory]
    public static NullFactoryList Create() => null;
}

/// <summary>The factory returns a subtype: the created instance keeps its runtime type.</summary>
public class BaseFactoryList : List<int>
{
    [NinoFactory]
    public static BaseFactoryList Create() => new DerivedFactoryList();
}

public class DerivedFactoryList : BaseFactoryList
{
}

/// <summary>Pooled factories may hand out instances that still hold stale contents.</summary>
public class DirtyList : List<string>
{
    [NinoFactory]
    public static DirtyList Create() => new DirtyList { "stale1", "stale2", "stale3", "stale4" };
}

public class DirtyRawList : List<int>
{
    [NinoFactory]
    public static DirtyRawList Create() => new DirtyRawList { 7, 7, 7, 7, 7 };
}

public class DirtySet : HashSet<int>
{
    [NinoFactory]
    public static DirtySet Create() => new DirtySet { 99 };
}

public class DirtyDictionary : Dictionary<int, string>
{
    [NinoFactory]
    public static DirtyDictionary Create() => new DirtyDictionary { [99] = "stale" };
}

public class DirtySortedDictionary : SortedDictionary<int, string>
{
    [NinoFactory]
    public static DirtySortedDictionary Create() => new DirtySortedDictionary { [99] = "stale" };
}

public class DirtyLinkedList : LinkedList<int>
{
    [NinoFactory]
    public static DirtyLinkedList Create()
    {
        var list = new DirtyLinkedList();
        list.AddLast(99);
        return list;
    }
}

public class DirtyQueue : Queue<int>
{
    [NinoFactory]
    public static DirtyQueue Create()
    {
        var queue = new DirtyQueue();
        queue.Enqueue(99);
        return queue;
    }
}

public class DirtyConcurrentBag : ConcurrentBag<int>
{
    [NinoFactory]
    public static DirtyConcurrentBag Create() => new DirtyConcurrentBag { 99 };
}

#endregion

#region Comparers

public class CaseInsensitiveDictionary : Dictionary<string, int>
{
    public CaseInsensitiveDictionary() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

public class CaseInsensitiveSet : HashSet<string>
{
    public CaseInsensitiveSet() : base(StringComparer.OrdinalIgnoreCase)
    {
    }
}

public class DescendingSet : SortedSet<int>
{
    public DescendingSet() : base(Comparer<int>.Create((a, b) => b.CompareTo(a)))
    {
    }
}

public class DescendingDictionary : SortedDictionary<int, string>
{
    public DescendingDictionary() : base(Comparer<int>.Create((a, b) => b.CompareTo(a)))
    {
    }
}

#endregion

#region Shapes

[NinoType]
public class NamedList : List<int>
{
    public string Name;
    public int Version;
}

public class StructOnlyList<T> : List<T> where T : struct
{
}

public class ClassOnlyList<T> : List<T> where T : class
{
}

public class Outer<T>
{
    public class Inner : List<T>
    {
    }
}

/// <summary>ICollection&lt;T&gt; for two different T: ambiguous, so no collection formatter applies.</summary>
public class TwoWayCollection : ICollection<int>, ICollection<string>
{
    private readonly List<int> _ints = new();
    private readonly List<string> _strings = new();
    public int Count => _ints.Count + _strings.Count;
    public bool IsReadOnly => false;
    public void Add(int item) => _ints.Add(item);
    public void Add(string item) => _strings.Add(item);
    public void Clear() => _ints.Clear();
    public bool Contains(int item) => _ints.Contains(item);
    public bool Contains(string item) => _strings.Contains(item);
    public void CopyTo(int[] array, int arrayIndex) => _ints.CopyTo(array, arrayIndex);
    public void CopyTo(string[] array, int arrayIndex) => _strings.CopyTo(array, arrayIndex);
    public bool Remove(int item) => _ints.Remove(item);
    public bool Remove(string item) => _strings.Remove(item);
    public IEnumerator<int> GetEnumerator() => _ints.GetEnumerator();
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => _strings.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    int ICollection<string>.Count => _strings.Count;
}

/// <summary>Every ICollection&lt;T&gt; member implemented explicitly.</summary>
public class ExplicitCollection : ICollection<int>
{
    private readonly List<int> _items = new();
    public int[] Items => _items.ToArray();
    int ICollection<int>.Count => _items.Count;
    bool ICollection<int>.IsReadOnly => false;
    void ICollection<int>.Add(int item) => _items.Add(item);
    void ICollection<int>.Clear() => _items.Clear();
    bool ICollection<int>.Contains(int item) => _items.Contains(item);
    void ICollection<int>.CopyTo(int[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    bool ICollection<int>.Remove(int item) => _items.Remove(item);
    IEnumerator<int> IEnumerable<int>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>Reports IsReadOnly: deserializing into it is a clear error, not a NotSupportedException from Add.</summary>
public class FrozenCollection : ICollection<int>
{
    private readonly List<int> _items = new() { 1, 2 };
    public int Count => _items.Count;
    public bool IsReadOnly => true;
    public void Add(int item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(int item) => _items.Contains(item);
    public void CopyTo(int[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public bool Remove(int item) => throw new NotSupportedException();
    public IEnumerator<int> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Collection&lt;T&gt; hooks run: Nino uses the public Add and Clear.</summary>
public class CountingCollection : Collection<int>
{
    public int Inserted;
    public int Cleared;

    protected override void InsertItem(int index, int item)
    {
        Inserted++;
        base.InsertItem(index, item * 10);
    }

    protected override void ClearItems()
    {
        Cleared++;
        base.ClearItems();
    }
}

public class CountingObservable : ObservableCollection<string>
{
    public int Changes;

    public CountingObservable() => CollectionChanged += (_, _) => Changes++;
}

[NinoType]
public class KeyedItem
{
    public int Id;
    public string Name;
}

public class KeyedItems : KeyedCollection<int, KeyedItem>
{
    protected override int GetKeyForItem(KeyedItem item) => item.Id;
}

public class Tree : List<Tree>
{
}

public class IntStack : Stack<int>
{
}

public class NameQueue : Queue<string>
{
}

public class ConcurrentMap : ConcurrentDictionary<int, string>
{
}

public class NameLinkedList : LinkedList<string>
{
}

public class NameSortedList : SortedList<string, int>
{
}

#endregion

#region Elements

public class RawList : List<int>
{
}

public class NullableRawList : List<long?>
{
}

[NinoType]
public abstract class Shape
{
    public int Id;
}

[NinoType]
public class Circle : Shape
{
    public float Radius;
}

[NinoType]
public class Square : Shape
{
    public float Side;
}

public class ShapeList : List<Shape>
{
}

public class ListOfLists : List<RawList>
{
}

public class StringList : List<string>
{
}

/// <summary>A pooled collection that is not a List&lt;T&gt;.</summary>
public class PooledCollection : Collection<int>, INinoPooled
{
    public int Returns;

    public void ReturnToPool() => Returns++;
}

#endregion

#region Holders (generated path; RuntimeBox<T> is the runtime path)

[NinoType] public sealed class OptionalCtorHolder { public OptionalCtorList Value; }
[NinoType] public sealed class FactoryOnlyHolder { public FactoryOnlyList Value; }
[NinoType] public sealed class FactoryAndCtorHolder { public FactoryAndCtorList Value; }
[NinoType] public sealed class ArgumentFactoryHolder { public ArgumentFactoryList Value; }
[NinoType] public sealed class NullFactoryHolder { public NullFactoryList Value; }
[NinoType] public sealed class BaseFactoryHolder { public BaseFactoryList Value; }
[NinoType] public sealed class DirtyHolder
{
    public DirtyList List;
    public DirtyRawList Raw;
    public DirtySet Set;
    public DirtyDictionary Dictionary;
    public DirtySortedDictionary Sorted;
    public DirtyLinkedList Linked;
    public DirtyQueue Queue;
    public DirtyConcurrentBag Bag;
}
[NinoType] public sealed class PlainHolder
{
    public List<string> List;
    public List<int> Raw;
    public HashSet<int> Set;
    public Dictionary<int, string> Dictionary;
    public SortedDictionary<int, string> Sorted;
    public LinkedList<int> Linked;
    public Queue<int> Queue;
    public ConcurrentBag<int> Bag;
}
[NinoType] public sealed class ComparerHolder
{
    public CaseInsensitiveDictionary Dictionary;
    public CaseInsensitiveSet Set;
    public DescendingSet Sorted;
    public DescendingDictionary SortedMap;
}
[NinoType] public sealed class NamedListHolder { public NamedList Value; }
[NinoType] public sealed class ConstrainedHolder
{
    public StructOnlyList<int> Structs;
    public ClassOnlyList<string> Classes;
    public Outer<long>.Inner Nested;
}
[NinoType] public sealed class ExplicitHolder { public ExplicitCollection Value; }
[NinoType] public sealed class FrozenHolder { public FrozenCollection Value; }
[NinoType] public sealed class ObjectModelHolder
{
    public CountingCollection Counting;
    public CountingObservable Observable;
    public KeyedItems Keyed;
}
[NinoType] public sealed class TreeHolder { public Tree Value; }
[NinoType] public sealed class BclSubclassHolder
{
    public IntStack Stack;
    public NameQueue Queue;
    public ConcurrentMap Map;
    public NameLinkedList Linked;
    public NameSortedList Sorted;
}
[NinoType] public sealed class BclHolder
{
    public Stack<int> Stack;
    public Queue<string> Queue;
    public ConcurrentDictionary<int, string> Map;
    public LinkedList<string> Linked;
    public SortedList<string, int> Sorted;
}
[NinoType] public sealed class RawListHolder { public RawList Value; }
[NinoType] public sealed class IntListHolder { public List<int> Value; }
[NinoType] public sealed class NullableRawHolder { public NullableRawList Value; }
[NinoType] public sealed class NullableListHolder { public List<long?> Value; }
[NinoType] public sealed class ShapeListHolder { public ShapeList Value; }
[NinoType] public sealed class ListOfListsHolder { public ListOfLists Value; }
[NinoType] public sealed class StringListHolder { public StringList Value; }
[NinoType] public sealed class DeclaredHolder
{
    public List<int> AsList;
    public IList<int> AsInterface;
}
[NinoType] public sealed class AliasHolder
{
    public RawList First;
    public RawList Second;
}
[NinoType(trackReferences: true)] public sealed class TrackedAliasHolder
{
    public RawList First;
    public RawList Second;
}
[NinoType] public sealed class StringDictionaryHolder { public Dictionary<string, int> Value; }
[NinoType] public sealed class CaseInsensitiveHolder { public CaseInsensitiveDictionary Value; }
[NinoType] public sealed class StringSetHolder { public HashSet<string> Value; }
[NinoType] public sealed class CaseInsensitiveSetHolder { public CaseInsensitiveSet Value; }

#endregion

/// <summary>Distinct closed types for the concurrency test, each first used there.</summary>
public class ConcurrentFirstUse<TMarker> : List<int>
{
}

public sealed class M0 { } public sealed class M1 { } public sealed class M2 { } public sealed class M3 { }
public sealed class M4 { } public sealed class M5 { } public sealed class M6 { } public sealed class M7 { }

[TestClass]
[DoNotParallelize]
public class UserCollectionCornerCaseTests
{
    /// <summary>
    /// Serializes through the generated holder and through RuntimeBox&lt;T&gt; (runtime resolver), checks both write
    /// the same bytes, and returns what each path reads back.
    /// </summary>
    private static (T Generated, T Runtime) BothPaths<THolder, T>(THolder holder, Func<THolder, T> get)
    {
        var generated = NinoSerializer.Serialize(holder);
        var runtime = NinoSerializer.Serialize(new RuntimeBox<T> { Value = get(holder) });
        CollectionAssert.AreEqual(generated, runtime, typeof(T).Name);
        return (get(NinoDeserializer.Deserialize<THolder>(generated)),
            NinoDeserializer.Deserialize<RuntimeBox<T>>(runtime).Value);
    }

    /// <summary>Reuse deserialization through both paths, into holders that hold <paramref name="existing"/>.</summary>
    private static void RefBothPaths<THolder, T>(THolder data, Func<THolder, T> get, Func<T, THolder> wrap,
        Func<T> existing, Action<T> check) where THolder : class
    {
        var target = wrap(existing());
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(data), ref target);
        check(get(target));
        var box = new RuntimeBox<T> { Value = existing() };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new RuntimeBox<T> { Value = get(data) }), ref box);
        check(box.Value);
    }

    private static T RuntimeOnly<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

    // ---- construction ----

    [TestMethod]
    public void OptionalParameterConstructor()
    {
        var list = new OptionalCtorList(1, "x") { 1, 2 };
        var (generated, runtime) = BothPaths(new OptionalCtorHolder { Value = list }, h => h.Value);
        foreach (var result in new[] { generated, runtime })
        {
            CollectionAssert.AreEqual(new[] { 1, 2 }, result);
            Assert.AreEqual(42, result.Seed, "created with the default arguments");
            Assert.AreEqual("default", result.Name);
        }

        // null vs empty
        Assert.IsNull(BothPaths(new OptionalCtorHolder(), h => h.Value).Generated);
        Assert.AreEqual(0, BothPaths(new OptionalCtorHolder { Value = new OptionalCtorList() }, h => h.Value).Runtime.Count);

        // in place: the existing instance (and its fields) is kept
        RefBothPaths(new OptionalCtorHolder { Value = list }, h => h.Value, v => new OptionalCtorHolder { Value = v },
            () => new OptionalCtorList(7) { 9, 9, 9 }, r =>
            {
                Assert.AreEqual(7, r.Seed);
                CollectionAssert.AreEqual(new[] { 1, 2 }, r);
            });
    }

    [TestMethod]
    public void PrivateConstructorAndFactory()
    {
        var list = FactoryOnlyList.Create();
        list.Add(3);
        int created = FactoryOnlyList.Created;
        var (generated, runtime) = BothPaths(new FactoryOnlyHolder { Value = list }, h => h.Value);
        CollectionAssert.AreEqual(new[] { 3 }, generated);
        CollectionAssert.AreEqual(new[] { 3 }, runtime);
        Assert.AreEqual(created + 2, FactoryOnlyList.Created);
    }

    [TestMethod]
    public void FactoryWinsOverConstructor()
    {
        var (generated, runtime) = BothPaths(new FactoryAndCtorHolder { Value = new FactoryAndCtorList { 1 } }, h => h.Value);
        Assert.IsTrue(generated.ViaFactory);
        Assert.IsTrue(runtime.ViaFactory);
    }

    [TestMethod]
    public void FactoryWithRequiredArgumentsIsNotUsed()
    {
        var (generated, runtime) = BothPaths(new ArgumentFactoryHolder { Value = new ArgumentFactoryList { 1 } }, h => h.Value);
        Assert.IsFalse(generated.ViaFactory);
        Assert.IsFalse(runtime.ViaFactory);
        CollectionAssert.AreEqual(new[] { 1 }, runtime);
    }

    [TestMethod]
    public void FactoryReturningNullIsAClearError()
    {
        var bytes = NinoSerializer.Serialize(new NullFactoryHolder { Value = new NullFactoryList { 1 } });
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<NullFactoryHolder>(bytes));
        StringAssert.Contains(e.Message, "returned null");
        e = Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly(new RuntimeBox<NullFactoryList> { Value = new() }));
        StringAssert.Contains(e.Message, "returned null");
        // null data never calls the factory
        Assert.IsNull(NinoDeserializer.Deserialize<NullFactoryHolder>(NinoSerializer.Serialize(new NullFactoryHolder())).Value);
    }

    [TestMethod]
    public void FactoryReturningASubtype()
    {
        var (generated, runtime) = BothPaths(new BaseFactoryHolder { Value = new BaseFactoryList { 5 } }, h => h.Value);
        Assert.IsInstanceOfType(generated, typeof(DerivedFactoryList));
        Assert.IsInstanceOfType(runtime, typeof(DerivedFactoryList));
        CollectionAssert.AreEqual(new[] { 5 }, runtime);
    }

    [TestMethod]
    public void PooledFactoryReturningStaleContents()
    {
        var plain = new PlainHolder
        {
            List = new List<string> { "a", null },
            Raw = new List<int> { 1, 2 },
            Set = new HashSet<int> { 1 },
            Dictionary = new Dictionary<int, string> { [1] = "one" },
            Sorted = new SortedDictionary<int, string> { [2] = "two" },
            Linked = new LinkedList<int>(new[] { 3 }),
            Queue = new Queue<int>(new[] { 4, 5 }),
            Bag = new ConcurrentBag<int> { 6 },
        };
        // Same shape and bytes: read into the dirty types, whose factories hand out non-empty instances.
        var dirty = NinoDeserializer.Deserialize<DirtyHolder>(NinoSerializer.Serialize(plain));
        CollectionAssert.AreEqual(new[] { "a", null }, dirty.List);
        CollectionAssert.AreEqual(new[] { 1, 2 }, dirty.Raw);
        CollectionAssert.AreEquivalent(new[] { 1 }, dirty.Set.ToArray());
        CollectionAssert.AreEqual(new[] { 1 }, dirty.Dictionary.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { 2 }, dirty.Sorted.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { 3 }, dirty.Linked.ToArray());
        CollectionAssert.AreEqual(new[] { 4, 5 }, dirty.Queue.ToArray());
        CollectionAssert.AreEqual(new[] { 6 }, dirty.Bag.ToArray());

        // Empty data: the stale contents go as well.
        var empty = NinoDeserializer.Deserialize<DirtyHolder>(NinoSerializer.Serialize(new PlainHolder
        {
            List = new(), Raw = new(), Set = new(), Dictionary = new(), Sorted = new(), Linked = new(), Queue = new(),
            Bag = new(),
        }));
        Assert.AreEqual(0, empty.List.Count + empty.Raw.Count + empty.Set.Count + empty.Dictionary.Count +
                           empty.Sorted.Count + empty.Linked.Count + empty.Queue.Count + empty.Bag.Count);

        // Runtime path too.
        var raw = NinoDeserializer.Deserialize<RuntimeBox<DirtyRawList>>(
            NinoSerializer.Serialize(new RuntimeBox<List<int>> { Value = new List<int> { 1 } })).Value;
        CollectionAssert.AreEqual(new[] { 1 }, raw);
    }

    [TestMethod]
    public void UncreatableTypesHaveNoFormatter()
    {
        // Runtime: like the generator (NINO007 below), the resolver finds no formatter.
        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly(new InternalCtorList()));
        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly<AbstractList>(new ConcreteList()));
        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly(new TwoWayCollection()));
        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly(new ReadOnlyCollection<int>(new[] { 1 })));
        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly(new LegacyList()));
        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly(new StructCollection()));
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoActivator<InternalCtorList>.Create());
    }

    [TestMethod]
    public async Task UncreatableTypesAreReportedAsNINO007()
    {
        var code = @"
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Nino.Core;

public class InternalCtorList : List<int> { internal InternalCtorList() { } }
public abstract class AbstractList : List<int> { }
public class LegacyList : ArrayList { }
public class TwoWay : ICollection<int>, ICollection<string>
{
    public int Count => 0;
    public bool IsReadOnly => false;
    public void Add(int item) { }
    public void Add(string item) { }
    public void Clear() { }
    public bool Contains(int item) => false;
    public bool Contains(string item) => false;
    public void CopyTo(int[] array, int arrayIndex) { }
    public void CopyTo(string[] array, int arrayIndex) { }
    public bool Remove(int item) => false;
    public bool Remove(string item) => false;
    public IEnumerator<int> GetEnumerator() => null;
    IEnumerator<string> IEnumerable<string>.GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}
public struct StructCollection : ICollection<int>
{
    private List<int> _items;
    public int Count => 0;
    public bool IsReadOnly => false;
    public void Add(int item) => (_items ??= new List<int>()).Add(item);
    public void Clear() { }
    public bool Contains(int item) => false;
    public void CopyTo(int[] array, int arrayIndex) { }
    public bool Remove(int item) => false;
    public IEnumerator<int> GetEnumerator() => null;
    IEnumerator IEnumerable.GetEnumerator() => null;
}
public class RequiredArgumentList : List<int> { public RequiredArgumentList(int capacity) { } }

[NinoType]
public class Holder
{
    public InternalCtorList {|#0:Internal|};
    public AbstractList {|#1:Abstract|};
    public LegacyList {|#2:Legacy|};
    public TwoWay {|#3:Ambiguous|};
    public ReadOnlyCollection<int> {|#4:ReadOnly|};
    public StructCollection {|#5:Struct|};
    public RequiredArgumentList {|#6:NoConstructor|};
}
";
        var test = new CSharpAnalyzerTest<Generator.Analysis.NinoAnalyzer, DefaultVerifier> { TestState = { Sources = { code } } };
        TestReferences.Apply(test.TestState);
        string[] names = { "Internal", "Abstract", "Legacy", "Ambiguous", "ReadOnly", "Struct", "NoConstructor" };
        string[] types =
        {
            "InternalCtorList", "AbstractList", "LegacyList", "TwoWay",
            "System.Collections.ObjectModel.ReadOnlyCollection<int>", "StructCollection", "RequiredArgumentList",
        };
        for (int i = 0; i < names.Length; i++)
            test.TestState.ExpectedDiagnostics.Add(Verify.Diagnostic("NINO007").WithLocation(i)
                .WithArguments(names[i], types[i], "Holder"));
        await test.RunAsync();
    }

    // ---- comparers ----

    [TestMethod]
    public void ComparersArePreserved()
    {
        var holder = new ComparerHolder
        {
            Dictionary = new CaseInsensitiveDictionary { ["Key"] = 1 },
            Set = new CaseInsensitiveSet { "Name" },
            Sorted = new DescendingSet { 1, 3, 2 },
            SortedMap = new DescendingDictionary { [1] = "a", [2] = "b" },
        };
        var bytes = NinoSerializer.Serialize(holder);
        var back = NinoDeserializer.Deserialize<ComparerHolder>(bytes);
        Assert.AreEqual(1, back.Dictionary["KEY"]);
        Assert.IsTrue(back.Set.Contains("NAME"));
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, back.Sorted.ToArray());
        CollectionAssert.AreEqual(new[] { 2, 1 }, back.SortedMap.Keys.ToArray());

        // In place: the existing instances (and comparers) are reused.
        var target = new ComparerHolder
        {
            Dictionary = new CaseInsensitiveDictionary { ["KEY"] = 5, ["Other"] = 6 },
            Set = new CaseInsensitiveSet { "x" },
            Sorted = new DescendingSet { 9 },
            SortedMap = new DescendingDictionary { [9] = "z" },
        };
        var dictionary = target.Dictionary;
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(dictionary, target.Dictionary);
        Assert.AreEqual(1, target.Dictionary["key"]);
        Assert.AreEqual(1, target.Dictionary.Count);
        Assert.IsTrue(target.Set.Contains("name"));
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, target.Sorted.ToArray());

        // Runtime path: same bytes as the generated one, same comparers.
        var runtime = BothPaths(new CaseInsensitiveHolder { Value = holder.Dictionary }, h => h.Value).Runtime;
        Assert.AreEqual(1, runtime["kEy"]);
        Assert.IsTrue(BothPaths(new CaseInsensitiveSetHolder { Value = holder.Set }, h => h.Value).Runtime.Contains("nAmE"));
    }

    // ---- shapes ----

    [TestMethod]
    public void NinoTypeCollectionWithMembers()
    {
        var list = new NamedList { 1, 2, 3 };
        list.Name = "n";
        list.Version = 4;
        var (generated, runtime) = BothPaths(new NamedListHolder { Value = list }, h => h.Value);
        foreach (var result in new[] { generated, runtime })
        {
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result);
            Assert.AreEqual("n", result.Name);
            Assert.AreEqual(4, result.Version);
        }

        var target = new NamedListHolder { Value = new NamedList { 9 } };
        var existing = target.Value;
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new NamedListHolder { Value = list }), ref target);
        Assert.AreSame(existing, target.Value);
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, existing);
        Assert.AreEqual("n", existing.Name);
    }

    [TestMethod]
    public void ConstrainedAndNestedGenericCollections()
    {
        var holder = new ConstrainedHolder
        {
            Structs = new StructOnlyList<int> { 1 },
            Classes = new ClassOnlyList<string> { "a", null },
            Nested = new Outer<long>.Inner { 5L },
        };
        var back = NinoDeserializer.Deserialize<ConstrainedHolder>(NinoSerializer.Serialize(holder));
        CollectionAssert.AreEqual(new[] { 1 }, back.Structs);
        CollectionAssert.AreEqual(new[] { "a", null }, back.Classes);
        CollectionAssert.AreEqual(new[] { 5L }, back.Nested);
        Assert.IsInstanceOfType(back.Nested, typeof(Outer<long>.Inner));
        CollectionAssert.AreEqual(new[] { 5L }, RuntimeOnly(new RuntimeBox<Outer<long>.Inner> { Value = holder.Nested }).Value);
        CollectionAssert.AreEqual(new[] { "a", null }, RuntimeOnly(new RuntimeBox<ClassOnlyList<string>> { Value = holder.Classes }).Value);
    }

    [TestMethod]
    public void ExplicitInterfaceCollection()
    {
        ICollection<int> value = new ExplicitCollection();
        value.Add(1);
        value.Add(2);
        var (generated, runtime) = BothPaths(new ExplicitHolder { Value = (ExplicitCollection)value }, h => h.Value);
        CollectionAssert.AreEqual(new[] { 1, 2 }, generated.Items);
        CollectionAssert.AreEqual(new[] { 1, 2 }, runtime.Items);
    }

    [TestMethod]
    public void ReadOnlyCollectionIsAClearError()
    {
        var bytes = NinoSerializer.Serialize(new FrozenHolder { Value = new FrozenCollection() });
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<FrozenHolder>(bytes));
        StringAssert.Contains(e.Message, "read-only");
        var target = new FrozenHolder { Value = new FrozenCollection() };
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize(bytes, ref target));
        CollectionAssert.AreEqual(new[] { 1, 2 }, target.Value.ToArray(), "left untouched");
        // Null data never touches it.
        Assert.IsNull(NinoDeserializer.Deserialize<FrozenHolder>(NinoSerializer.Serialize(new FrozenHolder())).Value);
    }

    [TestMethod]
    public void ObjectModelCollectionsRunTheirHooks()
    {
        var holder = new ObjectModelHolder
        {
            Counting = new CountingCollection { 1, 2 }, // stored as 10, 20
            Observable = new CountingObservable { "a" },
            Keyed = new KeyedItems { new KeyedItem { Id = 3, Name = "c" } },
        };
        var bytes = NinoSerializer.Serialize(holder);
        var back = NinoDeserializer.Deserialize<ObjectModelHolder>(bytes);
        CollectionAssert.AreEqual(new[] { 100, 200 }, back.Counting.ToArray(), "InsertItem ran on read");
        Assert.AreEqual(2, back.Counting.Inserted);
        Assert.AreEqual(0, back.Counting.Cleared, "a new collection is not cleared");
        Assert.AreEqual(1, back.Observable.Changes);
        Assert.AreEqual("c", back.Keyed[3].Name);

        var target = new ObjectModelHolder
        {
            Counting = new CountingCollection { 5 },
            Observable = new CountingObservable(),
            Keyed = new KeyedItems { new KeyedItem { Id = 9 } },
        };
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreEqual(1, target.Counting.Cleared);
        CollectionAssert.AreEqual(new[] { 100, 200 }, target.Counting.ToArray());
        Assert.IsFalse(target.Keyed.Contains(9));
        Assert.AreEqual("c", target.Keyed[3].Name);

        CollectionAssert.AreEqual(new[] { 100, 200 },
            RuntimeOnly(new RuntimeBox<CountingCollection> { Value = holder.Counting }).Value.ToArray());
    }

    [TestMethod]
    public void SelfReferencingCollection()
    {
        var tree = new Tree { new Tree(), new Tree { new Tree() }, null };
        var (generated, runtime) = BothPaths(new TreeHolder { Value = tree }, h => h.Value);
        foreach (var result in new[] { generated, runtime })
        {
            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(1, result[1].Count);
            Assert.IsNull(result[2]);
            Assert.IsInstanceOfType(result[1][0], typeof(Tree));
        }

        Assert.AreEqual(3, RuntimeOnly(tree).Count);
    }

    [TestMethod]
    public void BclCollectionSubclasses()
    {
        var subclasses = new BclSubclassHolder
        {
            Stack = new IntStack(),
            Queue = new NameQueue(),
            Map = new ConcurrentMap { [1] = "a" },
            Linked = new NameLinkedList(),
            Sorted = new NameSortedList { ["b"] = 2, ["a"] = 1 },
        };
        subclasses.Stack.Push(1);
        subclasses.Stack.Push(2);
        subclasses.Stack.Push(3);
        subclasses.Queue.Enqueue("x");
        subclasses.Queue.Enqueue("y");
        subclasses.Linked.AddLast("l1");
        subclasses.Linked.AddLast("l2");
        var bytes = NinoSerializer.Serialize(subclasses);

        // The same bytes as the BCL types themselves.
        var bcl = new BclHolder
        {
            Stack = new Stack<int>(new[] { 1, 2, 3 }),
            Queue = new Queue<string>(new[] { "x", "y" }),
            Map = new ConcurrentDictionary<int, string> { [1] = "a" },
            Linked = new LinkedList<string>(new[] { "l1", "l2" }),
            Sorted = new SortedList<string, int> { ["a"] = 1, ["b"] = 2 },
        };
        CollectionAssert.AreEqual(NinoSerializer.Serialize(bcl), bytes);

        var back = NinoDeserializer.Deserialize<BclSubclassHolder>(bytes);
        Assert.IsInstanceOfType(back.Stack, typeof(IntStack));
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, back.Stack.ToArray(), "stack order preserved");
        CollectionAssert.AreEqual(new[] { "x", "y" }, back.Queue.ToArray(), "queue order preserved");
        Assert.AreEqual("a", back.Map[1]);
        CollectionAssert.AreEqual(new[] { "l1", "l2" }, back.Linked.ToArray());
        CollectionAssert.AreEqual(new[] { "a", "b" }, back.Sorted.Keys.ToArray());

        var target = new BclSubclassHolder { Stack = new IntStack(), Queue = new NameQueue() };
        target.Stack.Push(9);
        target.Queue.Enqueue("z");
        var stack = target.Stack;
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(stack, target.Stack);
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, target.Stack.ToArray());
        CollectionAssert.AreEqual(new[] { "x", "y" }, target.Queue.ToArray());

        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, RuntimeOnly(new RuntimeBox<IntStack> { Value = subclasses.Stack }).Value.ToArray());
        CollectionAssert.AreEqual(new[] { "x", "y" }, RuntimeOnly(new RuntimeBox<NameQueue> { Value = subclasses.Queue }).Value.ToArray());
    }

    // ---- elements ----

    [TestMethod]
    public void RawElementsUseTheBlockCopyOfList()
    {
        var raw = new RawList();
        raw.AddRange(Enumerable.Range(0, 100_000));
        var bytes = NinoSerializer.Serialize(new RawListHolder { Value = raw });
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new IntListHolder { Value = new List<int>(raw) }), bytes);
        var (generated, runtime) = BothPaths(new RawListHolder { Value = raw }, h => h.Value);
        CollectionAssert.AreEqual(raw, generated);
        CollectionAssert.AreEqual(raw, runtime);

        var nullable = new NullableRawList { 1, null, long.MaxValue };
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new NullableListHolder { Value = new List<long?>(nullable) }),
            NinoSerializer.Serialize(new NullableRawHolder { Value = nullable }));
        CollectionAssert.AreEqual(nullable, BothPaths(new NullableRawHolder { Value = nullable }, h => h.Value).Runtime);
    }

    [TestMethod]
    public void PolymorphicAndNestedElements()
    {
        var shapes = new ShapeList { new Circle { Id = 1, Radius = 2 }, null, new Square { Id = 3, Side = 4 } };
        var (generated, runtime) = BothPaths(new ShapeListHolder { Value = shapes }, h => h.Value);
        foreach (var result in new[] { generated, runtime })
        {
            Assert.AreEqual(2f, ((Circle)result[0]).Radius);
            Assert.IsNull(result[1]);
            Assert.AreEqual(4f, ((Square)result[2]).Side);
        }

        var lists = new ListOfLists { new RawList { 1 }, null, new RawList() };
        var nested = BothPaths(new ListOfListsHolder { Value = lists }, h => h.Value);
        foreach (var result in new[] { nested.Generated, nested.Runtime })
        {
            Assert.IsInstanceOfType(result[0], typeof(RawList));
            CollectionAssert.AreEqual(new[] { 1 }, result[0]);
            Assert.IsNull(result[1]);
            Assert.AreEqual(0, result[2].Count);
        }

        var strings = new StringList { null, "", "x" };
        CollectionAssert.AreEqual(strings, BothPaths(new StringListHolder { Value = strings }, h => h.Value).Runtime);
    }

    // ---- declared vs runtime type ----

    [TestMethod]
    public void DeclaredTypeDecidesWhatIsCreated()
    {
        var holder = new DeclaredHolder { AsList = new RawList { 1 }, AsInterface = new PooledCollection { 2 } };
        var bytes = NinoSerializer.Serialize(holder);
        var back = NinoDeserializer.Deserialize<DeclaredHolder>(bytes);
        Assert.AreEqual(typeof(List<int>), back.AsList.GetType(), "a new instance is of the declared type");
        Assert.AreEqual(typeof(List<int>), back.AsInterface.GetType());

        // In place: a List<int> slot reuses the derived list it holds.
        var rawList = new RawList { 9, 9 };
        var target = new DeclaredHolder { AsList = rawList };
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(rawList, target.AsList);
        CollectionAssert.AreEqual(new[] { 1 }, rawList);

        // An IList<int> slot holding a pooled collection that is not a List<int>: replaced, returned once.
        var pooled = new PooledCollection { 5 };
        target = new DeclaredHolder { AsInterface = pooled };
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreEqual(1, pooled.Returns);
        CollectionAssert.AreEqual(new[] { 2 }, target.AsInterface.ToArray());

        // ... and dropped (null data): returned once.
        pooled = new PooledCollection();
        target = new DeclaredHolder { AsInterface = pooled };
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new DeclaredHolder()), ref target);
        Assert.IsNull(target.AsInterface);
        Assert.AreEqual(1, pooled.Returns);
    }

    // ---- aliasing ----

    [TestMethod]
    public void AliasedCollections()
    {
        var shared = new RawList { 1, 2 };
        var holder = new AliasHolder { First = shared, Second = shared };
        var back = NinoDeserializer.Deserialize<AliasHolder>(NinoSerializer.Serialize(holder));
        Assert.AreNotSame(back.First, back.Second, "collections are values: written twice, read as two instances");
        CollectionAssert.AreEqual(back.First, back.Second);

        // trackReferences tracks the owning [NinoType], not its collection members: the same holds.
        var tracked = NinoDeserializer.Deserialize<TrackedAliasHolder>(
            NinoSerializer.Serialize(new TrackedAliasHolder { First = shared, Second = shared }));
        Assert.AreNotSame(tracked.First, tracked.Second);

        // In place, an aliased instance is populated by each slot in turn and dropped by neither.
        var target = new AliasHolder { First = new RawList { 0 } };
        target.Second = target.First;
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new AliasHolder { First = new RawList { 1 }, Second = new RawList { 2 } }), ref target);
        Assert.AreSame(target.First, target.Second);
        CollectionAssert.AreEqual(new[] { 2 }, target.First);
    }

    // ---- concurrency ----

    [TestMethod]
    public void ConcurrentFirstUse()
    {
        var work = new Func<int>[]
        {
            () => Use<M0>(), () => Use<M1>(), () => Use<M2>(), () => Use<M3>(),
            () => Use<M4>(), () => Use<M5>(), () => Use<M6>(), () => Use<M7>(),
        };
        int threads = Math.Max(8, Environment.ProcessorCount);
        using var start = new Barrier(threads);
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Factory.StartNew(() =>
        {
            start.SignalAndWait();
            int sum = 0;
            for (int i = 0; i < work.Length; i++)
                sum += work[(i + t) % work.Length]();
            return sum;
        }, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(tasks);
        foreach (var task in tasks)
            Assert.AreEqual(work.Length * 6, task.Result);
    }

    private static int Use<TMarker>()
    {
        var list = new ConcurrentFirstUse<TMarker> { 1, 2, 3 };
        var box = NinoDeserializer.Deserialize<RuntimeBox<ConcurrentFirstUse<TMarker>>>(
            NinoSerializer.Serialize(new RuntimeBox<ConcurrentFirstUse<TMarker>> { Value = list }));
        var created = NinoActivator<ConcurrentFirstUse<TMarker>>.Create();
        Assert.AreEqual(0, created.Count);
        return box.Value.Sum();
    }

    // ---- data errors ----

    [TestMethod]
    public void DuplicateKeysAreAClearError()
    {
        // Distinct under the writer's comparer, the same under the reader's.
        var bytes = NinoSerializer.Serialize(new StringDictionaryHolder
            { Value = new Dictionary<string, int> { ["a"] = 1, ["A"] = 2 } });
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<CaseInsensitiveHolder>(bytes));
        StringAssert.Contains(e.Message, "twice");
        // Into an existing empty dictionary as well. (Merging into a non-empty one does not look for repeated keys:
        // see "Reuse deserialization and pooling" in docs/v4-design.md.)
        var target = new CaseInsensitiveHolder { Value = new CaseInsensitiveDictionary() };
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize(bytes, ref target));
        Assert.ThrowsExactly<InvalidOperationException>(() => RuntimeOnly(new RuntimeBox<CaseInsensitiveDictionary>
            { Value = NinoDeserializer.Deserialize<RuntimeBox<CaseInsensitiveDictionary>>(NinoSerializer.Serialize(
                new RuntimeBox<Dictionary<string, int>> { Value = new() { ["a"] = 1, ["A"] = 2 } })).Value }));

        var setBytes = NinoSerializer.Serialize(new StringSetHolder { Value = new HashSet<string> { "a", "A" } });
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<CaseInsensitiveSetHolder>(setBytes));
    }

    [TestMethod]
    public void RawKeyStringDictionaryChecksItsData()
    {
        // Raw keys and UTF-16 values are read by their own loop (Reader.ReadStringPairs), new or into an empty
        // dictionary: the same results and checks as the general one.
        var value = new Dictionary<int, string> { [1] = "a", [2] = null, [3] = "", [4] = "bc" };
        var bytes = NinoSerializer.Serialize(value);
        CollectionAssert.AreEqual(value.ToList(), NinoDeserializer.Deserialize<Dictionary<int, string>>(bytes).ToList());
        var empty = new Dictionary<int, string>();
        NinoDeserializer.Deserialize(bytes, ref empty);
        CollectionAssert.AreEqual(value.ToList(), empty.ToList());

        // Count, key 1, length 1, "a", then the second key: made 1 too.
        var repeated = (byte[])bytes.Clone();
        Assert.AreEqual(2, BitConverter.ToInt32(repeated, 14));
        BitConverter.GetBytes(1).CopyTo(repeated, 14);
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<Dictionary<int, string>>(repeated));
        StringAssert.Contains(e.Message, "twice");

        for (int cut = 4; cut < bytes.Length; cut++)
        {
            var truncated = bytes.AsSpan(0, cut).ToArray();
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<Dictionary<int, string>>(truncated),
                $"cut at {cut}");
        }
    }

    [TestMethod]
    public void TruncatedAndNegativeCounts()
    {
        var bytes = NinoSerializer.Serialize(new RawListHolder { Value = new RawList { 1, 2, 3 } });
        for (int cut = 1; cut < bytes.Length; cut++)
        {
            var truncated = bytes.AsSpan(0, cut).ToArray();
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<RawListHolder>(truncated),
                $"cut at {cut}");
        }

        // Count -5 (only -1 means null).
        var shape = NinoSerializer.Serialize(new RawListHolder { Value = new RawList() });
        int countAt = shape.Length - 4;
        BitConverter.GetBytes(-5).CopyTo(shape, countAt);
        var e = Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<RawListHolder>(shape));
        StringAssert.Contains(e.Message, "-5");

        // A huge count is rejected before anything is allocated, and a reused list is left as it was.
        BitConverter.GetBytes(int.MaxValue).CopyTo(shape, countAt);
        var target = new RawListHolder { Value = new RawList { 7 } };
        Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize(shape, ref target));
        CollectionAssert.AreEqual(new[] { 7 }, target.Value);
    }
}

// Types only the runtime-path tests use (the analyzer test declares its own copies).
public class InternalCtorList : List<int>
{
    internal InternalCtorList()
    {
    }
}

public abstract class AbstractList : List<int>
{
}

public class ConcreteList : AbstractList
{
}

public class LegacyList : ArrayList
{
}

/// <summary>A struct collection with state: no collection formatter (they need a class), and not raw memory.</summary>
public struct StructCollection : ICollection<int>
{
    private List<int> _items;
    public int Count => _items?.Count ?? 0;
    public bool IsReadOnly => false;
    public void Add(int item) => (_items ??= new List<int>()).Add(item);
    public void Clear() => _items?.Clear();
    public bool Contains(int item) => _items?.Contains(item) == true;
    public void CopyTo(int[] array, int arrayIndex) => _items?.CopyTo(array, arrayIndex);
    public bool Remove(int item) => _items?.Remove(item) == true;
    public IEnumerator<int> GetEnumerator() => (_items ?? new List<int>()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
