using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
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
    /// <summary>Collections of values written by an <see cref="INinoBlockFormatter{T}"/>.</summary>
    internal static class NinoBlock
    {
#if NET8_0_OR_GREATER
        /// <summary>
        /// The bits of an Arm64 lookup plan (<see cref="NinoBlock{T, TFormatter}.WireTables64"/>): from bit
        /// <see cref="TableBits"/> times q, quarter q's table (two bits its first quarter, two its quarters less one);
        /// bit <see cref="Written"/> + q, whether quarter q is stored; bit <see cref="Loaded"/> + k, whether quarter
        /// k is loaded.
        /// </summary>
        internal const int TableBits = 4, Written = 16, Loaded = 20;

#endif
        /// <summary>
        /// The <see cref="INinoBlockFormatter{T}.BlockSize"/> of <typeparamref name="TFormatter"/>, -1 when it does not
        /// implement it. A JIT-time constant; -1 without a lookup for references (shared code).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Size<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() ? NinoBlock<T, TFormatter>.Size : -1;

        /// <summary>
        /// <see cref="Size{T, TFormatter}"/> for reads: -1 in <see cref="NinoSettings.Untrusted"/> mode for values with
        /// fields to check (<see cref="NinoLayout{T}.Checks"/>), which are then read one by one through their
        /// formatter, and checked there.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() ? NinoBlock<T, TFormatter>.ReadSize : -1;

        /// <summary>Writes <paramref name="count"/> values at <paramref name="destination"/>, <paramref name="size"/> bytes apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteAll<T, TFormatter>(ref byte destination, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlock<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.Copy(ref destination, ref Unsafe.As<T, byte>(ref first), (uint)(count * size));
                return;
            }

            WriteEach<T, TFormatter>(ref destination, ref first, count);
        }

        /// <summary>
        /// <see cref="WriteAll{T, TFormatter}"/> of values whose blocks are not laid out as in memory. Out of line: its
        /// vector paths take many locals, and inlined into a collection formatter (also where the elements are copied
        /// whole and it is never reached) they left the reader or writer of the method around it on the stack.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void WriteEach<T, TFormatter>(ref byte destination, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            // The block size from its field rather than a parameter (here and in the other methods out of line): a
            // JIT-time constant, so the divisions by it (LastVector) and the multiplications are shifts and adds.
            int size = NinoBlock<T, TFormatter>.Size;
            int i = 0;
#if NET8_0_OR_GREATER
            // A group of values at a time, as full vectors of blocks: one two-source permute and one store each, none
            // overlapping (Groups).
            if (NinoBlock<T, TFormatter>.Groups && NinoBlock<T, TFormatter>.LastGroup(count) >= NinoBlock<T, TFormatter>.Group)
                i = WriteGroups<T, TFormatter>(ref destination, ref first, count);
            // Without AVX-512, a group of values at a time, each 16-byte lane shuffled from one or two loads (Lanes).
            else if (NinoBlock<T, TFormatter>.LanePlan<NinoWrites>.Worth(count))
                i = Lanes<T, TFormatter, NinoWrites>(ref Unsafe.As<T, byte>(ref first), ref destination, count);

            // Several values as 64 bytes, their bytes moved to their places in the block by one 64-byte byte permute
            // (Permute64); the tail written is overwritten by the next one.
            if (NinoBlock<T, TFormatter>.Permutes)
            {
                var toWire = NinoBlock<T, TFormatter>.ToWire64;
                int tables = NinoBlock<T, TFormatter>.WireTables64;
                int step = NinoBlock<T, TFormatter>.PerVector;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 64);
                ref byte source = ref Unsafe.As<T, byte>(ref first);
                i = Permute64(ref source, Unsafe.SizeOf<T>(), ref destination, size, i, last, step, toWire, tables);
                // The values after the last vector by one more, over values already written (the same bytes), rather
                // than one at a time.
                if (i < last + step && last >= 0)
                {
                    Permute64(ref source, (nint)last * Unsafe.SizeOf<T>(), ref destination, (nint)last * size,
                        toWire, tables);
                    i = last + step;
                }
            }

            // Values of more than 16 bytes with AVX-512 VBMI: several at a time from 128 bytes of their memory by one
            // two-source permute; the tail written is overwritten by the next one, as with Permute64.
            if (Avx512Vbmi.IsSupported && NinoBlock<T, TFormatter>.PerPair > 0)
                i = WritePairs<T, TFormatter>(ref Unsafe.As<T, byte>(ref first), ref destination, i, count);

            // Then each value as one 16-byte vector, by one shuffle. The last values, whose vector would reach past
            // the end of either side, one item at a time.
            if (NinoBlock<T, TFormatter>.Shuffles)
            {
                var toWire = NinoBlock<T, TFormatter>.ToWire;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 16);
                if (i <= last)
                {
                    // Cursors bumped by the sizes, and a count down to zero: no multiplications per value.
                    ref byte from = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref first, i));
                    ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
                    int n = last - i + 1;
                    do
                    {
                        NinoBlock<T, TFormatter>.Shuffle(Vector128.LoadUnsafe(ref from), toWire).StoreUnsafe(ref to);
                        from = ref Unsafe.Add(ref from, Unsafe.SizeOf<T>());
                        to = ref Unsafe.Add(ref to, size);
                    } while (--n != 0);

                    i = last + 1;
                }
            }
#else
            // The netstandard builds (Unity, .NET Framework): the registered kernels (NinoKernels), for runs long
            // enough to pay for the call; without any, one load and a compare that fails.
            if (NinoBlock<T, TFormatter>.Kernel && count >= NinoKernelHook.MinBlocks &&
                NinoKernelHook.TryPermute(ref Unsafe.As<T, byte>(ref first), Unsafe.SizeOf<T>(), ref destination, size,
                    NinoBlock<T, TFormatter>.KernelTables.ToWire, count))
                return;
