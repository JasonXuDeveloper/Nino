using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#if NET10_0_OR_GREATER
using System.Runtime.Intrinsics.Wasm;
#endif
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.Internal
{
#if NET8_0_OR_GREATER
    /// <summary>
    /// How <see cref="NinoBlock{T, TFormatter}.LanePlan{TDirection}"/> moves a group: worked out once, read into its
    /// constants. Each vector is the bytes of one or two loads, each load two 16-byte windows of the source (one per
    /// lane of a 32-byte vector; one window for 16-byte vectors), shuffled within each lane.
    /// </summary>
    internal sealed class NinoLanes
    {
        /// <summary>A second load not taken.</summary>
        public const int None = int.MinValue;

        public int Width, Group, Vectors, Start, End;

        /// <summary>For each vector, load and lane, where its window starts from the group's first value.</summary>
        public readonly int[] Windows = new int[16];

        /// <summary>
        /// Where lane <paramref name="lane"/> of load <paramref name="load"/> of vector <paramref name="vector"/> starts:
        /// its slot in <see cref="Windows"/>, laid out as LanePlan's W0 to W15.
        /// </summary>
        private ref int Window(int vector, int load, int lane) => ref Windows[vector * 4 + load * 2 + lane];

        // For each vector and load, the shuffle index of each byte (0x80 for none); Arm64: both loads in the first.
        private byte[][] _masks;

        // For each vector, the source byte of each of its bytes from the group's first value (-1: padding).
        private int[][] _sources;

        /// <summary>
        /// The plan, or null when a group would take more than four vectors or a lane draws on bytes no two windows
        /// cover. <paramref name="bytes"/> matches the bytes of a value and its block.
        /// </summary>
        public static NinoLanes Solve(int width, NinoBlockBytes bytes, bool writes, bool tables, bool partial)
        {
            int size = bytes.Size, memorySize = bytes.MemorySize;
            int target = writes ? size : memorySize, source = writes ? memorySize : size;
            int group = width / Gcd(target, width);
            // A group whose vectors are exactly its values would take more than four (5-byte blocks: 32 values, five
            // 32-byte vectors): where nothing else moves several values at a time (partial), as many values as one to
            // four vectors hold instead, the rest of the last vector over the next value, which the next group writes
            // again. Whichever costs least a value, with three for the loop around a group (1,000 (byte, int) on an
            // AVX-512 Xeon with AVX-512 off took 690 ns a value at a time; AVX2, six values a 32-byte vector: 157 ns;
            // SSSE3, twelve values a group of four 16-byte vectors: 161 ns, three a group of one: 253 ns).
            if (group * target / width > 4)
            {
                if (!partial)
                    return null;
                // Arm64 tries the grid first and keeps it on a tie, where its windows load once into consecutive
                // registers (Gridded): two Waypoints a step (15-byte blocks of 24-byte values) from three loads on the
                // grid, at the same cost here as three loads anywhere, wrote 1,000 of them in 0.76 times the time on
                // GitHub's Neoverse-N2 runners (430 to 325 ns, .NET A/B run 41164356389).
                NinoLanes best = null;
                for (int vectors = 1; vectors <= 4; vectors++)
                for (int pass = 0; pass < (width == 32 || tables ? 2 : 1); pass++)
                {
                    bool grid = tables ? pass == 0 : pass == 1;
                    int values = vectors * width / target;
                    var each = values < 2 ? null : Plan(width, bytes, writes, tables, values, grid);
                    if (tables && grid && each?.Gridded() == false)
                        each = null;
                    if (each != null && (best == null || (each.Cost() + 3) * best.Group < (best.Cost() + 3) * values))
                        best = each;
                }

                return best;
            }

            int gridGroup = tables ? group / Gcd(group, 16 / Gcd(source, 16)) * (16 / Gcd(source, 16)) : group;
            var plan = tables && !writes
                ? Plan(width, bytes, writes, tables, gridGroup, grid: false) ??
                  Plan(width, bytes, writes, tables, group, grid: false)
                : Plan(width, bytes, writes, tables, group, grid: false);
            // AVX2 takes whichever costs less a value: windows anywhere (fewest loads when each lane's bytes follow)
            // or on a 16-byte grid (loads repeat across a group's vectors, and two lanes far apart come from them by
            // one lane permute rather than two more loads). Arm64 too, when the grid plan's windows fit one 64-byte
            // span: they load once each into consecutive registers and every lookup takes two of them, as Lanes16
            // does (loaded for each lookup, the loads two lookups share took register copies: writes 415 ns against
            // 310 for six loads anywhere; reads seven copies a group of four 16-byte values from 12-byte blocks).
            // Its reads' other plans take the grid plan's group, so that a step moves as many values (one value a step:
            // 1.13 us against 0.99 to 1.01 with three grid loads a group, 1,000 (int, long) into a new list).
            if (width == 32 || tables)
            {
                var gridded = Plan(width, bytes, writes, tables, gridGroup, grid: true);
                if (tables && gridded?.Gridded() == false)
                    gridded = null;
                if (plan == null || gridded != null && gridded.Cost() * plan.Group < plan.Cost() * gridGroup)
                    plan = gridded;
            }

            // Arm64 reads of a plan that moves fewer values a step than four vectors hold: also as many as they hold,
            // each vector one lookup of one register in a load of its own anywhere, so that the loop goes round once
            // for them (1,000 (byte, int) into a new list, two values a step before: 790 to 810 ns against 840 to 870,
            // one Neoverse N2 runner, quick runs). Not where a step moves as many already: three grid loads and two
            // 2-register lookups took less than four loads anywhere, which can straddle cache lines ((short, int):
            // 755 to 765 ns against 780 to 805; (int, long) on par).
            if (tables && !writes && plan != null)
            {
                int most = group * Math.Max(1, 4 * width / (group * target));
                var single = most > plan.Group
                    ? Plan(width, bytes, writes, tables, most, grid: false)
                    : null;
                if (single != null && single.OneLoad() && (single.Cost() + 3) * plan.Group < (plan.Cost() + 3) * most)
                    plan = single;
            }

            return plan;
        }

        private static NinoLanes Plan(int width, NinoBlockBytes bytes, bool writes, bool tables, int group, bool grid)
        {
            int size = bytes.Size, memorySize = bytes.MemorySize;
            int sourceSize = writes ? memorySize : size, targets = group * (writes ? size : memorySize);
            int vectors = (targets + width - 1) / width;
            if (vectors > 4)
                return null;
            var plan = new NinoLanes
                { Width = width, Group = group, Vectors = vectors, _masks = new byte[8][], _sources = new int[4][] };
            for (int v = 0; v < vectors; v++)
            {
                // The source byte of each byte of the vector (-1: padding, any value).
                var sources = new int[width];
                for (int b = 0; b < width; b++)
                    sources[b] = v * width + b < targets ? Source(bytes, writes, v * width + b) : -1;
                if (!Cover(sources, width, grid, out int[] first, out int[] second))
                    return null;
                plan._sources[v] = sources;
                var a = new byte[width];
                var c = new byte[width];
                for (int b = 0; b < width; b++)
                {
                    int lane = b / 16, s = sources[b];
                    a[b] = c[b] = 0x80;
                    if (s < 0)
                        continue;
                    if (s >= first[lane] && s < first[lane] + 16)
                        a[b] = (byte)(s - first[lane]);
                    else if (tables)
                        a[b] = (byte)(16 + s - second[lane]);
                    else
                        c[b] = (byte)(s - second[lane]);
                }

                plan._masks[v * 2] = a;
                plan._masks[v * 2 + 1] = c;
                for (int lane = 0; lane < 2; lane++)
                {
                    plan.Window(v, 0, lane) = lane < width / 16 ? first[lane] : first[0] + 16;
                    plan.Window(v, 1, lane) =
                        second == null ? None : lane < width / 16 ? second[lane] : second[0] + 16;
                }
            }

            // The bytes loaded, from the group's first value; loads from before it: groups start where they stay
            // inside the source.
            int low = 0, high = 0;
            foreach (int at in plan.Loads())
            {
                low = Math.Min(low, at);
                high = Math.Max(high, at + width);
            }

            plan.Start = (-low + sourceSize - 1) / sourceSize;
            plan.End = high;
            return plan;
        }

        /// <summary>
        /// Arm64: whether each vector looks up in one 16-byte window of the group's first 64 bytes on the 16-byte grid,
        /// or in two consecutive ones, so that the group's windows load once into consecutive registers (see Lanes16).
        /// </summary>
        public bool Gridded()
        {
            if (Width != 16)
                return false;
            for (int v = 0; v < Vectors; v++)
            {
                int first = Window(v, 0, 0), second = Window(v, 1, 0);
                if (first < 0 || first % 16 != 0 || first + (second == None ? 16 : 32) > 64 ||
                    second != None && second != first + 16)
                    return false;
            }

            return true;
        }

        /// <summary>Whether each vector draws on one load.</summary>
        private bool OneLoad()
        {
            for (int v = 0; v < Vectors; v++)
                if (Window(v, 1, 0) != None)
                    return false;
            return true;
        }

        /// <summary>For <see cref="Gridded"/> plans, the 16-byte windows a group loads (bit k: bytes 16k to 16k + 15).</summary>
        public int GridLoads()
        {
            int loads = 0;
            for (int v = 0; v < Vectors; v++)
            {
                loads |= 1 << (Window(v, 0, 0) / 16);
                if (Window(v, 1, 0) != None)
                    loads |= 1 << (Window(v, 1, 0) / 16);
            }

            return loads;
        }

        /// <summary>Whether the windows <paramref name="low"/> and <paramref name="high"/> of a 32-byte load are lanes of
        /// loads on the 32-byte grid, permuted (vperm2i128): both on the 16-byte grid, not one load.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Paired(int low, int high) => high != low + 16 && low % 16 == 0 && high % 16 == 0;

        /// <summary>Where each load of a group starts (a vector width each), repeated loads once.</summary>
        private List<int> Loads()
        {
            var loads = new List<int>();
            for (int v = 0; v < Vectors; v++)
            for (int load = 0; load < 2; load++)
            {
                int low = Window(v, load, 0), high = Window(v, load, 1);
                if (low == None)
                    continue;
                if (Width == 16 || high == low + 16)
                    Add(low);
                else if (Paired(low, high))
                {
                    Add(low & ~31);
                    Add(high & ~31);
                }
                else
                {
                    Add(low);
                    Add(high - 16);
                }
            }

            return loads;

            void Add(int at)
            {
                if (!loads.Contains(at))
                    loads.Add(at);
            }
        }

        // Loads cost more than the shuffles: a load issues at most twice a cycle on Zen 3 for 32 bytes, and twice when
        // it straddles cache lines; a blend issues on any vector port, a lane permute on one or two.
        private int Cost()
        {
            int cost = 3 * Loads().Count;
            for (int v = 0; v < Vectors; v++)
            for (int load = 0; load < 2; load++)
            {
                int low = Window(v, load, 0), high = Window(v, load, 1);
                if (low != None && high != low + 16)
                    cost += Paired(low, high) ? 3 : 2;
            }

            return cost;
        }

        /// <summary>The source byte of byte <paramref name="at"/> of a group's targets, or -1 for padding.</summary>
        private static int Source(NinoBlockBytes bytes, bool writes, int at) =>
            writes ? bytes.MemoryAt(at) : bytes.WireAt(at);

        /// <summary>
        /// One window for each lane, or two: the first (the lowest that covers the lane's lowest byte, so it reaches
        /// furthest) and a second for the rest; null second when one is enough for every lane. On the
        /// <paramref name="grid"/>, both start on a multiple of 16, the second where the first ends.
        /// </summary>
        private static bool Cover(int[] sources, int width, bool grid, out int[] first, out int[] second)
        {
            int lanes = width / 16;
            first = new int[lanes];
            second = null;
            var rest = new int[lanes];
            bool two = false;
            for (int lane = 0; lane < lanes; lane++)
            {
                int min = int.MaxValue, max = int.MinValue;
                for (int b = lane * 16; b < lane * 16 + 16; b++)
                    if (sources[b] >= 0)
                    {
                        min = Math.Min(min, sources[b]);
                        max = Math.Max(max, sources[b]);
                    }

                if (min == int.MaxValue)
                {
                    first[lane] = rest[lane] = lane == 0 ? 0 : first[0] + 16 * lane;
                    continue;
                }

                if (grid)
                {
                    first[lane] = min & ~15;
                    if (max >= first[lane] + 32)
                        return false;
                    rest[lane] = max >= first[lane] + 16 ? first[lane] + 16 : first[lane];
                    two |= rest[lane] != first[lane];
                    continue;
                }

                first[lane] = min;
                int restMin = int.MaxValue, restMax = int.MinValue;
                for (int b = lane * 16; b < lane * 16 + 16; b++)
                    if (sources[b] >= first[lane] + 16)
                    {
                        restMin = Math.Min(restMin, sources[b]);
                        restMax = Math.Max(restMax, sources[b]);
                    }

                if (restMin == int.MaxValue)
                {
                    rest[lane] = first[lane];
                    continue;
                }

                if (restMax - restMin >= 16)
                    return false;
                rest[lane] = restMax - 15;
                two = true;
            }

            // Windows a lane apart make one 32-byte load: align the first lane's windows to the second's where they
            // still cover it.
            if (lanes == 2)
            {
                Align(sources, first);
                if (two)
                    Align(sources, rest);
            }

            if (two)
                second = rest;
            return true;
        }

        /// <summary>Moves lane 0's window to 16 bytes before lane 1's when it still covers lane 0's bytes there.</summary>
        private static void Align(int[] sources, int[] windows)
        {
            int wanted = windows[1] - 16;
            if (wanted == windows[0])
                return;
            for (int b = 0; b < 16; b++)
            {
                int s = sources[b];
                if (s >= 0 && (s >= windows[0] && s < windows[0] + 16) && (s < wanted || s >= wanted + 16))
                    return;
            }

            windows[0] = wanted;
        }

        /// <summary>
        /// Reads: for each vector, 0xFE at the bytes that hold a bool of a block (at <paramref name="bools"/> of each
        /// <paramref name="size"/>-byte block), whose bits other than the lowest must all be clear; 0 elsewhere.
        /// </summary>
        public byte[] Bools(int vector, int[] bools, int size)
        {
            var mask = new byte[Width];
            if (vector >= Vectors)
                return mask;
            for (int b = 0; b < Width; b++)
            {
                int s = _sources[vector][b];
                if (s >= 0 && Array.IndexOf(bools, s % size) >= 0)
                    mask[b] = 0xFE;
            }

            return mask;
        }

        public Vector256<byte>[] Masks32()
        {
            var masks = new Vector256<byte>[8];
            for (int i = 0; i < 8; i++)
                if (_masks[i] != null && Width == 32)
                    masks[i] = Vector256.Create(_masks[i]);
            return masks;
        }

        public Vector128<byte>[] Masks16()
        {
            var masks = new Vector128<byte>[8];
            for (int i = 0; i < 8; i++)
                if (_masks[i] != null && Width == 16)
                    masks[i] = Vector128.Create(_masks[i]);
            return masks;
        }

        internal static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
    }
#endif
}
