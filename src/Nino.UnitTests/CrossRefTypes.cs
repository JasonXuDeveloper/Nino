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
