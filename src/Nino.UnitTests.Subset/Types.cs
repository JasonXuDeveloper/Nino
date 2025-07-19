using Nino.Core;

namespace Nino.UnitTests.Subset;

[NinoType(false, true)]
public partial class SubsetClassWithPrivateField
{
    [NinoMember(1)]
    private int _id;

    [NinoMember(0)]
    public string Name;
    
    [NinoMember(2)]
    protected int Age;

    public int Id
    {
        get => _id;
        set => _id = value;
    }
}