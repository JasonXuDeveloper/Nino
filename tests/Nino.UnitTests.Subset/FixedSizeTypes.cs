using System;
using Nino.Core;
using Nino.Core.Generated;

// Fixed-size types (NinoSerializer.GetFixedSize, NinoDirect.SerializeFixed): they need an assembly without
// WEAK_VERSION_TOLERANCE, since a version-tolerant type is never fixed size.
namespace Nino.UnitTests.Subset.Fixed
{
    // Not raw (an ignored field), so it gets a generated formatter; written as A, B, C: 13 bytes.
    [NinoType]
    public struct FixedIgnored
    {
        public int A;
        [NinoIgnore] public int Skipped;
        public long B;
        public bool C;
    }

    // A raw struct inside a non-raw one: 1 + 16 + 2 + 13 = 32 bytes.
    [NinoType]
    public struct FixedNested
    {
        public byte Tag;
        public Guid G;
        public short S;
        public FixedIgnored Inner;
    }

    // A sealed class: the not-null flag, then 4 + 32 + 8 + 8 = 52 bytes, 53 in all.
    [NinoType]
    public sealed class FixedSealed
    {
        public int Id;
        public FixedNested Nested;
        public double D;
        public DateTime T;
    }

    // Not fixed size: a string member.
    [NinoType]
    public sealed class NotFixedSealed
    {
        public int Id;
        public string S;
    }

    // Not fixed size: not sealed.
    [NinoType]
    public class NotFixedOpen
    {
        public int Id;
    }

    // The calls the interceptors route a NinoSerializer.Serialize(T) returning byte[] to.
    public static class FixedDirect
    {
        public static byte[] Serialize(FixedIgnored value) =>
            NinoDirect.SerializeFixed<FixedIgnored, NinoGen.Nino_UnitTests_Subset_Fixed_FixedIgnoredFormatter>(value);

        public static byte[] Serialize(FixedNested value) =>
            NinoDirect.SerializeFixed<FixedNested, NinoGen.Nino_UnitTests_Subset_Fixed_FixedNestedFormatter>(value);

        public static byte[] Serialize(FixedSealed value) =>
            NinoDirect.SerializeFixed<FixedSealed, NinoGen.Nino_UnitTests_Subset_Fixed_FixedSealedFormatter>(value);
    }
}
