using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class CollectionSerializationTests
{
    [TestMethod]
    public void TestCollections()
    {
        IEnumerable<SimpleClass> collection = new List<SimpleClass>
        {
            new() {
                Id = 1,
                Name = "Test",
                CreateTime = DateTime.Today
            },
            new() {
                Id = 2,
                Name = "Test2",
                CreateTime = DateTime.Today
            }
        };
        byte[] bytes = NinoSerializer.Serialize(collection);
        Assert.IsNotNull(bytes);
        IEnumerable<SimpleClass> ienumerable = NinoDeserializer.Deserialize<IEnumerable<SimpleClass>>(bytes);
        Assert.AreEqual(collection.Count(), ienumerable.Count());
        for (int i = 0; i < collection.Count(); i++)
        {
            Assert.AreEqual(collection.ElementAt(i).Id, ienumerable.ElementAt(i).Id);
            Assert.AreEqual(collection.ElementAt(i).Name, ienumerable.ElementAt(i).Name);
            Assert.AreEqual(collection.ElementAt(i).CreateTime, ienumerable.ElementAt(i).CreateTime);
        }

        ConcurrentDictionary<int, int>[] dict = new ConcurrentDictionary<int, int>[1];
        for (int i = 0; i < dict.Length; i++)
        {
            dict[i] = new ConcurrentDictionary<int, int>();
            dict[i].TryAdd(i, i);
        }

        bytes = NinoSerializer.Serialize(dict);
        Assert.IsNotNull(bytes);
        Console.WriteLine(string.Join(", ", bytes));

        ConcurrentDictionary<int, int>[] result =
            NinoDeserializer.Deserialize<ConcurrentDictionary<int, int>[]>(bytes);
        Assert.AreEqual(dict.Length, result.Length);
        for (int i = 0; i < dict.Length; i++)
        {
            Assert.AreEqual(dict[i].Count, result[i].Count);
            Assert.AreEqual(dict[i][i], result[i][i]);
        }

        ConcurrentDictionary<int, string>[] dict2 = new ConcurrentDictionary<int, string>[10];
        for (int i = 0; i < dict2.Length; i++)
        {
            dict2[i] = new ConcurrentDictionary<int, string>();
            dict2[i].TryAdd(i, i.ToString());
        }

        bytes = NinoSerializer.Serialize(dict2);
        Assert.IsNotNull(bytes);

        IDictionary<int, string>[] result2 = NinoDeserializer.Deserialize<IDictionary<int, string>[]>(bytes);
        Assert.AreEqual(dict2.Length, result2.Length);
        for (int i = 0; i < dict2.Length; i++)
        {
            Assert.AreEqual(dict2[i].Count, result2[i].Count);
            Assert.AreEqual(dict2[i][i], result2[i][i]);
        }

        IDictionary<int, IDictionary<int, int[]>> dict3 = new Dictionary<int, IDictionary<int, int[]>>();
        for (int i = 0; i < 10; i++)
        {
            dict3[i] = new ConcurrentDictionary<int, int[]>();
            dict3[i].TryAdd(i, new int[] { i, i });
        }

        NinoSerializer.Serialize(dict2);
        bytes = NinoSerializer.Serialize(dict3);
        Assert.IsNotNull(bytes);

        IDictionary<int, IDictionary<int, int[]>> result3 =
            NinoDeserializer.Deserialize<IDictionary<int, IDictionary<int, int[]>>>(bytes);
        Assert.AreEqual(dict3.Count, result3.Count);
        for (int i = 0; i < dict3.Count; i++)
        {
            Assert.AreEqual(dict3[i].Count, result3[i].Count);
            Assert.AreEqual(dict3[i][i].Length, result3[i][i].Length);
            for (int j = 0; j < dict3[i][i].Length; j++)
            {
                Assert.AreEqual(dict3[i][i][j], result3[i][i][j]);
            }
        }
    }

    [TestMethod]
    public void TestList()
    {
        var arr = new List<TestClass3>
        {
            new() { A = 1, B = "Hello" },
            new() { A = 2, B = "World", C = 3 },
            new() { A = 3, B = "Test", C = 4, D = true },
            null
        };
        byte[] bytes = NinoSerializer.Serialize(arr);
        Console.WriteLine(string.Join(", ", bytes));
        Assert.IsNotNull(bytes);
    }

    [TestMethod]
    public void TestNonTrivialCollection()
    {
        Stack<int> stack = new();
        stack.Push(1);
        stack.Push(2);

        byte[] bytes = NinoSerializer.Serialize(stack);
        Assert.IsNotNull(bytes);

        Stack<int> result = NinoDeserializer.Deserialize<Stack<int>>(bytes);
        Assert.AreEqual(stack.Count, result.Count);
        Assert.AreEqual(stack.Pop(), result.Pop());
        Assert.AreEqual(stack.Pop(), result.Pop());

        Queue<int> queue = new();
        queue.Enqueue(1);
        queue.Enqueue(2);

        bytes = NinoSerializer.Serialize(queue);
        Assert.IsNotNull(bytes);

        Queue<int> result2 = NinoDeserializer.Deserialize<Queue<int>>(bytes);
        Assert.AreEqual(queue.Count, result2.Count);

        Assert.AreEqual(queue.Dequeue(), result2.Dequeue());
        Assert.AreEqual(queue.Dequeue(), result2.Dequeue());

        Stack<TestClass> stack2 = new();
        stack2.Push(new TestClass
        {
            A = 1,
            B = "Test"
        });

        bytes = NinoSerializer.Serialize(stack2);
        Assert.IsNotNull(bytes);

        Stack<TestClass> result3 = NinoDeserializer.Deserialize<Stack<TestClass>>(bytes);
        Assert.AreEqual(stack2.Count, result3.Count);
        var item = stack2.Pop();
        var item2 = result3.Pop();
        Assert.AreEqual(item.A, item2.A);
        Assert.AreEqual(item.B, item2.B);

        Queue<TestClass> queue2 = new();
        queue2.Enqueue(new TestClass
        {
            A = 1,
            B = "Test"
        });

        bytes = NinoSerializer.Serialize(queue2);
        Assert.IsNotNull(bytes);

        Queue<TestClass> result4 = NinoDeserializer.Deserialize<Queue<TestClass>>(bytes);
        Assert.AreEqual(queue2.Count, result4.Count);
        item = queue2.Dequeue();
        item2 = result4.Dequeue();
        Assert.AreEqual(item.A, item2.A);
        Assert.AreEqual(item.B, item2.B);

        Stack<Queue<(TestClass, int)>> stack3 = new();
        Queue<(TestClass, int)> queue3 = new();
        queue3.Enqueue((new TestClass
        {
            A = 1,
            B = "Test"
        }, 1));
        stack3.Push(queue3);

        bytes = NinoSerializer.Serialize(stack3);
        Assert.IsNotNull(bytes);

        Stack<Queue<(TestClass, int)>>
            result5 = NinoDeserializer.Deserialize<Stack<Queue<(TestClass, int)>>>(bytes);
        Assert.AreEqual(stack3.Count, result5.Count);
        var queue4 = stack3.Pop();
        var queue5 = result5.Pop();
        Assert.AreEqual(queue4.Count, queue5.Count);
        var item3 = queue4.Dequeue();
        var item4 = queue5.Dequeue();
        Assert.AreEqual(item3.Item1.A, item4.Item1.A);
        Assert.AreEqual(item3.Item1.B, item4.Item1.B);

        LinkedList<TestClass> linkedList = new();
        linkedList.AddLast(new TestClass
        {
            A = 1,
            B = "Test"
        });

        bytes = NinoSerializer.Serialize(linkedList);
        Assert.IsNotNull(bytes);

        LinkedList<TestClass> result6 = NinoDeserializer.Deserialize<LinkedList<TestClass>>(bytes);
        Assert.AreEqual(linkedList.Count, result6.Count);
        var node = linkedList.First;
        var node2 = result6.First;
        Assert.AreEqual(node.Value.A, node2.Value.A);
        Assert.AreEqual(node.Value.B, node2.Value.B);

        Queue<int> queue6 = new();
        queue6.Enqueue(1);
        queue6.Enqueue(2);
        queue6.Enqueue(3);

        bytes = NinoSerializer.Serialize(queue6);
        Assert.IsNotNull(bytes);

        Queue<int> result7 = NinoDeserializer.Deserialize<Queue<int>>(bytes);
        Assert.AreEqual(queue6.Count, result7.Count);
        Assert.AreEqual(queue6.Dequeue(), result7.Dequeue());
        Assert.AreEqual(queue6.Dequeue(), result7.Dequeue());
        Assert.AreEqual(queue6.Dequeue(), result7.Dequeue());
    }

    [TestMethod]
    public void WrappedQueuesWriteTheirElementsInOrder()
    {
        Check(new[] { 1, 2, 3, 4 });
        var day = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Check(new[] { day, day.AddDays(1), day.AddDays(2), day.ToOffset(TimeSpan.FromHours(2)) });

        static void Check<T>(T[] values)
        {
            var queue = new Queue<T>(values.Length);
            foreach (T value in values)
                queue.Enqueue(value);
            queue.Enqueue(queue.Dequeue()); // Keep it full and wrap its backing array.

            var expected = values.Skip(1).Append(values[0]).ToArray();
            using var buffer = new NinoArrayBufferWriter(0);
            NinoSerializer.Serialize(queue, buffer);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(expected), buffer.ToArray());
            CollectionAssert.AreEqual(expected, NinoDeserializer.Deserialize<Queue<T>>(buffer.WrittenSpan).ToArray());
        }
    }

    [TestMethod]
    public void TestNullCollection()
    {
        List<int> a = null;
        byte[] bytes = NinoSerializer.Serialize(a);
        Console.WriteLine(string.Join(", ", bytes));
        List<int> result = NinoDeserializer.Deserialize<List<int>>(bytes);
        Assert.IsNull(result);

        List<int?> b = null;
        bytes = NinoSerializer.Serialize(b);
        Console.WriteLine(string.Join(", ", bytes));
        List<int?> result2 = NinoDeserializer.Deserialize<List<int?>>(bytes);
        Assert.IsNull(result2);

        List<SimpleClass> c = null;
        bytes = NinoSerializer.Serialize(c);
        Console.WriteLine(string.Join(", ", bytes));
        List<SimpleClass> result3 = NinoDeserializer.Deserialize<List<SimpleClass>>(bytes);
        Assert.IsNull(result3);

        List<SimpleClass> d = null;
        bytes = NinoSerializer.Serialize(d);
        Console.WriteLine(string.Join(", ", bytes));
        List<SimpleClass> result4 = NinoDeserializer.Deserialize<List<SimpleClass>>(bytes);
        Assert.IsNull(result4);
    }

    [TestMethod]
    public void TestCursedGeneric()
    {
        CursedGeneric<int> cursedGeneric = new()
        {
            field = new ConcurrentDictionary<string, int[]>
            {
                ["Test"] = new[] { 1, 2, 3 }
            }
        };

        byte[] bytes = NinoSerializer.Serialize(cursedGeneric);
        Assert.IsNotNull(bytes);

        CursedGeneric<int> result = NinoDeserializer.Deserialize<CursedGeneric<int>>(bytes);
        Assert.AreEqual(cursedGeneric.field.Count, result.field.Count);
        Assert.AreEqual(cursedGeneric.field["Test"].Length, result.field["Test"].Length);
        for (int i = 0; i < cursedGeneric.field["Test"].Length; i++)
        {
            Assert.AreEqual(cursedGeneric.field["Test"][i], result.field["Test"][i]);
        }

        CursedGeneric<string> cursedGeneric2 = new()
        {
            field = new ConcurrentDictionary<string, string[]>
            {
                ["Test"] = new[] { "1", "2", "3" }
            }
        };

        bytes = NinoSerializer.Serialize(cursedGeneric2);
        Assert.IsNotNull(bytes);

        CursedGeneric<string> result2 = NinoDeserializer.Deserialize<CursedGeneric<string>>(bytes);
        Assert.AreEqual(cursedGeneric2.field.Count, result2.field.Count);
        Assert.AreEqual(cursedGeneric2.field["Test"].Length, result2.field["Test"].Length);
        for (int i = 0; i < cursedGeneric2.field["Test"].Length; i++)
        {
            Assert.AreEqual(cursedGeneric2.field["Test"][i], result2.field["Test"][i]);
        }

        CursedGeneric<TestClass> cursedGeneric3 = new()
        {
            field = new ConcurrentDictionary<string, TestClass[]>
            {
                ["Test"] = new[] { new TestClass { A = 1, B = "Test" }, new TestClass { A = 2, B = "Test2" } }
            }
        };

        bytes = NinoSerializer.Serialize(cursedGeneric3);
        Assert.IsNotNull(bytes);

        CursedGeneric<TestClass> result3 = NinoDeserializer.Deserialize<CursedGeneric<TestClass>>(bytes);
        Assert.AreEqual(cursedGeneric3.field.Count, result3.field.Count);
        Assert.AreEqual(cursedGeneric3.field["Test"].Length, result3.field["Test"].Length);
        for (int i = 0; i < cursedGeneric3.field["Test"].Length; i++)
        {
            Assert.AreEqual(cursedGeneric3.field["Test"][i].A, result3.field["Test"][i].A);
            Assert.AreEqual(cursedGeneric3.field["Test"][i].B, result3.field["Test"][i].B);
        }
    }

    [TestMethod]
    public void TestConcurrentCollections()
    {
        // Test ConcurrentQueue with primitives
        ConcurrentQueue<int> concurrentQueue = new();
        concurrentQueue.Enqueue(1);
        concurrentQueue.Enqueue(2);
        concurrentQueue.Enqueue(3);

        byte[] bytes = NinoSerializer.Serialize(concurrentQueue);
        Assert.IsNotNull(bytes);

        ConcurrentQueue<int> resultQueue = NinoDeserializer.Deserialize<ConcurrentQueue<int>>(bytes);
        Assert.AreEqual(concurrentQueue.Count, resultQueue.Count);
        Assert.IsTrue(resultQueue.TryDequeue(out int val1) && val1 == 1);
        Assert.IsTrue(resultQueue.TryDequeue(out int val2) && val2 == 2);
        Assert.IsTrue(resultQueue.TryDequeue(out int val3) && val3 == 3);

        // Test ConcurrentStack with primitives
        ConcurrentStack<int> concurrentStack = new();
        concurrentStack.Push(1);
        concurrentStack.Push(2);
        concurrentStack.Push(3);

        bytes = NinoSerializer.Serialize(concurrentStack);
        Assert.IsNotNull(bytes);

        ConcurrentStack<int> resultStack = NinoDeserializer.Deserialize<ConcurrentStack<int>>(bytes);
        Assert.AreEqual(concurrentStack.Count, resultStack.Count);
        Assert.IsTrue(resultStack.TryPop(out int sval1) && sval1 == 3);
        Assert.IsTrue(resultStack.TryPop(out int sval2) && sval2 == 2);
        Assert.IsTrue(resultStack.TryPop(out int sval3) && sval3 == 1);

        // Test ConcurrentQueue with complex types
        ConcurrentQueue<TestClass> concurrentQueue2 = new();
        concurrentQueue2.Enqueue(new TestClass { A = 1, B = "Test1" });
        concurrentQueue2.Enqueue(new TestClass { A = 2, B = "Test2" });

        bytes = NinoSerializer.Serialize(concurrentQueue2);
        Assert.IsNotNull(bytes);

        ConcurrentQueue<TestClass> resultQueue2 = NinoDeserializer.Deserialize<ConcurrentQueue<TestClass>>(bytes);
        Assert.AreEqual(concurrentQueue2.Count, resultQueue2.Count);
        Assert.IsTrue(resultQueue2.TryDequeue(out var item1));
        Assert.AreEqual(1, item1.A);
        Assert.AreEqual("Test1", item1.B);
        Assert.IsTrue(resultQueue2.TryDequeue(out var item2));
        Assert.AreEqual(2, item2.A);
        Assert.AreEqual("Test2", item2.B);

        // Test ConcurrentStack with complex types
        ConcurrentStack<TestClass> concurrentStack2 = new();
        concurrentStack2.Push(new TestClass { A = 1, B = "Test1" });
        concurrentStack2.Push(new TestClass { A = 2, B = "Test2" });

        bytes = NinoSerializer.Serialize(concurrentStack2);
        Assert.IsNotNull(bytes);

        ConcurrentStack<TestClass> resultStack2 = NinoDeserializer.Deserialize<ConcurrentStack<TestClass>>(bytes);
        Assert.AreEqual(concurrentStack2.Count, resultStack2.Count);
        Assert.IsTrue(resultStack2.TryPop(out var sitem1));
        Assert.AreEqual(2, sitem1.A);
        Assert.AreEqual("Test2", sitem1.B);
        Assert.IsTrue(resultStack2.TryPop(out var sitem2));
        Assert.AreEqual(1, sitem2.A);
        Assert.AreEqual("Test1", sitem2.B);

        // Test nested concurrent collections
        ConcurrentQueue<ConcurrentStack<int>> nestedQueue = new();
        ConcurrentStack<int> innerStack = new();
        innerStack.Push(1);
        innerStack.Push(2);
        nestedQueue.Enqueue(innerStack);

        bytes = NinoSerializer.Serialize(nestedQueue);
        Assert.IsNotNull(bytes);

        ConcurrentQueue<ConcurrentStack<int>> resultNestedQueue = NinoDeserializer.Deserialize<ConcurrentQueue<ConcurrentStack<int>>>(bytes);
        Assert.AreEqual(nestedQueue.Count, resultNestedQueue.Count);
        Assert.IsTrue(resultNestedQueue.TryDequeue(out var resultInnerStack));
        Assert.AreEqual(2, resultInnerStack.Count);
        Assert.IsTrue(resultInnerStack.TryPop(out int innerVal1) && innerVal1 == 2);
        Assert.IsTrue(resultInnerStack.TryPop(out int innerVal2) && innerVal2 == 1);
    }

    [TestMethod]
    public void ConcurrentQueueWritesASnapshotWhenItemsChangeDuringSerialization()
    {
        var queue = new ConcurrentQueue<SnapshotItem>();
        queue.Enqueue(new SnapshotItem(1, () =>
        {
            queue.TryDequeue(out _);
            queue.TryDequeue(out _);
            queue.Enqueue(new SnapshotItem(3));
        }));
        queue.Enqueue(new SnapshotItem(2));

        using var buffer = new NinoArrayBufferWriter(0);
        var writer = new Writer(buffer);
        default(ProducerConsumerFormatter<ConcurrentQueue<SnapshotItem>, SnapshotItem, SnapshotItemFormatter>)
            .Serialize(queue, ref writer);
        writer.Flush();

        CollectionAssert.AreEqual(new[] { 1, 2 }, NinoDeserializer.Deserialize<int[]>(buffer.WrittenSpan));
        Assert.AreEqual(1, queue.Count);
    }

    [TestMethod]
    public void DerivedConcurrentQueueKeepsItsInterfaceSnapshot()
    {
        ConcurrentQueue<int> queue = new AlternateSnapshotQueue();
        queue.Enqueue(7);
        using var buffer = new NinoArrayBufferWriter(0);
        var writer = new Writer(buffer);
        default(ProducerConsumerFormatter<ConcurrentQueue<int>, int, UnmanagedFormatter<int>>)
            .Serialize(queue, ref writer);
        writer.Flush();

        CollectionAssert.AreEqual(new[] { 42 }, NinoDeserializer.Deserialize<int[]>(buffer.WrittenSpan));
    }

    private sealed class AlternateSnapshotQueue : ConcurrentQueue<int>, IProducerConsumerCollection<int>
    {
        int[] IProducerConsumerCollection<int>.ToArray() => new[] { 42 };
    }

    [TestMethod]
    public void DerivedConcurrentStackKeepsItsInterfaceRefill()
    {
        var stack = new AlternateRefillStack();
        stack.Push(99);
        ConcurrentStack<int> target = stack;
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(new[] { 1, 2 }), ref target);

        Assert.AreSame(stack, target);
        Assert.AreEqual(2, stack.TakeCalls);
        CollectionAssert.AreEqual(new[] { 12, 11 }, stack.ToArray());
    }

    private sealed class AlternateRefillStack : ConcurrentStack<int>, IProducerConsumerCollection<int>
    {
        public int TakeCalls;

        bool IProducerConsumerCollection<int>.TryTake(out int item)
        {
            TakeCalls++;
            return TryPop(out item);
        }

        bool IProducerConsumerCollection<int>.TryAdd(int item)
        {
            Push(item + 10);
            return true;
        }
    }

    private sealed record SnapshotItem(int Value, Action OnWrite = null);

    private struct SnapshotItemFormatter : INinoFormatter<SnapshotItem>
    {
        public void Serialize(SnapshotItem value, ref Writer writer)
        {
            value.OnWrite?.Invoke();
            writer.Write(value.Value);
        }

        public void Deserialize(ref Reader reader, ref SnapshotItem value) => value = new SnapshotItem(reader.Read<int>());
    }

