using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class BasicSerializationTests
{
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

        byte[] bytes = NinoSerializer.Serialize(data);
        Console.WriteLine(string.Join(", ", bytes));
        Assert.IsNotNull(bytes);

        StringData result = NinoDeserializer.Deserialize<StringData>(bytes);
        Assert.AreEqual(data.Str, result.Str);

        bytes = NinoSerializer.Serialize(data2);
        Console.WriteLine(string.Join(", ", bytes));
        Assert.IsNotNull(bytes);

        StringData2 result2 = NinoDeserializer.Deserialize<StringData2>(bytes);
        Assert.AreEqual(data2.Str, result2.Str);

        Assert.AreEqual(result.Str, result2.Str);
    }

    [TestMethod]
    public void TestStaticMethodConstructor()
    {
        TestMethodCtor testMethodCtor = new TestMethodCtor()
        {
            A = 999,
            B = "Test"
        };
        byte[] bytes = NinoSerializer.Serialize(testMethodCtor);
        Assert.IsNotNull(bytes);
        TestMethodCtor result = NinoDeserializer.Deserialize<TestMethodCtor>(bytes);
        Assert.AreEqual(testMethodCtor.A, result.A);
        Assert.AreEqual(testMethodCtor.B, result.B);
    }

    [TestMethod]
    public void TestSomeNestedPrivateEnum()
    {
        SomeNestedPrivateEnum data = new SomeNestedPrivateEnum()
        {
            Id = 1,
            EnumVal = 1
        };

        byte[] bytes = NinoSerializer.Serialize(data);
        Assert.IsNotNull(bytes);

        SomeNestedPrivateEnum result = NinoDeserializer.Deserialize<SomeNestedPrivateEnum>(bytes);
        Assert.AreEqual(data.Id, result.Id);
        Assert.AreEqual(2, result.EnumVal); // not 1 because we discarded Enum during serialization
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
            H = new System.Collections.Generic.List<TestStruct2>
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
        byte[] bytes = NinoSerializer.Serialize(testClass);
        Console.WriteLine(string.Join(", ", bytes));
        Assert.IsNotNull(bytes);
    }
}
