using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino_UnitTests_Nino;

namespace Nino.UnitTests
{
    [TestClass]
    public class SimpleTests
    {
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
            Assert.ThrowsException<InvalidCastException>(() =>
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
            Assert.AreEqual(1, n.Value.A);
            Assert.AreEqual("Test", n.Value.B);
            Deserializer.Deserialize(m.Serialize(), out m);
            Assert.AreEqual(3, m.Count);
            foreach (var item in m)
            {
                Assert.IsTrue(item is >= 1 and <= 3);
            }
        }
    }
}