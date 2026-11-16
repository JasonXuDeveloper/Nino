using System;
using Nino.Core;

// The types whose bytes BclFixtures.cs holds, compiled by Unity's Runtime.Tests (BclLayoutTests) and linked into
// tests/Nino.RuntimeTests (BclStructs): one declaration, so both check the same shapes against the same bytes.

/// <summary>A plain (raw) struct around a BCL struct, after a byte and before an int.</summary>
public struct Around<T>
{
    public byte A;
    public T B;
    public int C;
}

/// <summary>
/// What replaces a Vector&lt;T&gt; member (NINO029): 16 floats (64 bytes, the widest Vector&lt;float&gt;) stored and
/// written, the vectors a [NinoIgnore] view over them: 4, 2 or 1 of them by the CPU, the same bytes on every runtime.
/// </summary>
[NinoType]
public sealed class LaneParticle
{
    public int Id;
    public float[] Lanes = new float[16];

    [NinoIgnore]
    public Span<System.Numerics.Vector<float>> Vectors =>
        System.Runtime.InteropServices.MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(Lanes.AsSpan());

    /// <summary>Lanes 0 to 15 doubled a vector at a time (checked: the view covers every lane on this CPU).</summary>
    public static LaneParticle Doubled(int id)
    {
        var particle = new LaneParticle { Id = id };
        for (int i = 0; i < particle.Lanes.Length; i++)
            particle.Lanes[i] = i;
        foreach (ref var vector in particle.Vectors)
            vector *= 2;
        for (int i = 0; i < particle.Lanes.Length; i++)
            if (particle.Lanes[i] != 2 * i)
                throw new InvalidOperationException("the vector view missed lane " + i);
        return particle;
    }
}