#if NET8_0_OR_GREATER && !NINO_CORE_NETSTANDARD
    [TestMethod]
    [DoNotParallelize] // Forces a process-wide compacting collection.
    public void ConcurrentStackKeepsTheCapturedChainAcrossPopsAndCompaction()
    {
        Assert.IsTrue(ConcurrentStackNodes<int>.Usable);
        var stack = new ConcurrentStack<int>(new[] { 1, 2, 3, 4 });
        using var buffer = new NinoArrayBufferWriter(0, new RentCallbackPool(() =>
        {
            Assert.IsTrue(stack.TryPop(out _));
            Assert.AreEqual(2, stack.TryPopRange(new int[2]));
            stack.Clear();
            stack.Push(99);
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }));
        NinoSerializer.Serialize(stack, buffer);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, NinoDeserializer.Deserialize<int[]>(buffer.WrittenSpan));
        CollectionAssert.AreEqual(new[] { 99 }, stack.ToArray());
    }

    private sealed class RentCallbackPool : ArrayPool<byte>
    {
        private readonly Action _callback;

        public RentCallbackPool(Action callback) => _callback = callback;

        public override byte[] Rent(int minimumLength)
        {
            _callback();
            return ArrayPool<byte>.Shared.Rent(minimumLength);
        }

        public override void Return(byte[] array, bool clearArray = false) =>
            ArrayPool<byte>.Shared.Return(array, clearArray);
    }
