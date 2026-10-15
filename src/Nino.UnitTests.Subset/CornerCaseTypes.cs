using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nino.Core;

// Types for the corner-case tests that need a newer C# version than the test project (C# 11 `required`,
// C# 12 primary constructors) or an assembly without WEAK_VERSION_TOLERANCE (raw structs, per-assembly checks).
// Instances are built by the factories below, so the test project never names a required member itself.
namespace Nino.UnitTests.Subset.Corner
{
    [NinoType]
    public class RequiredInitMembers
    {
        public required int Id { get; init; }
        public required string Name { get; init; }
        public int Settable { get; set; }
    }

    // C# 12 primary constructor (no parameterless constructor) whose parameters initialize settable
    // properties: re-created from it, then the extra member is assigned.
    [NinoType]
    public class PrimaryCtorClass(int id, string name)
    {
        public int Id { get; set; } = id;
        public string Name { get; set; } = name;
        public int Extra { get; set; }
    }

    // Primary constructor parameters copied into settable fields.
    [NinoType]
    public struct PrimaryCtorStruct(int x, long y)
    {
        public int X = x;
        public long Y = y;
    }

    // Primary constructor parameters in a different order than the members, initializing readonly fields.
    [NinoType]
    public class PrimaryCtorReordered(string name, int id)
    {
        public readonly int Id = id;
        public readonly string Name = name;
    }

    // Primary constructor whose parameters initialize get-only auto-properties. Get-only auto-properties are not
    // serialized members, so this type is only usable through its settable member (see the corner-case tests).
    [NinoType]
    public class PrimaryCtorCaptured(int seed)
    {
        public int Seed => seed;
        public int Value { get; set; }

        public PrimaryCtorCaptured() : this(0)
        {
        }
    }

    // Plain sealed class: in this assembly (no WEAK_VERSION_TOLERANCE) it has no length prefix.
    [NinoType]
    public sealed class PlainSealed
    {
        public int A;
        public string B;
    }

    [NinoType(versionTolerant: true)]
    public sealed class ExplicitTolerantSealed
    {
        public int A;
        public string B;
    }

    // Raw-memory structs (this assembly is not version tolerant, so they qualify).
    [NinoType]
    public struct RawPadded
    {
        public byte A;
        public int B;
        public short C;
        public long D;
    }

    [NinoType]
    public struct RawNested
    {
        public RawPadded Inner;
        public byte Tail;
        public RawPadded Second;
    }

    [NinoType]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RawPacked
    {
        public byte A;
        public long B;
        public short C;
    }

    [NinoType]
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct RawExplicit
    {
        [FieldOffset(0)] public int A;
        [FieldOffset(8)] public long B;
    }

    // Overlapping fields (a C-style union).
    [NinoType]
    [StructLayout(LayoutKind.Explicit)]
    public struct RawUnion
    {
        [FieldOffset(0)] public long Whole;
        [FieldOffset(0)] public int Low;
        [FieldOffset(4)] public int High;
    }

    public static class CornerFactory
    {
        public static RequiredInitMembers RequiredInit(int id, string name, int settable) =>
            new RequiredInitMembers { Id = id, Name = name, Settable = settable };

        public static PrimaryCtorClass Primary(int id, string name, int extra) =>
            new PrimaryCtorClass(id, name) { Extra = extra };

        public static PrimaryCtorStruct PrimaryStruct(int x, long y) => new PrimaryCtorStruct(x, y);

        public static PrimaryCtorCaptured Captured(int seed, int value) => new PrimaryCtorCaptured(seed) { Value = value };

        public static PrimaryCtorReordered PrimaryReordered(int id, string name) => new PrimaryCtorReordered(name, id);
    }
}
