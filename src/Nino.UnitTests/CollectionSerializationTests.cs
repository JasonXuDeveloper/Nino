using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

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
            new SimpleClass
            {
                Id = 1,
                Name = "Test",
                CreateTime = DateTime.Today
            },
            new SimpleClass
            {
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
        Stack<int> stack = new Stack<int>();
        stack.Push(1);
        stack.Push(2);

        byte[] bytes = NinoSerializer.Serialize(stack);
        Assert.IsNotNull(bytes);

        Stack<int> result = NinoDeserializer.Deserialize<Stack<int>>(bytes);
        Assert.AreEqual(stack.Count, result.Count);
        Assert.AreEqual(stack.Pop(), result.Pop());
        Assert.AreEqual(stack.Pop(), result.Pop());

        Queue<int> queue = new Queue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);

        bytes = NinoSerializer.Serialize(queue);
        Assert.IsNotNull(bytes);

        Queue<int> result2 = NinoDeserializer.Deserialize<Queue<int>>(bytes);
        Assert.AreEqual(queue.Count, result2.Count);

        Assert.AreEqual(queue.Dequeue(), result2.Dequeue());
        Assert.AreEqual(queue.Dequeue(), result2.Dequeue());

        Stack<TestClass> stack2 = new Stack<TestClass>();
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

        Queue<TestClass> queue2 = new Queue<TestClass>();
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

        Stack<Queue<(TestClass, int)>> stack3 = new Stack<Queue<(TestClass, int)>>();
        Queue<(TestClass, int)> queue3 = new Queue<(TestClass, int)>();
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

        LinkedList<TestClass> linkedList = new LinkedList<TestClass>();
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

        Queue<int> queue6 = new Queue<int>();
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

        List<SimpleClass?> d = null;
        bytes = NinoSerializer.Serialize(d);
        Console.WriteLine(string.Join(", ", bytes));
        List<SimpleClass?> result4 = NinoDeserializer.Deserialize<List<SimpleClass?>>(bytes);
        Assert.IsNull(result4);
    }

    [TestMethod]
    public void TestCursedGeneric()
    {
        CursedGeneric<int> cursedGeneric = new CursedGeneric<int>
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

        CursedGeneric<string> cursedGeneric2 = new CursedGeneric<string>
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

        CursedGeneric<TestClass> cursedGeneric3 = new CursedGeneric<TestClass>
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
        ConcurrentQueue<int> concurrentQueue = new ConcurrentQueue<int>();
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
        ConcurrentStack<int> concurrentStack = new ConcurrentStack<int>();
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
        ConcurrentQueue<TestClass> concurrentQueue2 = new ConcurrentQueue<TestClass>();
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
        ConcurrentStack<TestClass> concurrentStack2 = new ConcurrentStack<TestClass>();
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
        ConcurrentQueue<ConcurrentStack<int>> nestedQueue = new ConcurrentQueue<ConcurrentStack<int>>();
        ConcurrentStack<int> innerStack = new ConcurrentStack<int>();
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
    public void TestConcurrentCollectionsRefOverload()
    {
        // Test ConcurrentQueue ref overload
        ConcurrentQueue<int> concurrentQueue = new ConcurrentQueue<int>();
        concurrentQueue.Enqueue(1);
        concurrentQueue.Enqueue(2);

        byte[] bytes = NinoSerializer.Serialize(concurrentQueue);
        Assert.IsNotNull(bytes);

        ConcurrentQueue<int> tempQueue = new ConcurrentQueue<int>();
        tempQueue.Enqueue(99); // Should be cleared
        NinoDeserializer.Deserialize(bytes, ref tempQueue);
        Assert.AreEqual(2, tempQueue.Count);
        Assert.IsTrue(tempQueue.TryDequeue(out int val1) && val1 == 1);
        Assert.IsTrue(tempQueue.TryDequeue(out int val2) && val2 == 2);

        // Test ConcurrentStack ref overload
        ConcurrentStack<int> concurrentStack = new ConcurrentStack<int>();
        concurrentStack.Push(1);
        concurrentStack.Push(2);

        bytes = NinoSerializer.Serialize(concurrentStack);
        Assert.IsNotNull(bytes);

        ConcurrentStack<int> tempStack = new ConcurrentStack<int>();
        tempStack.Push(99); // Should be cleared
        NinoDeserializer.Deserialize(bytes, ref tempStack);
        Assert.AreEqual(2, tempStack.Count);
        Assert.IsTrue(tempStack.TryPop(out int sval1) && sval1 == 2);
        Assert.IsTrue(tempStack.TryPop(out int sval2) && sval2 == 1);
    }
}