#endif
            // Cursors bumped by the sizes and a count down: an index scaled twice per value took 1.75 times as long
            // as the cursors (1,000 dictionary entries without hardware intrinsics).
            if (i < count)
            {
                ref T from = ref Unsafe.Add(ref first, i);
                ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
                int n = count - i;
                do
                {
                    WriteValue<T, TFormatter>(ref to, in from);
                    from = ref Unsafe.Add(ref from, 1);
                    to = ref Unsafe.Add(ref to, size);
                } while (--n != 0);
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// The groups of <see cref="WriteAll{T, TFormatter}"/>, two or more (one is cheaper as single vectors than as a
        /// call): returns the values written. Not inlined, so its dynamic PGO profile is that of long runs only: compiled
        /// inside WriteAll after short ones, the loop took 3.5 times as long (1,000 12-byte blocks: 577 ns instead of 160).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int WriteGroups<T, TFormatter>(ref byte destination, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            int i = 0;
            int group = NinoBlock<T, TFormatter>.Group;
            int last = NinoBlock<T, TFormatter>.LastGroup(count);
            ref byte source = ref Unsafe.As<T, byte>(ref first);
            // The groups from the first value whose blocks start on a vector boundary: the blocks usually follow a
            // 4-byte length, and a store across two cache lines costs two (measured on an AVX-512 Xeon, 1,000
            // 12-byte blocks: 230 ns unaligned, 150 to 170 aligned). The values before it, and those after the last
            // group, by one more group each, unaligned, over values the aligned groups also write (the same bytes):
            // one at a time, up to a group less one at each end took up to a fifth of the time (1,000 (int, long):
            // 123 to 149 ns by the alignment of the blocks, 100 to 125 with the two groups).
            int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), size, 0, group, 63);
            if (skip > 0)
            {
                WriteGroup<T, TFormatter>(ref source, ref destination, 0, size);
                i = skip;
            }

            if (i <= last)
            {
                // Cursors bumped a group at a time and a count down, not two multiplications per group.
                int n = (last - i) / group + 1;
                ref byte from = ref Unsafe.Add(ref source, (nint)i * Unsafe.SizeOf<T>());
                ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
                i += n * group;
                do
                {
                    Permute64x2<T, TFormatter>(ref from, 0, ref to, 0);
                    from = ref Unsafe.Add(ref from, (nint)group * Unsafe.SizeOf<T>());
                    to = ref Unsafe.Add(ref to, (nint)group * size);
                } while (--n != 0);
            }

            if (i < last + group)
            {
                WriteGroup<T, TFormatter>(ref source, ref destination, last, size);
                i = last + group;
            }

            return i;
        }

        /// <summary>
        /// The values from value <paramref name="i"/> of <paramref name="count"/>,
        /// <see cref="NinoBlock{T, TFormatter}.PerPair"/> at a time, each step one two-source byte permute of 128 bytes
        /// of their memory stored as 64 bytes of blocks (the bytes past the step's blocks rewritten by the next); the
        /// values after the last step by one more, over values already written (the same bytes). Returns the values
        /// written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WritePairs<T, TFormatter>(ref byte source, ref byte destination, int i, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            int step = NinoBlock<T, TFormatter>.PerPair;
            int last = NinoBlock<T, TFormatter>.LastPair(count);
            if (i > last)
                return i;
            var toWire = NinoBlock<T, TFormatter>.ToWire128;
            int n = (last - i) / step + 1;
            ref byte from = ref Unsafe.Add(ref source, (nint)i * Unsafe.SizeOf<T>());
            ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
            i += n * step;
            do
            {
                Avx512Vbmi.PermuteVar64x8x2(Vector512.LoadUnsafe(ref from), toWire, Vector512.LoadUnsafe(ref from, 64))
                    .StoreUnsafe(ref to);
                from = ref Unsafe.Add(ref from, (nint)step * Unsafe.SizeOf<T>());
                to = ref Unsafe.Add(ref to, (nint)step * size);
            } while (--n != 0);

            if (i < last + step)
            {
                from = ref Unsafe.Add(ref source, (nint)last * Unsafe.SizeOf<T>());
                Avx512Vbmi.PermuteVar64x8x2(Vector512.LoadUnsafe(ref from), toWire, Vector512.LoadUnsafe(ref from, 64))
                    .StoreUnsafe(ref destination, (nuint)last * (nuint)size);
                i = last + step;
            }

            return i;
        }

        /// <summary>The group of values from value <paramref name="i"/> (<see cref="Permute64x2{T, TFormatter}"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteGroup<T, TFormatter>(ref byte source, ref byte destination, int i, int size)
            where TFormatter : struct, INinoFormatter<T> =>
            Permute64x2<T, TFormatter>(ref source, (nuint)i * (nuint)Unsafe.SizeOf<T>(), ref destination,
                (nuint)i * (nuint)size);
