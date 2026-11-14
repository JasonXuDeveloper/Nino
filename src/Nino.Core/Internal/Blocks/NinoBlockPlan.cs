using System;
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
    internal static partial class NinoBlockPlan<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
#if !NET8_0_OR_GREATER
        /// <summary>
        /// The formatter boxed once, for the calls of one value at a time: IL2CPP (its C++ shows it) and runtimes
        /// without RyuJIT's box removal box <c>default(TFormatter)</c> at every cast to an interface, a GC allocation
        /// per value, where RyuJIT calls the struct directly. One interface call per value remains; the vector and
        /// kernel paths take runs of values without it.
        /// </summary>
        public static readonly INinoBlockFormatter<T> Formatter = default(TFormatter) as INinoBlockFormatter<T>;

#endif
        /// <summary>
        /// <see cref="NinoBlockCollections.FixedSize{T, TFormatter}"/>, read from a field: one load where the type test
        /// and the size were each an inlined call, which the JIT counts against what else it inlines into the method.
        /// </summary>
        public static readonly int Size = default(TFormatter) is INinoBlockFormatter<T> formatter
            ? formatter.FixedSize
            : -1;

        /// <summary>Where the bytes of a block lie in a value's memory, as the formatter describes it (null: not
        /// known).</summary>
        private static readonly NinoBlockMap Description = NinoBlockCollections.FixedSize<T, TFormatter>() > 0
            ? ((INinoBlockFormatter<T>)default(TFormatter)).BlockMap()
            : null;

        /// <summary>
        /// <see cref="NinoBlockCollections.ReadSize{T, TFormatter}"/>: -1 in <see cref="NinoSettings.Untrusted"/> mode
        /// for values with fields to check, by their layout (<see cref="NinoLayout{T}.Checks"/>) or their items'
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
        private static readonly int[] Map = Description?.Runs(NinoBlockCollections.FixedSize<T, TFormatter>(), Unsafe.SizeOf<T>());

        /// <summary>
        /// Whether a read of values can reject the data after it has started writing them: a block with a
        /// <c>bool</c> (0 or 1), or one whose map failed (values read one at a time, their bools not all known).
        /// Such reads clear the values when they do (<see cref="NinoBlockCollections"/>.ReadRejecting).
        /// </summary>
        public static readonly bool Rejects = Size > 0 && (Map == null || Description.Bools != null);

        /// <summary>Whether a value's memory is its block: a collection of them is one copy.</summary>
        public static readonly bool IsMemory = ComputeIsMemory();

        /// <summary>A short identity-mapped prefix and one mapped final byte, calculated only on a single write.</summary>
        internal static class SingleWrite
        {
            public static readonly int LastByte = FindLastByte();

            private static int FindLastByte()
            {
                if (Size < 9 || Size > 17 || Map == null)
                    return -1;

                int last = -1;
                for (int i = 0; i < Map.Length; i += 3)
                {
                    int memory = Map[i], wire = Map[i + 1], length = Map[i + 2];
                    if (wire < Size - 1 && memory != wire)
                        return -1;
                    if (wire + length == Size)
                        last = memory + length - 1;
                }

                return last;
            }
        }

        private static bool ComputeIsMemory()
        {
            if (Map == null || Unsafe.SizeOf<T>() != NinoBlockCollections.FixedSize<T, TFormatter>())
                return false;
            for (int i = 0; i < Map.Length; i += 3)
                if (Map[i] != Map[i + 1])
                    return false;
            return true;
        }

        /// <summary>
        /// The block offsets of the <c>bool</c>s of a block that reads move without
        /// <see cref="INinoBlockFormatter{T}.ReadBlock"/> (as one copy where <see cref="IsMemory"/>, or by the vector
        /// paths), and so check first (<see cref="NinoBlockCollections"/>.ReadAll); null for none.
        /// </summary>
#if NET8_0_OR_GREATER
        public static readonly int[] Bools = Map != null ? Description.Bools : null;
#else
        public static readonly int[] Bools = IsMemory ? Description.Bools : null;
#endif

#if !NET8_0_OR_GREATER
        /// <summary>
        /// Whether values are moved by the registered kernels (<see cref="NinoKernels"/>, the netstandard2.1 build only):
        /// blocks not laid out as in memory, both at most 64 bytes.
        /// </summary>
        public static readonly bool Kernel = Map != null && !IsMemory && NinoBlockCollections.FixedSize<T, TFormatter>() <= 64 &&
                                             Unsafe.SizeOf<T>() <= 64;

        /// <summary>The tables of <see cref="NinoKernels.Permute"/>, built the first time a kernel is called for these
        /// values.</summary>
        internal static class KernelTables
        {
            /// <summary>For each block byte, the memory byte it comes from.</summary>
            public static readonly IntPtr ToWire =
                NinoKernelHook.Table(Map, NinoBlockCollections.FixedSize<T, TFormatter>(), Unsafe.SizeOf<T>(), toWire: true);

            /// <summary>For each memory byte, the block byte it comes from (0xFF: padding, cleared).</summary>
            public static readonly IntPtr ToMemory =
                NinoKernelHook.Table(Map, NinoBlockCollections.FixedSize<T, TFormatter>(), Unsafe.SizeOf<T>(), toWire: false);

            /// <summary>The block offsets of the bools, checked before a read (null: none).</summary>
            public static readonly int[] Bools = Kernel ? Description.Bools : null;
        }

