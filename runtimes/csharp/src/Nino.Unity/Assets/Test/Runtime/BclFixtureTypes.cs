using System;
using System.Collections.Generic;
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

// Structs under every [StructLayout] form (#378): IL2CPP's Pack never moves a BCL struct field (DateTime, decimal,
// Guid), so such a struct is laid out apart there (a Pack = 4 { int; DateTime } is 12 bytes on CoreCLR and Mono, 16 on
// IL2CPP). Each runtime must still write and read
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

/// <summary>Auto layout: CoreCLR reorders the fields by size (the long first), Mono and IL2CPP may not.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct AutoByteLongByte { public byte A; public long B; public byte C; }

/// <summary>Auto layout around a DateTime, as the BCL's DateTimeOffset.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public struct AutoIntDateTime { public int Id; public DateTime At; }

/// <summary>Explicit offsets that overlap: an int and a float sharing their bytes, a long over both halves.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
public struct ExplicitUnion
{
    [System.Runtime.InteropServices.FieldOffset(0)] public long All;
    [System.Runtime.InteropServices.FieldOffset(0)] public int Low;
    [System.Runtime.InteropServices.FieldOffset(0)] public float LowFloat;
    [System.Runtime.InteropServices.FieldOffset(4)] public int High;
}

/// <summary>Explicit offsets with a gap and Size past the last field.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 24)]
public struct ExplicitGapSized
{
    [System.Runtime.InteropServices.FieldOffset(1)] public byte A;
    [System.Runtime.InteropServices.FieldOffset(8)] public int B;
    [System.Runtime.InteropServices.FieldOffset(13)] public short C;
}

/// <summary>
/// A Pack = 4 struct at an explicit offset, after a gap. At 8, not an odd offset: IL2CPP on Unity 6000.6 refuses to
/// load an explicit struct field at offset 1 (TypeLoadException "Invalid field offset", 2022.3 loads it).
/// </summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
public struct ExplicitHoldsPacked
{
    [System.Runtime.InteropServices.FieldOffset(0)] public byte A;
    [System.Runtime.InteropServices.FieldOffset(8)] public PackedIntDateTime4 P;
}

/// <summary>Pack and Size together: 5 bytes of fields, 12 of struct.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1, Size = 12)]
public struct PackedSized { public byte A; public int B; }

/// <summary>Size smaller than the fields, which every runtime ignores.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = 2)]
public struct SizedTooSmall { public int A; public int B; }

/// <summary>Pack = 16, above every field's alignment: moves nothing.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 16)]
public struct PackedByteLong16 { public byte A; public long B; }

/// <summary>Pack = 1 over every leaf type Nino knows.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct PackedLeaves1
{
    public bool A; public char B; public short C; public float D; public byte E; public double F; public sbyte G;
    public decimal H; public byte I; public Guid J; public byte K; public TimeSpan L; public byte M; public DateTime N;
}

/// <summary>Pack = 2 with floats.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 2)]
public struct PackedFloats2 { public byte A; public float B; public byte C; public double D; }

public enum LongEnum : long { Zero, Big = 0x0102030405060708 }

/// <summary>Pack = 1 with an enum of a long.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct PackedEnum1 { public byte A; public LongEnum B; public byte C; }

/// <summary>Pack = 1 with a fixed buffer of ints.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public unsafe struct PackedFixed1 { public byte A; public fixed int B[3]; public long C; }

/// <summary>A generic struct with Pack = 1.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 1)]
public struct PackedPair1<T> { public byte A; public T B; }

/// <summary>Pack = 4 holding a Pack = 1 struct and a DateTimeOffset (auto layout).</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
public struct PackedMix4 { public byte A; public PackedByteLong1 B; public short C; public DateTimeOffset D; }

/// <summary>A natural struct holding an Explicit one and an Auto one.</summary>
public struct HoldsKinds { public byte A; public ExplicitIntLong E; public byte B; public AutoByteLongByte U; }

/// <summary>CharSet changes only marshalling: the managed layout is the natural one.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,
    CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
public struct AnsiCharInt { public char A; public bool B; public int C; }

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
    public AutoByteLongByte Auto;
    public AutoIntDateTime AutoDate;
    public ExplicitUnion Union;
    public ExplicitGapSized Gap;
    public ExplicitHoldsPacked ExplicitPacked;
    public PackedSized PackSize;
    public SizedTooSmall Small;
    public PackedByteLong16 P16;
    public PackedLeaves1 Leaves;
    public PackedFloats2 Floats;
    public PackedEnum1 Enum;
    public PackedFixed1 Fixed;
    public PackedPair1<long> Pair;
    public PackedMix4 Mix;
    public HoldsKinds Kinds;
    public AnsiCharInt Ansi;
}