#endif

        /// <summary>Reads <paramref name="count"/> values from <paramref name="source"/>, <paramref name="size"/> bytes apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadAll<T, TFormatter>(ref byte source, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            // The bools of the blocks first: the copy and the vector paths move their bytes without a look, where
            // ReadBlock checks them (null for values without bools, and the test folds away).
            if (NinoBlock<T, TFormatter>.Bools != null)
                CheckBools<T, TFormatter>(ref source, count);
            if (NinoBlock<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref first), ref source, (uint)(count * size));
                return;
            }

            ReadEach<T, TFormatter>(ref source, ref first, count);
        }

        /// <summary>
        /// Throws if a bool of the <paramref name="count"/> blocks at <paramref name="source"/>
        /// (<see cref="NinoBlock{T, TFormatter}.Bools"/>) is neither 0 nor 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckBools<T, TFormatter>(ref byte source, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            foreach (int at in NinoBlock<T, TFormatter>.Bools)
                NinoChecks.Bools(ref Unsafe.Add(ref source, at), count, size);
        }

        /// <summary>
        /// <see cref="ReadAll{T, TFormatter}"/> of values whose blocks are not laid out as in memory, out of line as
        /// <see cref="WriteEach{T, TFormatter}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void ReadEach<T, TFormatter>(ref byte source, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            int i = 0;
#if NET8_0_OR_GREATER
            // As WriteAll, the other way. Padding is unspecified, as when a value is read a field at a time.
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            if (NinoBlock<T, TFormatter>.ReadPlan<NinoPhase0>.Enabled &&
                NinoBlock<T, TFormatter>.ReadPlan<NinoPhase0>.Last(count) >= NinoBlock<T, TFormatter>.Group)
                i = ReadGroups<T, TFormatter>(ref source, ref first, count);
            else if (NinoBlock<T, TFormatter>.LanePlan<NinoReads>.Worth(count))
                i = Lanes<T, TFormatter, NinoReads>(ref source, ref destination, count);

            if (NinoBlock<T, TFormatter>.Permutes)
            {
                var toMemory = NinoBlock<T, TFormatter>.ToMemory64;
                int tables = NinoBlock<T, TFormatter>.MemoryTables64;
                int step = NinoBlock<T, TFormatter>.PerVector;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 64);
                // Where the values fill whole vectors (64 bytes a multiple of their size), the stores do not overlap
                // and are exactly the values': from the first value on the destination's first boundary (64 bytes with
                // AVX-512, 16 for the four Arm64 stores), so that none straddles two cache lines, and the values
                // before it, and those after the last vector, by one more vector each, unaligned (as WriteGroups).
                bool whole = 64 % Unsafe.SizeOf<T>() == 0;
                if (whole && i == 0 && last >= step)
                {
                    int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), Unsafe.SizeOf<T>(), 0, step,
                        Avx512Vbmi.IsSupported ? 63u : 15u);
                    if (skip > 0)
                    {
                        Permute64(ref source, 0, ref destination, 0, toMemory, tables);
                        i = skip;
                    }
                }

                i = Permute64(ref source, size, ref destination, Unsafe.SizeOf<T>(), i, last, step, toMemory, tables);
                if (whole && i < last + step && last >= 0)
                {
                    Permute64(ref source, (nint)last * size, ref destination, (nint)last * Unsafe.SizeOf<T>(),
                        toMemory, tables);
                    i = last + step;
                }
            }

            if (NinoBlock<T, TFormatter>.Shuffles)
            {
                var toMemory = NinoBlock<T, TFormatter>.ToMemory;
                int last = NinoBlock<T, TFormatter>.LastVector(count, size, 16);
                if (i <= last)
                {
                    // As in WriteEach.
                    ref byte from = ref Unsafe.Add(ref source, (nint)i * size);
                    ref byte to = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref first, i));
                    int n = last - i + 1;
                    do
                    {
                        NinoBlock<T, TFormatter>.Shuffle(Vector128.LoadUnsafe(ref from), toMemory).StoreUnsafe(ref to);
                        from = ref Unsafe.Add(ref from, size);
                        to = ref Unsafe.Add(ref to, Unsafe.SizeOf<T>());
                    } while (--n != 0);

                    i = last + 1;
                }
            }
#else
            // As in WriteEach.
            if (NinoBlock<T, TFormatter>.Kernel && count >= NinoKernelHook.MinBlocks && ReadKernel<T, TFormatter>(
                    ref source, ref first, count))
                return;
#endif
            for (; i < count; i++)
                ReadValue<T, TFormatter>(ref Unsafe.Add(ref source, (nint)i * size), ref Unsafe.Add(ref first, i));
        }

#if !NET8_0_OR_GREATER
        /// <summary>
        /// <see cref="ReadEach{T, TFormatter}"/> by the registered kernels: false (nothing read) without any. The kernel
        /// moves bytes without a look, where ReadBlock checks the bools, so they are checked first (as the vector paths
        /// of the .NET 8 builds do).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadKernel<T, TFormatter>(ref byte source, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!NinoKernelHook.HasPermute)
                return false;
            int size = NinoBlock<T, TFormatter>.Size;
            var bools = NinoBlock<T, TFormatter>.KernelTables.Bools;
            if (bools != null)
                foreach (int at in bools)
                    NinoChecks.Bools(ref Unsafe.Add(ref source, at), count, size);
            return NinoKernelHook.TryPermute(ref source, size, ref Unsafe.As<T, byte>(ref first), Unsafe.SizeOf<T>(),
                NinoBlock<T, TFormatter>.KernelTables.ToMemory, count);
        }

#endif

        /// <summary>Writes one value as its block (<see cref="INinoBlockFormatter{T}.WriteBlock"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteValue<T, TFormatter>(ref byte destination, in T value)
            where TFormatter : struct, INinoFormatter<T> =>
#if NET8_0_OR_GREATER
            ((INinoBlockFormatter<T>)default(TFormatter)).WriteBlock(ref destination, in value);
#else
            NinoBlock<T, TFormatter>.Formatter.WriteBlock(ref destination, in value);
