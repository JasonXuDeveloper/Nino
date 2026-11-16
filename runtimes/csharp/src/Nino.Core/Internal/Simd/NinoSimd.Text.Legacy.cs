#if !NET8_0_OR_GREATER
using System.Numerics;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    internal static partial class NinoSimd
    {
        /// <summary>
        /// The netstandard2.1 build's narrowing and widening of long text with <see cref="Vector{T}"/>, where it is
        /// fast (<see cref="NinoUnsafe.NumericVectors"/>: .NET Core, never Mono or Unity, which take their Burst
        /// kernels, tested first). Measured with the then netstandard2.0 build on .NET 10 (the only runtime here that
        /// accelerates it): 1,024 chars written in 200 ns instead of 450 to 770, read in 310 instead of 530 to 780.
        /// The same bytes as the scalar loops.
        /// </summary>
        internal static class TextVectors
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static bool CanNarrow(nuint length) =>
                NinoUnsafe.NumericVectors && length >= (uint)(2 * Vector<ushort>.Count);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal static bool CanWiden(nuint length) =>
                NinoUnsafe.NumericVectors && length >= (uint)Vector<byte>.Count;

            /// <summary>
            /// Narrows at least two vectors of chars: two at a time, then the last two
            /// again, overlapping the previous (the chars are the same).
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static bool Narrow(ref char source, ref byte destination, nuint n)
            {
                nuint count = (nuint)Vector<ushort>.Count, step = 2 * count;
                var high = new Vector<ushort>(0xFF00);
                ref byte from = ref Unsafe.As<char, byte>(ref source);
                nuint i = 0;
                while (true)
                {
                    var a = Unsafe.ReadUnaligned<Vector<ushort>>(ref Unsafe.Add(ref from, (nint)(i * 2)));
                    var b = Unsafe.ReadUnaligned<Vector<ushort>>(ref Unsafe.Add(ref from, (nint)((i + count) * 2)));
                    if (!Vector.EqualsAll((a | b) & high, Vector<ushort>.Zero))
                        return false;
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, (nint)i), Vector.Narrow(a, b));
                    if (i + step == n)
                        return true;
                    i = i + 2 * step <= n ? i + step : n - step;
                }
            }

            /// <summary>
            /// Widens at least one vector of bytes: one at a time, then the last one again,
            /// overlapping the previous.
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Widen(ref byte source, ref ushort destination, nuint n)
            {
                nuint step = (nuint)Vector<byte>.Count;
                ref byte to = ref Unsafe.As<ushort, byte>(ref destination);
                nuint i = 0;
                while (true)
                {
                    Vector.Widen(Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref source, (nint)i)),
                        out Vector<ushort> lower, out Vector<ushort> upper);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, (nint)(i * 2)), lower);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref to, (nint)(i * 2 + step)), upper);
                    if (i + step == n)
                        return;
                    i = i + 2 * step <= n ? i + step : n - step;
                }
            }
        }
    }
}
#endif