/// <summary>
/// The generic structs IL2CPP lays out in two ways (LayoutShapes' cases it refuses through generic code) as members of a
/// [NinoType]: the generator registers their compiled offsets, so the IL2CPP player writes CoreCLR's bytes. Not sealed,
/// as <see cref="LayoutHolder"/>.
/// </summary>
[NinoType]
public class SplitHolder
{
    public Around<PackedIntDateTime4> AroundPacked;
    public Around<HoldsPacked> AroundHolds;
    public Around<PackedLeaves1> AroundLeaves;
    public Around<PackedMix4> AroundMix;
    public Around<ExplicitHoldsPacked> AroundExplicit;
    public Around<PackedPair1<long>> AroundPair;
    public HoldsPacked? Holds;
    public HoldsPacked? NoHolds;
    public PackedLeaves1?[] Leaves;
    public List<PackedMix4?> Mixes;
    public ExplicitHoldsPacked? Explicit;
    public PackedPair1<long>? Pair;
    public PackedPair1<long>[] Pairs;
    public List<PackedPair1<long>> PairList;
}

/// <summary>Takes one case: its name (the key of its bytes in BclFixtures) and its value.</summary>
public interface ILayoutCaseVisitor
{
    /// <summary>A shape, once before its cases: its size and its T?'s on this runtime.</summary>
    void Sizes<T>(string name) where T : struct;

    void Case<T>(string name, T value);
}

