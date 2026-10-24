using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nino.Core;

// Structs of fixed size that are not raw, whose generated formatters write collections of them as blocks
// (INinoBlockFormatter): in an assembly without WEAK_VERSION_TOLERANCE, as fixed-size types need.
namespace Nino.UnitTests.Subset.Blocks
{
    // A value tuple member (not raw): 4 + 12 + 1 = 17 bytes, 32 in memory.
    [NinoType]
    public struct BlockTuple
    {
        public int A;
        public (int, long) T;
        public bool B;
    }

    // Laid out by the runtime (not raw): the wire keeps the declared order, 1 + 8 + 2 + 1 = 12 bytes.
    [NinoType]
    [StructLayout(LayoutKind.Auto)]
    public struct BlockAuto
    {
        public byte Tag;
        public long L;
        public short S;
        public bool On;
    }

    // A pair and a float: 4 + 2 + 4 = 10 bytes.
    [NinoType]
    public struct BlockPair
    {
        public KeyValuePair<int, short> K;
        public float F;
    }

    // Auto-properties, a tuple of a bool among them: 4 + 1 + 3 = 8 bytes.
    [NinoType]
    public struct BlockProps
    {
        public int X { get; set; }
        public bool Flag { get; set; }
        public (short, bool) P { get; set; }
    }

    // Another block struct inside: 17 + 1 = 18 bytes.
    [NinoType]
    public struct BlockNested
    {
        public BlockTuple Inner;
        public byte Tag;
    }

    // Values checked in Untrusted mode: 4 + 8 + 3 = 15 bytes.
    [NinoType]
    public struct BlockDated
    {
        public int Id;
        public DateTime When;
        public (short, bool) Flags;
    }

    // A sealed class with a tuple member: of fixed size, 1 + 4 + 12 = 17 bytes.
    [NinoType]
    public sealed class FixedTupleSealed
    {
        public int Id;
        public (int, long) T;
    }

    // Not blocks: an ignored field keeps its own value, so reads may not overwrite the whole value.
    [NinoType]
    public struct NotBlockIgnored
    {
        public int A;
        [NinoIgnore] public int Kept;
        public (int, int) T;
    }

    // Not blocks: built by its constructor.
    [NinoType]
    public struct NotBlockConstructed
    {
        public readonly int A;
        public readonly (int, int) T;

        [NinoConstructor(nameof(A), nameof(T))]
        public NotBlockConstructed(int a, (int, int) t)
        {
            A = a;
            T = t;
        }
    }
}
