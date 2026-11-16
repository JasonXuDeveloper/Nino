using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Nino.Core;

namespace Nino.UnitTests.Subset;

/// <summary>
/// The documented replacement for a <c>Vector&lt;T&gt;</c> member (NINO029, docs/pitfalls.md): a fixed number of lanes
/// stored, the vectors exposed through <c>[NinoIgnore]</c> members. 16 floats are 64 bytes, the widest
/// <c>Vector&lt;float&gt;</c>, so they split into whole vectors on every CPU (4, 2 or 1 of them) and are the same bytes
/// everywhere. Built like Unity builds it (netstandard2.1, the Roslyn 4.3 generator), without version tolerance.
/// </summary>
[NinoType]
public sealed partial class Particle
{
    public const int LaneCount = 16;

    public int Id;

    public float[] Lanes = new float[LaneCount];

    /// <summary>The lanes as <c>Vector&lt;float&gt;</c>s: as many as this CPU's vectors make of 16 floats.</summary>
    [NinoIgnore]
    public Span<Vector<float>> Vectors => MemoryMarshal.Cast<float, Vector<float>>(Lanes.AsSpan());

    /// <summary>The first <c>Vector&lt;float&gt;.Count</c> lanes (4, 8 or 16, by the CPU) as one vector.</summary>
    [NinoIgnore]
    public Vector<float> Head
    {
        get => Vectors[0];
        set => Vectors[0] = value;
    }

    /// <summary>Multiplies every lane, a vector at a time.</summary>
    public void Scale(float factor)
    {
        foreach (ref var vector in Vectors)
            vector *= factor;
    }
}
