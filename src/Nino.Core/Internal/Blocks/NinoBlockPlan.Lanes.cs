#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#if NET10_0_OR_GREATER
using System.Runtime.Intrinsics.Wasm;
#endif
using System.Runtime.Intrinsics.X86;

namespace Nino.Core.Internal
{
    // The lane plan; the plan's static fields stay in NinoBlockPlan.cs, in initialization order.
    internal static partial class NinoBlockPlan<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
        /// <summary>
        /// Values moved a group at a time without AVX-512, in the direction of <typeparamref name="TDirection"/>:
        /// each vector written (32 bytes with AVX2, else 16: SSSE3, Arm64, WebAssembly on .NET 10) is made of 16-byte
        /// lanes, each the bytes of at most two 16-byte windows of the source shuffled within the lane (vpshufb issues
        /// twice a cycle on Zen 3 and Intel since Ice Lake, where cross-lane permutes issue once). The windows are at
        /// fixed offsets from the group's first value, so a load can start before it or reach past it. Stores start
        /// on a vector boundary where values allow it, else are unaligned.
        /// </summary>
        internal static class LanePlan<TDirection> where TDirection : struct, INinoDirection
        {
            // Before the constants read from it (static fields initialize in order).
            private static readonly NinoLanes Plan = Solve();

            /// <summary>Whether values are moved this way.</summary>
            public static readonly bool Enabled = Plan != null;

            /// <summary>The vector width (32 or 16), the values a group, and its vectors (1 to 4).</summary>
            public static readonly int Width = Plan?.Width ?? 0, Group = Plan?.Group ?? 0, Vectors = Plan?.Vectors ?? 0;

            /// <summary>The first value a group may start at: its windows start inside the source.</summary>
            public static readonly int Start = Plan?.Start ?? 0;

            private static readonly int End = Plan?.End ?? 0;

            /// <summary>Arm64: the group's windows load once, on the 16-byte grid (NinoLanes.Gridded).</summary>
            public static readonly bool Gridded = Plan?.Gridded() == true;

            private static readonly int GridLoads = Gridded ? Plan.GridLoads() : 0;

            /// <summary>Whether a <see cref="Gridded"/> group loads bytes 16 <paramref name="k"/> to 16
            /// <paramref name="k"/> + 15.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Loads(int k) => (GridLoads & (1 << k)) != 0;

            // The windows of each vector (Window); NinoLanes.None for a second load not taken.
            private static readonly int W0 = Get(0), W1 = Get(1), W2 = Get(2), W3 = Get(3), W4 = Get(4), W5 = Get(5),
                W6 = Get(6), W7 = Get(7), W8 = Get(8), W9 = Get(9), W10 = Get(10), W11 = Get(11), W12 = Get(12),
                W13 = Get(13), W14 = Get(14), W15 = Get(15);

            /// <summary>The shuffle indices of each vector's first and second load (Arm64: of both, in the
            /// first).</summary>
            public static readonly Vector256<byte>[] Masks32 = Plan?.Masks32() ?? new Vector256<byte>[8];

            /// <inheritdoc cref="Masks32"/>
            public static readonly Vector128<byte>[] Masks16 = Plan?.Masks16() ?? new Vector128<byte>[8];

            /// <summary>
            /// Reads: whether the groups check the bools of the blocks they move (<see cref="Bools"/>) in the same
            /// pass, in place of the strided pass before them (NinoBlockCollections.LanesChecked; AVX-512 CPUs take
            /// <see cref="ReadPlan{TPhase}.Checks"/> instead). 32-byte groups only: on Arm64, Lanes16 is at the edge of
            /// the JIT's inlining budget. Not for values that <see cref="Shuffles"/> moves after the groups without a
            /// look; the others are read one at a time, through ReadBlock, which checks them.
            /// </summary>
            public static readonly bool Checks = Plan is { Width: 32 } && typeof(TDirection) == typeof(NinoReads) &&
                                                 Bools != null && !Shuffles && !ReadPlan<NinoPhase0>.Enabled;

            /// <summary>
            /// For each vector of <see cref="Checks"/>, 0xFE at the bools of the values in it (fields: the JIT reads
            /// each where it is used, vpand's memory operand).
            /// </summary>
            public static readonly Vector256<byte> BoolsA = BoolMask(0), BoolsB = BoolMask(1), BoolsC = BoolMask(2),
                BoolsD = BoolMask(3);

            private static Vector256<byte> BoolMask(int vector) =>
                Checks ? Vector256.Create(Plan.Bools(vector, Bools, NinoBlockCollections.FixedSize<T, TFormatter>())) : default;

            private static int Get(int index) => Plan == null ? 0 : Plan.Windows[index];

            /// <summary>
            /// Where lane <paramref name="lane"/> of load <paramref name="load"/> of vector <paramref name="vector"/>
            /// starts, from the group's first value. Constants in, a constant out.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Window(int vector, int load, int lane)
            {
                int index = vector * 4 + load * 2 + lane;
                return index switch
                {
                    0 => W0, 1 => W1, 2 => W2, 3 => W3, 4 => W4, 5 => W5, 6 => W6, 7 => W7, 8 => W8, 9 => W9,
                    10 => W10, 11 => W11, 12 => W12, 13 => W13, 14 => W14, _ => W15
                };
            }

            /// <summary>
            /// Whether <paramref name="count"/> values hold three groups past <see cref="Start"/> (and the values moved
            /// alone to align the stores): fewer are moved one at a time.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Worth(int count) => Enabled && count >= Start + 3 * Group + 2;

            /// <summary>
            /// The last of <paramref name="count"/> values a group may start at: its loads stay inside the source
            /// (<paramref name="from"/> bytes a value) and its stores inside the destination (<paramref name="to"/>
            /// bytes a value).
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Last(int count, int from, int to)
            {
                long loads = ((long)count * from - End) / from;
                long stores = ((long)count * to - Vectors * Width) / to;
                return (int)Math.Min(loads, stores);
            }

            private static NinoLanes Solve()
            {
                if (Map == null || IsMemory || NinoBlockCollections.FixedSize<T, TFormatter>() > Unsafe.SizeOf<T>())
                    return null;
                bool shuffles = Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;
#if NET10_0_OR_GREATER
                shuffles |= PackedSimd.IsSupported;
#endif
                int width = Avx2.IsSupported ? 32 : shuffles ? 16 : 0;
                if (width == 0)
                    return null;
                bool writes = typeof(TDirection) == typeof(NinoWrites);
                // Partial groups where nothing else moves several values at a time: without AVX-512 VBMI, and with it
                // for values its group and 64-byte permutes do not take (more than 16 bytes in memory: 1,000 values of
                // 24 bytes, 15-byte blocks, took 1.7 us written one at a time on an AVX-512 Xeon, 0.43 us in groups);
                // on Arm64 for values its 64-byte permutes do not take (more than 8 bytes in memory: the same 1,000
                // values took 1.44 us written one at a time on GitHub's Neoverse-N2 runners, MemoryPack 0.39 us).
                bool others = Permutes || (writes ? Groups || PerPair > 0 : ReadPlan<NinoPhase0>.Enabled);
                var plan = NinoLanes.Solve(width, Bytes, writes, AdvSimd.Arm64.IsSupported,
                    AdvSimd.Arm64.IsSupported ? !others : !Avx512Vbmi.IsSupported || !others);
                // A value a group is what Shuffles does already, without the group loop around it.
                return plan is { Group: 1 } && Shuffles ? null : plan;
            }
        }
    }
}
#endif
