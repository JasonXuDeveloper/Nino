using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.Internal
{
    /// <summary>Collections of values written by an <see cref="INinoBlockFormatter{T}"/>.</summary>
    internal static partial class NinoBlockCollections
    {
#if NET8_0_OR_GREATER
        /// <summary>
        /// The bits of an Arm64 lookup plan (<see cref="NinoBlockPlan{T, TFormatter}.WireTables64"/>): from bit
        /// <see cref="TableBits"/> times q, quarter q's table (two bits its first quarter, two its quarters less one);
        /// bit <see cref="Written"/> + q, whether quarter q is stored; bit <see cref="Loaded"/> + k, whether quarter
        /// k is loaded.
        /// </summary>
        internal const int TableBits = 4, Written = 16, Loaded = 20;

#endif

        /// <summary>
        /// The <see cref="INinoFixedSizeFormatter{T}.FixedSize"/> of the block formatter
        /// <typeparamref name="TFormatter"/>, -1 when it does not implement it. A JIT-time constant; -1 without a
        /// lookup for references (shared code).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int FixedSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() ? NinoBlockPlan<T, TFormatter>.Size : -1;

        /// <summary>
        /// <see cref="FixedSize{T, TFormatter}"/> for reads: -1 in <see cref="NinoSettings.Untrusted"/> mode for values
        /// with fields to check (<see cref="NinoLayout{T}.Checks"/>), which are then read one by one through their
        /// formatter, and checked there.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() ? NinoBlockPlan<T, TFormatter>.ReadSize : -1;

        /// <summary>Writes <paramref name="count"/> values at <paramref name="destination"/>, <paramref name="size"/>
        /// bytes apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteAll<T, TFormatter>(ref byte destination, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlockPlan<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.Copy(ref destination, ref Unsafe.As<T, byte>(ref first), (uint)(count * size));
                return;
            }

            if (NinoUnsafe.Is64Bit && count == 1)
            {
                int last = NinoBlockPlan<T, TFormatter>.SingleWrite.LastByte;
                if (last >= 0)
                {
                    ref byte source = ref Unsafe.As<T, byte>(ref first);
                    NinoUnsafe.Write(ref destination, NinoUnsafe.Read<ulong>(ref source, 0));
                    // Both word stores stay inside the 8- to 16-byte prefix.
                    int tail = size - 1;
                    int second = tail - 8;
                    NinoUnsafe.Write(ref destination, second, NinoUnsafe.Read<ulong>(ref source, second));
                    Unsafe.Add(ref destination, tail) = Unsafe.Add(ref source, last);
                    return;
                }
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
            int size = NinoBlockPlan<T, TFormatter>.Size;
            int i = 0;
#if NET8_0_OR_GREATER
            // A group of values at a time, as full vectors of blocks: one two-source permute and one store each, none
            // overlapping (Groups).
            if (NinoBlockPlan<T, TFormatter>.Groups && NinoBlockPlan<T, TFormatter>.LastGroup(count) >= NinoBlockPlan<T, TFormatter>.Group)
                i = WriteGroups<T, TFormatter>(ref destination, ref first, count);
            // Without AVX-512, a group of values at a time, each 16-byte lane shuffled from one or two loads (Lanes).
            else if (NinoBlockPlan<T, TFormatter>.LanePlan<NinoWrites>.Worth(count))
                i = Lanes<T, TFormatter, NinoWrites>(ref Unsafe.As<T, byte>(ref first), ref destination, count);

            // Several values as 64 bytes, their bytes moved to their places in the block by one 64-byte byte permute
            // (Permute64); the tail written is overwritten by the next one.
            if (NinoBlockPlan<T, TFormatter>.Permutes)
            {
                var toWire = NinoBlockPlan<T, TFormatter>.ToWire64;
                int tables = NinoBlockPlan<T, TFormatter>.WireTables64;
                int step = NinoBlockPlan<T, TFormatter>.PerVector;
                int last = NinoBlockPlan<T, TFormatter>.LastVector(count, size, 64);
                ref byte source = ref Unsafe.As<T, byte>(ref first);
                i = Permute64(ref source, Unsafe.SizeOf<T>(), ref destination, size, i, last, step, toWire, tables,
                    default, false);
                // The values after the last vector by one more, over values already written (the same bytes), rather
                // than one at a time.
                if (i < last + step && last >= 0)
                {
                    Permute64(ref source, (nint)last * Unsafe.SizeOf<T>(), ref destination, (nint)last * size,
                        toWire, tables, default, false);
                    i = last + step;
                }
            }

            // Values of more than 16 bytes with AVX-512 VBMI: several at a time from 128 bytes of their memory by one
            // two-source permute; the tail written is overwritten by the next one, as with Permute64.
            if (Avx512Vbmi.IsSupported && NinoBlockPlan<T, TFormatter>.PerPair > 0)
                i = WritePairs<T, TFormatter>(ref Unsafe.As<T, byte>(ref first), ref destination, i, count);

            // Then each value as one 16-byte vector, by one shuffle. The last values, whose vector would reach past
            // the end of either side, one item at a time.
            if (NinoBlockPlan<T, TFormatter>.Shuffles)
            {
                var toWire = NinoBlockPlan<T, TFormatter>.ToWire;
                int last = NinoBlockPlan<T, TFormatter>.LastVector(count, size, 16);
                if (i <= last)
                {
                    // Cursors bumped by the sizes, and a count down to zero: no multiplications per value.
                    ref byte from = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref first, i));
                    ref byte to = ref Unsafe.Add(ref destination, (nint)i * size);
                    int n = last - i + 1;
                    do
                    {
                        NinoBlockPlan<T, TFormatter>.Shuffle(Vector128.LoadUnsafe(ref from), toWire).StoreUnsafe(ref to);
                        from = ref Unsafe.Add(ref from, Unsafe.SizeOf<T>());
                        to = ref Unsafe.Add(ref to, size);
                    } while (--n != 0);

                    i = last + 1;
                }
            }
