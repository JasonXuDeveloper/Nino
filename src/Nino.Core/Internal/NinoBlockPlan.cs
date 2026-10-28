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
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// How values of <typeparamref name="T"/> map to their blocks (<see cref="INinoBlockFormatter{T}.BlockMap"/>),
    /// worked out once. The flags are JIT-time constants.
    /// </summary>
    internal static class NinoBlock<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
        /// <summary>
        /// <see cref="NinoBlock.Size{T, TFormatter}"/>, read from a field: one load where the type test and the size
        /// were each an inlined call, which the JIT counts against what else it inlines into the method.
        /// </summary>
        public static readonly int Size = default(TFormatter) is INinoBlockFormatter<T>
            ? ((INinoBlockFormatter<T>)default(TFormatter)).BlockSize
            : -1;

        /// <summary>Where the bytes of a block lie in a value's memory, as the formatter describes it (null: not known).</summary>
        private static readonly NinoBlockMap Description = NinoBlock.Size<T, TFormatter>() > 0
            ? ((INinoBlockFormatter<T>)default(TFormatter)).BlockMap()
            : null;

        /// <summary>
        /// <see cref="NinoBlock.ReadSize{T, TFormatter}"/>: -1 in <see cref="NinoSettings.Untrusted"/> mode for values
        /// with fields to check, by their layout (<see cref="NinoLayout{T}.Checks"/>) or their items'
        /// (<see cref="NinoBlockMap.Checked"/>).
        /// </summary>
        public static readonly int ReadSize =
            Size > 0 && NinoLimits.Untrusted && (NinoLayout<T>.Checks != null || Description?.Checked == true)
                ? -1
                : Size;

        /// <summary>
        /// (memory offset, block offset, size) triples covering every byte of a block once (see
        /// <see cref="NinoBlockMap"/>), or null: values are then moved one at a time.
        /// </summary>
        private static readonly int[] Map = Description?.Runs(NinoBlock.Size<T, TFormatter>(), Unsafe.SizeOf<T>());

        /// <summary>Whether a value's memory is its block: a collection of them is one copy.</summary>
        public static readonly bool IsMemory = ComputeIsMemory();

        private static bool ComputeIsMemory()
        {
            if (Map == null || Unsafe.SizeOf<T>() != NinoBlock.Size<T, TFormatter>())
                return false;
            for (int i = 0; i < Map.Length; i += 3)
                if (Map[i] != Map[i + 1])
                    return false;
            return true;
        }

        /// <summary>
        /// The block offsets of the <c>bool</c>s of a block that reads move without <see cref="INinoBlockFormatter{T}.ReadBlock"/>
        /// (as one copy where <see cref="IsMemory"/>, or by the vector paths), and so check first
        /// (<see cref="NinoBlock"/>.ReadAll); null for none.
        /// </summary>
#if NET8_0_OR_GREATER
        public static readonly int[] Bools = Map != null ? Description.Bools : null;
#else
        public static readonly int[] Bools = IsMemory ? Description.Bools : null;
#endif

#if !NET8_0_OR_GREATER
        /// <summary>
        /// Whether values are moved by the registered kernels (<see cref="NinoKernels"/>, netstandard builds only): blocks
        /// not laid out as in memory, both at most 64 bytes.
        /// </summary>
        public static readonly bool Kernel = Map != null && !IsMemory && NinoBlock.Size<T, TFormatter>() <= 64 &&
                                             Unsafe.SizeOf<T>() <= 64;

        /// <summary>The tables of <see cref="NinoKernels.Permute"/>, built the first time a kernel is called for these values.</summary>
        internal static class KernelTables
        {
            /// <summary>For each block byte, the memory byte it comes from.</summary>
            public static readonly IntPtr ToWire =
                NinoKernelHook.Table(Map, NinoBlock.Size<T, TFormatter>(), Unsafe.SizeOf<T>(), toWire: true);

            /// <summary>For each memory byte, the block byte it comes from (0xFF: padding, cleared).</summary>
            public static readonly IntPtr ToMemory =
                NinoKernelHook.Table(Map, NinoBlock.Size<T, TFormatter>(), Unsafe.SizeOf<T>(), toWire: false);

            /// <summary>The block offsets of the bools, checked before a read (null: none).</summary>
            public static readonly int[] Bools = Kernel ? Description.Bools : null;
        }

