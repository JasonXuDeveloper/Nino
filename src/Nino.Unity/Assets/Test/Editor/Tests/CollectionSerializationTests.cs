using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nino.Core;
using Nino.Test;
using NUnit.Framework;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Test.Editor.Tests
{
    public class CollectionSerializationTests
    {
        [Test]
        public void TestCollections()
        {
            IEnumerable<Data> collection = new List<Data>
            {
                new Data
                {
                    x = 1,
                    y = 100,
                    z = 200,
                    f = 1.5f,
                    d = 10.5m,
                    db = 20.5,
                    bo = true,
                    en = TestEnum.A
                },
                new Data
                {
                    x = 2,
                    y = 200,
                    z = 300,
                    f = 2.5f,
                    d = 30.5m,
                    db = 40.5,
                    bo = false,
                    en = TestEnum.B
                }
            };
            byte[] bytes = NinoSerializer.Serialize(collection);
            Assert.IsNotNull(bytes);
            IEnumerable<Data> ienumerable = NinoDeserializer.Deserialize<IEnumerable<Data>>(bytes);
            Assert.AreEqual(collection.Count(), ienumerable.Count());
            for (int i = 0; i < collection.Count(); i++)
            {
                Assert.AreEqual(collection.ElementAt(i).x, ienumerable.ElementAt(i).x);
                Assert.AreEqual(collection.ElementAt(i).y, ienumerable.ElementAt(i).y);
                Assert.AreEqual(collection.ElementAt(i).z, ienumerable.ElementAt(i).z);
            }

            ConcurrentDictionary<int, int>[] dict = new ConcurrentDictionary<int, int>[1];
            for (int i = 0; i < dict.Length; i++)
            {
                dict[i] = new ConcurrentDictionary<int, int>();
                dict[i].TryAdd(i, i);
            }

            bytes = NinoSerializer.Serialize(dict);
            Assert.IsNotNull(bytes);
            Debug.Log($"ConcurrentDictionary bytes: {string.Join(", ", bytes)}");

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

        [Test]
        public void TestList()
        {
            var arr = new List<Data>
            {
                new Data { x = 1, y = 100, z = 200, f = 1.5f, d = 10.5m, db = 20.5, bo = true, en = TestEnum.A },
                new Data { x = 2, y = 200, z = 300, f = 2.5f, d = 30.5m, db = 40.5, bo = false, en = TestEnum.B },
                new Data { x = 3, y = 300, z = 400, f = 3.5f, d = 50.5m, db = 60.5, bo = true, en = TestEnum.A }
            };
            byte[] bytes = NinoSerializer.Serialize(arr);
            Debug.Log($"List<Data> bytes: {string.Join(", ", bytes)}");
            Assert.IsNotNull(bytes);
        }

        [Test]
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

            Stack<NotIncludeAllClass> stack2 = new Stack<NotIncludeAllClass>();
            stack2.Push(new NotIncludeAllClass
            {
                a = 1,
                b = 100
            });

            bytes = NinoSerializer.Serialize(stack2);
            Assert.IsNotNull(bytes);

            Stack<NotIncludeAllClass> result3 = NinoDeserializer.Deserialize<Stack<NotIncludeAllClass>>(bytes);
            Assert.AreEqual(stack2.Count, result3.Count);
            var item = stack2.Pop();
            var item2 = result3.Pop();
            Assert.AreEqual(item.a, item2.a);
            Assert.AreEqual(item.b, item2.b);

            Queue<NotIncludeAllClass> queue2 = new Queue<NotIncludeAllClass>();
            queue2.Enqueue(new NotIncludeAllClass
            {
                a = 1,
                b = 100
            });

            bytes = NinoSerializer.Serialize(queue2);
            Assert.IsNotNull(bytes);

            Queue<NotIncludeAllClass> result4 = NinoDeserializer.Deserialize<Queue<NotIncludeAllClass>>(bytes);
            Assert.AreEqual(queue2.Count, result4.Count);
            item = queue2.Dequeue();
            item2 = result4.Dequeue();
            Assert.AreEqual(item.a, item2.a);
            Assert.AreEqual(item.b, item2.b);

            Stack<Queue<(NotIncludeAllClass, int)>> stack3 = new Stack<Queue<(NotIncludeAllClass, int)>>();
            Queue<(NotIncludeAllClass, int)> queue3 = new Queue<(NotIncludeAllClass, int)>();
            queue3.Enqueue((new NotIncludeAllClass
            {
                a = 1,
                b = 100
            }, 1));
            stack3.Push(queue3);

            bytes = NinoSerializer.Serialize(stack3);
            Assert.IsNotNull(bytes);

            Stack<Queue<(NotIncludeAllClass, int)>>
                result5 = NinoDeserializer.Deserialize<Stack<Queue<(NotIncludeAllClass, int)>>>(bytes);
            Assert.AreEqual(stack3.Count, result5.Count);
            var queue4 = stack3.Pop();
            var queue5 = result5.Pop();
            Assert.AreEqual(queue4.Count, queue5.Count);
            var item3 = queue4.Dequeue();
            var item4 = queue5.Dequeue();
            Assert.AreEqual(item3.Item1.a, item4.Item1.a);
            Assert.AreEqual(item3.Item1.b, item4.Item1.b);

            LinkedList<NotIncludeAllClass> linkedList = new LinkedList<NotIncludeAllClass>();
            linkedList.AddLast(new NotIncludeAllClass
            {
                a = 1,
                b = 100
            });

            bytes = NinoSerializer.Serialize(linkedList);
            Assert.IsNotNull(bytes);

            LinkedList<NotIncludeAllClass> result6 =
                NinoDeserializer.Deserialize<LinkedList<NotIncludeAllClass>>(bytes);
            Assert.AreEqual(linkedList.Count, result6.Count);
            var node = linkedList.First;
            var node2 = result6.First;
            Assert.AreEqual(node.Value.a, node2.Value.a);
            Assert.AreEqual(node.Value.b, node2.Value.b);

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

        [Test]
        public void TestNullCollection()
        {
            List<int> a = null;
            byte[] bytes = NinoSerializer.Serialize(a);
            Debug.Log($"Null List<int> bytes: {string.Join(", ", bytes)}");
            List<int> result = NinoDeserializer.Deserialize<List<int>>(bytes);
            Assert.IsNull(result);

            List<int?> b = null;
            bytes = NinoSerializer.Serialize(b);
            Debug.Log($"Null List<int?> bytes: {string.Join(", ", bytes)}");
            List<int?> result2 = NinoDeserializer.Deserialize<List<int?>>(bytes);
            Assert.IsNull(result2);

            List<Data> c = null;
            bytes = NinoSerializer.Serialize(c);
            Debug.Log($"Null List<Data> bytes: {string.Join(", ", bytes)}");
            List<Data> result3 = NinoDeserializer.Deserialize<List<Data>>(bytes);
            Assert.IsNull(result3);

            List<Data?> d = null;
            bytes = NinoSerializer.Serialize(d);
            Debug.Log($"Null List<Data?> bytes: {string.Join(", ", bytes)}");
            List<Data?> result4 = NinoDeserializer.Deserialize<List<Data?>>(bytes);
            Assert.IsNull(result4);
        }

        [Test]
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

            CursedGeneric<NotIncludeAllClass> cursedGeneric3 = new CursedGeneric<NotIncludeAllClass>
            {
                field = new ConcurrentDictionary<string, NotIncludeAllClass[]>
                {
                    ["Test"] = new[]
                    {
                        new NotIncludeAllClass { a = 1, b = 100 },
                        new NotIncludeAllClass { a = 2, b = 200 }
                    }
                }
            };

            bytes = NinoSerializer.Serialize(cursedGeneric3);
            Assert.IsNotNull(bytes);

            CursedGeneric<NotIncludeAllClass> result3 =
                NinoDeserializer.Deserialize<CursedGeneric<NotIncludeAllClass>>(bytes);
            Assert.AreEqual(cursedGeneric3.field.Count, result3.field.Count);
            Assert.AreEqual(cursedGeneric3.field["Test"].Length, result3.field["Test"].Length);
            for (int i = 0; i < cursedGeneric3.field["Test"].Length; i++)
            {
                Assert.AreEqual(cursedGeneric3.field["Test"][i].a, result3.field["Test"][i].a);
                Assert.AreEqual(cursedGeneric3.field["Test"][i].b, result3.field["Test"][i].b);
            }
        }

        [Test]
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
            ConcurrentQueue<NotIncludeAllClass> concurrentQueue2 = new ConcurrentQueue<NotIncludeAllClass>();
            concurrentQueue2.Enqueue(new NotIncludeAllClass { a = 1, b = 100 });
            concurrentQueue2.Enqueue(new NotIncludeAllClass { a = 2, b = 200 });

            bytes = NinoSerializer.Serialize(concurrentQueue2);
            Assert.IsNotNull(bytes);

            ConcurrentQueue<NotIncludeAllClass> resultQueue2 =
                NinoDeserializer.Deserialize<ConcurrentQueue<NotIncludeAllClass>>(bytes);
            Assert.AreEqual(concurrentQueue2.Count, resultQueue2.Count);
            Assert.IsTrue(resultQueue2.TryDequeue(out var item1));
            Assert.AreEqual(1, item1.a);
            Assert.AreEqual(100, item1.b);
            Assert.IsTrue(resultQueue2.TryDequeue(out var item2));
            Assert.AreEqual(2, item2.a);
            Assert.AreEqual(200, item2.b);

            // Test ConcurrentStack with complex types
            ConcurrentStack<NotIncludeAllClass> concurrentStack2 = new ConcurrentStack<NotIncludeAllClass>();
            concurrentStack2.Push(new NotIncludeAllClass { a = 1, b = 100 });
            concurrentStack2.Push(new NotIncludeAllClass { a = 2, b = 200 });

            bytes = NinoSerializer.Serialize(concurrentStack2);
            Assert.IsNotNull(bytes);

            ConcurrentStack<NotIncludeAllClass> resultStack2 =
                NinoDeserializer.Deserialize<ConcurrentStack<NotIncludeAllClass>>(bytes);
            Assert.AreEqual(concurrentStack2.Count, resultStack2.Count);
            Assert.IsTrue(resultStack2.TryPop(out var sitem1));
            Assert.AreEqual(2, sitem1.a);
            Assert.AreEqual(200, sitem1.b);
            Assert.IsTrue(resultStack2.TryPop(out var sitem2));
            Assert.AreEqual(1, sitem2.a);
            Assert.AreEqual(100, sitem2.b);

            // Test nested concurrent collections
            ConcurrentQueue<ConcurrentStack<int>> nestedQueue = new ConcurrentQueue<ConcurrentStack<int>>();
            ConcurrentStack<int> innerStack = new ConcurrentStack<int>();
            innerStack.Push(1);
            innerStack.Push(2);
            nestedQueue.Enqueue(innerStack);

            bytes = NinoSerializer.Serialize(nestedQueue);
            Assert.IsNotNull(bytes);

            ConcurrentQueue<ConcurrentStack<int>> resultNestedQueue =
                NinoDeserializer.Deserialize<ConcurrentQueue<ConcurrentStack<int>>>(bytes);
            Assert.AreEqual(nestedQueue.Count, resultNestedQueue.Count);
            Assert.IsTrue(resultNestedQueue.TryDequeue(out var resultInnerStack));
            Assert.AreEqual(2, resultInnerStack.Count);
            Assert.IsTrue(resultInnerStack.TryPop(out int innerVal1) && innerVal1 == 2);
            Assert.IsTrue(resultInnerStack.TryPop(out int innerVal2) && innerVal2 == 1);
        }

        [Test]
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

    // Test class definitions needed for the tests
    [NinoType]
    public sealed class CursedGeneric<T>
    {
        public ConcurrentDictionary<string, T[]> field;
    }
}
