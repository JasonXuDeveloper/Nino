using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Numerics;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// The fields of a raw type that can hold bytes no constructor makes (<see cref="NinoLayout.ValueChecks"/>), and
    /// how runs of its values are checked in <see cref="NinoSettings.Untrusted"/> mode: one field at a time over every
    /// value, its test branch-free and accumulated, so the loop has one branch (where each value used to take a switch
    /// per field); on .NET 8 and later, runs of at least a vector as whole vectors, each lane's field masked and
    /// tested against its bound by masks repeated with the values' period (<see cref="Plan"/>), where the fields lie on
    /// lanes of their size and are dense enough for that to take half the work.
    /// </summary>
    internal sealed class NinoValueChecks
    {
        // Kinds: bool (0 or 1); DateTime: the ticks of its 64 bits (the top two are its kind), at most
        // DateTime.MaxValue's; decimal: flags first in every runtime's decimal, the sign (bit 31), the scale (bits 16
        // to 23, at most 28), nothing else; DateOnly: day numbers up to DateOnly.MaxValue's; TimeOnly: ticks below one day.
        public const int Bool = 1, DateTime = 2, Decimal = 3, DateOnly = 4, TimeOnly = 5;

        private const ulong DateTimeTicksMask = 0x3FFF_FFFF_FFFF_FFFF;
        private const ulong MaxDateTimeTicks = 3155378975999999999;
        private const uint DecimalBits = 0x7F00_FFFF, DecimalScale = 0x00FF_0000, MaxDecimalScale = 28 << 16;
        private const uint MaxDayNumber = 3652058;
        private const ulong MaxTimeTicks = 864000000000 - 1;

        /// <summary>(offset in memory, kind) pairs.</summary>
        private readonly int[] _pairs;

        public NinoValueChecks(int[] pairs) => _pairs = pairs;

        /// <summary>
        /// Checks <paramref name="count"/> values of <paramref name="type"/> in memory (or in the data, where it lays
        /// them out the same), <paramref name="stride"/> bytes apart, at <paramref name="first"/>: invalid data throws
        /// <see cref="NinoDataException"/>.
        /// </summary>
        public void Check(ref byte first, int count, int stride, Type type)
        {
#if NET8_0_OR_GREATER
            if (Vector.IsHardwareAccelerated && (long)count * stride >= Vector<byte>.Count &&
                PlanFor(stride) is { } plan && plan.Valid(ref first, count * stride))
                return;
#endif
            for (int i = 0; i < _pairs.Length; i += 2)
            {
                ref byte field = ref Unsafe.Add(ref first, _pairs[i]);
                if (!Valid(ref field, count, stride, _pairs[i + 1]))
                    NinoThrow.InvalidValue(type, _pairs[i]);
            }
        }

        /// <summary>Whether the field of <paramref name="kind"/> at <paramref name="field"/> is valid in each of <paramref name="count"/> values.</summary>
        private static bool Valid(ref byte field, int count, int stride, int kind)
        {
            // Each test branch-free, or-ed in (bad is non-zero once one fails), one branch per loop.
            ulong bad = 0;
            ref byte at = ref field;
            switch (kind)
            {
                case Bool:
                    for (int n = count; n > 0; n--, at = ref Unsafe.Add(ref at, stride))
                        bad |= at;
                    return (bad & 0xFE) == 0;
                case DateTime:
                    // Both sides below 2^62: the difference wraps past 2^63 exactly when the ticks are past the maximum.
                    for (int n = count; n > 0; n--, at = ref Unsafe.Add(ref at, stride))
                        bad |= MaxDateTimeTicks - (Unsafe.ReadUnaligned<ulong>(ref at) & DateTimeTicksMask);
                    return bad >> 63 == 0;
                case Decimal:
                    for (int n = count; n > 0; n--, at = ref Unsafe.Add(ref at, stride))
                    {
                        uint flags = Unsafe.ReadUnaligned<uint>(ref at);
                        bad |= flags & DecimalBits | (ulong)((long)MaxDecimalScale - (flags & DecimalScale)) >> 63;
                    }

                    return bad == 0;
                case DateOnly:
                    for (int n = count; n > 0; n--, at = ref Unsafe.Add(ref at, stride))
                        bad |= (ulong)((long)MaxDayNumber - Unsafe.ReadUnaligned<uint>(ref at));
                    return bad >> 63 == 0;
                default:
                    // Ticks of 2^63 and more fail as well: unsigned, any carry out is caught by the second term.
                    for (int n = count; n > 0; n--, at = ref Unsafe.Add(ref at, stride))
                    {
                        ulong ticks = Unsafe.ReadUnaligned<ulong>(ref at);
                        bad |= (MaxTimeTicks - ticks) | ticks;
                    }

                    return bad >> 63 == 0;
            }
        }

#if NET8_0_OR_GREATER
        // The plan of the last stride checked (one per type in practice: its size, or a pair's): replaced whole, so a
        // race only builds it twice. Plan.None where the fields do not lie on lanes of their size.
        private Plan _plan;

        private Plan PlanFor(int stride)
        {
            var plan = _plan;
            if (plan == null || plan.Stride != stride)
                _plan = plan = Plan.Create(_pairs, stride);
            return plan.Passes == null ? null : plan;
        }

        /// <summary>
        /// The vector form of the checks for one stride: passes over the values' bytes as lanes of 1, 4 or 8 bytes,
        /// each lane masked and tested against its bound (<see cref="Test{T}"/>), from two tables repeated with the
        /// period of the values and the vectors (lcm(stride, vector width) bytes, one vector more for the last,
        /// overlapping one). Lanes without a field are masked to zero against a bound of zero. A field takes a pass of
        /// its size (its offset and the stride multiples of it); a test that needs two (a decimal's sign and scale, the
        /// top bit of a DateOnly or TimeOnly) two passes.
        /// </summary>
        private sealed class Plan
        {
            private const int MaxPeriod = 4096;

            public readonly int Stride;
            public readonly Pass[] Passes;
            private readonly int _period;

            private Plan(int stride, Pass[] passes, int period)
            {
                Stride = stride;
                Passes = passes;
                _period = period;
            }

            public static Plan Create(int[] pairs, int stride)
            {
                int width = Vector<byte>.Count;
                int period = stride / Gcd(stride, width) * width;
                var passes = new List<Pass>();
                if (stride <= 0 || period > MaxPeriod)
                    return new Plan(stride, null, 0);
                for (int i = 0; i < pairs.Length; i += 2)
                {
                    int offset = pairs[i];
                    switch (pairs[i + 1])
                    {
                        case Bool:
                            Add(passes, 1, offset, stride, period, 0xFE, 0);
                            break;
                        case DateTime:
                            Add(passes, 8, offset, stride, period, DateTimeTicksMask, MaxDateTimeTicks);
                            break;
                        case Decimal:
                            Add(passes, 4, offset, stride, period, DecimalBits, 0);
                            Add(passes, 4, offset, stride, period, DecimalScale, MaxDecimalScale);
                            break;
                        case DateOnly:
                            Add(passes, 4, offset, stride, period, int.MaxValue, MaxDayNumber);
                            Add(passes, 4, offset, stride, period, 1u << 31, 0);
                            break;
                        default:
                            Add(passes, 8, offset, stride, period, long.MaxValue, MaxTimeTicks);
                            Add(passes, 8, offset, stride, period, 1ul << 63, 0);
                            break;
                    }
                }

                // Each pass costs about what one field of one value costs the scalar loop, per vector of bytes: worth it
                // where the fields are dense, at half the scalar work or less (DateTime[]: 415 ns a thousand instead of
                // 895; a 24-byte struct with a DateTime and a bool, two passes: 1,616 instead of 1,526; a 48-byte one
                // with six fields, five passes: 9,849 instead of 5,658; AVX-512 Xeon, 32-byte vectors).
                return new Plan(stride,
                    passes.Contains(null) || 2 * passes.Count * stride > pairs.Length / 2 * width ? null : passes.ToArray(),
                    period);
            }

            /// <summary>
            /// Sets the lanes of the field at <paramref name="offset"/> in the first pass of <paramref name="size"/>-byte
            /// lanes where they are free (a new one otherwise); null where the field does not lie on such lanes.
            /// </summary>
            private static void Add(List<Pass> passes, int size, int offset, int stride, int period, ulong mask,
                ulong bound)
            {
                if (offset % size != 0 || stride % size != 0)
                {
                    passes.Add(null);
                    return;
                }

                Pass pass = null;
                foreach (var p in passes)
                    if (p != null && p.Size == size && p.Free(offset, stride, period))
                    {
                        pass = p;
                        break;
                    }

                if (pass == null)
                    passes.Add(pass = new Pass(size, period + Vector<byte>.Count));
                for (int at = offset; at < pass.Masks.Length; at += stride)
                {
                    pass.Set(pass.Masks, at, mask);
                    pass.Set(pass.Bounds, at, bound);
                }
            }

            /// <summary>Whether every value in the <paramref name="length"/> bytes at <paramref name="first"/> passes.</summary>
            public bool Valid(ref byte first, int length)
            {
                foreach (var pass in Passes)
                {
                    bool valid = pass.Size switch
                    {
                        1 => Valid<byte>(ref first, length, pass),
                        4 => Valid<uint>(ref first, length, pass),
                        _ => Valid<ulong>(ref first, length, pass),
                    };
                    if (!valid)
                        return false;
                }

                return true;
            }

            private bool Valid<T>(ref byte first, int length, Pass pass) where T : unmanaged
            {
                int width = Vector<byte>.Count;
                ref byte masks = ref pass.Masks[0];
                ref byte bounds = ref pass.Bounds[0];
                var bad = Vector<T>.Zero;
                int phase = 0;
                int last = length - width;
                for (int at = 0; at <= last; at += width)
                {
                    bad |= Test<T>(Load<T>(ref first, at), ref masks, ref bounds, phase);
                    phase += width;
                    if (phase == _period)
                        phase = 0;
                }

                // The last vector again, over values already checked.
                if (length % width != 0)
                    bad |= Test<T>(Load<T>(ref first, last), ref masks, ref bounds, last % _period);
                if (typeof(T) == typeof(byte))
                    return bad == Vector<T>.Zero;
                // Wider lanes: their sign bits.
                return (Vector.AsVectorUInt64(bad) & new Vector<ulong>(typeof(T) == typeof(uint)
                    ? 0x8000_0000_8000_0000
                    : 0x8000_0000_0000_0000)) == Vector<ulong>.Zero;
            }

            /// <summary>
            /// Lanes of one byte: the bits of the mask, which a valid byte never has. Wider lanes: the bound less the
            /// masked lane, whose sign bit is set exactly when the lane is past the bound (masks and bounds below the
            /// sign bit, or a mask of the sign bit alone and a bound of zero). Subtractions and ors, not compares:
            /// unsigned compares are several instructions on x64, and these were slower than the scalar loop.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector<T> Test<T>(Vector<T> value, ref byte masks, ref byte bounds, int phase)
                where T : unmanaged =>
                typeof(T) == typeof(byte)
                    ? value & Load<T>(ref masks, phase)
                    : Load<T>(ref bounds, phase) - (value & Load<T>(ref masks, phase));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static Vector<T> Load<T>(ref byte source, int at) where T : unmanaged =>
                Unsafe.ReadUnaligned<Vector<T>>(ref Unsafe.Add(ref source, at));

            private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
        }

        /// <summary>One pass of <see cref="Plan"/>: lanes of <see cref="Size"/> bytes, their masks and bounds.</summary>
        private sealed class Pass
        {
            public readonly int Size;
            public readonly byte[] Masks, Bounds;

            public Pass(int size, int length)
            {
                Size = size;
                Masks = new byte[length];
                Bounds = new byte[length];
            }

            /// <summary>Whether no field has the lanes at <paramref name="offset"/> of each value yet.</summary>
            public bool Free(int offset, int stride, int period)
            {
                for (int at = offset; at < period; at += stride)
                    for (int b = 0; b < Size; b++)
                        if (Masks[at + b] != 0)
                            return false;
                return true;
            }

            public void Set(byte[] table, int at, ulong value)
            {
                for (int b = 0; b < Size && at + b < table.Length; b++)
                    table[at + b] = (byte)(value >> (8 * b));
            }
        }
#endif
    }
}