/// <summary>The StructLayout cases, listed once for every runtime that checks them.</summary>
public static class LayoutShapes
{
    internal static readonly DateTime At1 = new DateTime(0x0123456789ABCDEF & 0x0FFFFFFFFFFFFFFF, DateTimeKind.Utc);
    internal static readonly DateTime At2 = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Local);

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

        Each(v, "AutoByteLongByte", new AutoByteLongByte { A = 1, B = 0x0102030405060708, C = 2 },
            new AutoByteLongByte { A = 0xFE, B = -3, C = 0x80 });
        Each(v, "AutoIntDateTime", new AutoIntDateTime { Id = 0x11223344, At = At1 }, new AutoIntDateTime { Id = -5, At = At2 });
        Each(v, "ExplicitUnion", new ExplicitUnion { All = 0x0102030405060708 }, new ExplicitUnion { Low = -1, High = 7 });
        Each(v, "ExplicitGapSized", new ExplicitGapSized { A = 1, B = 0x01020304, C = -2 },
            new ExplicitGapSized { A = 0xFF, B = -3, C = short.MaxValue });
        Each(v, "ExplicitHoldsPacked", new ExplicitHoldsPacked { A = 1, P = new PackedIntDateTime4 { Id = 2, At = At1 } },
            new ExplicitHoldsPacked { A = 0xFE, P = new PackedIntDateTime4 { Id = -3, At = At2 } });
        Each(v, "PackedSized", new PackedSized { A = 1, B = 0x01020304 }, new PackedSized { A = 0xFE, B = -2 });
        Each(v, "SizedTooSmall", new SizedTooSmall { A = 1, B = 2 }, new SizedTooSmall { A = -3, B = int.MaxValue });
        Each(v, "PackedByteLong16", new PackedByteLong16 { A = 1, B = -2 }, new PackedByteLong16 { A = 0x80, B = 3 });
        Each(v, "PackedLeaves1", Leaves(1), Leaves(-2));
        Each(v, "PackedFloats2", new PackedFloats2 { A = 1, B = 1.5f, C = 2, D = -2.25 },
            new PackedFloats2 { A = 0xFE, B = float.MinValue, C = 0x80, D = double.Epsilon });
        Each(v, "PackedEnum1", new PackedEnum1 { A = 1, B = LongEnum.Big, C = 2 }, new PackedEnum1 { A = 3, B = (LongEnum)(-4), C = 5 });
        Each(v, "PackedFixed1", Fixed(1), Fixed(-7));
        Each(v, "PackedPair1<long>", new PackedPair1<long> { A = 1, B = 0x0102030405060708 }, new PackedPair1<long> { A = 2, B = -3 });
        Each(v, "PackedPair1<DateTime>", new PackedPair1<DateTime> { A = 1, B = At1 }, new PackedPair1<DateTime> { A = 2, B = At2 });
        Each(v, "PackedMix4", Mix(1), Mix(-2));
        Each(v, "HoldsKinds", Kinds(1), Kinds(-2));
        Each(v, "AnsiCharInt", new AnsiCharInt { A = 'x', B = true, C = 0x01020304 }, new AnsiCharInt { A = '\u4E2D', B = false, C = -5 });

        var p1 = new PackedIntDateTime4 { Id = 7, At = At1 };
        var p2 = new PackedIntDateTime4 { Id = -8, At = At2 };
        var holds = new HoldsPacked { A = 1, P = p1, C = 2 };
        // Built only where its arrays' elements fit: IL2CPP allocates a PackedLeaves1?[] with smaller elements than
        // its code writes (#378), and Unity's tests read SplitHolder from CoreCLR's bytes there instead.
        if (ElementsFit<PackedLeaves1?>())
            v.Case("SplitHolder", new SplitHolder
        {
            AroundPacked = new Around<PackedIntDateTime4> { A = 3, B = p2, C = -4 },
            AroundHolds = new Around<HoldsPacked> { A = 5, B = holds, C = -6 },
            AroundLeaves = new Around<PackedLeaves1> { A = 7, B = Leaves(3), C = -8 },
            AroundMix = new Around<PackedMix4> { A = 9, B = Mix(4), C = -10 },
            AroundExplicit = new Around<ExplicitHoldsPacked> { A = 11, B = new ExplicitHoldsPacked { A = 12, P = p1 }, C = -13 },
            AroundPair = new Around<PackedPair1<long>> { A = 14, B = new PackedPair1<long> { A = 15, B = -16 }, C = 17 },
            Holds = holds,
            NoHolds = null,
            Leaves = new PackedLeaves1?[] { Leaves(5), null, Leaves(-6) },
            Mixes = new List<PackedMix4?> { null, Mix(7) },
            Explicit = new ExplicitHoldsPacked { A = 18, P = p2 },
            Pair = new PackedPair1<long> { A = 19, B = 0x0102030405060708 },
            Pairs = new[] { new PackedPair1<long> { A = 20, B = -21 }, new PackedPair1<long> { A = 22, B = 23 } },
            PairList = new List<PackedPair1<long>> { new PackedPair1<long> { A = 24, B = 25 } },
        });
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
            Auto = new AutoByteLongByte { A = 20, B = 21, C = 22 },
            AutoDate = new AutoIntDateTime { Id = 23, At = At2 },
            Union = new ExplicitUnion { All = 0x0102030405060708 },
            Gap = new ExplicitGapSized { A = 24, B = 25, C = 26 },
            ExplicitPacked = new ExplicitHoldsPacked { A = 27, P = p1 },
            PackSize = new PackedSized { A = 28, B = 29 },
            Small = new SizedTooSmall { A = 30, B = 31 },
            P16 = new PackedByteLong16 { A = 32, B = 33 },
            Leaves = Leaves(34),
            Floats = new PackedFloats2 { A = 35, B = 36, C = 37, D = 38 },
            Enum = new PackedEnum1 { A = 39, B = LongEnum.Big, C = 40 },
            Fixed = Fixed(41),
            Pair = new PackedPair1<long> { A = 42, B = 43 },
            Mix = Mix(44),
            Kinds = Kinds(45),
            Ansi = new AnsiCharInt { A = 'y', B = true, C = 46 },
        });
    }

    internal static PackedLeaves1 Leaves(int n) => new PackedLeaves1
    {
        A = n > 0, B = (char)('a' + (n & 7)), C = (short)(n * 3), D = n * 1.25f, E = (byte)n, F = n * -2.5,
        G = (sbyte)(-n), H = n * 1.1m, I = (byte)(n + 1), J = new Guid(n, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11),
        K = (byte)(n + 2), L = TimeSpan.FromTicks(n * 1234567L), M = (byte)(n + 3), N = n > 0 ? At1 : At2,
    };

    private static unsafe PackedFixed1 Fixed(int n)
    {
        var f = new PackedFixed1 { A = (byte)n, C = n * 0x0101010101L };
        f.B[0] = n; f.B[1] = n * 2; f.B[2] = int.MinValue + n;
        return f;
    }

    internal static PackedMix4 Mix(int n) => new PackedMix4
    {
        A = (byte)n, B = new PackedByteLong1 { A = (byte)(n + 1), B = n * 3L }, C = (short)(n * 5),
        D = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.FromHours(n % 14)),
    };

    private static HoldsKinds Kinds(int n) => new HoldsKinds
    {
        A = (byte)n, E = new ExplicitIntLong { A = n * 2, B = n * 3L }, B = (byte)(n + 4),
        U = new AutoByteLongByte { A = (byte)(n + 5), B = n * 6L, C = (byte)(n + 7) },
    };

    /// <summary>
    /// Whether this runtime allocates arrays of <typeparamref name="TElement"/> with elements at least as large as its
    /// compiled code writes: IL2CPP does not where it lays out a struct with Pack one way at run time and another in
    /// C++ (libil2cpp, #378). Always true elsewhere.
    /// </summary>
    internal static bool ElementsFit<TElement>()
    {
        var array = new TElement[2];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            long runtime = (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(array, 1) -
                           (long)System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(array, 0);
            return System.Runtime.CompilerServices.Unsafe.SizeOf<TElement>() <= runtime;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>A shape on its own, as a T? and its null, in an array, a T?[], a list and inside a plain struct.</summary>
    private static void Each<T>(ILayoutCaseVisitor v, string name, T first, T second) where T : struct
    {
        v.Sizes<T>(name);
        v.Case(name, first);
        v.Case(name + "?", (T?)second);
        v.Case(name + "? null", (T?)null);
        v.Case(name + "[]", new[] { first, second });
        // IL2CPP allocates a T?[] of some packed structs with smaller elements than its code writes (#378): building
        // one would write past it, so it is built only where its elements fit.
        if (ElementsFit<T?>())
            v.Case(name + "?[]", new T?[] { first, null, second });
        v.Case("List<" + name + ">", new System.Collections.Generic.List<T> { second, first });
        v.Case("Around<" + name + ">", new Around<T> { A = 1, B = first, C = -1 });
    }
}
