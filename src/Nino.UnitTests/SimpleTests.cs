using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.UnitTests.NinoGen;

#nullable disable
namespace Nino.UnitTests
{
    [TestClass]
    public class SimpleTests
    {
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

            byte[] bytes = cursedGeneric.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out CursedGeneric<int> result);
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

            bytes = cursedGeneric2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out CursedGeneric<string> result2);
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

            bytes = cursedGeneric3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out CursedGeneric<TestClass> result3);
            Assert.AreEqual(cursedGeneric3.field.Count, result3.field.Count);
            Assert.AreEqual(cursedGeneric3.field["Test"].Length, result3.field["Test"].Length);
            for (int i = 0; i < cursedGeneric3.field["Test"].Length; i++)
            {
                Assert.AreEqual(cursedGeneric3.field["Test"][i].A, result3.field["Test"][i].A);
                Assert.AreEqual(cursedGeneric3.field["Test"][i].B, result3.field["Test"][i].B);
            }
        }

        [TestMethod]
        public void TestNonTrivialCollection()
        {
            Stack<int> stack = new Stack<int>();
            stack.Push(1);
            stack.Push(2);

            byte[] bytes = stack.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Stack<int> result);
            Assert.AreEqual(stack.Count, result.Count);
            Assert.AreEqual(stack.Pop(), result.Pop());
            Assert.AreEqual(stack.Pop(), result.Pop());

            Queue<int> queue = new Queue<int>();
            queue.Enqueue(1);
            queue.Enqueue(2);

            bytes = queue.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Queue<int> result2);
            Assert.AreEqual(queue.Count, result2.Count);

            Assert.AreEqual(queue.Dequeue(), result2.Dequeue());
            Assert.AreEqual(queue.Dequeue(), result2.Dequeue());

            Stack<TestClass> stack2 = new Stack<TestClass>();
            stack2.Push(new TestClass
            {
                A = 1,
                B = "Test"
            });

            bytes = stack2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Stack<TestClass> result3);
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

            bytes = queue2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Queue<TestClass> result4);
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

            bytes = stack3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Stack<Queue<(TestClass, int)>> result5);
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

            bytes = linkedList.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out LinkedList<TestClass> result6);
            Assert.AreEqual(linkedList.Count, result6.Count);
            var node = linkedList.First;
            var node2 = result6.First;
            Assert.AreEqual(node.Value.A, node2.Value.A);
            Assert.AreEqual(node.Value.B, node2.Value.B);

            Queue<int> queue6 = new Queue<int>();
            queue6.Enqueue(1);
            queue6.Enqueue(2);
            queue6.Enqueue(3);

            bytes = queue6.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Queue<int> result7);
            Assert.AreEqual(queue6.Count, result7.Count);
            Assert.AreEqual(queue6.Dequeue(), result7.Dequeue());
            Assert.AreEqual(queue6.Dequeue(), result7.Dequeue());
            Assert.AreEqual(queue6.Dequeue(), result7.Dequeue());
        }

        [TestMethod]
        public void TestTuple()
        {
            Tuple<int, string> tuple = new Tuple<int, string>(1, "Test");
            byte[] bytes = tuple.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Tuple<int, string> result);
            Assert.AreEqual(tuple.Item1, result.Item1);
            Assert.AreEqual(tuple.Item2, result.Item2);

            Tuple<int, int> tuple2 = new Tuple<int, int>(1, 2);
            bytes = tuple2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Tuple<int, int> result2);
            Assert.AreEqual(tuple2.Item1, result2.Item1);
            Assert.AreEqual(tuple2.Item2, result2.Item2);

            Tuple<string, Tuple<int, string>> tuple3 =
                new Tuple<string, Tuple<int, string>>("Test", new Tuple<int, string>(1, "Test"));
            bytes = tuple3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Tuple<string, Tuple<int, string>> result3);
            Assert.AreEqual(tuple3.Item1, result3.Item1);
            Assert.AreEqual(tuple3.Item2.Item1, result3.Item2.Item1);
            Assert.AreEqual(tuple3.Item2.Item2, result3.Item2.Item2);

            Tuple<Data, Tuple<int, int>> tuple4 = new Tuple<Data, Tuple<int, int>>(new Data()
            {
                En = TestEnum.A,
                Name = "Test",
                X = 1,
                Y = 2
            }, new Tuple<int, int>(1, 2));
            bytes = tuple4.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out Tuple<Data, Tuple<int, int>> result4);
            Assert.AreEqual(tuple4.Item1.En, result4.Item1.En);
            Assert.AreEqual(tuple4.Item1.Name, result4.Item1.Name);
            Assert.AreEqual(tuple4.Item1.X, result4.Item1.X);
            Assert.AreEqual(tuple4.Item1.Y, result4.Item1.Y);
            Assert.AreEqual(tuple4.Item2.Item1, result4.Item2.Item1);
            Assert.AreEqual(tuple4.Item2.Item2, result4.Item2.Item2);
        }

        [TestMethod]
        public void TestValueTuple()
        {
            ValueTuple<int, string> tuple = new ValueTuple<int, string>(1, "Test");
            byte[] bytes = tuple.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out ValueTuple<int, string> result);
            Assert.AreEqual(tuple.Item1, result.Item1);
            Assert.AreEqual(tuple.Item2, result.Item2);

            ValueTuple<int, int> tuple2 = new ValueTuple<int, int>(1, 2);
            bytes = tuple2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out ValueTuple<int, int> result2);
            Assert.AreEqual(tuple2.Item1, result2.Item1);
            Assert.AreEqual(tuple2.Item2, result2.Item2);

            ValueTuple<string, ValueTuple<int, string>> tuple3 =
                new ValueTuple<string, ValueTuple<int, string>>("Test", new ValueTuple<int, string>(1, "Test"));
            bytes = tuple3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out ValueTuple<string, ValueTuple<int, string>> result3);
            Assert.AreEqual(tuple3.Item1, result3.Item1);
            Assert.AreEqual(tuple3.Item2.Item1, result3.Item2.Item1);
            Assert.AreEqual(tuple3.Item2.Item2, result3.Item2.Item2);

            ValueTuple<Data, ValueTuple<int, int>> tuple4 = new ValueTuple<Data, ValueTuple<int, int>>(new Data()
            {
                En = TestEnum.A,
                Name = "Test",
                X = 1,
                Y = 2
            }, new ValueTuple<int, int>(1, 2));
            bytes = tuple4.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out ValueTuple<Data, ValueTuple<int, int>> result4);
            Assert.AreEqual(tuple4.Item1.En, result4.Item1.En);
            Assert.AreEqual(tuple4.Item1.Name, result4.Item1.Name);
            Assert.AreEqual(tuple4.Item1.X, result4.Item1.X);
            Assert.AreEqual(tuple4.Item1.Y, result4.Item1.Y);
            Assert.AreEqual(tuple4.Item2.Item1, result4.Item2.Item1);
            Assert.AreEqual(tuple4.Item2.Item2, result4.Item2.Item2);

            (bool a, string b) val = (true, "1");
            bytes = val.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out (bool, string) result5);
            Assert.AreEqual(val.a, result5.Item1);
            Assert.AreEqual(val.b, result5.Item2);

            (bool, string) val2 = (false, "2");
            bytes = val2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out (bool a, string b) result6);
            Assert.AreEqual(val2.Item1, result6.a);
            Assert.AreEqual(val2.Item2, result6.b);
        }

        [TestMethod]
        public void TestKvp()
        {
            KeyValuePair<int, long> kvp = new KeyValuePair<int, long>(1, 1234567890);
            byte[] bytes = kvp.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out KeyValuePair<int, long> result);
            Assert.AreEqual(kvp.Key, result.Key);
            Assert.AreEqual(kvp.Value, result.Value);

            KeyValuePair<int, string> kvp2 = new KeyValuePair<int, string>(1, "Test");
            bytes = kvp2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out KeyValuePair<int, string> result2);
            Assert.AreEqual(kvp2.Key, result2.Key);
            Assert.AreEqual(kvp2.Value, result2.Value);

            KeyValuePair<int, KeyValuePair<int, long>> kvp3 =
                new KeyValuePair<int, KeyValuePair<int, long>>(1, new KeyValuePair<int, long>(2, 1234567890));
            bytes = kvp3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out KeyValuePair<int, KeyValuePair<int, long>> result3);
            Assert.AreEqual(kvp3.Key, result3.Key);
            Assert.AreEqual(kvp3.Value.Key, result3.Value.Key);

            KeyValuePair<string, KeyValuePair<bool, string>> kvp4 =
                new KeyValuePair<string, KeyValuePair<bool, string>>("Test111",
                    new KeyValuePair<bool, string>(true, "Test"));
            bytes = kvp4.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out KeyValuePair<string, KeyValuePair<bool, string>> result4);
            Assert.AreEqual(kvp4.Key, result4.Key);
            Assert.AreEqual(kvp4.Value.Key, result4.Value.Key);
        }

        [TestMethod]
        public void TestCollections()
        {
            ConcurrentDictionary<int, int>[] dict = new ConcurrentDictionary<int, int>[10];
            for (int i = 0; i < dict.Length; i++)
            {
                dict[i] = new ConcurrentDictionary<int, int>();
                dict[i].TryAdd(i, i);
            }

            var bytes = dict.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out ConcurrentDictionary<int, int>[] result);
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

            bytes = dict2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out IDictionary<int, string>[] result2);
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

            dict2.Serialize();
            bytes = dict3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out IDictionary<int, IDictionary<int, int[]>> result3);
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
        public void TestModifyListMemberDataStructure()
        {
            List<IListElementClass> list = new List<IListElementClass>
            {
                new ListElementClass
                {
                    Id = 1,
                    Name = "Test",
                    CreateTime = new DateTime(2025, 2, 22)
                },
                new ListElementClass2Renamed
                {
                    Id = 2,
                    Name = "Test2",
                    CreateTime = new DateTime(2025, 2, 22)
                }
            };

            byte[] bytes = list.Serialize();
            Assert.IsNotNull(bytes);
            Console.WriteLine(string.Join(", ", bytes));

            Deserializer.Deserialize(bytes, out List<IListElementClass> result);
            Assert.AreEqual(list.Count, result.Count);
            foreach (var item in list)
            {
                switch (item)
                {
                    case ListElementClass listElementClass:
                        Assert.IsTrue(result[0] is ListElementClass);
                        Assert.AreEqual(listElementClass.Id, ((ListElementClass)result[0]).Id);
                        Assert.AreEqual(listElementClass.Name, ((ListElementClass)result[0]).Name);
                        Assert.AreEqual(listElementClass.CreateTime, ((ListElementClass)result[0]).CreateTime);
                        break;
                    case ListElementClass2Renamed listElementClass2:
                        Assert.IsTrue(result[1] is ListElementClass2Renamed);
                        Assert.AreEqual(listElementClass2.Id, ((ListElementClass2Renamed)result[1]).Id);
                        Assert.AreEqual(listElementClass2.Name, ((ListElementClass2Renamed)result[1]).Name);
                        Assert.AreEqual(listElementClass2.CreateTime, ((ListElementClass2Renamed)result[1]).CreateTime);
                        break;
                }
            }
        }


