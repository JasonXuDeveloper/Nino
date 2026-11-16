using Nino.Core;

// Classes whose bodies are raw (positional): generated WriteRawBody copies their fields as one block where the runtime
// lays them out as the body does (NinoFieldBlock), else writes them one by one (FieldBlockTests).
namespace Nino.UnitTests.Subset.FieldBlocks
{
    /// <summary>Fields the runtime keeps in declaration order without gaps: the raw body is one block copy.</summary>
    [NinoType]
    public sealed class FieldBlockInOrder
    {
        public int A;
        public float B;
        public int C;
    }

    /// <summary>Fields the runtime reorders (CoreCLR puts the long first): written field by field.</summary>
    [NinoType]
    public sealed class FieldBlockReordered
    {
        public byte A;
        public long B;
        public int C;
    }

    /// <summary>A raw struct with padding: its value is never copied as it lies in memory.</summary>
    public struct FieldBlockPadded
    {
        public byte A;
        public int B;
    }

    [NinoType]
    public sealed class FieldBlockHoldsPadded
    {
        public int Id;
        public FieldBlockPadded Value;
    }

    [NinoType]
    public abstract class FieldBlockBase
    {
        public int Id;
    }

    /// <summary>The base's field, then the subtype's: one block in the object after the header.</summary>
    [NinoType]
    public sealed class FieldBlockDerived : FieldBlockBase
    {
        public int X;
        public float Y;
    }
}
