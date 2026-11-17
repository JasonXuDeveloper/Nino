using System;
using Nino.Core;

// The types whose bytes BclFixtures.cs holds, compiled by Unity's Runtime.Tests (BclLayoutTests) and linked into
// runtimes/csharp/tests/Nino.RuntimeTests (BclStructs): one declaration, so both check the same shapes against the same bytes.

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

// Structs under every [StructLayout] form (#378): IL2CPP ignores Pack, so a struct Pack moves is laid out apart there
// (a Pack = 4 { int; DateTime } is 12 bytes on CoreCLR and Mono, 16 on IL2CPP). Each runtime must still write and read
// CoreCLR x64's bytes, on its own, as a T?, in arrays and lists, inside a plain struct, and as a member of a [NinoType]
// (generated code's raw members). LayoutShapes.Visit lists the cases once for Nino.RuntimeTests and BclLayoutTests.

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct PackedByteLong1 { public byte A; public long B; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 2)]
public struct PackedByteIntLong2 { public byte A; public int B; public long C; }

/// <summary>The benchmark's SimpleStruct: an int and a DateTime, 12 bytes with Pack = 4.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct PackedIntDateTime4 { public int Id; public DateTime At; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct PackedIntLong4 { public int A; public long B; }

/// <summary>Pack = 4 that moves nothing: copied as it lies on every runtime.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct PackedIntInt4 { public int A; public int B; }

/// <summary>Pack = 8, the natural alignment of its fields: moves nothing.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 8)]
public struct PackedByteLong8 { public byte A; public long B; }

/// <summary>A struct Pack moves inside a struct without Pack.</summary>
public struct HoldsPacked { public byte A; public PackedIntDateTime4 P; public int C; }

/// <summary>A natural struct inside a Pack = 1 struct.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct PacksNatural { public byte A; public PackedByteLong8 N; }

/// <summary>Size larger than the fields: trailing padding.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 16)]
public struct SizedInt16 { public int A; }

/// <summary>Explicit offsets, a long at 4 (unaligned).</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
public struct ExplicitIntLong
{
    [System.Runtime.InteropServices.FieldOffset(0)] public int A;
    [System.Runtime.InteropServices.FieldOffset(4)] public long B;
}

/// <summary>
/// Every shape above as a member of a [NinoType]: the generator's raw members, or their formatters. Not sealed: a sealed
/// class's __Top twin indexes a ReadOnlySpan, which stock Mono's class library (the net472 build) cannot compile (CS0570).
/// </summary>
[NinoType]
public class LayoutHolder
{
    public PackedByteLong1 P1;
    public PackedByteIntLong2 P2;
    public PackedIntDateTime4 P4;
    public PackedIntLong4 P4L;
    public PackedIntInt4 P4I;
    public PackedByteLong8 P8;
    public HoldsPacked Nested;
    public PacksNatural Outer;
    public SizedInt16 Sized;
    public ExplicitIntLong Explicit;
    public PackedIntDateTime4? Maybe;
    public PackedIntDateTime4? None;
    public PackedIntDateTime4[] Array;
    public PackedIntDateTime4?[] Maybes;
}

/// <summary>Takes one case: its name (the key of its bytes in BclFixtures) and its value.</summary>
public interface ILayoutCaseVisitor
{
    void Case<T>(string name, T value);
}

/// <summary>The StructLayout cases, listed once for every runtime that checks them.</summary>
public static class LayoutShapes
{
    private static readonly DateTime At1 = new DateTime(0x0123456789ABCDEF & 0x0FFFFFFFFFFFFFFF, DateTimeKind.Utc);
    private static readonly DateTime At2 = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Local);

    public static void Visit(ILayoutCaseVisitor v)
    {
        Each(v, "PackedByteLong1", new PackedByteLong1 { A = 1, B = -2 }, new PackedByteLong1 { A = 0xFE, B = long.MaxValue });
        Each(v, "PackedByteIntLong2", new PackedByteIntLong2 { A = 1, B = -2, C = 3 },
            new PackedByteIntLong2 { A = 0xFE, B = int.MaxValue, C = long.MinValue });
        Each(v, "PackedIntDateTime4", new PackedIntDateTime4 { Id = 0x11223344, At = At1 },
            new PackedIntDateTime4 { Id = -5, At = At2 });
        Each(v, "PackedIntLong4", new PackedIntLong4 { A = 1, B = 0x0102030405060708 }, new PackedIntLong4 { A = -3, B = -4 });
        Each(v, "PackedIntInt4", new PackedIntInt4 { A = 1, B = 2 }, new PackedIntInt4 { A = -3, B = int.MinValue });
        Each(v, "PackedByteLong8", new PackedByteLong8 { A = 1, B = -2 }, new PackedByteLong8 { A = 0x80, B = 3 });
        Each(v, "HoldsPacked", new HoldsPacked { A = 1, P = new PackedIntDateTime4 { Id = 2, At = At1 }, C = 3 },
            new HoldsPacked { A = 0xFE, P = new PackedIntDateTime4 { Id = -4, At = At2 }, C = int.MaxValue });
        Each(v, "PacksNatural", new PacksNatural { A = 1, N = new PackedByteLong8 { A = 2, B = 3 } },
            new PacksNatural { A = 4, N = new PackedByteLong8 { A = 0xFF, B = long.MinValue } });
        Each(v, "SizedInt16", new SizedInt16 { A = 0x01020304 }, new SizedInt16 { A = -1 });
        Each(v, "ExplicitIntLong", new ExplicitIntLong { A = 1, B = 0x0102030405060708 }, new ExplicitIntLong { A = -2, B = -3 });

        var p1 = new PackedIntDateTime4 { Id = 7, At = At1 };
        var p2 = new PackedIntDateTime4 { Id = -8, At = At2 };
        v.Case("LayoutHolder", new LayoutHolder
        {
            P1 = new PackedByteLong1 { A = 1, B = -2 },
            P2 = new PackedByteIntLong2 { A = 3, B = 4, C = -5 },
            P4 = p1,
            P4L = new PackedIntLong4 { A = 6, B = -7 },
            P4I = new PackedIntInt4 { A = 8, B = 9 },
            P8 = new PackedByteLong8 { A = 10, B = 11 },
            Nested = new HoldsPacked { A = 12, P = p2, C = 13 },
            Outer = new PacksNatural { A = 14, N = new PackedByteLong8 { A = 15, B = 16 } },
            Sized = new SizedInt16 { A = 17 },
            Explicit = new ExplicitIntLong { A = 18, B = 19 },
            Maybe = p2,
            None = null,
            Array = new[] { p1, p2 },
            Maybes = new PackedIntDateTime4?[] { p1, null, p2 },
        });
    }

    /// <summary>A shape on its own, as a T? and its null, in an array, a T?[], a list and inside a plain struct.</summary>
    private static void Each<T>(ILayoutCaseVisitor v, string name, T first, T second) where T : struct
    {
        v.Case(name, first);
        v.Case(name + "?", (T?)second);
        v.Case(name + "? null", (T?)null);
        v.Case(name + "[]", new[] { first, second });
        v.Case(name + "?[]", new T?[] { first, null, second });
        v.Case("List<" + name + ">", new System.Collections.Generic.List<T> { second, first });
        v.Case("Around<" + name + ">", new Around<T> { A = 1, B = first, C = -1 });
    }
}
