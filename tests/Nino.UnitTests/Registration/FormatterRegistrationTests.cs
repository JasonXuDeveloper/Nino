using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests;

// A third-party collection Nino has no built-in formatter for, registered the way Nino.Core registers its own.
[assembly: NinoFormatter(typeof(RingFormatter<,>))]

namespace Nino.UnitTests;

/// <summary>A fixed-capacity buffer: enumerable, but not an <see cref="ICollection{T}"/>.</summary>
public sealed class Ring<T> : IEnumerable<T>
{
    private readonly Queue<T> _items = new();

    public Ring(int capacity) => Capacity = capacity;

    public int Capacity { get; }

    public int Count => _items.Count;

    public void Push(T item)
    {
        if (_items.Count == Capacity)
            _items.Dequeue();
        _items.Enqueue(item);
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Capacity (-1 = null), count, then the items; the element formatter is resolved by Nino.</summary>
public struct RingFormatter<T, TFormatter> : INinoFormatter<Ring<T>> where TFormatter : struct, INinoFormatter<T>
{
    public void Serialize(Ring<T> value, ref Writer writer)
    {
        if (value == null)
        {
            writer.Write(-1);
            return;
        }

        writer.Write(value.Capacity);
        writer.Write(value.Count);
        foreach (var item in value)
            default(TFormatter).Serialize(item, ref writer);
    }

    public void Deserialize(ref Ring<T> value, ref Reader reader)
    {
        reader.Read(out int capacity);
        if (capacity < 0)
        {
            value = null;
            return;
        }

        reader.Read(out int count);
        value = new Ring<T>(capacity);
        for (int i = 0; i < count; i++)
        {
            T item = default;
            default(TFormatter).Deserialize(ref item, ref reader);
            value.Push(item);
        }
    }
}

[NinoType]
public sealed class RingHolder
{
    public Ring<int> Ints;
    public Ring<string> Names;
    public List<Ring<int>> Nested;
}

/// <summary>
/// A generic holder: its member is formatted by the formatter its closed type names, which the runtime resolver
/// chooses for instances the generator did not see.
/// </summary>
[NinoType]
public sealed class RuntimeBox<T>
{
    public T Value;
}

[NinoType] public sealed class RingBox { public Ring<int> Value; }
[NinoType] public sealed class NullableListBox { public List<int?> Value; }
[NinoType] public sealed class NestedDictionaryBox { public Dictionary<string, List<int>> Value; }
[NinoType] public sealed class ListInterfaceBox { public IList<string> Value; }
[NinoType] public sealed class SetInterfaceBox { public ISet<int> Value; }
[NinoType] public sealed class SortedSetBox { public SortedSet<int> Value; }
[NinoType] public sealed class LinkedListBox { public LinkedList<string> Value; }
[NinoType] public sealed class ConcurrentQueueBox { public ConcurrentQueue<int> Value; }
[NinoType] public sealed class SortedDictionaryBox { public SortedDictionary<int, string> Value; }
[NinoType] public sealed class TupleBox { public (int, string) Value; }
[NinoType] public sealed class MatrixBox { public int[,] Value; }
[NinoType] public sealed class PairBox { public KeyValuePair<int, string> Value; }
[NinoType] public sealed class DerivedListBox { public DerivedIntList Value; }
[NinoType] public sealed class OffsetBox { public DateTimeOffset Value; }
[NinoType] public sealed class ImmutableArrayBox { public ImmutableArray<string> Value; }
[NinoType] public sealed class ImmutableListBox { public ImmutableList<int> Value; }
#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD // PriorityQueue: .NET 6+ builds of Nino.Core only
[NinoType] public sealed class PriorityQueueBox { public PriorityQueue<string, int> Value; }
#endif

public sealed class DerivedIntList : List<int>
{
}

[TestClass]
public class FormatterRegistrationTests
{
    private static Ring<int> NewRing(params int[] items)
    {
        var ring = new Ring<int>(4);
        foreach (var item in items)
            ring.Push(item);
        return ring;
    }

    [TestMethod]
    public void GeneratedCodeUsesRegisteredFormatter()
    {
        var holder = new RingHolder
        {
            Ints = NewRing(1, 2, 3, 4, 5),
            Names = new Ring<string>(2),
            Nested = new List<Ring<int>> { NewRing(7), null },
        };
        holder.Names.Push("a");
        holder.Names.Push(null);

        var result = NinoDeserializer.Deserialize<RingHolder>(NinoSerializer.Serialize(holder));

        Assert.AreEqual(4, result.Ints.Capacity);
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, result.Ints.ToArray());
        CollectionAssert.AreEqual(new[] { "a", null }, result.Names.ToArray());
        CollectionAssert.AreEqual(new[] { 7 }, result.Nested[0].ToArray());
        Assert.IsNull(result.Nested[1]);
    }

    [TestMethod]
    public void TopLevelUsesRegisteredFormatter()
    {
        var bytes = NinoSerializer.Serialize(NewRing(9, 8));
        // Capacity, count, two raw ints.
        Assert.AreEqual(16, bytes.Length);
        CollectionAssert.AreEqual(new[] { 9, 8 }, NinoDeserializer.Deserialize<Ring<int>>(bytes).ToArray());
    }

    [TestMethod]
    public void RuntimeResolverUsesRegisteredFormatter()
    {
        var box = new RuntimeBox<Ring<long>> { Value = new Ring<long>(3) };
        box.Value.Push(long.MaxValue);
        var result = NinoDeserializer.Deserialize<RuntimeBox<Ring<long>>>(NinoSerializer.Serialize(box));
        Assert.AreEqual(3, result.Value.Capacity);
        CollectionAssert.AreEqual(new[] { long.MaxValue }, result.Value.ToArray());
    }

    // The generator and the runtime resolver apply the same registrations: a member resolved at compile time
    // and the same member type resolved at runtime (through RuntimeBox<T>) write the same bytes.
    private static void AssertSameBytes<TBox, T>(TBox generated, T value)
    {
        var runtime = NinoSerializer.Serialize(new RuntimeBox<T> { Value = value });
        CollectionAssert.AreEqual(NinoSerializer.Serialize(generated), runtime, typeof(T).Name);
        var back = NinoDeserializer.Deserialize<RuntimeBox<T>>(runtime);
        CollectionAssert.AreEqual(runtime, NinoSerializer.Serialize(back), typeof(T).Name);
    }

    [TestMethod]
    public void GeneratedAndRuntimeFormattersAgree()
    {
        AssertSameBytes(new RingBox { Value = NewRing(1, 2) }, NewRing(1, 2));
        AssertSameBytes(new NullableListBox { Value = new List<int?> { 1, null } }, new List<int?> { 1, null });
        var dictionary = new Dictionary<string, List<int>> { ["a"] = new List<int> { 1 }, ["b"] = null };
        AssertSameBytes(new NestedDictionaryBox { Value = dictionary }, dictionary);
        IList<string> list = new[] { "x", null };
        AssertSameBytes(new ListInterfaceBox { Value = list }, list);
        ISet<int> set = new HashSet<int> { 3 };
        AssertSameBytes(new SetInterfaceBox { Value = set }, set);
        AssertSameBytes(new SortedSetBox { Value = new SortedSet<int> { 2, 1 } }, new SortedSet<int> { 2, 1 });
        AssertSameBytes(new LinkedListBox { Value = new LinkedList<string>(new[] { "l" }) },
            new LinkedList<string>(new[] { "l" }));
        AssertSameBytes(new ConcurrentQueueBox { Value = new ConcurrentQueue<int>(new[] { 5 }) },
            new ConcurrentQueue<int>(new[] { 5 }));
        AssertSameBytes(new SortedDictionaryBox { Value = new SortedDictionary<int, string> { [1] = "s" } },
            new SortedDictionary<int, string> { [1] = "s" });
        AssertSameBytes(new TupleBox { Value = (1, "t") }, (1, "t"));
        AssertSameBytes(new MatrixBox { Value = new[,] { { 1, 2 }, { 3, 4 } } }, new[,] { { 1, 2 }, { 3, 4 } });
        AssertSameBytes(new PairBox { Value = new KeyValuePair<int, string>(1, "p") },
            new KeyValuePair<int, string>(1, "p"));
        AssertSameBytes(new DerivedListBox { Value = new DerivedIntList { 4 } }, new DerivedIntList { 4 });
        var offset = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8));
        AssertSameBytes(new OffsetBox { Value = offset }, offset);
        // Registered by the Nino.Immutable package.
        AssertSameBytes(new ImmutableArrayBox { Value = ImmutableArray.Create("a", null) },
            ImmutableArray.Create("a", null));
        AssertSameBytes(new ImmutableArrayBox(), default(ImmutableArray<string>));
        AssertSameBytes(new ImmutableListBox { Value = ImmutableList.Create(1, 2) }, ImmutableList.Create(1, 2));
#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD
        var queue = new PriorityQueue<string, int>();
        queue.Enqueue("q", 2);
        AssertSameBytes(new PriorityQueueBox { Value = queue }, queue);
#endif
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    // The wire format of the formatters that used to be generated into each assembly is unchanged.
    [TestMethod]
    public void MovedFormattersKeepTheirBytes()
    {
        Assert.AreEqual("020000000100000002000000",
            Hex(NinoSerializer.Serialize(ImmutableArray.Create(1, 2))));
        Assert.AreEqual("FFFFFFFF", Hex(NinoSerializer.Serialize(default(ImmutableArray<int>))));
        Assert.AreEqual("0100000005000000", Hex(NinoSerializer.Serialize(ImmutableList.Create(5))));
#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD
        var queue = new PriorityQueue<int, int>();
        queue.Enqueue(7, 3);
        Assert.AreEqual("010000000700000003000000", Hex(NinoSerializer.Serialize(queue)));
        var back = NinoDeserializer.Deserialize<PriorityQueue<int, int>>(NinoSerializer.Serialize(queue));
        Assert.IsTrue(back.TryDequeue(out int element, out int priority));
        Assert.AreEqual((7, 3), (element, priority));
#endif
    }

    private sealed class TryRegisterTarget
    {
    }

    private struct TryRegisterTargetFormatter : INinoFormatter<TryRegisterTarget>
    {
        public void Serialize(TryRegisterTarget value, ref Writer writer) => writer.Write(0);

        public void Deserialize(ref TryRegisterTarget value, ref Reader reader) => reader.Read(out int _);
    }

    [TestMethod]
    public void TryRegisterSaysWhetherThisCallRegistered()
    {
        Assert.IsTrue(NinoFormatterCache<TryRegisterTarget>.TryRegister<TryRegisterTargetFormatter>());
        Assert.IsFalse(NinoFormatterCache<TryRegisterTarget>.TryRegister<TryRegisterTargetFormatter>());
        Assert.IsTrue(NinoActivator<TryRegisterTarget>.TryRegister(static () => new TryRegisterTarget()));
        Assert.IsFalse(NinoActivator<TryRegisterTarget>.TryRegister(static () => new TryRegisterTarget()));
    }
}
