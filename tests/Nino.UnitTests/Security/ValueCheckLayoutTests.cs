#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

namespace Nino.UnitTests;

/// <summary>
/// The checks of Untrusted mode (<see cref="NinoValueChecks"/>) over generated struct layouts: random fields of each
/// checked kind at random offsets and strides (aligned as the runtime lays them out, and packed), random bytes
/// everywhere else, runs of random lengths, and fields made invalid at random. Each run must fail exactly when a field
/// holds a value its type's own constructor rejects, whichever form checks it: the scalar loops, or the vector passes
/// (a bool joining a wider lane's pass, periods, the overlapping last vector) at the width this process has; CI's
/// SIMD levels and <c>DOTNET_MaxVectorTBitWidth</c> give the others. Each run is checked again where its bytes end
/// with its last value's last field, right before an inaccessible page (the values of raw dictionary pairs end a key
/// before the next pair): a check that reads past them crashes the test host.
/// </summary>
[TestClass]
public class ValueCheckLayoutTests
{
    private static readonly int[] Kinds =
    {
        NinoValueChecks.Bool, NinoValueChecks.DateTime, NinoValueChecks.Decimal, NinoValueChecks.DateOnly,
        NinoValueChecks.TimeOnly,
    };

    private static readonly FieldInfo PlanField =
        typeof(NinoValueChecks).GetField("_plan", BindingFlags.Instance | BindingFlags.NonPublic);

    [TestMethod]
    public void ChecksFailExactlyOnInvalidFields()
    {
        var random = new Random(129);
        int vectored = 0, invalid = 0;
        for (int layout = 0; layout < 3000; layout++)
        {
            var (pairs, stride) = Layout(random);
            var checks = new NinoValueChecks(pairs);
            for (int run = 0; run < 6; run++)
            {
                int count = random.Next(4) switch
                {
                    0 => random.Next(1, 4),
                    1 => random.Next(1, 300),
                    _ => Math.Max(1, (64 * random.Next(1, 5) + random.Next(-2, 3)) / stride),
                };
                var data = new byte[count * stride];
                random.NextBytes(data);
                for (int i = 0; i < count; i++)
                    for (int f = 0; f < pairs.Length; f += 2)
                        WriteValid(random, data, i * stride + pairs[f], pairs[f + 1]);
                // Half the runs valid, the others with one to three fields made invalid (some by bytes that stay valid).
                if (run % 2 == 1)
                    for (int bad = random.Next(1, 4); bad > 0; bad--)
                    {
                        int f = 2 * random.Next(pairs.Length / 2);
                        WriteInvalid(random, data, random.Next(count) * stride + pairs[f], pairs[f + 1]);
                    }

                bool expected = true;
                for (int i = 0; i < count && expected; i++)
                    for (int f = 0; f < pairs.Length && expected; f += 2)
                        expected = IsValid(data, i * stride + pairs[f], pairs[f + 1]);
                bool passed = Passes(checks, ref data[0], count, stride);
                var end = GuardedMemory.AtEnd(data.AsSpan(0, (count - 1) * stride + End(pairs)));
                if (!end.IsEmpty && Passes(checks, ref end[0], count, stride) != passed)
                    Assert.Fail($"stride {stride}, count {count}: the run at a page's end checked otherwise");

                if (passed != expected)
                    Assert.Fail($"{(expected ? "rejected" : "accepted")}: stride {stride}, count {count}, fields " +
                                $"[{string.Join(", ", pairs)}], vector width {System.Numerics.Vector<byte>.Count}, " +
                                $"data {Convert.ToHexString(data)}");
                if (!expected)
                    invalid++;
            }

            // The plan of this stride, built when a run was a vector or more: its passes are null where it stays scalar.
            if (PlanField?.GetValue(checks) is { } plan &&
                plan.GetType().GetField("Passes")?.GetValue(plan) != null)
                vectored++;
        }

        Assert.IsTrue(invalid > 3000, $"{invalid} invalid runs");
        if (System.Numerics.Vector.IsHardwareAccelerated)
            Assert.IsTrue(vectored > 300, $"{vectored} layouts took the vector passes");
    }

    private static bool Passes(NinoValueChecks checks, ref byte first, int count, int stride)
    {
        try
        {
            checks.Check(ref first, count, stride, typeof(ValueCheckLayoutTests));
            return true;
        }
        catch (NinoDataException)
        {
            return false;
        }
    }

    /// <summary>Where the last field of a value ends.</summary>
    private static int End(int[] pairs)
    {
        int end = 0;
        for (int i = 0; i < pairs.Length; i += 2)
            end = Math.Max(end, pairs[i] + Size(pairs[i + 1]));
        return end;
    }