#endif
#if NET8_0_OR_GREATER
        /// <summary>The bytes of a value and of its block matched, which every table below is read from.</summary>
        private static readonly NinoBlockBytes Bytes = Map != null
            ? new NinoBlockBytes(Map, NinoBlockCollections.FixedSize<T, TFormatter>(), Unsafe.SizeOf<T>())
            : null;

        /// <summary>
        /// Whether values are moved to and from their blocks by one 16-byte shuffle each: both fit in 16 bytes, and the
        /// CPU shuffles bytes by a variable mask, an index from 0x80 up giving a zero (SSSE3, Arm64, or WebAssembly
        /// SIMD on .NET 10).
        /// </summary>
        public static readonly bool Shuffles = Map != null && !IsMemory && Unsafe.SizeOf<T>() <= 16 &&
                                               NinoBlockCollections.FixedSize<T, TFormatter>() <= 16 &&
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
                                             NinoBlockCollections.FixedSize<T, TFormatter>() < Unsafe.SizeOf<T>() &&
                                             Avx512F.IsSupported && GroupMasks() is { } masks &&
                                             (Avx512Vbmi.IsSupported || Words(masks));

        /// <summary>
        /// Whether the 64-byte vectors of blocks move whole 4-byte words (vpermt2d): one micro-op at one a cycle on
        /// Intel, where vpermt2b takes three and issues every other cycle (1,000 12-byte blocks of 16-byte values on an
        /// AVX-512 Xeon: 121 ns with words, 150 to 190 with bytes, 123 for a copy of their memory).
        /// </summary>
        public static readonly bool GroupWords = Groups && Words(GroupMasks());

        /// <summary>Values per group: the fewest whose blocks fill whole vectors.</summary>
        public static readonly int Group = Groups ? 64 / NinoLanes.Gcd(NinoBlockCollections.FixedSize<T, TFormatter>(), 64) : 0;

        /// <summary>Vectors of blocks per group (1 to 4); one memory vector more is read.</summary>
        public static readonly int GroupVectors = Groups ? Group * NinoBlockCollections.FixedSize<T, TFormatter>() / 64 : 0;

        /// <summary>
        /// The permute indices of each vector of blocks in a group: bytes, or for <see cref="GroupWords"/> one int per
        /// word (0 to 31).
        /// </summary>
        public static readonly Vector512<byte> Group0 = GroupMask(0), Group1 = GroupMask(1), Group2 = GroupMask(2),
            Group3 = GroupMask(3);

        /// <summary>The last of <paramref name="count"/> values a group may start at: its reads stay inside the
        /// values.</summary>
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
            int size = NinoBlockCollections.FixedSize<T, TFormatter>();
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

        /// <summary>Values per 64-byte vector (at least 2).</summary>
        public static readonly int PerVector = 64 / Unsafe.SizeOf<T>();

        /// <summary>
        /// Whether several values at a time are moved by one 64-byte byte permute
        /// (<see cref="NinoBlockCollections"/>.Permute64): at least two fit, in memory and as blocks, with AVX-512
        /// VBMI; with Arm64's 4-register table lookups, eight or more. A 4-register lookup issues at half the rate of a
        /// 1-register one (Arm's Neoverse optimization guides), so the four per 64 bytes pay off over a shuffle and a
        /// store per value from eight values on.
        /// </summary>
        public static readonly bool Permutes = Shuffles && PerVector >= 2 &&
                                               (Avx512Vbmi.IsSupported ||
                                                AdvSimd.Arm64.IsSupported && Unsafe.SizeOf<T>() <= 8);

        /// <summary>
        /// Values written per two-source 64-byte byte permute (<see cref="NinoBlockCollections"/>.WritePairs), 0 when
        /// they are not: where AVX-512 VBMI's pays off (<see cref="NinoSimd.PairPermutes"/>), values of more than 16
        /// bytes (which neither <see cref="Shuffles"/> nor <see cref="Permutes"/> takes) whose blocks are not written
        /// in <see cref="Groups"/>, as many as 128 bytes of their memory and 64 of their blocks hold, at least three.
        /// Each step is two loads, one permute and one store (1,000 24-byte values, 15-byte blocks, four a step: on
        /// GitHub's Xeon Platinum 8573C runners 386 ns in lane groups of two, 252 ns in steps, MemoryPack's copy 234
        /// ns).
        /// </summary>
        public static readonly int PerPair = Map != null && !IsMemory && !Groups && !Shuffles &&
                                             NinoBlockCollections.FixedSize<T, TFormatter>() < Unsafe.SizeOf<T>() &&
                                             NinoSimd.PairPermutes &&
                                             Math.Min(128 / Unsafe.SizeOf<T>(), 64 / NinoBlockCollections.FixedSize<T, TFormatter>()) is
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
                (64 + NinoBlockCollections.FixedSize<T, TFormatter>() - 1) / NinoBlockCollections.FixedSize<T, TFormatter>());

        /// <summary>
        /// <see cref="ToWire"/> for <see cref="PerVector"/> values; on Arm64 each quarter's indices count from the
        /// first quarter of its table (<see cref="WireTables64"/>).
        /// </summary>
        public static readonly Vector512<byte> ToWire64 = Mask64(toWire: true);

        /// <summary><see cref="ToMemory"/> for <see cref="PerVector"/> values, as <see cref="ToWire64"/>.</summary>
        public static readonly Vector512<byte> ToMemory64 = Mask64(toWire: false);

        /// <summary>
        /// With AVX-512 VBMI, whose permute fills a padding byte of <see cref="ToMemory64"/> with some byte of the
        /// blocks: whether its values have padding, then cleared by <see cref="KeepMemory64"/> (0 at padding), so that
        /// a read writes every byte of the values. Arm64's lookups write 0 there by themselves.
        /// </summary>
        public static readonly bool PermutePads = Permutes && Avx512Vbmi.IsSupported &&
                                                  Array.IndexOf(Indices64(toWire: false), (byte)0x80) >= 0;

        /// <inheritdoc cref="PermutePads"/>
        public static readonly Vector512<byte> KeepMemory64 = PermutePads ? Keep64() : default;

        private static Vector512<byte> Keep64()
        {
            var keep = Indices64(toWire: false);
            for (int b = 0; b < 64; b++)
                keep[b] = keep[b] == 0x80 ? (byte)0 : (byte)0xFF;
            return Vector512.Create(keep);
        }

        /// <summary>
        /// Arm64's lookups for <see cref="ToWire64"/> (<see cref="NinoBlockCollections"/>.Permute64): for each 16 bytes
        /// written, only the quarters of the 64 bytes loaded that its bytes come from, as a lookup takes a micro-op per
        /// register on Neoverse cores (1,000 (byte, int), five bytes each and 40 a group, written as 64 bytes by four
        /// 4-register lookups, took twice as long as (short, int)), and only the quarters within the values' bytes are
        /// written. Bits 4q and 4q + 1: the first quarter of quarter q's table, 4q + 2 and 4q + 3: its quarters less
        /// one; 16 + q: whether quarter q is written; 20 + k: whether quarter k is loaded
        /// (<see cref="NinoBlockCollections.TableBits"/>, <see cref="NinoBlockCollections.Written"/>,
        /// <see cref="NinoBlockCollections.Loaded"/>). 0 with AVX-512 VBMI.
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
                    mask[b] -= (byte)(16 * (tables >> NinoBlockCollections.TableBits * (b / 16) & 3));
            return Vector512.Create(mask);
        }

        private static int Tables64(bool toWire)
        {
            if (!Permutes || Avx512Vbmi.IsSupported)
                return 0;
            var mask = Indices64(toWire);
            int written = PerVector * (toWire ? NinoBlockCollections.FixedSize<T, TFormatter>() : Unsafe.SizeOf<T>());
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
                tables |= (first | (last - first) << 2) << NinoBlockCollections.TableBits * q | 1 << NinoBlockCollections.Written + q;
                for (int k = first; k <= last; k++)
                    tables |= 1 << NinoBlockCollections.Loaded + k;
            }

            return tables;
        }

        /// <summary>The indices of <see cref="ToWire64"/> or <see cref="ToMemory64"/> into all 64 bytes.</summary>
        private static byte[] Indices64(bool toWire) => Bytes.Indices(toWire, PerVector, 64);

        /// <summary>
        /// pshufb, tbl or i8x16.swizzle: one instruction on each platform <see cref="Shuffles"/> is on for, each asked
        /// for by name behind its <c>IsSupported</c>. NativeAOT compiles x64 code for SSE2 and emits a later set's
        /// instructions only there (a check at run time, which the JIT folds to a constant):
        /// <c>Vector128.ShuffleNative</c> took its software form under NativeAOT while <see cref="Shuffles"/> was on
        /// (1,000 (int, long) written 200 times as slowly as under the JIT), and a fallback that is a call (that form,
        /// or a platform's intrinsic called where it does not exist) left the vectors around it on the stack.
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
    /// <summary>A constant number of bytes, as a type argument
    /// (<see cref="NinoBlockPlan{T, TFormatter}.ReadPlan{TPhase}"/>).</summary>
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

    /// <summary>A direction values move in, as a type argument
    /// (<see cref="NinoBlockPlan{T, TFormatter}.LanePlan{TDirection}"/>).</summary>
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
