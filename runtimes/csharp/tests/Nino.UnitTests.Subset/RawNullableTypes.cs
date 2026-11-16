using Nino.Core;

// Unmanaged [NinoType] structs that hold T? and NinoNullable<T> fields: raw, written as their memory with padding and
// absent values cleared (RawStructsHoldingNullablesTests).
namespace Nino.UnitTests.Subset.RawNullables
{
    [NinoType]
    public struct RawHoldsNullables
    {
        public int Id;
        public long? Total;
        public double? Score;
    }

    [NinoType]
    public struct RawHoldsNinoNullables
    {
        public int Id;
        public NinoNullable<long> Total;
        public NinoNullable<decimal> Price;
    }

    // A T? of a raw [NinoType] struct that holds one itself.
    [NinoType]
    public struct RawHoldsNested
    {
        public byte Tag;
        public RawHoldsNullables? Inner;
    }

    // A custom formatter or an ignored field keeps a struct member by member, its T? a presence byte and the value.
    [NinoType]
    public struct MemberByMember
    {
        public int Id;
        [NinoIgnore] public int Skipped;
        public long? Total;
    }
}
