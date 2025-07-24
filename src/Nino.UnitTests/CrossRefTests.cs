using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.NinoGen;
using Nino.UnitTests.Subset;

#nullable disable
namespace Nino.UnitTests;

[TestClass]
public class CrossRefTests
{
    [TestMethod]
    public void TestSimpleCrossRef()
    {
        SimpleCrossRefTest test = new SimpleCrossRefTest()
        {
            A = new SubsetClassWithPrivateField()
            {
                Id = 1,
                Name = "Test"
            }
        };

        byte[] bytes = test.Serialize();
        byte[] bytes2 = NinoSerializer.Serialize<SimpleCrossRefTest>(test);
        byte[] byte3 = Serializer.Serialize(test);
        Assert.IsTrue(bytes.SequenceEqual(bytes2));
        Assert.IsTrue(bytes.SequenceEqual(byte3));
        Console.WriteLine(string.Join(", ", bytes));
        Deserializer.Deserialize(bytes, out SimpleCrossRefTest result);
        Assert.AreEqual(test.A.Id, result.A.Id);
        Assert.AreEqual(test.A.Name, result.A.Name);
    }

    [TestMethod]
    public void TestNotSoSimpleCrossRef()
    {
        NotSoSimpleCrossRefTest test = new NotSoSimpleCrossRefTest()
        {
            Id = 1,
            Name = "Test",
            NewField = true,
            NewField2Prop = 2
        };

        byte[] bytes = test.Serialize();
        Deserializer.Deserialize(bytes, out NotSoSimpleCrossRefTest result);
        Assert.AreEqual(test.Id, result.Id);
        Assert.AreEqual(test.Name, result.Name);
        Assert.AreEqual(test.NewField, result.NewField);
        Assert.AreEqual(test.NewField2Prop, result.NewField2Prop);
    }
}