using Nino.Core;

namespace Nino.SeamProto
{
    public enum SeamKind : byte { None, Small, Large }

    /// <summary>
    /// Shape A: one string after a run of raw members; not sealed (a subtype leaves the run). The members are declared
    /// largest first, so CoreCLR lays them out as the wire does (one block of 21 bytes).
    /// </summary>
    [NinoType]
    public partial class SeamOne
    {
        public long Stamp;
        public double Score;
        public int Id;
        public SeamKind Kind;
        public string Name;
    }

    [NinoType]
    public partial class SeamOneDerived : SeamOne
    {
        public int Extra;
    }

    /// <summary>Shape B: several strings (one UTF-8) between raw members; sealed.</summary>
    [NinoType]
    public sealed partial class SeamMany
    {
        public int Id;
        public string Name;
        [NinoUtf8] public string Tag;
        public System.DateTime At;
        public bool Flag;
        public string Text;
    }
}