#else
            // The netstandard2.1 build (Unity, Mono): the registered kernels (NinoKernels), for runs long
            // enough to pay for the call; without any, one load and a compare that fails.
            if (NinoBlockPlan<T, TFormatter>.Kernel && count >= NinoKernelHook.MinBlocks &&
                NinoKernelHook.TryPermute(ref Unsafe.As<T, byte>(ref first), Unsafe.SizeOf<T>(), ref destination, size,
                    NinoBlockPlan<T, TFormatter>.KernelTables.ToWire, count))
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

        /// <summary>Reads <paramref name="count"/> values from <paramref name="source"/>, <paramref name="size"/> bytes
        /// apart.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadAll<T, TFormatter>(ref byte source, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlockPlan<T, TFormatter>.Rejects)
            {
                ReadRejecting<T, TFormatter>(ref source, ref first, count, size);
                return;
            }

            ReadUnchecked<T, TFormatter>(ref source, ref first, count, size);
        }

        /// <summary><see cref="ReadAll{T, TFormatter}"/> itself.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadUnchecked<T, TFormatter>(ref byte source, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            // The bools of the blocks first: the copy and the vector paths move their bytes without a look, where
            // ReadBlock checks them (null for values without bools, and the test folds away).
            if (NinoBlockPlan<T, TFormatter>.Bools != null && NeedsBoolPrepass<T, TFormatter>(count))
                CheckBools<T, TFormatter>(ref source, count);
            if (NinoBlockPlan<T, TFormatter>.IsMemory)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref first), ref source, (uint)(count * size));
                return;
            }

            ReadEach<T, TFormatter>(ref source, ref first, count);
        }

        /// <summary>
        /// <see cref="ReadAll{T, TFormatter}"/> of values whose read can reject the data after it has written some
        /// (<see cref="NinoBlockPlan{T, TFormatter}.Rejects"/>: a bool other than 0 or 1, found by the strided pass, by
        /// the groups that check them as they go, or by ReadBlock): the values are cleared when it does. New arrays of
        /// blocks are allocated uninitialized (<see cref="NinoElements.CopiedWhole{T, TFormatter}"/>), and one already
        /// in the caller's object (read into an existing value) must not keep stale heap memory. Here, out of line,
        /// rather than in the formatters, which recurse through nested collections (their Debug frames grew past a
        /// 1 MB stack). A finally, not a catch that rethrows (see NinoCollections.ReadRented).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadRejecting<T, TFormatter>(ref byte source, ref T first, int count, int size)
            where TFormatter : struct, INinoFormatter<T>
        {
            bool read = false;
            try
            {
                ReadUnchecked<T, TFormatter>(ref source, ref first, count, size);
                read = true;
            }
            finally
            {
                if (!read)
                    Unsafe.InitBlockUnaligned(ref Unsafe.As<T, byte>(ref first), 0,
                        (uint)count * (uint)Unsafe.SizeOf<T>());
            }
        }

        /// <summary>Whether the selected block read can move bytes without checking their bools.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedsBoolPrepass<T, TFormatter>(int count) where TFormatter : struct, INinoFormatter<T>
        {
#if NET8_0_OR_GREATER
            if (NinoBlockPlan<T, TFormatter>.IsMemory || NinoBlockPlan<T, TFormatter>.Shuffles)
                return true;

            if (NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase0>.Enabled &&
                NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase0>.Last(count) >=
                NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase0>.Group)
                return !NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase0>.Checks;

            return NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Worth(count) &&
                   !NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Checks;
#else
            return true;
#endif
        }

        /// <summary>
        /// Throws if a bool of the <paramref name="count"/> blocks at <paramref name="source"/>
        /// (<see cref="NinoBlockPlan{T, TFormatter}.Bools"/>) is neither 0 nor 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckBools<T, TFormatter>(ref byte source, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoBlockPlan<T, TFormatter>.Size;
            foreach (int at in NinoBlockPlan<T, TFormatter>.Bools)
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
            int size = NinoBlockPlan<T, TFormatter>.Size;
            int i = 0;
