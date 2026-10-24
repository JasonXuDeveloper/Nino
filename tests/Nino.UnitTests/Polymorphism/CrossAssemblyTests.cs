using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public class SimpleCrossRefTest
{
    public SubsetClassWithPrivateField A;
}

[NinoType(true, true)]
public partial class NotSoSimpleCrossRefTest : SubsetClassWithPrivateField
{
    public bool NewField;

    private int _newField2;

    [NinoIgnore]
    public int NewField2Prop
    {
        get => _newField2;
        set => _newField2 = value;
    }
}
// No [NinoType]: inherits it from a type in another assembly (found through the referenced-assembly scan),
// and a second level that inherits it from the first (found through the name closure).
public partial class CrossAssemblyInherited : SubsetClassWithPrivateField
{
    [NinoMember(10)] public int Extra;
}

public partial class CrossAssemblyInherited2 : CrossAssemblyInherited
{
    [NinoMember(11)] public string More;
}

[TestClass]
public class CrossAssemblyTests
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
        SimpleCrossRefTest result = NinoDeserializer.Deserialize<SimpleCrossRefTest>(bytes);
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

    [TestMethod]
    public void UnmarkedSubtypesOfReferencedNinoTypeAreGenerated()
    {
        var value = new CrossAssemblyInherited2 { Name = "n", Extra = 3, More = "m" };
        var result = NinoDeserializer.Deserialize<Nino.UnitTests.Subset.SubsetClassWithPrivateField>(
            NinoSerializer.Serialize<Nino.UnitTests.Subset.SubsetClassWithPrivateField>(value));
        var typed = (CrossAssemblyInherited2)result;
        Assert.AreEqual("n", typed.Name);
        Assert.AreEqual(3, typed.Extra);
        Assert.AreEqual("m", typed.More);
    }
}