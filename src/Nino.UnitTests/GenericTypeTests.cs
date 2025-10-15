using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class GenericTypeTests
{
    [TestMethod]
    public void TestGeneric()
    {
#pragma warning disable CS0219 // Variable is assigned but its value is never used
        Generic<object> placeholder1 = null;
        Generic<Task[]> placeholder2 = null;
#pragma warning restore CS0219 // Variable is assigned but its value is never used

        Generic<int> a = new Generic<int>()
        {
            Val = 1
        };
        byte[] bytes = NinoSerializer.Serialize(a);

        var result = NinoDeserializer.Deserialize<Generic<int>>(bytes);
        Assert.AreEqual(a.Val, result.Val);

        Generic<string> b = new Generic<string>()
        {
            Val = "Test"
        };
        bytes = NinoSerializer.Serialize(b);
        Generic<string> result2 = NinoDeserializer.Deserialize<Generic<string>>(bytes);

        Assert.AreEqual(b.Val, result2.Val);
    }

    [TestMethod]
    public void TestGenericStruct()
    {
        GenericStruct<int> a = new GenericStruct<int>()
        {
            Val = 1
        };
        byte[] bytes = NinoSerializer.Serialize(a);

        GenericStruct<int> result = NinoDeserializer.Deserialize<GenericStruct<int>>(bytes);
        Assert.AreEqual(a.Val, result.Val);

        GenericStruct<string> b = new GenericStruct<string>()
        {
            Val = "Test"
        };
        bytes = NinoSerializer.Serialize(b);
        GenericStruct<string> result2 = NinoDeserializer.Deserialize<GenericStruct<string>>(bytes);

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

        byte[] bytes = NinoSerializer.Serialize(a);
        ComplexGeneric<List<int>> result = NinoDeserializer.Deserialize<ComplexGeneric<List<int>>>(bytes);
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

        byte[] bytes = NinoSerializer.Serialize(a);
        ComplexGeneric2<Generic<SimpleClass>> result =
            NinoDeserializer.Deserialize<ComplexGeneric2<Generic<SimpleClass>>>(bytes);
        Assert.AreEqual(a.Val.Val.Val.Id, result.Val.Val.Val.Id);
        Assert.AreEqual(a.Val.Val.Val.Name, result.Val.Val.Val.Name);
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

        Test(NinoSerializer.Serialize(a));
        Test(NinoSerializer.Serialize(b));
        Test(NinoSerializer.Serialize(c));
        Test(NinoSerializer.Serialize(d));
        Test(NinoSerializer.Serialize(e));
        Test(NinoSerializer.Serialize(f));
        Test(NinoSerializer.Serialize(g));
        Test(NinoSerializer.Serialize(h));
        Test(NinoSerializer.Serialize(i));
        Test(NinoSerializer.Serialize(j));
        Test(NinoSerializer.Serialize(m));
        Test(NinoSerializer.Serialize(n));
        TestStruct? nn = NinoDeserializer.Deserialize<TestStruct?>(NinoSerializer.Serialize(n));
        Assert.AreEqual(1, nn!.Value.A);
        Assert.AreEqual("Test", nn.Value.B);
        m = NinoDeserializer.Deserialize<HashSet<int>>(NinoSerializer.Serialize(m));
        Assert.AreEqual(3, m.Count);
        foreach (var item in m)
        {
            Assert.IsTrue(item is >= 1 and <= 3);
        }
    }

    [TestMethod]
    public void TestGenericObject()
    {
        // Test Generic<object> serialization
        Generic<object> genericObj = new Generic<object>
        {
            Val = new TestClass { A = 123, B = "Test" }
        };

        byte[] bytes = NinoSerializer.Serialize(genericObj);
        Assert.IsNotNull(bytes);
        Console.WriteLine($"Serialized Generic<object>: {string.Join(", ", bytes)}");

        Generic<object> result = NinoDeserializer.Deserialize<Generic<object>>(bytes);
        Assert.IsInstanceOfType(result.Val, typeof(TestClass));
        var testResult = (TestClass)result.Val;
        var testOriginal = (TestClass)genericObj.Val;
        Assert.AreEqual(testOriginal.A, testResult.A);
        Assert.AreEqual(testOriginal.B, testResult.B);

        // Test with different object types
        genericObj.Val = new TestClass { A = 456, B = "Different" };
        bytes = NinoSerializer.Serialize(genericObj);
        result = NinoDeserializer.Deserialize<Generic<object>>(bytes);
        Assert.IsInstanceOfType(result.Val, typeof(TestClass));
        var testResult2 = (TestClass)result.Val;
        var testOriginal2 = (TestClass)genericObj.Val;
        Assert.AreEqual(testOriginal2.A, testResult2.A);
        Assert.AreEqual(testOriginal2.B, testResult2.B);
    }

    [TestMethod]
    public void TestListObject()
    {
        // Test List<object> serialization with [NinoType] objects only
        List<object> listObj = new List<object>
        {
            new TestClass { A = 111, B = "First" },
            new TestClass { A = 222, B = "Second" },
            new TestClass { A = 999, B = "ObjectTest" },
            null
        };

        byte[] bytes = NinoSerializer.Serialize(listObj);
        Assert.IsNotNull(bytes);
        Console.WriteLine($"Serialized List<object>: {string.Join(", ", bytes)}");

        List<object> result = NinoDeserializer.Deserialize<List<object>>(bytes);
        Assert.AreEqual(listObj.Count, result.Count);

        // Check TestClass objects
        Assert.IsInstanceOfType(result[0], typeof(TestClass));
        var testResult0 = (TestClass)result[0];
        var testOriginal0 = (TestClass)listObj[0];
        Assert.AreEqual(testOriginal0.A, testResult0.A);
        Assert.AreEqual(testOriginal0.B, testResult0.B);

        Assert.IsInstanceOfType(result[1], typeof(TestClass));
        var testResult1 = (TestClass)result[1];
        var testOriginal1 = (TestClass)listObj[1];
        Assert.AreEqual(testOriginal1.A, testResult1.A);
        Assert.AreEqual(testOriginal1.B, testResult1.B);

        Assert.IsInstanceOfType(result[2], typeof(TestClass));
        var testResult2 = (TestClass)result[2];
        var testOriginal2 = (TestClass)listObj[2];
        Assert.AreEqual(testOriginal2.A, testResult2.A);
        Assert.AreEqual(testOriginal2.B, testResult2.B);

        Assert.IsNull(result[3]); // null
    }

    [TestMethod]
    public void TestUnboundGeneric()
    {
        var obj = new ClassWithUnboundGenericType()
        {
            OtherData = 42
        };
        var serialized = NinoSerializer.Serialize(obj);
        var deserialized = NinoDeserializer.Deserialize<ClassWithUnboundGenericType>(serialized);
        Assert.AreEqual(obj.OtherData, deserialized.OtherData);
    }
}
