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
    
    private int NewField2;
}