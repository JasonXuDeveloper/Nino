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