    /// <summary>
    /// (offset, kind) pairs and a stride: fields at their natural alignment in a struct as long as its largest
    /// alignment requires (most layouts, densely packed with bools so they share lanes), or packed at any offset.
    /// </summary>
    private static (int[] Pairs, int Stride) Layout(Random random)
    {
        bool packed = random.Next(5) == 0;
        int fields = random.Next(1, 9);
        var pairs = new List<int>();
        int end = 0, align = 1;
        for (int i = 0; i < fields; i++)
        {
            int kind = random.Next(3) == 0 ? NinoValueChecks.Bool : Kinds[random.Next(Kinds.Length)];
            int size = Size(kind);
            int fieldAlign = packed ? 1 : Math.Min(size, 8);
            // Sometimes a gap of unchecked bytes (an int, a byte, padding).
            int at = end + (random.Next(3) == 0 ? random.Next(0, 9) : 0);
            at = (at + fieldAlign - 1) / fieldAlign * fieldAlign;
            pairs.Add(at);
            pairs.Add(kind);
            end = at + size;
            align = Math.Max(align, fieldAlign);
        }

        int stride = end + (random.Next(4) == 0 ? random.Next(0, 9) : 0);
        stride = (stride + align - 1) / align * align;
        return (pairs.ToArray(), stride);
    }

    private static int Size(int kind) => kind switch
    {
        NinoValueChecks.Bool => 1,
        NinoValueChecks.DateOnly => 4,
        NinoValueChecks.Decimal => 16,
        _ => 8,
    };

    private static void WriteValid(Random random, byte[] data, int at, int kind)
    {
        switch (kind)
        {
            case NinoValueChecks.Bool:
                data[at] = (byte)random.Next(2);
                break;
            case NinoValueChecks.DateTime:
                ulong ticks = random.Next(3) == 0
                    ? (ulong)DateTime.MaxValue.Ticks - (ulong)random.Next(2)
                    : (ulong)random.NextInt64(DateTime.MaxValue.Ticks);
                Write(data, at, ticks | (ulong)random.Next(4) << 62);
                break;
            case NinoValueChecks.Decimal:
                // Flags: the sign and a scale up to 28; then any 96-bit integer.
                Write(data, at, (uint)random.Next(2) << 31 | (uint)(random.Next(3) == 0 ? 28 : random.Next(29)) << 16);
                break;
            case NinoValueChecks.DateOnly:
                Write(data, at, random.Next(3) == 0 ? (uint)DateOnly.MaxValue.DayNumber : (uint)random.Next(DateOnly.MaxValue.DayNumber));
                break;
            default:
                Write(data, at, random.Next(3) == 0 ? (ulong)TimeOnly.MaxValue.Ticks : (ulong)random.NextInt64(TimeOnly.MaxValue.Ticks));
                break;
        }
    }

    /// <summary>A value just past the bound, a bit set the type never has, or random bytes over the field.</summary>
    private static void WriteInvalid(Random random, byte[] data, int at, int kind)
    {
        switch (kind)
        {
            case NinoValueChecks.Bool:
                data[at] = (byte)random.Next(2, 256);
                break;
            case NinoValueChecks.DateTime:
                Write(data, at, random.Next(2) == 0
                    ? (ulong)DateTime.MaxValue.Ticks + 1 + (ulong)random.Next(1000) | (ulong)random.Next(4) << 62
                    : (ulong)random.NextInt64());
                break;
            case NinoValueChecks.Decimal:
                uint flags = random.Next(3) switch
                {
                    0 => (uint)random.Next(29, 256) << 16,
                    1 => ReadUInt(data, at) | 1u << random.Next(32),
                    _ => (uint)random.Next() << 1 | (uint)random.Next(2),
                };
                Write(data, at, flags);
                break;
            case NinoValueChecks.DateOnly:
                Write(data, at, random.Next(2) == 0
                    ? (uint)DateOnly.MaxValue.DayNumber + 1 + (uint)random.Next(1000)
                    : (uint)random.Next() | (uint)random.Next(2) << 31);
                break;
            default:
                Write(data, at, random.Next(2) == 0
                    ? (ulong)TimeOnly.MaxValue.Ticks + 1 + (ulong)random.Next(1000)
                    : (ulong)random.NextInt64() | (ulong)random.Next(2) << 63);
                break;
        }
    }

    /// <summary>Whether the type's own constructor takes the field's bytes (the oracle, independent of Nino's masks).</summary>
    private static bool IsValid(byte[] data, int at, int kind)
    {
        try
        {
            switch (kind)
            {
                case NinoValueChecks.Bool:
                    return data[at] <= 1;
                case NinoValueChecks.DateTime:
                    _ = new DateTime((long)(ReadULong(data, at) & 0x3FFF_FFFF_FFFF_FFFF));
                    return true;
                case NinoValueChecks.Decimal:
                    // Flags first in memory (as Nino's checks read them); decimal(int[]) takes lo, mid, hi, flags.
                    _ = new decimal(new[] { 0, 0, 0, (int)ReadUInt(data, at) });
                    return true;
                case NinoValueChecks.DateOnly:
                    uint day = ReadUInt(data, at);
                    if (day > int.MaxValue)
                        return false;
                    _ = DateOnly.FromDayNumber((int)day);
                    return true;
                default:
                    _ = new TimeOnly((long)ReadULong(data, at));
                    return true;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static uint ReadUInt(byte[] data, int at) => Unsafe.ReadUnaligned<uint>(ref data[at]);
    private static ulong ReadULong(byte[] data, int at) => Unsafe.ReadUnaligned<ulong>(ref data[at]);
    private static void Write(byte[] data, int at, uint value) => Unsafe.WriteUnaligned(ref data[at], value);
    private static void Write(byte[] data, int at, ulong value) => Unsafe.WriteUnaligned(ref data[at], value);
}
#endif