#if NET8_0_OR_GREATER
            // As WriteAll, the other way. Every byte of the values is written, padding as 0 (ReadOne, Keeps), so that
            // an array of them can be allocated uninitialized.
            ref byte destination = ref Unsafe.As<T, byte>(ref first);
            if (NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase0>.Enabled &&
                NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase0>.Last(count) >=
                NinoBlockPlan<T, TFormatter>.ReadPlan<NinoPhase0>.Group)
                i = ReadGroups<T, TFormatter>(ref source, ref first, count);
            else if (NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Worth(count))
                i = NinoBlockPlan<T, TFormatter>.LanePlan<NinoReads>.Checks
                    ? LanesChecked<T, TFormatter>(ref source, ref destination, count)
                    : Lanes<T, TFormatter, NinoReads>(ref source, ref destination, count);

            if (NinoBlockPlan<T, TFormatter>.Permutes)
            {
                var toMemory = NinoBlockPlan<T, TFormatter>.ToMemory64;
                int tables = NinoBlockPlan<T, TFormatter>.MemoryTables64;
                var keep = NinoBlockPlan<T, TFormatter>.KeepMemory64;
                bool pads = NinoBlockPlan<T, TFormatter>.PermutePads;
                int step = NinoBlockPlan<T, TFormatter>.PerVector;
                int last = NinoBlockPlan<T, TFormatter>.LastVector(count, size, 64);
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
                        Permute64(ref source, 0, ref destination, 0, toMemory, tables, keep, pads);
                        i = skip;
                    }
                }

                i = Permute64(ref source, size, ref destination, Unsafe.SizeOf<T>(), i, last, step, toMemory, tables,
                    keep, pads);
                if (whole && i < last + step && last >= 0)
                {
                    Permute64(ref source, (nint)last * size, ref destination, (nint)last * Unsafe.SizeOf<T>(),
                        toMemory, tables, keep, pads);
                    i = last + step;
                }
            }

            if (NinoBlockPlan<T, TFormatter>.Shuffles)
            {
                var toMemory = NinoBlockPlan<T, TFormatter>.ToMemory;
                int last = NinoBlockPlan<T, TFormatter>.LastVector(count, size, 16);
                if (i <= last)
                {
                    // As in WriteEach.
                    ref byte from = ref Unsafe.Add(ref source, (nint)i * size);
                    ref byte to = ref Unsafe.As<T, byte>(ref Unsafe.Add(ref first, i));
                    int n = last - i + 1;
                    do
                    {
                        NinoBlockPlan<T, TFormatter>.Shuffle(Vector128.LoadUnsafe(ref from), toMemory).StoreUnsafe(ref to);
                        from = ref Unsafe.Add(ref from, size);
                        to = ref Unsafe.Add(ref to, Unsafe.SizeOf<T>());
                    } while (--n != 0);

                    i = last + 1;
                }
            }
