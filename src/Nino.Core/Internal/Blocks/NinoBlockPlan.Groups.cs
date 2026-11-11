#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nino.Core.Internal
{
    // The AVX-512 group tables; the flags they follow (Groups, GroupWords) are in NinoBlockPlan.cs.
    internal static partial class NinoBlockPlan<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
        /// <summary>
        /// The write groups of <see cref="GroupWords"/> values loaded from 64-byte boundaries
        /// (<see cref="NinoBlockCollections"/>.WriteAligned, Ice Lake): for each place of the group's first value in a
        /// 64-byte line (4-byte steps) and each vector of blocks, the aligned memory vector it starts at and one or two
        /// word permutes. A vector of blocks draws on 128 bytes of memory at most (<see cref="GroupMasks"/>), which lie
        /// in two or three aligned vectors: from two, one permute (Inner); from three, Inner of the first two, then
        /// Outer keeps its words or takes one of the third. Its own class, so that only CPUs that take the path build
        /// the tables.
        /// </summary>
        internal static class Aligned
        {
            /// <summary>Per alignment and vector of blocks: Inner, then Outer (default when two vectors do).</summary>
            public static readonly Vector512<byte>[] Tables = Build(out Spans);

            /// <summary>
            /// Per alignment and vector of blocks: the byte offset of its first aligned memory vector from the group's
            /// aligned start, plus 1 when it draws on three.
            /// </summary>
            public static readonly int[] Spans;

            /// <summary>Bytes from a group's aligned start that its loads reach, at most (two vectors past the
            /// blocks').</summary>
            public static readonly int Reach = (GroupVectors + 2) * 64;

            private static Vector512<byte>[] Build(out int[] spans)
            {
                spans = null;
                if (!GroupWords || Group * Unsafe.SizeOf<T>() % 64 != 0)
                    return null;
                var tables = new Vector512<byte>[16 * 4 * 2];
                spans = new int[16 * 4];
                var inner = new int[16];
                var outer = new int[16];
                for (int shift = 0; shift < 64; shift += 4)
                {
                    for (int vector = 0; vector < GroupVectors; vector++)
                    {
                        int low = int.MaxValue, high = 0;
                        for (int word = 0; word < 16; word++)
                        {
                            int at = shift + Bytes.MemoryAt(vector * 64 + word * 4);
                            low = Math.Min(low, at / 64);
                            high = Math.Max(high, at / 64);
                        }

                        if (high - low > 2)
                            return null;
                        for (int word = 0; word < 16; word++)
                        {
                            int at = shift + Bytes.MemoryAt(vector * 64 + word * 4);
                            int from = at / 64 - low, index = at % 64 / 4;
                            inner[word] = from < 2 ? from * 16 + index : 0;
                            outer[word] = from < 2 ? word : 16 + index;
                        }

                        int slot = shift / 4 * 4 + vector;
                        tables[slot * 2] = Vector512.Create(inner).AsByte();
                        tables[slot * 2 + 1] = high - low == 2 ? Vector512.Create(outer).AsByte() : default;
                        spans[slot] = low * 64 + (high - low == 2 ? 1 : 0);
                    }
                }

                return tables;
            }
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
            private static readonly GroupReads Plan = new(default(TPhase).Bytes);

            /// <summary>Whether values are read this way.</summary>
            public static readonly bool Enabled = Plan.Masks != null;

            /// <summary>Whether it moves whole 4-byte words (vpermt2d), else bytes (VBMI).</summary>
            public static readonly bool Words = Plan.Words;

            /// <summary>
            /// Values per group: as many as fill whole vectors of blocks
            /// (<see cref="NinoBlockPlan{T, TFormatter}.Group"/>), or, for values over 16 bytes where those would take
            /// more than four vectors of memory, as many as fill whole vectors of memory (8 24-byte values, 15 bytes
            /// each on the wire, in three).
            /// </summary>
            public static readonly int Group = Plan.Group;

            /// <summary>Memory vectors per group (<see cref="Group"/> values), and vectors of blocks loaded for
            /// them.</summary>
            public static readonly int Vectors = Plan.Vectors, Loads = Plan.Loads;

            /// <summary>For memory vector k of a group, the vector of blocks it draws on (with the next).</summary>
            public static readonly int Base0 = Plan.Base(0), Base1 = Plan.Base(1), Base2 = Plan.Base(2),
                Base3 = Plan.Base(3);

            /// <summary>The permute indices of memory vector k (one int per word for <see cref="Words"/>).</summary>
            public static readonly Vector512<byte>[] Masks = { Plan.Mask(0), Plan.Mask(1), Plan.Mask(2), Plan.Mask(3) };

            /// <summary>
            /// Whether a memory vector holds padding, which a permute fills with some byte of the blocks (it reads
            /// only an index's low bits): then each is masked by <see cref="Keeps"/> (0 at padding), so that a read
            /// writes every byte of the values, padding as 0, and an array of them can be allocated uninitialized.
            /// </summary>
            public static readonly bool Pads = Plan.Pads;

            /// <summary>The masks of <see cref="Pads"/>, one per memory vector.</summary>
            public static readonly Vector512<byte>[] Keeps = { Plan.Keep(0), Plan.Keep(1), Plan.Keep(2), Plan.Keep(3) };

            /// <summary>
            /// Whether the groups check the bools of the blocks they load (<see cref="Bools"/>) in the same pass, in
            /// place of the strided pass before them: values over 16 bytes, whose values after the groups are read one
            /// at a time, through ReadBlock, which checks them.
            /// </summary>
            public static readonly bool Checks = Plan.Masks != null && Bools != null && Unsafe.SizeOf<T>() > 16;

            /// <summary>
            /// For each vector of blocks loaded, 0xFE at the bools of the blocks in it (any block: those past the group
            /// are in the data too), whose bits other than the lowest must all be clear.
            /// </summary>
            public static readonly Vector512<byte>[] BoolMasks = Checks
                ? new[] { Plan.Bools(0), Plan.Bools(1), Plan.Bools(2), Plan.Bools(3), Plan.Bools(4) }
                : new Vector512<byte>[5];

            /// <summary>
            /// The last of <paramref name="count"/> values a group may start at: its loads stay inside the blocks,
            /// and its stores (past the group by the phase) inside the values.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static int Last(int count) =>
                count - Math.Max(Group + (default(TPhase).Bytes > 0 ? 1 : 0),
                    (Loads * 64 + NinoBlockCollections.FixedSize<T, TFormatter>() - 1) / NinoBlockCollections.FixedSize<T, TFormatter>());
        }

        /// <summary>How <see cref="ReadPlan{TPhase}"/> moves a group: worked out once, read into its
        /// constants.</summary>
        private sealed class GroupReads
        {
            public readonly byte[] Masks;
            public readonly int[] Bases = new int[4];
            public readonly bool Words;
            public readonly int Vectors, Loads, Group;

            public GroupReads(int phase)
            {
                int size = NinoBlockCollections.FixedSize<T, TFormatter>(), memorySize = Unsafe.SizeOf<T>();
                if (Map == null || IsMemory || size >= memorySize || !Avx512F.IsSupported)
                    return;
                int group = 64 / NinoLanes.Gcd(size, 64), vectors = group * memorySize / 64;
                if ((group * memorySize % 64 != 0 || vectors > 4) && memorySize > 16)
                {
                    group = 64 / NinoLanes.Gcd(memorySize, 64);
                    vectors = group * memorySize / 64;
                }

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

                if (!words && !Avx512Vbmi.IsSupported || Loads > 5)
                    return;
                Group = group;
                Masks = masks;
                Words = words;
                Vectors = vectors;
            }

            public int Base(int k) => Masks != null && k < Vectors ? Bases[k] : 0;

            public bool Pads => Masks != null && Array.IndexOf(Masks, (byte)0xFF) >= 0;

            public Vector512<byte> Keep(int k)
            {
                if (Masks == null || k >= Vectors)
                    return default;
                var keep = new byte[64];
                for (int b = 0; b < 64; b++)
                    keep[b] = Masks[k * 64 + b] == 0xFF ? (byte)0 : (byte)0xFF;
                return Vector512.Create(keep);
            }

            public Vector512<byte> Bools(int load)
            {
                if (load >= Loads)
                    return default;
                int size = NinoBlockCollections.FixedSize<T, TFormatter>();
                var bools = new byte[64];
                for (int b = 0; b < 64; b++)
                    if (Array.IndexOf(NinoBlockPlan<T, TFormatter>.Bools, (load * 64 + b) % size) >= 0)
                        bools[b] = 0xFE;
                return Vector512.Create(bools);
            }

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
    }
}
#endif