#endif

        /// <summary>Reads one value from its block (<see cref="INinoBlockFormatter{T}.ReadBlock"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadValue<T, TFormatter>(ref byte source, ref T value)
            where TFormatter : struct, INinoFormatter<T> =>
#if NET8_0_OR_GREATER
            ((INinoBlockFormatter<T>)default(TFormatter)).ReadBlock(ref source, ref value);
#else
            NinoBlock<T, TFormatter>.Formatter.ReadBlock(ref source, ref value);
#endif

#if NET8_0_OR_GREATER
        /// <summary>
        /// The read groups of <see cref="ReadAll{T, TFormatter}"/>, two or more: returns the values read. Not inlined,
        /// as <see cref="WriteGroups{T, TFormatter}"/>. Stores are aligned: the plan whose vectors start on a 64-byte
        /// boundary at a value, or 8 bytes into one.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int ReadGroups<T, TFormatter>(ref byte source, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlock<T, TFormatter>.Size;
            nuint at = (nuint)Unsafe.AsPointer(ref first);
            if (NinoBlock<T, TFormatter>.ReadPlan<NinoPhase8>.Enabled && at % 16 == 8)
                return ReadGroups<T, TFormatter, NinoPhase8>(ref source, ref first, count, size, at);
            return ReadGroups<T, TFormatter, NinoPhase0>(ref source, ref first, count, size, at);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadGroups<T, TFormatter, TPhase>(ref byte source, ref T first, int count, int size,
            nuint at)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            int i = 0, phase = default(TPhase).Bytes;
            int group = NinoBlock<T, TFormatter>.Group;
            int last = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Last(count);
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            // The groups from the first value whose vectors start on a 64-byte boundary (from the first value,
            // unaligned, when none does); the values before it, and those after the last group, by one more group
            // each, unaligned (as WriteGroups). With a phase, the first value is read whole too: a group's vectors
            // start past its first bytes, and each group's last vector writes those of the next.
            int skip = FirstAligned(at + (nuint)phase, Unsafe.SizeOf<T>(), 0, group, 63);
            if (phase > 0)
                ReadValue<T, TFormatter>(ref source, ref first);

            // The indices from an array, not constants, so they stay in registers: the JIT reloads a vector
            // constant at every use (1,000 (int, long) into an array: 135 to 150 ns, against 115 to 130 with them in
            // registers).
            var masks = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Masks;
            Vector512<byte> m0 = masks[0], m1 = masks[1], m2 = masks[2], m3 = masks[3];
            if (skip > 0)
            {
                ReadGroup<T, TFormatter, TPhase>(ref source, 0, ref destination, (nuint)phase, m0, m1, m2, m3);
                i = skip;
            }

            if (i <= last)
            {
                // As in WriteGroups.
                int n = (last - i) / group + 1;
                ref byte from = ref Unsafe.Add(ref source, (nint)i * size);
                ref byte to = ref Unsafe.Add(ref destination, (nint)i * Unsafe.SizeOf<T>() + phase);
                i += n * group;
                do
                {
                    ReadGroup<T, TFormatter, TPhase>(ref from, 0, ref to, 0, m0, m1, m2, m3);
                    from = ref Unsafe.Add(ref from, (nint)group * size);
                    to = ref Unsafe.Add(ref to, (nint)group * Unsafe.SizeOf<T>());
                } while (--n != 0);
            }

            if (i < last + group)
            {
                ReadGroup<T, TFormatter, TPhase>(ref source, (nuint)last * (nuint)size, ref destination,
                    (nuint)last * (nuint)Unsafe.SizeOf<T>() + (nuint)phase, m0, m1, m2, m3);
                i = last + group;
            }

            return i;
        }

        /// <summary>One read group: the vectors of blocks loaded once, each memory vector one permute of two.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadGroup<T, TFormatter, TPhase>(ref byte source, nuint from, ref byte destination,
            nuint to, Vector512<byte> m0, Vector512<byte> m1, Vector512<byte> m2, Vector512<byte> m3)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            int loads = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Loads;
            int vectors = NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Vectors;
            var w0 = Vector512.LoadUnsafe(ref source, from);
            var w1 = loads > 1 ? Vector512.LoadUnsafe(ref source, from + 64) : default;
            var w2 = loads > 2 ? Vector512.LoadUnsafe(ref source, from + 128) : default;
            var w3 = loads > 3 ? Vector512.LoadUnsafe(ref source, from + 192) : default;
            var w4 = loads > 4 ? Vector512.LoadUnsafe(ref source, from + 256) : default;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base0, w0, w1, w2, w3, w4, m0)
                .StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base1, w0, w1, w2, w3, w4, m1)
                .StoreUnsafe(ref destination, to + 64);
            if (vectors < 3)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base2, w0, w1, w2, w3, w4, m2)
                .StoreUnsafe(ref destination, to + 128);
            if (vectors < 4)
                return;
            ReadVector<T, TFormatter, TPhase>(NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Base3, w0, w1, w2, w3, w4, m3)
                .StoreUnsafe(ref destination, to + 192);
        }

        /// <summary>
        /// A memory vector from vectors of blocks <paramref name="pair"/> and the next (a constant); the next is not
        /// loaded (default) when the vector draws on <paramref name="pair"/> alone.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> ReadVector<T, TFormatter, TPhase>(int pair, Vector512<byte> w0,
            Vector512<byte> w1, Vector512<byte> w2, Vector512<byte> w3, Vector512<byte> w4, Vector512<byte> mask)
            where TFormatter : struct, INinoFormatter<T>
            where TPhase : struct, INinoPhase
        {
            var low = pair == 0 ? w0 : pair == 1 ? w1 : pair == 2 ? w2 : w3;
            var high = pair == 0 ? w1 : pair == 1 ? w2 : pair == 2 ? w3 : w4;
            // Padding takes whatever its index points at: as in a value read a field at a time, it is unspecified,
            // and clearing it would take an instruction a vector (vpermt2d cannot zero by itself here).
            return NinoBlock<T, TFormatter>.ReadPlan<TPhase>.Words
                ? Avx512F.PermuteVar16x32x2(low.AsInt32(), mask.AsInt32(), high.AsInt32()).AsByte()
                : Avx512Vbmi.PermuteVar64x8x2(low, mask, high);
        }

        /// <summary>
        /// One group of values (<see cref="NinoBlock{T, TFormatter}.Groups"/>) with AVX-512: vector k of blocks is one
        /// two-source permute (<see cref="Pair{T, TFormatter}"/>) of memory vectors k and k + 1, each memory vector
        /// loaded once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64x2<T, TFormatter>(ref byte source, nuint from, ref byte destination, nuint to)
            where TFormatter : struct, INinoFormatter<T>
        {
            int vectors = NinoBlock<T, TFormatter>.GroupVectors;
            var a = Vector512.LoadUnsafe(ref source, from);
            var b = Vector512.LoadUnsafe(ref source, from + 64);
            Pair<T, TFormatter>(a, NinoBlock<T, TFormatter>.Group0, b).StoreUnsafe(ref destination, to);
            if (vectors < 2)
                return;
            var c = Vector512.LoadUnsafe(ref source, from + 128);
            Pair<T, TFormatter>(b, NinoBlock<T, TFormatter>.Group1, c).StoreUnsafe(ref destination, to + 64);
            if (vectors < 3)
                return;
            var d = Vector512.LoadUnsafe(ref source, from + 192);
            Pair<T, TFormatter>(c, NinoBlock<T, TFormatter>.Group2, d).StoreUnsafe(ref destination, to + 128);
            if (vectors < 4)
                return;
            var e = Vector512.LoadUnsafe(ref source, from + 256);
            Pair<T, TFormatter>(d, NinoBlock<T, TFormatter>.Group3, e).StoreUnsafe(ref destination, to + 192);
        }

        /// <summary>
        /// The two-source permute of a write group: 4-byte words (vpermt2d, AVX-512F) where every block moves whole
        /// words (<see cref="NinoBlock{T, TFormatter}.GroupWords"/>), else bytes (vpermt2b, AVX-512 VBMI).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> Pair<T, TFormatter>(Vector512<byte> low, Vector512<byte> mask,
            Vector512<byte> high)
            where TFormatter : struct, INinoFormatter<T> =>
            Avx512F.IsSupported && NinoBlock<T, TFormatter>.GroupWords
                ? Avx512F.PermuteVar16x32x2(low.AsInt32(), mask.AsInt32(), high.AsInt32()).AsByte()
                : Avx512Vbmi.PermuteVar64x8x2(low, mask, high);

        /// <summary>
        /// The first of the <paramref name="count"/> values from <paramref name="from"/>, <paramref name="stride"/>
        /// bytes apart from <paramref name="at"/>, that starts on a boundary of <paramref name="mask"/> + 1 bytes;
        /// <paramref name="from"/> when none does.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FirstAligned(nuint at, int stride, int from, int count, nuint mask)
        {
            for (int i = from; i < from + count; i++)
                if (((at + (nuint)(i * stride)) & mask) == 0)
                    return i;
            return from;
        }

        /// <summary>
        /// Moves values a group at a time as <see cref="NinoBlock{T, TFormatter}.LanePlan{TDirection}"/> describes, from
        /// their memory to their blocks (<see cref="NinoWrites"/>) or back: returns the values moved. Not inlined, as
        /// <see cref="WriteGroups{T, TFormatter}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int Lanes<T, TFormatter, TDirection>(ref byte source, ref byte destination, int count)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int size = NinoBlock<T, TFormatter>.Size;
            bool writes = typeof(TDirection) == typeof(NinoWrites);
            int from = writes ? Unsafe.SizeOf<T>() : size, to = writes ? size : Unsafe.SizeOf<T>();
            int group = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Group;
            int width = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Width;
            int last = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Last(count, from, to);
            // The first value whose loads start inside the source (a window may start before the group's values),
            // then the first from there whose vectors start on a boundary of their width, so that no store straddles
            // two cache lines (when one does). The values before the first one at a time; those from it to the
            // aligned one, and those after the last group, by one more group each, unaligned (as WriteGroups; the
            // stores of a group are its values, and at most part of the next value's, which is moved after it).
            int start = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Start;
            int skip = FirstAligned((nuint)Unsafe.AsPointer(ref destination), to, start, group, (nuint)(width - 1));
            int i = 0;
            for (; i < start; i++)
                Move<T, TFormatter, TDirection>(ref source, ref destination, i, size);

            // The indices from an array, so they stay in registers (see ReadGroups).
            if (width == 32)
            {
                var masks = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Masks32;
                Vector256<byte> a0 = masks[0], b0 = masks[1], a1 = masks[2], b1 = masks[3], a2 = masks[4],
                    b2 = masks[5], a3 = masks[6], b3 = masks[7];
                if (skip > start && start <= last)
                {
                    Lanes32<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)start * from),
                        ref Unsafe.Add(ref destination, (nint)start * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = skip;
                }

                if (i <= last)
                {
                    // Cursors bumped a group at a time and a count down, not a multiply and an add per group.
                    int n = (last - i) / group + 1;
                    ref byte at = ref Unsafe.Add(ref source, (nint)i * from);
                    ref byte into = ref Unsafe.Add(ref destination, (nint)i * to);
                    i += n * group;
                    do
                    {
                        Lanes32<T, TFormatter, TDirection>(ref at, ref into, a0, b0, a1, b1, a2, b2, a3, b3);
                        at = ref Unsafe.Add(ref at, (nint)group * from);
                        into = ref Unsafe.Add(ref into, (nint)group * to);
                    } while (--n != 0);
                }

                if (i < last + group && last >= start)
                {
                    Lanes32<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)last * from),
                        ref Unsafe.Add(ref destination, (nint)last * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = last + group;
                }
            }
            else
            {
                var masks = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Masks16;
                Vector128<byte> a0 = masks[0], b0 = masks[1], a1 = masks[2], b1 = masks[3], a2 = masks[4],
                    b2 = masks[5], a3 = masks[6], b3 = masks[7];
                if (skip > start && start <= last)
                {
                    Lanes16<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)start * from),
                        ref Unsafe.Add(ref destination, (nint)start * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = skip;
                }

                if (i <= last)
                {
                    // Cursors bumped a group at a time and a count down, not a multiply and an add per group.
                    int n = (last - i) / group + 1;
                    ref byte at = ref Unsafe.Add(ref source, (nint)i * from);
                    ref byte into = ref Unsafe.Add(ref destination, (nint)i * to);
                    i += n * group;
                    do
                    {
                        Lanes16<T, TFormatter, TDirection>(ref at, ref into, a0, b0, a1, b1, a2, b2, a3, b3);
                        at = ref Unsafe.Add(ref at, (nint)group * from);
                        into = ref Unsafe.Add(ref into, (nint)group * to);
                    } while (--n != 0);
                }

                if (i < last + group && last >= start)
                {
                    Lanes16<T, TFormatter, TDirection>(ref Unsafe.Add(ref source, (nint)last * from),
                        ref Unsafe.Add(ref destination, (nint)last * to), a0, b0, a1, b1, a2, b2, a3, b3);
                    i = last + group;
                }
            }

            return i;
        }

        /// <summary>Value <paramref name="i"/> moved alone, in the direction of <typeparamref name="TDirection"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Move<T, TFormatter, TDirection>(ref byte source, ref byte destination, int i, int size)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            if (typeof(TDirection) == typeof(NinoWrites))
                WriteValue<T, TFormatter>(ref Unsafe.Add(ref destination, (nint)i * size),
                    in Unsafe.Add(ref Unsafe.As<byte, T>(ref source), i));
            else
                ReadValue<T, TFormatter>(ref Unsafe.Add(ref source, (nint)i * size),
                    ref Unsafe.Add(ref Unsafe.As<byte, T>(ref destination), i));
        }

        /// <summary>
        /// One group of 32-byte vectors (AVX2): each the bytes of two loads shuffled within their 16-byte lanes
        /// (vpshufb, which zeroes where an index has its high bit set) and ORed, or of one load alone. All are worked
        /// out before any is stored, so that a load repeated across them is one (no store in between).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lanes32<T, TFormatter, TDirection>(ref byte source, ref byte destination,
            Vector256<byte> a0, Vector256<byte> b0, Vector256<byte> a1, Vector256<byte> b1, Vector256<byte> a2,
            Vector256<byte> b2, Vector256<byte> a3, Vector256<byte> b3)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int vectors = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Vectors;
            var v0 = Lane32<T, TFormatter, TDirection>(ref source, 0, a0, b0);
            var v1 = vectors > 1 ? Lane32<T, TFormatter, TDirection>(ref source, 1, a1, b1) : default;
            var v2 = vectors > 2 ? Lane32<T, TFormatter, TDirection>(ref source, 2, a2, b2) : default;
            var v3 = vectors > 3 ? Lane32<T, TFormatter, TDirection>(ref source, 3, a3, b3) : default;
            v0.StoreUnsafe(ref destination);
            if (vectors > 1)
                v1.StoreUnsafe(ref destination, 32);
            if (vectors > 2)
                v2.StoreUnsafe(ref destination, 64);
            if (vectors > 3)
                v3.StoreUnsafe(ref destination, 96);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Lane32<T, TFormatter, TDirection>(ref byte source, int vector, Vector256<byte> a,
            Vector256<byte> b)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            var result = Avx2.Shuffle(Load32(ref source, NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0),
                NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 1)), a);
            if (NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0) == NinoLanes.None)
                return result;
            return result | Avx2.Shuffle(Load32(ref source, NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0),
                NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 1)), b);
        }

        /// <summary>
        /// The 16 bytes at <paramref name="low"/> and the 16 at <paramref name="high"/>: one load when they follow;
        /// on the 16-byte grid, a lane of each of two loads on the 32-byte grid (vperm2i128), loads the group's other
        /// vectors share; else the low lane of one load and the high lane of another, blended (vpblendd issues three
        /// or four a cycle, where vinserti128 issues once a cycle on Intel and Zen 3).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Load32(ref byte source, int low, int high)
        {
            if (high == low + 16)
                return Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low));
            if (NinoLanes.Paired(low, high))
            {
                // The lane of each that holds the window: its low (0) or high (1) half.
                var x = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low & ~31));
                var y = Vector256.LoadUnsafe(ref Unsafe.Add(ref source, high & ~31));
                return ((low >> 4) & 1, (high >> 4) & 1) switch
                {
                    (0, 0) => Avx2.Permute2x128(x, y, 0x20),
                    (0, _) => Avx2.Permute2x128(x, y, 0x30),
                    (_, 0) => Avx2.Permute2x128(x, y, 0x21),
                    _ => Avx2.Permute2x128(x, y, 0x31)
                };
            }

            return Avx2.Blend(Vector256.LoadUnsafe(ref Unsafe.Add(ref source, low)).AsInt32(),
                Vector256.LoadUnsafe(ref Unsafe.Add(ref source, high - 16)).AsInt32(), 0xF0).AsByte();
        }

        /// <summary>
        /// One group of 16-byte vectors: each one or two 16-byte loads, as one table lookup of one or two registers on
        /// Arm64 (the second's indices from 16), or each load shuffled (an index from 0x80 giving a zero) and ORed. As
        /// <see cref="Lanes32{T, TFormatter, TDirection}"/>, all before any is stored.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Lanes16<T, TFormatter, TDirection>(ref byte source, ref byte destination,
            Vector128<byte> a0, Vector128<byte> b0, Vector128<byte> a1, Vector128<byte> b1, Vector128<byte> a2,
            Vector128<byte> b2, Vector128<byte> a3, Vector128<byte> b3)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int vectors = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Vectors;
            Vector128<byte> v0, v1, v2, v3;
            if (AdvSimd.Arm64.IsSupported && NinoBlock<T, TFormatter>.LanePlan<TDirection>.Gridded)
            {
                // The group's windows once each, before any lookup: the JIT then gives them consecutive registers,
                // and each lookup takes two of them as they are (loaded for each lookup, the loads it repeats take
                // register copies).
                var w0 = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Loads(0) ? Vector128.LoadUnsafe(ref source) : default;
                var w1 = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Loads(1)
                    ? Vector128.LoadUnsafe(ref source, 16)
                    : default;
                var w2 = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Loads(2)
                    ? Vector128.LoadUnsafe(ref source, 32)
                    : default;
                var w3 = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Loads(3)
                    ? Vector128.LoadUnsafe(ref source, 48)
                    : default;
                v0 = Table16<T, TFormatter, TDirection>(0, w0, w1, w2, w3, a0);
                v1 = vectors > 1 ? Table16<T, TFormatter, TDirection>(1, w0, w1, w2, w3, a1) : default;
                v2 = vectors > 2 ? Table16<T, TFormatter, TDirection>(2, w0, w1, w2, w3, a2) : default;
                v3 = vectors > 3 ? Table16<T, TFormatter, TDirection>(3, w0, w1, w2, w3, a3) : default;
            }
            else
            {
                v0 = Lane16<T, TFormatter, TDirection>(ref source, 0, a0, b0);
                v1 = vectors > 1 ? Lane16<T, TFormatter, TDirection>(ref source, 1, a1, b1) : default;
                v2 = vectors > 2 ? Lane16<T, TFormatter, TDirection>(ref source, 2, a2, b2) : default;
                v3 = vectors > 3 ? Lane16<T, TFormatter, TDirection>(ref source, 3, a3, b3) : default;
            }

            v0.StoreUnsafe(ref destination);
            if (vectors > 1)
                v1.StoreUnsafe(ref destination, 16);
            if (vectors > 2)
                v2.StoreUnsafe(ref destination, 32);
            if (vectors > 3)
                v3.StoreUnsafe(ref destination, 48);
        }

        /// <summary>Vector <paramref name="vector"/> of a <see cref="NinoLanes.Gridded"/> group: a lookup in its window, or
        /// its two consecutive ones (Arm64).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Table16<T, TFormatter, TDirection>(int vector, Vector128<byte> w0,
            Vector128<byte> w1, Vector128<byte> w2, Vector128<byte> w3, Vector128<byte> a)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            int k = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0) / 16;
            if (NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0) == NinoLanes.None)
                return AdvSimd.Arm64.VectorTableLookup(k == 0 ? w0 : k == 1 ? w1 : k == 2 ? w2 : w3, a);
            return k == 0 ? AdvSimd.Arm64.VectorTableLookup((w0, w1), a)
                : k == 1 ? AdvSimd.Arm64.VectorTableLookup((w1, w2), a)
                : AdvSimd.Arm64.VectorTableLookup((w2, w3), a);
        }

        /// <summary>
        /// For Arm64, <paramref name="a"/> holds the indices into both loads (a two-register lookup); elsewhere
        /// <paramref name="a"/> and <paramref name="b"/> those into each.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Lane16<T, TFormatter, TDirection>(ref byte source, int vector, Vector128<byte> a,
            Vector128<byte> b)
            where TFormatter : struct, INinoFormatter<T>
            where TDirection : struct, INinoDirection
        {
            var low = Vector128.LoadUnsafe(
                ref Unsafe.Add(ref source, NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 0, 0)));
            int second = NinoBlock<T, TFormatter>.LanePlan<TDirection>.Window(vector, 1, 0);
            if (second == NinoLanes.None)
                return NinoBlock<T, TFormatter>.Shuffle(low, a);
            var high = Vector128.LoadUnsafe(ref Unsafe.Add(ref source, second));
            if (AdvSimd.Arm64.IsSupported)
                return AdvSimd.Arm64.VectorTableLookup((low, high), a);
            return NinoBlock<T, TFormatter>.Shuffle(low, a) | NinoBlock<T, TFormatter>.Shuffle(high, b);
        }

        /// <summary>
        /// <see cref="Permute64(ref byte, nint, ref byte, nint, Vector512{byte}, int)"/> of the values from value
        /// <paramref name="i"/> to value <paramref name="last"/>, <paramref name="step"/> at a time (none when
        /// <paramref name="i"/> is past <paramref name="last"/>), each <paramref name="from"/> bytes apart in
        /// <paramref name="source"/> and <paramref name="to"/> in <paramref name="destination"/>; returns the value
        /// after them. Cursors bumped a vector at a time and a count down, and on Arm64 the quarters of the indices
        /// taken out once: there a 64-byte vector is a struct of four, and a group at a time it was copied through the
        /// stack and its quarters loaded from it again.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Permute64(ref byte source, nint from, ref byte destination, nint to, int i, int last,
            int step, Vector512<byte> indices, int tables)
        {
            if (i > last)
                return i;
            var (q0, q1, q2, q3) = Quarters(indices);
            int n = (last - i) / step + 1;
            ref byte at = ref Unsafe.Add(ref source, i * from);
            ref byte into = ref Unsafe.Add(ref destination, i * to);
            i += n * step;
            do
            {
                Permute64(ref at, ref into, indices, tables, q0, q1, q2, q3);
                at = ref Unsafe.Add(ref at, step * from);
                into = ref Unsafe.Add(ref into, step * to);
            } while (--n != 0);

            return i;
        }

        /// <summary>
        /// One 64-byte byte permute from <paramref name="source"/> + <paramref name="from"/> to
        /// <paramref name="destination"/> + <paramref name="to"/>: each byte written is the source byte its index
        /// names. AVX-512 VBMI's permute, which reads only an index's low six bits (so a padding byte, index 0x80, takes
        /// some byte: unspecified, as when a value is read a field at a time), or on Arm64 four 4-register table
        /// lookups, one per 16 bytes written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64(ref byte source, nint from, ref byte destination, nint to,
            Vector512<byte> indices, int tables)
        {
            var (q0, q1, q2, q3) = Quarters(indices);
            Permute64(ref Unsafe.Add(ref source, from), ref Unsafe.Add(ref destination, to), indices, tables, q0, q1,
                q2, q3);
        }

        /// <summary>The 16-byte quarters of <paramref name="indices"/> for the Arm64 table lookups (none with AVX-512 VBMI).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (Vector128<byte>, Vector128<byte>, Vector128<byte>, Vector128<byte>) Quarters(
            Vector512<byte> indices) =>
            Avx512Vbmi.IsSupported
                ? default
                : (indices.GetLower().GetLower(), indices.GetLower().GetUpper(), indices.GetUpper().GetLower(),
                    indices.GetUpper().GetUpper());

        /// <summary>
        /// One permute of <see cref="Permute64(ref byte, nint, ref byte, nint, Vector512{byte}, int)"/>; on Arm64 each
        /// 16 bytes written by one lookup in the quarters <paramref name="tables"/> names
        /// (<see cref="NinoBlock{T, TFormatter}.WireTables64"/>), each quarter loaded once.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Permute64(ref byte source, ref byte destination, Vector512<byte> indices, int tables,
            Vector128<byte> q0, Vector128<byte> q1, Vector128<byte> q2, Vector128<byte> q3)
        {
            if (Avx512Vbmi.IsSupported)
            {
                Avx512Vbmi.PermuteVar64x8(Vector512.LoadUnsafe(ref source), indices).StoreUnsafe(ref destination);
                return;
            }

            Vector128<byte> t0 = default, t1 = default, t2 = default, t3 = default;
            if ((tables & 1 << Loaded) != 0)
                t0 = Vector128.LoadUnsafe(ref source);
            if ((tables & 1 << Loaded + 1) != 0)
                t1 = Vector128.LoadUnsafe(ref source, 16);
            if ((tables & 1 << Loaded + 2) != 0)
                t2 = Vector128.LoadUnsafe(ref source, 32);
            if ((tables & 1 << Loaded + 3) != 0)
                t3 = Vector128.LoadUnsafe(ref source, 48);
            // All lookups before the stores, so the JIT pairs them (stp).
            var r0 = Table64(t0, t1, t2, t3, tables, q0);
            var r1 = Table64(t0, t1, t2, t3, tables >> TableBits, q1);
            var r2 = Table64(t0, t1, t2, t3, tables >> 2 * TableBits, q2);
            var r3 = Table64(t0, t1, t2, t3, tables >> 3 * TableBits, q3);
            if ((tables & 1 << Written) != 0)
                r0.StoreUnsafe(ref destination);
            if ((tables & 1 << Written + 1) != 0)
                r1.StoreUnsafe(ref destination, 16);
            if ((tables & 1 << Written + 2) != 0)
                r2.StoreUnsafe(ref destination, 32);
            if ((tables & 1 << Written + 3) != 0)
                r3.StoreUnsafe(ref destination, 48);
        }

        /// <summary>
        /// A lookup of <paramref name="index"/> in the consecutive quarters <paramref name="table"/> names (its low two
        /// bits the first, the next two their count less one), whose indices count from the first.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Table64(Vector128<byte> t0, Vector128<byte> t1, Vector128<byte> t2,
            Vector128<byte> t3, int table, Vector128<byte> index)
        {
            int first = table & 3;
            switch (table >> 2 & 3)
            {
                case 0:
                    return AdvSimd.Arm64.VectorTableLookup(first == 0 ? t0 : first == 1 ? t1 : first == 2 ? t2 : t3,
                        index);
                case 1:
                    return first == 0 ? AdvSimd.Arm64.VectorTableLookup((t0, t1), index)
                        : first == 1 ? AdvSimd.Arm64.VectorTableLookup((t1, t2), index)
                        : AdvSimd.Arm64.VectorTableLookup((t2, t3), index);
                case 2:
                    return first == 0 ? AdvSimd.Arm64.VectorTableLookup((t0, t1, t2), index)
                        : AdvSimd.Arm64.VectorTableLookup((t1, t2, t3), index);
                default:
                    return AdvSimd.Arm64.VectorTableLookup((t0, t1, t2, t3), index);
            }
        }
#endif
    }
}