#if WEAK_VERSION_TOLERANCE
        [TestMethod]
        public void TestModifyListMemberDataStructure2()
        {
            // same data as above
            List<IListElementClass> list = new List<IListElementClass>
            {
                new ListElementClass
                {
                    Id = 1,
                    Name = "Test",
                    CreateTime = new DateTime(2025, 2, 22)
                },
                new ListElementClass2Renamed
                {
                    Id = 2,
                    Name = "Test2",
                    CreateTime = new DateTime(2025, 2, 22)
                }
            };

            // serialized old data structure
            byte[] bytes = new byte[]
            {
                128, 0, 0, 2, 32, 0, 0, 0, 125, 234, 9, 159, 1, 0, 0, 0, 128, 0, 0, 4, 84, 0, 101, 0, 115, 0, 116, 0, 0,
                64, 172, 217, 211, 82, 221, 136, 34, 0, 0, 0, 75, 83, 158, 19, 2, 0, 0, 0, 128, 0, 0, 5, 84, 0, 101, 0,
                115, 0, 116, 0, 50, 0, 0, 64, 172, 217, 211, 82, 221, 136
            };

            Deserializer.Deserialize(bytes, out List<IListElementClass> result);
            Assert.AreEqual(list.Count, result.Count);
            foreach (var item in list)
            {
                switch (item)
                {
                    case ListElementClass listElementClass:
                        Assert.IsTrue(result[0] is ListElementClass);
                        Assert.AreEqual(listElementClass.Id, ((ListElementClass)result[0]).Id);
                        Assert.AreEqual(listElementClass.Name, ((ListElementClass)result[0]).Name);
                        Assert.AreEqual(listElementClass.CreateTime, ((ListElementClass)result[0]).CreateTime);
                        Assert.AreEqual(listElementClass.Extra, false);
                        break;
                    case ListElementClass2Renamed listElementClass2:
                        Assert.IsTrue(result[1] is ListElementClass2Renamed);
                        Assert.AreEqual(listElementClass2.Id, ((ListElementClass2Renamed)result[1]).Id);
                        Assert.AreEqual(listElementClass2.Name, ((ListElementClass2Renamed)result[1]).Name);
                        Assert.AreEqual(listElementClass2.CreateTime, ((ListElementClass2Renamed)result[1]).CreateTime);
                        Assert.AreEqual(listElementClass2.Extra, null);
                        break;
                }
            }
        }