#endif

    [TestMethod]
    public void TestConcurrentCollectionsRefOverload()
    {
        // Test ConcurrentQueue ref overload
        ConcurrentQueue<int> concurrentQueue = new();
        concurrentQueue.Enqueue(1);
        concurrentQueue.Enqueue(2);

        byte[] bytes = NinoSerializer.Serialize(concurrentQueue);
        Assert.IsNotNull(bytes);

        ConcurrentQueue<int> tempQueue = new();
        tempQueue.Enqueue(99); // Should be cleared
        NinoDeserializer.Deserialize(bytes, ref tempQueue);
        Assert.AreEqual(2, tempQueue.Count);
        Assert.IsTrue(tempQueue.TryDequeue(out int val1) && val1 == 1);
        Assert.IsTrue(tempQueue.TryDequeue(out int val2) && val2 == 2);

        // The concrete stack registration also wins when the runtime resolves the formatter itself.
        var formatter = NinoFormatterResolver.GetFormatterType(typeof(ConcurrentStack<int>), topLevel: true);
        Assert.AreEqual(typeof(ConcurrentStackFormatter<,>), formatter.GetGenericTypeDefinition());

        ConcurrentStack<int> concurrentStack = new();
        concurrentStack.Push(1);
        concurrentStack.Push(2);

        bytes = NinoSerializer.Serialize(concurrentStack);
        Assert.IsNotNull(bytes);

        ConcurrentStack<int> tempStack = new();
        tempStack.Push(99); // Should be cleared
        NinoDeserializer.Deserialize(bytes, ref tempStack);
        Assert.AreEqual(2, tempStack.Count);
        Assert.IsTrue(tempStack.TryPop(out int sval1) && sval1 == 2);
        Assert.IsTrue(tempStack.TryPop(out int sval2) && sval2 == 1);
    }

    [TestMethod]
    public void SetInterfacesReadLikeHashSets()
    {
        // ISet<T> writes and reads exactly like HashSet<T>: the same bytes, and a repeated element is an error.
        ISet<int> set = new HashSet<int> { 1, 2, 3 };
        var bytes = NinoSerializer.Serialize(set);
        CollectionAssert.AreEqual(NinoSerializer.Serialize((HashSet<int>)set), bytes);
        Assert.IsTrue(NinoDeserializer.Deserialize<ISet<int>>(bytes).SetEquals(set));
        ISet<string> strings = new HashSet<string> { "a", null };
        Assert.IsTrue(NinoDeserializer.Deserialize<ISet<string>>(NinoSerializer.Serialize(strings)).SetEquals(strings));

        var repeated = NinoSerializer.Serialize(new List<int> { 7, 7 });
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<HashSet<int>>(repeated));
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<ISet<int>>(repeated));
        repeated = NinoSerializer.Serialize(new List<string> { "s", "s" });
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<ISet<string>>(repeated));

        // An existing set is refilled in place.
        ISet<int> target = new HashSet<int> { 9 };
        var same = target;
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(same, target);
        Assert.IsTrue(target.SetEquals(set));
    }

    [TestMethod]
    public void TestNewCollectionTypes()
    {
        // Test SortedSet<T>
        // 1. Primitive unmanaged
        SortedSet<int> sortedSetInt = new() { 5, 3, 1, 4, 2 };
        byte[] bytes = NinoSerializer.Serialize(sortedSetInt);
        Assert.IsNotNull(bytes);
        SortedSet<int> sortedSetIntResult = NinoDeserializer.Deserialize<SortedSet<int>>(bytes);
        Assert.AreEqual(5, sortedSetIntResult.Count);
        Assert.IsTrue(sortedSetIntResult.SetEquals(sortedSetInt));

        // 2. TestStruct3 (unmanaged struct)
        SortedSet<TestStruct3> sortedSetStruct = new()
        {
            new TestStruct3 { A = 1, B = 1.5f },
            new TestStruct3 { A = 2, B = 2.5f }
        };
        bytes = NinoSerializer.Serialize(sortedSetStruct);
        Assert.IsNotNull(bytes);
        SortedSet<TestStruct3> sortedSetStructResult = NinoDeserializer.Deserialize<SortedSet<TestStruct3>>(bytes);
        Assert.AreEqual(2, sortedSetStructResult.Count);

        // 3. Managed reference type
        SortedSet<string> sortedSetString = new() { "delta", "alpha", "charlie", "bravo" };
        bytes = NinoSerializer.Serialize(sortedSetString);
        Assert.IsNotNull(bytes);
        SortedSet<string> sortedSetStringResult = NinoDeserializer.Deserialize<SortedSet<string>>(bytes);
        Assert.AreEqual(4, sortedSetStringResult.Count);
        Assert.IsTrue(sortedSetStringResult.SetEquals(sortedSetString));

        Console.WriteLine("SortedSet tests passed!");

#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD // PriorityQueue: .NET 6+ builds of Nino.Core only
        // Test PriorityQueue<TElement, TPriority>
        // 1. Primitive unmanaged
        PriorityQueue<int, int> priorityQueueInt = new();
        priorityQueueInt.Enqueue(10, 3);
        priorityQueueInt.Enqueue(20, 1);
        priorityQueueInt.Enqueue(30, 2);
        bytes = NinoSerializer.Serialize(priorityQueueInt);
        Assert.IsNotNull(bytes);
        PriorityQueue<int, int> priorityQueueIntResult = NinoDeserializer.Deserialize<PriorityQueue<int, int>>(bytes);
        Assert.AreEqual(3, priorityQueueIntResult.Count);
        Assert.AreEqual(20, priorityQueueIntResult.Dequeue()); // Priority 1
        Assert.AreEqual(30, priorityQueueIntResult.Dequeue()); // Priority 2
        Assert.AreEqual(10, priorityQueueIntResult.Dequeue()); // Priority 3

        // 2. String element with int priority (managed + unmanaged)
        PriorityQueue<string, int> priorityQueueStr = new();
        priorityQueueStr.Enqueue("low", 10);
        priorityQueueStr.Enqueue("high", 1);
        priorityQueueStr.Enqueue("medium", 5);
        bytes = NinoSerializer.Serialize(priorityQueueStr);
        Assert.IsNotNull(bytes);
        PriorityQueue<string, int> priorityQueueStrResult =
            NinoDeserializer.Deserialize<PriorityQueue<string, int>>(bytes);
        Assert.AreEqual(3, priorityQueueStrResult.Count);
        Assert.AreEqual("high", priorityQueueStrResult.Dequeue());

        // 3. TestStruct3 with byte priority (both unmanaged)
        PriorityQueue<TestStruct3, byte> priorityQueueStruct = new();
        priorityQueueStruct.Enqueue(new TestStruct3 { A = 1, B = 1.5f }, 3);
        priorityQueueStruct.Enqueue(new TestStruct3 { A = 2, B = 2.5f }, 1);
        bytes = NinoSerializer.Serialize(priorityQueueStruct);
        Assert.IsNotNull(bytes);
        PriorityQueue<TestStruct3, byte> priorityQueueStructResult =
            NinoDeserializer.Deserialize<PriorityQueue<TestStruct3, byte>>(bytes);
        Assert.AreEqual(2, priorityQueueStructResult.Count);

        Console.WriteLine("PriorityQueue tests passed!");
#endif

        // Test SortedDictionary<TKey, TValue>
        // 1. Primitive unmanaged key and value
        SortedDictionary<int, int> sortedDictInt = new()
        {
            { 3, 30 },
            { 1, 10 },
            { 2, 20 }
        };
        bytes = NinoSerializer.Serialize(sortedDictInt);
        Assert.IsNotNull(bytes);
        SortedDictionary<int, int> sortedDictIntResult =
            NinoDeserializer.Deserialize<SortedDictionary<int, int>>(bytes);
        Assert.AreEqual(3, sortedDictIntResult.Count);
        Assert.AreEqual(10, sortedDictIntResult[1]);
        Assert.AreEqual(20, sortedDictIntResult[2]);
        Assert.AreEqual(30, sortedDictIntResult[3]);

        // 2. String key, managed value type
        SortedDictionary<string, TestStruct2> sortedDictMixed = new()
        {
            { "first", new TestStruct2 { A = 1, B = true, C = new TestStruct3 { A = 1, B = 1.5f } } },
            { "second", new TestStruct2 { A = 2, B = false, C = new TestStruct3 { A = 2, B = 2.5f } } }
        };
        bytes = NinoSerializer.Serialize(sortedDictMixed);
        Assert.IsNotNull(bytes);
        SortedDictionary<string, TestStruct2> sortedDictMixedResult =
            NinoDeserializer.Deserialize<SortedDictionary<string, TestStruct2>>(bytes);
        Assert.AreEqual(2, sortedDictMixedResult.Count);
        Assert.AreEqual(1, sortedDictMixedResult["first"].A);

        Console.WriteLine("SortedDictionary tests passed!");

        // Test SortedList<TKey, TValue>
        SortedList<int, string> sortedList = new()
        {
            { 3, "three" },
            { 1, "one" },
            { 2, "two" }
        };
        bytes = NinoSerializer.Serialize(sortedList);
        Assert.IsNotNull(bytes);
        SortedList<int, string> sortedListResult = NinoDeserializer.Deserialize<SortedList<int, string>>(bytes);
        Assert.AreEqual(3, sortedListResult.Count);
        Assert.AreEqual("one", sortedListResult[1]);

        Console.WriteLine("SortedList tests passed!");

        // Test ReadOnlyDictionary<TKey, TValue>
        Dictionary<string, int> innerDict = new()
        {
            { "one", 1 },
            { "two", 2 },
            { "three", 3 }
        };
        System.Collections.ObjectModel.ReadOnlyDictionary<string, int> readOnlyDict =
            new(innerDict);
        bytes = NinoSerializer.Serialize(readOnlyDict);
        Assert.IsNotNull(bytes);
        System.Collections.ObjectModel.ReadOnlyDictionary<string, int> readOnlyDictResult =
            NinoDeserializer.Deserialize<System.Collections.ObjectModel.ReadOnlyDictionary<string, int>>(bytes);
        Assert.AreEqual(3, readOnlyDictResult.Count);
        Assert.AreEqual(1, readOnlyDictResult["one"]);

        Console.WriteLine("ReadOnlyDictionary tests passed!");

        // Test ImmutableArray<T>
        // 1. Primitive unmanaged
        ImmutableArray<int> immutableArrayInt =
            ImmutableArray.Create(1, 2, 3, 4, 5);
        bytes = NinoSerializer.Serialize(immutableArrayInt);
        Assert.IsNotNull(bytes);
        ImmutableArray<int> immutableArrayIntResult =
            NinoDeserializer.Deserialize<ImmutableArray<int>>(bytes);
        Assert.AreEqual(5, immutableArrayIntResult.Length);
        Assert.AreEqual(1, immutableArrayIntResult[0]);
        Assert.AreEqual(5, immutableArrayIntResult[4]);

        // 2. Managed reference type
        ImmutableArray<string> immutableArrayStr =
            ImmutableArray.Create("one", "two", "three");
        bytes = NinoSerializer.Serialize(immutableArrayStr);
        Assert.IsNotNull(bytes);
        ImmutableArray<string> immutableArrayStrResult =
            NinoDeserializer.Deserialize<ImmutableArray<string>>(bytes);
        Assert.AreEqual(3, immutableArrayStrResult.Length);
        Assert.AreEqual("one", immutableArrayStrResult[0]);

        // 3. TestStruct3 (unmanaged struct)
        ImmutableArray<TestStruct3> immutableArrayStruct =
            ImmutableArray.Create(
                new TestStruct3 { A = 1, B = 1.5f },
                new TestStruct3 { A = 2, B = 2.5f });
        bytes = NinoSerializer.Serialize(immutableArrayStruct);
        Assert.IsNotNull(bytes);
        ImmutableArray<TestStruct3> immutableArrayStructResult =
            NinoDeserializer.Deserialize<ImmutableArray<TestStruct3>>(bytes);
        Assert.AreEqual(2, immutableArrayStructResult.Length);
        Assert.AreEqual((byte)1, immutableArrayStructResult[0].A);

        Console.WriteLine("ImmutableArray tests passed!");

        // Test ImmutableList<T>
        // 1. Primitive unmanaged
        ImmutableList<int> immutableListInt =
            ImmutableList.Create(10, 20, 30);
        bytes = NinoSerializer.Serialize(immutableListInt);
        Assert.IsNotNull(bytes);
        ImmutableList<int> immutableListIntResult =
            NinoDeserializer.Deserialize<ImmutableList<int>>(bytes);
        Assert.AreEqual(3, immutableListIntResult.Count);
        Assert.AreEqual(10, immutableListIntResult[0]);

        // 2. Managed value type (TestStruct2)
        ImmutableList<TestStruct2> immutableListStruct =
            ImmutableList.Create(
                new TestStruct2 { A = 1, B = true, C = new TestStruct3 { A = 1, B = 1.5f } });
        bytes = NinoSerializer.Serialize(immutableListStruct);
        Assert.IsNotNull(bytes);
        ImmutableList<TestStruct2> immutableListStructResult =
            NinoDeserializer.Deserialize<ImmutableList<TestStruct2>>(bytes);
        Assert.AreEqual(1, immutableListStructResult.Count);
        Assert.AreEqual(1, immutableListStructResult[0].A);

        Console.WriteLine("ImmutableList tests passed!");
    }

    [TestMethod]
    public void TestNewCollectionTypesComplex()
    {
        // Test nested generic types with new collections
        // 1. List of SortedSet
        List<SortedSet<int>> listOfSortedSets = new()
        {
            new SortedSet<int> { 3, 1, 2 },
            new SortedSet<int> { 6, 4, 5 }
        };
        byte[] bytes = NinoSerializer.Serialize(listOfSortedSets);
        Assert.IsNotNull(bytes);
        List<SortedSet<int>> listOfSortedSetsResult = NinoDeserializer.Deserialize<List<SortedSet<int>>>(bytes);
        Assert.AreEqual(2, listOfSortedSetsResult.Count);
        Assert.AreEqual(3, listOfSortedSetsResult[0].Count);

        // 2. Dictionary with ImmutableArray values
        Dictionary<string, ImmutableArray<int>> dictWithImmutableArrays =
            new()
            {
                { "first", ImmutableArray.Create(1, 2, 3) },
                { "second", ImmutableArray.Create(4, 5, 6) }
            };
        bytes = NinoSerializer.Serialize(dictWithImmutableArrays);
        Assert.IsNotNull(bytes);
        Dictionary<string, ImmutableArray<int>> dictWithImmutableArraysResult =
            NinoDeserializer.Deserialize<Dictionary<string, ImmutableArray<int>>>(bytes);
        Assert.AreEqual(2, dictWithImmutableArraysResult.Count);
        Assert.AreEqual(3, dictWithImmutableArraysResult["first"].Length);

        // 3. Jagged array with SortedDictionary
        SortedDictionary<int, string>[] jaggedSortedDict = new SortedDictionary<int, string>[]
        {
            new() { { 1, "one" }, { 2, "two" } },
            new() { { 3, "three" }, { 4, "four" } }
        };
        bytes = NinoSerializer.Serialize(jaggedSortedDict);
        Assert.IsNotNull(bytes);
        SortedDictionary<int, string>[] jaggedSortedDictResult =
            NinoDeserializer.Deserialize<SortedDictionary<int, string>[]>(bytes);
        Assert.AreEqual(2, jaggedSortedDictResult.Length);
        Assert.AreEqual("one", jaggedSortedDictResult[0][1]);

        // 4. Multi-dimensional array as type parameter in ImmutableList
        ImmutableList<int[,]> immutableListOf2DArrays =
            ImmutableList.Create(
                new int[,] { { 1, 2 }, { 3, 4 } },
                new int[,] { { 5, 6 }, { 7, 8 } }
            );
        bytes = NinoSerializer.Serialize(immutableListOf2DArrays);
        Assert.IsNotNull(bytes);
        ImmutableList<int[,]> immutableListOf2DArraysResult =
            NinoDeserializer.Deserialize<ImmutableList<int[,]>>(bytes);
        Assert.AreEqual(2, immutableListOf2DArraysResult.Count);
        Assert.AreEqual(1, immutableListOf2DArraysResult[0][0, 0]);
        Assert.AreEqual(8, immutableListOf2DArraysResult[1][1, 1]);

#if NET6_0_OR_GREATER && !NINO_CORE_NETSTANDARD // PriorityQueue: .NET 6+ builds of Nino.Core only
        // 5. PriorityQueue with nested generic type parameters
        PriorityQueue<List<string>, int> priorityQueueNested = new();
        priorityQueueNested.Enqueue(new List<string> { "a", "b" }, 2);
        priorityQueueNested.Enqueue(new List<string> { "c", "d" }, 1);
        bytes = NinoSerializer.Serialize(priorityQueueNested);
        Assert.IsNotNull(bytes);
        PriorityQueue<List<string>, int> priorityQueueNestedResult =
            NinoDeserializer.Deserialize<PriorityQueue<List<string>, int>>(bytes);
        Assert.AreEqual(2, priorityQueueNestedResult.Count);
        List<string> firstItem = priorityQueueNestedResult.Dequeue();
        Assert.AreEqual(2, firstItem.Count);
        Assert.AreEqual("c", firstItem[0]); // Priority 1 comes first
#endif

        // 6. ReadOnlyDictionary with complex keys and values
        Dictionary<int, ImmutableArray<TestStruct3>> innerComplexDict =
            new()
            {
                { 1, ImmutableArray.Create(new TestStruct3 { A = 1, B = 1.5f }) },
                { 2, ImmutableArray.Create(new TestStruct3 { A = 2, B = 2.5f }) }
            };
        System.Collections.ObjectModel.ReadOnlyDictionary<int, ImmutableArray<TestStruct3>>
            readOnlyComplexDict =
                new(innerComplexDict);
        bytes = NinoSerializer.Serialize(readOnlyComplexDict);
        Assert.IsNotNull(bytes);
        System.Collections.ObjectModel.ReadOnlyDictionary<int, ImmutableArray<TestStruct3>>
            readOnlyComplexDictResult =
                NinoDeserializer
                    .Deserialize<System.Collections.ObjectModel.ReadOnlyDictionary<int,
                        ImmutableArray<TestStruct3>>>(bytes);
        Assert.AreEqual(2, readOnlyComplexDictResult.Count);
        Assert.AreEqual((byte)1, readOnlyComplexDictResult[1][0].A);

        Console.WriteLine("Complex nested collection tests passed!");
    }
}