#endif
#if NET8_0_OR_GREATER
        /// <summary>The bytes of a value and of its block matched, which every table below is read from.</summary>
        private static readonly NinoBlockBytes Bytes = Map != null
            ? new NinoBlockBytes(Map, NinoBlock.Size<T, TFormatter>(), Unsafe.SizeOf<T>())
            : null;

        /// <summary>
        /// Whether values are moved to and from their blocks by one 16-byte shuffle each: both fit in 16 bytes, and the
        /// CPU shuffles bytes by a variable mask, an index from 0x80 up giving a zero (SSSE3, Arm64, or WebAssembly
        /// SIMD on .NET 10).
        /// </summary>
        public static readonly bool Shuffles = Map != null && !IsMemory && Unsafe.SizeOf<T>() <= 16 &&
                                               NinoBlock.Size<T, TFormatter>() <= 16 &&
                                               (Ssse3.IsSupported || AdvSimd.Arm64.IsSupported
#if NET10_0_OR_GREATER
                                                || PackedSimd.IsSupported
#endif
                                               );

        /// <summary>For each block byte, the memory byte it comes from (0x80: none, a zero).</summary>
        public static readonly Vector128<byte> ToWire = Mask(toWire: true);

        /// <summary>For each memory byte, the block byte it comes from (0x80: padding, a zero).</summary>
        public static readonly Vector128<byte> ToMemory = Mask(toWire: false);

        private static Vector128<byte> Mask(bool toWire)
        {
            return Shuffles ? Vector128.Create(Bytes.Indices(toWire, 1, 16)) : default;
        }

        /// <summary>
        /// The last of <paramref name="count"/> values a <paramref name="width"/>-byte vector may start at: its load
        /// and store stay inside both the values and their blocks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastVector(int count, int size, int width) =>
            count - Math.Max((width + Unsafe.SizeOf<T>() - 1) / Unsafe.SizeOf<T>(), (width + size - 1) / size);

        /// <summary>
        /// Whether values are written <see cref="Group"/> at a time as <see cref="GroupVectors"/> full vectors of
        /// blocks, each one two-source permute of two consecutive vectors of their memory: blocks are smaller than
        /// values (padding left out), so a value at a time stores a vector for fewer bytes, while a group stores each
        /// vector once (a quarter fewer stores than a copy of the values' memory for 12-byte blocks of 16-byte values).
        /// Only where each vector of blocks k draws on memory vectors k and k + 1, with up to four vectors a group.
        /// </summary>
        /// AVX-512 only (a two-source permute of 4-byte words, or with VBMI of bytes); other CPUs use
        /// <see cref="LanePlan{TDirection}"/>.
        public static readonly bool Groups = Map != null && !IsMemory &&
                                             NinoBlock.Size<T, TFormatter>() < Unsafe.SizeOf<T>() &&
                                             Avx512F.IsSupported && GroupMasks() is { } masks &&
                                             (Avx512Vbmi.IsSupported || Words(masks));

        /// <summary>
        /// Whether the 64-byte vectors of blocks move whole 4-byte words (vpermt2d): one micro-op at one a cycle on
        /// Intel, where vpermt2b takes three and issues every other cycle (1,000 12-byte blocks of 16-byte values on an
        /// AVX-512 Xeon: 121 ns with words, 150 to 190 with bytes, 123 for a copy of their memory).
        /// </summary>
        public static readonly bool GroupWords = Groups && Words(GroupMasks());

        /// <summary>Values per group: the fewest whose blocks fill whole vectors.</summary>
        public static readonly int Group = Groups ? 64 / NinoLanes.Gcd(NinoBlock.Size<T, TFormatter>(), 64) : 0;

        /// <summary>Vectors of blocks per group (1 to 4); one memory vector more is read.</summary>
        public static readonly int GroupVectors = Groups ? Group * NinoBlock.Size<T, TFormatter>() / 64 : 0;

        /// <summary>
        /// The permute indices of each vector of blocks in a group: bytes, or for <see cref="GroupWords"/> one int per
        /// word (0 to 31).
        /// </summary>
        public static readonly Vector512<byte> Group0 = GroupMask(0), Group1 = GroupMask(1), Group2 = GroupMask(2),
            Group3 = GroupMask(3);

        /// <summary>The last of <paramref name="count"/> values a group may start at: its reads stay inside the values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastGroup(int count) =>
            count - Math.Max(Group, ((GroupVectors + 1) * 64 + Unsafe.SizeOf<T>() - 1) / Unsafe.SizeOf<T>());

        private static Vector512<byte> GroupMask(int vector)
        {
            if (!Groups || vector >= GroupVectors)
                return default;
            var masks = GroupMasks();
            var mask = new byte[64];
            if (GroupWords)
            {
                // Words: the byte index of a word's first byte, over 4.
                for (int word = 0; word < 16; word++)
                    Unsafe.WriteUnaligned(ref mask[word * 4], masks[vector * 64 + word * 4] / 4);
            }
            else
            {
                Array.Copy(masks, vector * 64, mask, 0, 64);
            }

            return Vector512.Create(mask);
        }

        /// <summary>
        /// For each byte of each 64-byte vector of blocks in a group, the byte of
        /// memory vectors k and k + 1 it comes from; null when a group would take more than four vectors or a vector of
        /// blocks draws on other memory vectors.
        /// </summary>
        private static byte[] GroupMasks()
        {
            const int width = 64;
            int size = NinoBlock.Size<T, TFormatter>();
            int group = width / NinoLanes.Gcd(size, width), vectors = group * size / width;
            if (vectors > 4)
                return null;
            var masks = new byte[vectors * width];
            for (int wire = 0; wire < vectors * width; wire++)
            {
                int memory = Bytes.MemoryAt(wire);
                // Every block byte belongs to a field; vector k reads memory vectors k and k + 1.
                int index = memory - wire / width * width;
                if (memory < 0 || index < 0 || index >= 2 * width)
                    return null;
                masks[wire] = (byte)index;
            }

            return masks;
        }

        /// <summary>Whether each 4-byte word of blocks is one whole, aligned word of memory.</summary>
        private static bool Words(byte[] masks)
        {
            if (masks == null)
                return false;
            for (int word = 0; word < masks.Length; word += 4)
                if (masks[word] % 4 != 0 || masks[word + 1] != masks[word] + 1 || masks[word + 2] != masks[word] + 2 ||
                    masks[word + 3] != masks[word] + 3)
                    return false;
            return true;
        }

        /// <summary>
        /// Values read a group at a time (<see cref="Groups"/> the other way, with AVX-512): each 64-byte vector of
        /// memory is one two-source permute of the two vectors of blocks it draws on, stored at an aligned address.
        /// Its vectors start <typeparamref name="TPhase"/> bytes into a value: arrays are only 8-byte aligned, so
        /// 16-byte values start on a 64-byte boundary in half of them and 8 bytes past one in the other half, where
        /// 64-byte stores at value boundaries would each straddle two cache lines (1,000 12-byte
        /// blocks into a list of 16-byte values on an AVX-512 Xeon: 260 to 275 ns, against 175 to 180 aligned).
        /// </summary>
        internal static class ReadPlan<TPhase> where TPhase : struct, INinoPhase
        {
            // Before the constants read from it (static fields initialize in order).
            private static readonly GroupReads Plan = new GroupReads(default(TPhase).Bytes);

            /// <summary>Whether values are read this way.</summary>
            public static readonly bool Enabled = Plan.Masks != null;

            /// <summary>Whether it moves whole 4-byte words (vpermt2d), else bytes (VBMI).</summary>
            public static readonly bool Words = Plan.Words;

            /// <summary>Memory vectors per group (<see cref="Group"/> values), and vectors of blocks loaded for them.</summary>
            public static readonly int Vectors = Plan.Vectors, Loads = Plan.Loads;

            /// <summary>For memory vector k of a group, the vector of blocks it draws on (with the next).</summary>
            public static readonly int Base0 = Plan.Base(0), Base1 = Plan.Base(1), Base2 = Plan.Base(2),
                Base3 = Plan.Base(3);

            /// <summary>The permute indices of memory vector k (one int per word for <see cref="Words"/>).</summary>
            public static readonly Vector512<byte>[] Masks = { Plan.Mask(0), Plan.Mask(1), Plan.Mask(2), Plan.Mask(3) };

            /// <summary>
            /// The last of <paramref name="count"/> values a group may start at: its loads stay inside the blocks,
            /// and its stores (past the group by the phase) inside the values.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Last(int count) =>
                count - Math.Max(Group + (default(TPhase).Bytes > 0 ? 1 : 0),
                    (Loads * 64 + NinoBlock.Size<T, TFormatter>() - 1) / NinoBlock.Size<T, TFormatter>());
        }

        /// <summary>How <see cref="ReadPlan{TPhase}"/> moves a group: worked out once, read into its constants.</summary>
        private sealed class GroupReads
        {
            public readonly byte[] Masks;
            public readonly int[] Bases = new int[4];
            public readonly bool Words;
            public readonly int Vectors, Loads;

            public GroupReads(int phase)
            {
                int size = NinoBlock.Size<T, TFormatter>(), memorySize = Unsafe.SizeOf<T>();
                if (Map == null || IsMemory || size >= memorySize || !Avx512F.IsSupported)
                    return;
                int group = 64 / NinoLanes.Gcd(size, 64), vectors = group * memorySize / 64;
                // A phase only where values can start that far past a boundary (8-byte aligned arrays).
                if (group * memorySize % 64 != 0 || vectors > 4 || phase >= NinoLanes.Gcd(memorySize, 64))
                    return;
                var masks = new byte[vectors * 64];
                for (int k = 0; k < vectors; k++)
                {
                    // The block byte of each memory byte (-1: padding), and the vector of blocks the lowest is in.
                    var wires = new int[64];
                    int low = int.MaxValue;
                    for (int b = 0; b < 64; b++)
                    {
                        int wire = Bytes.WireAt(phase + k * 64 + b);
                        wires[b] = wire;
                        if (wire >= 0)
                            low = Math.Min(low, wire);
                    }

                    int first = low == int.MaxValue ? 0 : low / 64, high = 0;
                    Bases[k] = first;
                    for (int b = 0; b < 64; b++)
                    {
                        int index = wires[b] < 0 ? 0xFF : wires[b] - first * 64;
                        if (wires[b] >= 0 && index >= 128)
                            return;
                        if (wires[b] >= 0)
                            high = Math.Max(high, index);
                        masks[k * 64 + b] = (byte)index;
                    }

                    // The next vector of blocks only when this one reaches into it.
                    Loads = Math.Max(Loads, first + (high >= 64 ? 2 : 1));
                }

                // Words: each memory word padding only, or one whole aligned word of blocks.
                bool words = true;
                for (int w = 0; w < masks.Length && words; w += 4)
                {
                    bool padding = masks[w] == 0xFF && masks[w + 1] == 0xFF && masks[w + 2] == 0xFF &&
                                   masks[w + 3] == 0xFF;
                    words = padding || (masks[w] % 4 == 0 && masks[w + 1] == masks[w] + 1 &&
                                        masks[w + 2] == masks[w] + 2 && masks[w + 3] == masks[w] + 3);
                }

                if (!words && !Avx512Vbmi.IsSupported)
                    return;
                Masks = masks;
                Words = words;
                Vectors = vectors;
            }

            public int Base(int k) => Masks != null && k < Vectors ? Bases[k] : 0;

            public Vector512<byte> Mask(int k)
            {
                if (Masks == null || k >= Vectors)
                    return default;
                var mask = new byte[64];
                for (int b = 0; b < 64; b++)
                {
                    int index = Masks[k * 64 + b] == 0xFF ? 0 : Masks[k * 64 + b];
                    if (!Words)
                        mask[b] = (byte)index;
                    else if (b % 4 == 0)
                        Unsafe.WriteUnaligned(ref mask[b], index / 4);
                }

                return Vector512.Create(mask);
            }
        }

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

            /// <summary>Whether a <see cref="Gridded"/> group loads bytes 16 <paramref name="k"/> to 16 <paramref name="k"/> + 15.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Loads(int k) => (GridLoads & (1 << k)) != 0;

            // The windows of each vector (Window); NinoLanes.None for a second load not taken.
            private static readonly int W0 = Get(0), W1 = Get(1), W2 = Get(2), W3 = Get(3), W4 = Get(4), W5 = Get(5),
                W6 = Get(6), W7 = Get(7), W8 = Get(8), W9 = Get(9), W10 = Get(10), W11 = Get(11), W12 = Get(12),
                W13 = Get(13), W14 = Get(14), W15 = Get(15);

            /// <summary>The shuffle indices of each vector's first and second load (Arm64: of both, in the first).</summary>
            public static readonly Vector256<byte>[] Masks32 = Plan?.Masks32() ?? new Vector256<byte>[8];

            /// <inheritdoc cref="Masks32"/>
            public static readonly Vector128<byte>[] Masks16 = Plan?.Masks16() ?? new Vector128<byte>[8];

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
            /// (<paramref name="from"/> bytes a value) and its stores inside the destination (<paramref name="to"/> bytes a value).
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
                if (Map == null || IsMemory || NinoBlock.Size<T, TFormatter>() > Unsafe.SizeOf<T>())
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

        /// <summary>Values per 64-byte vector (at least 2).</summary>
        public static readonly int PerVector = 64 / Unsafe.SizeOf<T>();

        /// <summary>
        /// Whether several values at a time are moved by one 64-byte byte permute (<see cref="NinoBlock"/>.Permute64):
        /// at least two fit, in memory and as blocks, with AVX-512 VBMI; with Arm64's 4-register table lookups, eight
        /// or more. A 4-register lookup issues at half the rate of a 1-register one (Arm's Neoverse optimization
        /// guides), so the four per 64 bytes pay off over a shuffle and a store per value from eight values on.
        /// </summary>
        public static readonly bool Permutes = Shuffles && PerVector >= 2 &&
                                               (Avx512Vbmi.IsSupported ||
                                                AdvSimd.Arm64.IsSupported && Unsafe.SizeOf<T>() <= 8);

        /// <summary>
        /// Values written per two-source 64-byte byte permute (<see cref="NinoBlock"/>.WritePairs), 0 when they are not:
        /// with AVX-512 VBMI, values of more than 16 bytes (which neither <see cref="Shuffles"/> nor
        /// <see cref="Permutes"/> takes) whose blocks are not written in <see cref="Groups"/>, as many as 128 bytes of
        /// their memory and 64 of their blocks hold, at least three. Each step is two loads, one permute and one store
        /// (1,000 24-byte values, 15-byte blocks, four a step: on GitHub's Xeon Platinum 8573C runners they took 425 ns
        /// in lane groups of two, MemoryPack's copy of their memory 249 ns).
        /// </summary>
        public static readonly int PerPair = Map != null && !IsMemory && !Groups && !Shuffles &&
                                             NinoBlock.Size<T, TFormatter>() < Unsafe.SizeOf<T>() &&
                                             Avx512Vbmi.IsSupported &&
                                             Math.Min(128 / Unsafe.SizeOf<T>(), 64 / NinoBlock.Size<T, TFormatter>()) is
                                                 var pair && pair >= 3
            ? pair
            : 0;

        /// <summary>The indices of <see cref="PerPair"/> values' blocks into 128 bytes of their memory.</summary>
        public static readonly Vector512<byte> ToWire128 =
            PerPair > 0 ? Vector512.Create(Bytes.Indices(toWire: true, PerPair, 64)) : default;

        /// <summary>
        /// The last of <paramref name="count"/> values a <see cref="PerPair"/> step may start at: its 128 bytes of
        /// loads stay inside the values, its 64-byte store inside their blocks.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastPair(int count) =>
            count - Math.Max((128 + Unsafe.SizeOf<T>() - 1) / Unsafe.SizeOf<T>(),
                (64 + NinoBlock.Size<T, TFormatter>() - 1) / NinoBlock.Size<T, TFormatter>());

        /// <summary>
        /// <see cref="ToWire"/> for <see cref="PerVector"/> values; on Arm64 each quarter's indices count from the first
        /// quarter of its table (<see cref="WireTables64"/>).
        /// </summary>
        public static readonly Vector512<byte> ToWire64 = Mask64(toWire: true);

        /// <summary><see cref="ToMemory"/> for <see cref="PerVector"/> values, as <see cref="ToWire64"/>.</summary>
        public static readonly Vector512<byte> ToMemory64 = Mask64(toWire: false);

        /// <summary>
        /// Arm64's lookups for <see cref="ToWire64"/> (<see cref="NinoBlock"/>.Permute64): for each 16 bytes written,
        /// only the quarters of the 64 bytes loaded that its bytes come from, as a lookup takes a micro-op per register
        /// on Neoverse cores (1,000 (byte, int), five bytes each and 40 a group, written as 64 bytes by four 4-register
        /// lookups, took twice as long as (short, int)), and only the quarters within the values' bytes are written.
        /// Bits 4q and 4q + 1: the first quarter of quarter q's table, 4q + 2 and 4q + 3: its quarters less one;
        /// 16 + q: whether quarter q is written; 20 + k: whether quarter k is loaded (<see cref="NinoBlock.TableBits"/>,
        /// <see cref="NinoBlock.Written"/>, <see cref="NinoBlock.Loaded"/>). 0 with AVX-512 VBMI.
        /// </summary>
        public static readonly int WireTables64 = Tables64(toWire: true);

        /// <summary><see cref="WireTables64"/> for <see cref="ToMemory64"/>.</summary>
        public static readonly int MemoryTables64 = Tables64(toWire: false);

        private static Vector512<byte> Mask64(bool toWire)
        {
            if (!Permutes)
                return default;
            var mask = Indices64(toWire);
            int tables = Tables64(toWire);
            for (int b = 0; b < 64; b++)
                if (mask[b] < 64)
                    mask[b] -= (byte)(16 * (tables >> NinoBlock.TableBits * (b / 16) & 3));
            return Vector512.Create(mask);
        }

        private static int Tables64(bool toWire)
        {
            if (!Permutes || Avx512Vbmi.IsSupported)
                return 0;
            var mask = Indices64(toWire);
            int written = PerVector * (toWire ? NinoBlock.Size<T, TFormatter>() : Unsafe.SizeOf<T>());
            int tables = 0;
            for (int q = 0; q * 16 < written; q++)
            {
                int first = 3, last = 0;
                for (int b = q * 16; b < q * 16 + 16; b++)
                    if (mask[b] < 64)
                    {
                        first = Math.Min(first, mask[b] / 16);
                        last = Math.Max(last, mask[b] / 16);
                    }

                if (first > last)
                    first = last = 0;
                tables |= (first | (last - first) << 2) << NinoBlock.TableBits * q | 1 << NinoBlock.Written + q;
                for (int k = first; k <= last; k++)
                    tables |= 1 << NinoBlock.Loaded + k;
            }

            return tables;
        }

        /// <summary>The indices of <see cref="ToWire64"/> or <see cref="ToMemory64"/> into all 64 bytes.</summary>
        private static byte[] Indices64(bool toWire) => Bytes.Indices(toWire, PerVector, 64);

        /// <summary>
        /// pshufb, tbl or i8x16.swizzle: one instruction on each platform <see cref="Shuffles"/> is on for, each asked
        /// for by name behind its <c>IsSupported</c>. NativeAOT compiles x64 code for SSE2 and emits a later set's
        /// instructions only there (a check at run time, which the JIT folds to a constant): <c>Vector128.ShuffleNative</c>
        /// took its software form under NativeAOT while <see cref="Shuffles"/> was on (1,000 (int, long) written 200
        /// times as slowly as under the JIT), and a fallback that is a call (that form, or a platform's intrinsic
        /// called where it does not exist) left the vectors around it on the stack.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> Shuffle(Vector128<byte> value, Vector128<byte> mask)
        {
            if (Ssse3.IsSupported)
                return Ssse3.Shuffle(value, mask);
            if (AdvSimd.Arm64.IsSupported)
                return AdvSimd.Arm64.VectorTableLookup(value, mask);
#if NET10_0_OR_GREATER
            if (PackedSimd.IsSupported)
                return PackedSimd.Swizzle(value, mask);
#endif
            // Never reached: Shuffles and the 16-byte lanes are off without one of the above.
            return value;
        }
#endif
    }

#if NET8_0_OR_GREATER
    /// <summary>A constant number of bytes, as a type argument (<see cref="NinoBlock{T, TFormatter}.ReadPlan{TPhase}"/>).</summary>
    internal interface INinoPhase
    {
        int Bytes { get; }
    }

    internal struct NinoPhase0 : INinoPhase
    {
        public int Bytes => 0;
    }

    internal struct NinoPhase8 : INinoPhase
    {
        public int Bytes => 8;
    }

    /// <summary>A direction values move in, as a type argument (<see cref="NinoBlock{T, TFormatter}.LanePlan{TDirection}"/>).</summary>
    internal interface INinoDirection
    {
    }

    /// <summary>From their blocks to memory.</summary>
    internal struct NinoReads : INinoDirection
    {
    }

    /// <summary>From memory to their blocks.</summary>
    internal struct NinoWrites : INinoDirection
    {
    }
#endif
}