#endif

        [TestMethod]
        public void TestPrivateAccess()
        {
            ProtectedShouldInclude protectedShouldInclude = new ProtectedShouldInclude()
            {
                Id = 991122
            };
            byte[] bytes = protectedShouldInclude.Serialize();
            Deserializer.Deserialize(bytes, out ProtectedShouldInclude protectedShouldInclude2);
            Assert.AreEqual(protectedShouldInclude.Id, protectedShouldInclude2.Id);

            ShouldIgnorePrivate data = new ShouldIgnorePrivate
            {
                Id = 1,
                Name = "Test",
                CreateTime = DateTime.Today
            };
            bytes = data.Serialize();
            Deserializer.Deserialize(bytes, out ShouldIgnorePrivate shouldIgnorePrivate);
            Assert.AreNotEqual(data.Id, shouldIgnorePrivate.Id);
            Assert.AreEqual(data.Name, shouldIgnorePrivate.Name);
            Assert.AreEqual(data.CreateTime, shouldIgnorePrivate.CreateTime);

            TestPrivateMemberClass pcls = new TestPrivateMemberClass();
            pcls.A = 1;

            bytes = pcls.Serialize();
            Deserializer.Deserialize(bytes, out TestPrivateMemberClass pcls2);
            Assert.AreEqual(pcls.A, pcls2.A);
            Assert.AreEqual(pcls.ReadonlyId, pcls2.ReadonlyId);

            RecordWithPrivateMember record = new RecordWithPrivateMember("Test");
            Assert.IsNotNull(record.Name);
            Assert.AreEqual("Test", record.Name);

            bytes = record.Serialize();
            Deserializer.Deserialize(bytes, out RecordWithPrivateMember r1);
            Assert.AreEqual(record.Name, r1.Name);
            Assert.AreEqual(record.ReadonlyId, r1.ReadonlyId);

            RecordWithPrivateMember2 record2 = new RecordWithPrivateMember2("Test");
            Assert.IsNotNull(record2.Name);
            Assert.AreEqual("Test", record2.Name);

            bytes = record2.Serialize();
            Deserializer.Deserialize(bytes, out RecordWithPrivateMember2 r2);
            Assert.AreEqual(record2.Name, r2.Name);
            Assert.AreEqual(record2.ReadonlyId, r2.ReadonlyId);

            StructWithPrivateMember s = new StructWithPrivateMember
            {
                Id = 1,
            };
            s.SetName("Test");
            Assert.AreEqual("Test", s.GetName());

            bytes = s.Serialize();
            Deserializer.Deserialize(bytes, out StructWithPrivateMember s2);
            Assert.AreEqual(s.Id, s2.Id);
            Assert.AreEqual(s.GetName(), s2.GetName());

            ClassWithPrivateMember<float> cls = new ClassWithPrivateMember<float>();
            cls.Flag = true;
            cls.List = new List<float>
            {
                1.1f,
                2.2f,
                3.3f
            };
            Assert.IsNotNull(cls.Name);

            bytes = cls.Serialize();
            Deserializer.Deserialize(bytes, out ClassWithPrivateMember<float> result);
            Assert.AreEqual(cls.Id, result.Id);
            //private field
            Assert.AreEqual(cls.Name, result.Name);
            //private property
            Assert.AreEqual(cls.Flag, result.Flag);
            //private generic field, list sequentially equal
            Assert.AreEqual(cls.List.Count, result.List.Count);
            for (int i = 0; i < cls.List.Count; i++)
            {
                Assert.AreEqual(cls.List[i], result.List[i]);
            }

            ClassWithPrivateMember<int> cls2 = new ClassWithPrivateMember<int>();
            cls2.Flag = false;
            cls2.List = new List<int>
            {
                3,
                2,
                1
            };
            Assert.IsNotNull(cls2.Name);

            bytes = cls2.Serialize();
            Deserializer.Deserialize(bytes, out ClassWithPrivateMember<int> result2);
            Assert.AreEqual(cls2.Id, result2.Id);
            Assert.AreEqual(cls2.Name, result2.Name);
            Assert.AreEqual(cls2.Flag, result2.Flag);
            Assert.AreEqual(cls2.List.Count, result2.List.Count);
            for (int i = 0; i < cls2.List.Count; i++)
            {
                Assert.AreEqual(cls2.List[i], result2.List[i]);
            }

            Bindable<int> bindable = new Bindable<int>(1);
            bytes = bindable.Serialize();
            Deserializer.Deserialize(bytes, out Bindable<int> bindable2);
            Assert.AreEqual(bindable.Value, bindable2.Value);
        }

        [TestMethod]
        public void TestInterfaceVariants()
        {
            Struct1 a = new Struct1
            {
                A = 1,
                B = DateTime.Today,
                C = Guid.NewGuid()
            };
            //polymorphism serialization and real type deserialization
            byte[] bytes = ((ISerializable)a).Serialize();
            Deserializer.Deserialize(bytes, out Struct1 i11);
            Assert.AreEqual(a, i11);

            //real type serialization and deserialization with polymorphism
            bytes = a.Serialize();
            Deserializer.Deserialize(bytes, out ISerializable i1);

            Assert.AreEqual(i1, i11);

            Assert.IsInstanceOfType(i1, typeof(Struct1));
            var result = (Struct1)i1;
            Assert.AreEqual(a.A, result.A);

            Class1 b = new Class1
            {
                A = 1,
                B = DateTime.Today,
                C = Guid.NewGuid(),
                D = a
            };

            bytes = b.Serialize();
            Deserializer.Deserialize(bytes, out ISerializable i2);
            Assert.IsInstanceOfType(i2, typeof(Class1));
            var result2 = (Class1)i2;
            Assert.AreEqual(b.A, result2.A);
            Assert.AreEqual(b.B, result2.B);
            Assert.AreEqual(b.C, result2.C);
            Assert.AreEqual((Struct1)b.D, (Struct1)result2.D);

            Struct2 c = new Struct2
            {
                A = 1,
                B = DateTime.Today,
                C = "Test",
                D = b
            };

            bytes = c.Serialize();
            Deserializer.Deserialize(bytes, out ISerializable i3);
            Assert.IsInstanceOfType(i3, typeof(Struct2));
            var result3 = (Struct2)i3;

            Assert.AreEqual(c.A, result3.A);
            Assert.AreEqual(c.B, result3.B);
            Assert.AreEqual(c.C, result3.C);
            Assert.AreEqual(c.D.A, result3.D.A);
            Assert.AreEqual(c.D.B, result3.D.B);
            Assert.AreEqual(c.D.C, result3.D.C);
            Assert.AreEqual(((Struct1)c.D.D).A, ((Struct1)result3.D.D).A);
        }

        [TestMethod]
        public void TestString()
        {
            StringData data = new StringData
            {
                Str = "Hello, World!"
            };
            StringData2 data2 = new StringData2
            {
                Str = "Hello, World!"
            };

            byte[] bytes = data.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out StringData result);
            Assert.AreEqual(data.Str, result.Str);

            bytes = data2.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out StringData2 result2);
            Assert.AreEqual(data2.Str, result2.Str);

            Assert.AreEqual(result.Str, result2.Str);
        }

        [TestMethod]
        public void TestDeserializeOldData()
        {
            SaveData data = new SaveData
            {
                Id = 1,
                Name = "Test",
            };

            //from serialization old version of data
            /*
             * [NinoType(false)]
               public class SaveData
               {
                   [NinoMember(1)] public int Id;
                   [NinoMember(2)] public string Name;
               }
             */

            //print all const int in NinoTypeConst

            Console.WriteLine(string.Join(", ", data.Serialize()));
            // public const int Nino_UnitTests_SaveData = 1770431639;
            Assert.AreEqual(1770431639, NinoTypeConst.Nino_UnitTests_SaveData);
            byte[] oldData =
            {
                151, 164, 134, 105, 1, 0, 0, 0, 128, 0, 0, 4, 84, 101, 115, 116
            };
            //require symbol WEAK_VERSION_TOLERANCE to be defined
#if WEAK_VERSION_TOLERANCE
            Deserializer.Deserialize(oldData, out SaveData result);
            Assert.AreEqual(data.Id, result.Id);
            Assert.AreEqual(data.Name, result.Name);
            Assert.AreEqual(default, result.NewField1);
            Assert.AreEqual(default, result.NewField2);
#else
            //should throw out of range exception
            Assert.ThrowsException<IndexOutOfRangeException>(() =>
            {
                Deserializer.Deserialize(oldData, out SaveData _);
            });
#endif
        }

        [TestMethod]
        public void TestRecordStruct()
        {
            SimpleRecordStruct record = new SimpleRecordStruct
            {
                Id = 1,
                Name = "Test",
                CreateTime = DateTime.Today
            };

            byte[] bytes = record.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecordStruct result);
            Assert.AreEqual(record, result);

            SimpleRecordStruct2 record2 = new SimpleRecordStruct2(1, DateTime.Today);
            bytes = record2.Serialize();

            Deserializer.Deserialize(bytes, out SimpleRecordStruct2 result2);
            Assert.AreEqual(record2, result2);

            SimpleRecordStruct2<int> record3 = new SimpleRecordStruct2<int>(1, 1234);
            bytes = record3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecordStruct2<int> result3);
            Assert.AreEqual(record3, result3);

            SimpleRecordStruct2<string> record4 = new SimpleRecordStruct2<string>(1, "Test");
            bytes = record4.Serialize();

            Deserializer.Deserialize(bytes, out SimpleRecordStruct2<string> result4);
            Assert.AreEqual(record4, result4);
        }

        [TestMethod]
        public void TestRecords()
        {
            SimpleRecord record = new SimpleRecord
            {
                Id = 1,
                Name = "Test",
                CreateTime = DateTime.Today
            };

            byte[] bytes = record.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecord result);
            Assert.AreEqual(record, result);

            SimpleRecord2 record2 = new SimpleRecord2(1, "Test", DateTime.Today);
            bytes = record2.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecord2 result2);
            Assert.AreEqual(record2, result2);

            SimpleRecord3 record3 = new SimpleRecord3(1, "Test", DateTime.Today)
            {
                Flag = true,
                Ignored = 999
            };
            bytes = record3.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecord3 result3);
            Assert.AreEqual(result3.Ignored, 0);
            result3.Ignored = 999;
            Assert.AreEqual(record3, result3);

            SimpleRecord4 record4 = new SimpleRecord4(1, "Test", DateTime.Today)
            {
                Flag = true,
                ShouldNotIgnore = 1234
            };
            bytes = record4.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecord4 result4);
            Assert.AreEqual(record4.ShouldNotIgnore, result4.ShouldNotIgnore);
            Assert.AreEqual(result4.Flag, false);
            result4.Flag = true;
            Assert.AreEqual(record4, result4);

            SimpleRecord5 record5 = new SimpleRecord5(1, "Test", DateTime.Today)
            {
                Flag = true,
                ShouldNotIgnore = 1234
            };

            bytes = record5.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecord5 result5);
            Assert.AreEqual(record5.ShouldNotIgnore, result5.ShouldNotIgnore);

            SimpleRecord6<int> record6 = new SimpleRecord6<int>(1, 1234);
            bytes = record6.Serialize();
            Assert.IsNotNull(bytes);

            Deserializer.Deserialize(bytes, out SimpleRecord6<int> result6);
            Assert.AreEqual(record6, result6);
        }

        [TestMethod]
        public void TestSubTypes()
        {
            //Create an instance of TestClass
            var testClass = new TestClass3
            {
                A = 10,
                B = "Hello, World!",
                C = 20,
                D = true,
                E = new TestClass
                {
                    A = 1,
                    B = null
                },
                F = new TestStruct
                {
                    A = 2,
                    B = "Test"
                },
                H = new List<TestStruct2>
                {
                    new()
                    {
                        A = 3,
                        B = true,
                        C = new TestStruct3
                        {
                            A = 4,
                            B = 5.5f
                        }
                    }
                },
            };
            byte[] bytes = testClass.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Assert.IsNotNull(bytes);
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
            byte[] bytes = arr.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Assert.IsNotNull(bytes);
        }

        [TestMethod]
        public void TestPolymorphism()
        {
            var arr = new List<TestClass>
            {
                new() { A = 1, B = "Hello" },
                new TestClass2 { A = 2, B = "World", C = 3 },
                new TestClass3 { A = 3, B = "Test", C = 4, D = true },
                null
            };
            byte[] bytes = arr.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Assert.IsNotNull(bytes);
            Deserializer.Deserialize(bytes, out List<TestClass> result);
            Assert.AreEqual(4, result.Count);
            Assert.AreEqual(1, result[0].A);
            Assert.AreEqual("Hello", result[0].B);
            Assert.IsInstanceOfType(result[0], typeof(TestClass));
            Assert.IsInstanceOfType(result[1], typeof(TestClass2));
            Assert.IsInstanceOfType(result[2], typeof(TestClass3));
            Assert.IsNull(result[3]);
            //should throw error due to type mismatch in polymorphism
            Assert.ThrowsException<InvalidOperationException>(() =>
            {
                Deserializer.Deserialize(bytes, out List<TestClass3> _);
            });
        }

        [TestMethod]
        public void TestComplexTypeGen()
        {
            List<List<int[]>> a = new List<List<int[]>>();
            Dictionary<TestStruct3, int> b = new Dictionary<TestStruct3, int>();
            Dictionary<TestStruct3, int>[] c =
            {
                b
            };
            Dictionary<TestStruct3[], List<bool>[]>[] d = new Dictionary<TestStruct3[], List<bool>[]>[1];
            IList<float> e = new List<float>();
            IDictionary<int, int> f = new ConcurrentDictionary<int, int>();
            IDictionary<int, TestClass3> g = new Dictionary<int, TestClass3>();
            ArraySegment<bool[]> h = new ArraySegment<bool[]>();
            ArraySegment<TestClass> i = new ArraySegment<TestClass>();
            HashSet<TestStruct> j = new HashSet<TestStruct>();
            Span<int> k = stackalloc int[10];
            var l = (Span<DateTime>)stackalloc DateTime[10];
            HashSet<int> m = new HashSet<int>()
            {
                1, 2, 3
            };
            TestStruct? n = new TestStruct()
            {
                A = 1,
                B = "Test"
            };

            void Test(byte[] bytes)
            {
                Console.WriteLine(string.Join(", ", bytes));
                Assert.IsNotNull(bytes);
            }

            Test(a.Serialize());
            Test(b.Serialize());
            Test(c.Serialize());
            Test(d.Serialize());
            Test(e.Serialize());
            Test(f.Serialize());
            Test(g.Serialize());
            Test(h.Serialize());
            Test(i.Serialize());
            Test(j.Serialize());
            Test(k.Serialize());
            Test(l.Serialize());
            Test(m.Serialize());
            Test(n.Serialize());
            Deserializer.Deserialize(n.Serialize(), out TestStruct? nn);
            Assert.AreEqual(1, nn!.Value.A);
            Assert.AreEqual("Test", nn.Value.B);
            Deserializer.Deserialize(m.Serialize(), out m);
            Assert.AreEqual(3, m.Count);
            foreach (var item in m)
            {
                Assert.IsTrue(item is >= 1 and <= 3);
            }
        }

        [TestMethod]
        public void TestGenericStruct()
        {
            GenericStruct<int> a = new GenericStruct<int>()
            {
                Val = 1
            };
            byte[] bytes = a.Serialize();

            Deserializer.Deserialize(bytes, out GenericStruct<int> result);
            Assert.AreEqual(a.Val, result.Val);

            GenericStruct<string> b = new GenericStruct<string>()
            {
                Val = "Test"
            };
            bytes = b.Serialize();
            Deserializer.Deserialize(bytes, out GenericStruct<string> result2);

            Assert.AreEqual(b.Val, result2.Val);
        }

        [TestMethod]
        public void TestGeneric()
        {
            Generic<int> a = new Generic<int>()
            {
                Val = 1
            };
            byte[] bytes = a.Serialize();

            Deserializer.Deserialize(bytes, out Generic<int> result);
            Assert.AreEqual(a.Val, result.Val);

            Generic<string> b = new Generic<string>()
            {
                Val = "Test"
            };
            bytes = b.Serialize();
            Deserializer.Deserialize(bytes, out Generic<string> result2);

            Assert.AreEqual(b.Val, result2.Val);
        }

        [TestMethod]
        public void TestComplexGeneric()
        {
            ComplexGeneric<List<int>> a = new ComplexGeneric<List<int>>()
            {
                Val = new List<int>()
                {
                    1,
                    2
                }
            };

            byte[] bytes = a.Serialize();
            Deserializer.Deserialize(bytes, out ComplexGeneric<List<int>> result);
            Assert.AreEqual(a.Val.Count, result.Val.Count);
            Assert.AreEqual(a.Val[0], result.Val[0]);
            Assert.AreEqual(a.Val[1], result.Val[1]);
        }

        [TestMethod]
        public void TestComplexGeneric2()
        {
            ComplexGeneric2<Generic<SimpleClass>> a = new ComplexGeneric2<Generic<SimpleClass>>()
            {
                Val = new Generic<Generic<SimpleClass>>()
                {
                    Val = new Generic<SimpleClass>()
                    {
                        Val = new SimpleClass()
                        {
                            Id = 1,
                            Name = "Test"
                        }
                    }
                }
            };

            byte[] bytes = a.Serialize();
            Deserializer.Deserialize(bytes, out ComplexGeneric2<Generic<SimpleClass>> result);
            Assert.AreEqual(a.Val.Val.Val.Id, result.Val.Val.Val.Id);
            Assert.AreEqual(a.Val.Val.Val.Name, result.Val.Val.Val.Name);
        }

        [TestMethod]
        public void TestNullCollection()
        {
            List<int> a = null;
            byte[] bytes = a.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Deserializer.Deserialize(bytes, out List<int> result);
            Assert.IsNull(result);

            List<int?> b = null;
            bytes = b.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Deserializer.Deserialize(bytes, out List<int?> result2);
            Assert.IsNull(result2);

            List<SimpleClass> c = null;
            bytes = c.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Deserializer.Deserialize(bytes, out List<SimpleClass> result3);
            Assert.IsNull(result3);

            List<SimpleClass?> d = null;
            bytes = d.Serialize();
            Console.WriteLine(string.Join(", ", bytes));
            Deserializer.Deserialize(bytes, out List<SimpleClass?> result4);
            Assert.IsNull(result4);
        }
    }
}