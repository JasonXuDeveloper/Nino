using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class PolymorphismTests
{
    [TestMethod]
    public void TestPolymorphism()
    {
        var arr = new List<TestClass>
        {
            new() { A = 1, B = "Hello" },
            new TestClass2 { A = 2, B = "World", C = 3 },
            new TestClass3 { A = 5, B = "Test", C = 4, D = true },
            null
        };
        byte[] bytes = NinoSerializer.Serialize(arr);
        Console.WriteLine(string.Join(", ", bytes));
        Assert.IsNotNull(bytes);
        List<TestClass> result = NinoDeserializer.Deserialize<List<TestClass>>(bytes);
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
            var _ = NinoDeserializer.Deserialize<List<TestClass3>>(bytes);
        });
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
        byte[] bytes = NinoSerializer.Serialize((ISerializable)a);
        Console.WriteLine(string.Join(",", bytes));
        Struct1 i11 = NinoDeserializer.Deserialize<Struct1>(bytes);
        Assert.AreEqual(a, i11);

        //real type serialization and deserialization with polymorphism
        bytes = NinoSerializer.Serialize(a);
        Console.WriteLine(string.Join(",", bytes));
        ISerializable i1 = NinoDeserializer.Deserialize<ISerializable>(bytes);

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

        bytes = NinoSerializer.Serialize(b);
        ISerializable i2 = NinoDeserializer.Deserialize<ISerializable>(bytes);
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

        bytes = NinoSerializer.Serialize(c);
        ISerializable i3 = NinoDeserializer.Deserialize<ISerializable>(bytes);
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

        byte[] bytes = NinoSerializer.Serialize(list);
        Assert.IsNotNull(bytes);
        Console.WriteLine(string.Join(", ", bytes));

        List<IListElementClass> result = NinoDeserializer.Deserialize<List<IListElementClass>>(bytes);
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
}
