using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
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

        byte[] bytes = NinoSerializer.Serialize(test);
        Console.WriteLine(string.Join(", ", bytes));
        NinoDeserializer.Deserialize(bytes, out SimpleCrossRefTest result);
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

        byte[] bytes = NinoSerializer.Serialize(test);
        Console.WriteLine(string.Join(", ", bytes));
        var result = NinoDeserializer.Deserialize<NotSoSimpleCrossRefTest>(bytes);
        Assert.AreEqual(test.Id, result.Id);
        Assert.AreEqual(test.Name, result.Name);
        Assert.AreEqual(test.NewField, result.NewField);
        Assert.AreEqual(test.NewField2Prop, result.NewField2Prop);
    }
}