#else
            // As in WriteEach.
            if (NinoBlockPlan<T, TFormatter>.Kernel && count >= NinoKernelHook.MinBlocks && ReadKernel<T, TFormatter>(
                    ref source, ref first, count))
                return;
#endif
            for (; i < count; i++)
                ReadOne<T, TFormatter>(ref Unsafe.Add(ref source, (nint)i * size), ref Unsafe.Add(ref first, i));
        }

        /// <summary>
        /// One value read through its formatter, which writes its fields: a padded value is cleared first, so that
        /// every byte of the destination is written, as the vector paths write it (padding as zero). New arrays read
        /// as blocks are allocated uninitialized (<see cref="NinoElements.CopiedWhole{T, TFormatter}"/>), and no stale
        /// byte of the heap may show through their padding.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadOne<T, TFormatter>(ref byte source, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoBlockPlan<T, TFormatter>.Size < Unsafe.SizeOf<T>())
                Unsafe.InitBlockUnaligned(ref Unsafe.As<T, byte>(ref value), 0, (uint)Unsafe.SizeOf<T>());
            ReadValue<T, TFormatter>(ref source, ref value);
        }

#if !NET8_0_OR_GREATER
        /// <summary>
        /// <see cref="ReadEach{T, TFormatter}"/> by the registered kernels: false (nothing read) without any. The
        /// kernel moves bytes without a look, where ReadBlock checks the bools, so they are checked first (as the
        /// vector paths of the .NET 8 builds do).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadKernel<T, TFormatter>(ref byte source, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!NinoKernelHook.HasPermute)
                return false;
            int size = NinoBlockPlan<T, TFormatter>.Size;
            var bools = NinoBlockPlan<T, TFormatter>.KernelTables.Bools;
            if (bools != null)
                foreach (int at in bools)
                    NinoChecks.Bools(ref Unsafe.Add(ref source, at), count, size);
            return NinoKernelHook.TryPermute(ref source, size, ref Unsafe.As<T, byte>(ref first), Unsafe.SizeOf<T>(),
                NinoBlockPlan<T, TFormatter>.KernelTables.ToMemory, count);
        }

#endif

        /// <summary>Writes one value as its block (<see cref="INinoFixedSizeFormatter{T}.WriteFixed"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteValue<T, TFormatter>(ref byte destination, in T value)
            where TFormatter : struct, INinoFormatter<T> =>
#if NET8_0_OR_GREATER
            ((INinoBlockFormatter<T>)default(TFormatter)).WriteFixed(in value, ref destination);
#else
            NinoBlockPlan<T, TFormatter>.Formatter.WriteFixed(in value, ref destination);
#endif

        /// <summary>Reads one value from its block (<see cref="INinoBlockFormatter{T}.ReadBlock"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadValue<T, TFormatter>(ref byte source, ref T value)
            where TFormatter : struct, INinoFormatter<T> =>
#if NET8_0_OR_GREATER
            ((INinoBlockFormatter<T>)default(TFormatter)).ReadBlock(ref value, ref source);
#else
            NinoBlockPlan<T, TFormatter>.Formatter.ReadBlock(ref value, ref source);
#endif

#if NET8_0_OR_GREATER
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
#endif
    }
}
