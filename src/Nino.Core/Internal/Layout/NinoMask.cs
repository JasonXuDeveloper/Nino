using System;
using System.Collections.Generic;
#if !NET8_0_OR_GREATER
using System.Numerics;
#endif
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// A byte mask (0xFF keeps a byte, 0 clears it) applied while copying values to the wire, in one pass:
    /// SIMD where it is fast (<see cref="NinoUnsafe.VectorMasks"/>), 8-byte words otherwise.
    /// </summary>
    internal sealed class NinoMask
    {
        private readonly byte[] _mask;
        private readonly ulong[] _words; // the mask repeated over lcm(length, 8) bytes
        // The mask repeated over the widest selected period plus one vector, so every width shares one table.
        private readonly byte[] _vectorMask;
        private readonly int _vectorPeriod;
#if NET8_0_OR_GREATER
        private readonly int _period256;
        private readonly int _period512;
#endif
        private readonly int[] _gaps; // (offset, length) of each run of cleared bytes in one element
        private readonly int[] _absent; // NinoLayout.Info.Absent: values cleared when their flag is 0
#if !NET8_0_OR_GREATER
        // For the registered kernels (NinoKernels.Permute): each byte from itself, padding cleared (0xFF). Zero for
        // elements above 64 bytes.
        private readonly IntPtr _kernelTable;
#endif

        /// <summary>(offset, length) of each run of cleared bytes in one element.</summary>
        public int[] Gaps => _gaps;

        /// <summary>Whether values whose flag is 0 are cleared too (<see cref="NinoLayout.Info.Absent"/>).</summary>
        public bool ClearsAbsent => _absent != null;

        /// <summary>
        /// Returns a mask, or null when <paramref name="mask"/> keeps every byte and there are no
        /// <paramref name="absent"/> values (<see cref="NinoLayout.Info.Absent"/>) to clear.
        /// </summary>
        public static NinoMask Create(byte[] mask, int[] absent = null) =>
            Array.IndexOf(mask, (byte)0) < 0 && absent == null ? null : new NinoMask(mask, absent);

        /// <summary>A mask of <paramref name="size"/> bytes that keeps every byte.</summary>
        public static byte[] Whole(int size)
        {
            var mask = new byte[size];
            mask.AsSpan().Fill(0xFF);
            return mask;
        }

        private NinoMask(byte[] mask, int[] absent)
        {
            _mask = mask;
            _absent = absent;
            var gaps = new List<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0)
                    continue;
                int start = i;
                while (i + 1 < mask.Length && mask[i + 1] == 0)
                    i++;
                gaps.Add(start);
                gaps.Add(i + 1 - start);
            }

            _gaps = gaps.ToArray();
#if !NET8_0_OR_GREATER
            if (mask.Length <= 64)
            {
                _kernelTable = NinoKernelHook.Table(mask.Length);
                unsafe
                {
                    for (int j = 0; j < mask.Length; j++)
                        if (mask[j] != 0)
                            ((byte*)_kernelTable)[j] = (byte)j;
                }
            }
#endif
            var words = Repeat(mask, 8, 0, out _);
            _words = new ulong[words.Length / 8];
            for (int i = 0; i < _words.Length; i++)
                _words[i] = Unsafe.ReadUnaligned<ulong>(ref words[i * 8]);
#if NET8_0_OR_GREATER
            // Not on Ice Lake (NinoSimd.Prefer512): GitHub's Xeon Platinum 8370C wrote 1,000 8-byte structs with a
            // padding byte in 116 ns with 64-byte vectors, 83 with 32-byte ones (MemoryPack's copy 88).
            if (Vector128.IsHardwareAccelerated)
            {
                int width = NinoSimd.Prefer512 ? 64 : Vector256.IsHardwareAccelerated ? 32 : 16;
                _vectorMask = Repeat(mask, width, width, out int period);
                _vectorPeriod = width == 16 ? period : Period(mask.Length, 16);
                _period256 = width == 32 ? period : width == 64 ? Period(mask.Length, 32) : 0;
                _period512 = width == 64 ? period : 0;
            }
#else
            if (NinoUnsafe.VectorMasks)
                _vectorMask = Repeat(mask, Vector<byte>.Count, Vector<byte>.Count, out _vectorPeriod);
#endif
        }

        /// <summary>
        /// <paramref name="mask"/> repeated over its <paramref name="period"/>, the least multiple of its length that
        /// is a multiple of <paramref name="multiple"/>, and <paramref name="extra"/> bytes more.
        /// </summary>
        private static byte[] Repeat(byte[] mask, int multiple, int extra, out int period)
        {
            period = Period(mask.Length, multiple);
            var bytes = new byte[period + extra];
            mask.AsSpan().CopyTo(bytes);
            // Double the filled prefix until only a partial copy remains.
            for (int filled = mask.Length; filled < bytes.Length;)
            {
                int count = Math.Min(filled, bytes.Length - filled);
                bytes.AsSpan(0, count).CopyTo(bytes.AsSpan(filled));
                filled += count;
            }
            return bytes;
        }

        private static int Period(int length, int multiple)
        {
            int period = length;
            while (period % multiple != 0)
                period += length;
            return period;
        }

        /// <summary>Copies <paramref name="count"/> masked elements (the mask's length each).</summary>
        // Out of line: one call per collection, around loops for any count. Inlined (the JIT did, on its profile
        // data), its loops took the inlining budget of the generated body writing the collection, which then left
        // helpers such as Writer.SetBuffer as calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Copy(ref byte destination, ref byte source, int count)
        {
            CopyMasked(ref destination, ref source, count);
            if (_absent != null)
                ClearAbsent(ref destination, count);
        }

        /// <summary>
        /// Zeroes the <c>Nullable</c> values without one in <paramref name="count"/> elements, after their bytes are in
        /// place: data-dependent, so not part of the mask. Only types that hold a <c>Nullable</c> pay for it.
        /// </summary>
        private void ClearAbsent(ref byte destination, int count)
        {
            for (int e = 0; e < count; e++)
                NinoLayout.ClearAbsent(_absent, ref Unsafe.Add(ref destination, e * _mask.Length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CopyMasked(ref byte destination, ref byte source, int count)
        {
            int total = checked(count * _mask.Length);
#if !NET8_0_OR_GREATER
            // The netstandard builds (Unity, .NET Framework): the registered kernels, for runs long enough.
            if (count >= NinoKernelHook.MinCopies && _kernelTable != IntPtr.Zero &&
                NinoKernelHook.TryPermute(ref source, _mask.Length, ref destination, _mask.Length, _kernelTable, count))
                return;
#endif
#if NET8_0_OR_GREATER
            if (Vector512.IsHardwareAccelerated && _period512 != 0 && total >= Vector512<byte>.Count)
            {
                CopyVectors<Vector512<byte>, NinoVectors512>(ref destination, ref source, total, _vectorMask, _period512);
                return;
            }
            if (Vector256.IsHardwareAccelerated && _period256 != 0 && total >= Vector256<byte>.Count)
            {
                CopyVectors<Vector256<byte>, NinoVectors256>(ref destination, ref source, total, _vectorMask, _period256);
                return;
            }
            if (_vectorMask != null && total >= Vector128<byte>.Count)
            {
                CopyVectors<Vector128<byte>, NinoVectors128>(ref destination, ref source, total, _vectorMask, _vectorPeriod);
                return;
            }
#else
            if (_vectorMask != null && total >= Vector<byte>.Count)
            {
                CopyVectors<Vector<byte>, NinoVectors>(ref destination, ref source, total, _vectorMask, _vectorPeriod);
                return;
            }
#endif

            int i = 0;
            int words = _words.Length;
            int maskWord = 0;
            ref ulong word = ref NinoUnsafe.GetArrayDataReference(_words);
            // The platform fast path of hosts without vector masks. In the netstandard builds, runs of
            // NinoKernelHook.MinCopies elements or more (CopyFloor, 16, at least) went to the registered Permute kernel
            // first (Burst on Unity, a native or wasm kernel elsewhere), so on Unity with Burst these unrolls copy only
            // the shorter runs; they copy every run on hosts without a registered kernel (.NET Framework, Armv7, Unity
            // with Burst off). IL2CPP does not accelerate Vector<T>, so no vector path above runs there either. On
            // .NET 8+ they serve hosts without SIMD. Where vectors run, a total of one vector or more took a vector
            // path above (16 bytes on .NET 8+, Vector<byte>.Count in the netstandard builds), so only shorter totals
            // get here. Periods of one to three words, unrolled with every mask word in a register, all loads before
            // the stores (Arm64 pairs them as ldp / stp); the shared word loop below wraps the mask index per word.
            // Measured with SIMD disabled on an Apple M4 (.NET 10, not on Mono or IL2CPP): 12-byte elements 38-56%
            // faster, 24-byte 44-55%, 16-byte 37-67% around 64 KiB, one-word periods 39-48% (7a3cf2c6, f3d6bd81,
            // 4c8a1e11; vectors-copies.md, "Masked copy"). Keep them until a Unity or Mono measurement says otherwise.
            if (words == 3 && total >= 24)
            {
                ulong mask0 = word, mask1 = Unsafe.Add(ref word, 1), mask2 = Unsafe.Add(ref word, 2);
                for (; i <= total - 24; i += 24)
                {
                    ref byte src = ref Unsafe.Add(ref source, i);
                    ref byte dst = ref Unsafe.Add(ref destination, i);
                    ulong a = Unsafe.ReadUnaligned<ulong>(ref src);
                    ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8));
                    ulong c = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 16));
                    Unsafe.WriteUnaligned(ref dst, a & mask0);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), b & mask1);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 16), c & mask2);
                }
            }
            else if (words <= 2 && total >= 16)
            {
                ulong mask0 = word, mask1 = words == 1 ? mask0 : Unsafe.Add(ref word, 1);
                for (; i <= total - 16; i += 16)
                {
                    ref byte src = ref Unsafe.Add(ref source, i);
                    ref byte dst = ref Unsafe.Add(ref destination, i);
                    ulong a = Unsafe.ReadUnaligned<ulong>(ref src);
                    ulong b = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 8));
                    Unsafe.WriteUnaligned(ref dst, a & mask0);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref dst, 8), b & mask1);
                }
            }

            for (; i <= total - 8; i += 8)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                    Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref source, i)) & Unsafe.Add(ref word, maskWord));
                if (++maskWord == words)
                    maskWord = 0;
            }

            ref byte mask = ref Unsafe.As<ulong, byte>(ref word);
            for (int k = maskWord * 8; i < total; i++, k++)
                Unsafe.Add(ref destination, i) = (byte)(Unsafe.Add(ref source, i) & Unsafe.Add(ref mask, k));
        }

        /// <summary>
        /// Vector copy with aligned stores (aligned loads on Arm64, <see cref="AlignedSide"/>): the destination usually
        /// follows a 4-byte length, and unaligned vector stores straddle cache lines every other store. The first and
        /// last vectors are unaligned and overlap the aligned body; the mask of each vector is loaded at its offset
        /// in <paramref name="masks"/>, the mask repeated over <paramref name="repeat"/> bytes and one vector more.
        /// Offsets are native-sized (nuint): int offsets took a sign extension and an address add per vector, measured
        /// on an AVX-512 Xeon as 8,000 bytes in 74 ns instead of 66 (64-byte vectors). <typeparamref name="TVector"/>
        /// is <see cref="Vector{T}"/> of bytes, or a 64-byte vector where those are accelerated (not on CPUs that slow
        /// down for them): measured on an AVX-512 Xeon, structs of 8 bytes with a padding byte, source and destination
        /// of different alignments, 8,000 bytes in 66 ns (97 ns with 32-byte vectors, 57 ns for a plain copy, 75 ns
        /// Memmove), 1,200 bytes in 14 ns (19.5 ns, and 18.6 ns).
        /// </summary>
        // Fully optimized from the first call: a profile taken on short copies (lists of a few elements) would lay
        // out the unrolled loop as cold code for the long ones.
#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private static unsafe void CopyVectors<TVector, TOps>(ref byte destination, ref byte source, int total,
            byte[] masks, int repeat)
            where TVector : struct
            where TOps : struct, INinoVectors<TVector>
        {
            var ops = default(TOps);
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            nuint period = (nuint)repeat;
            ref byte mask = ref NinoUnsafe.GetArrayDataReference(masks);
            ops.Store(ops.And(ops.Load(ref source, 0), ops.Load(ref mask, 0)), ref destination, 0);

            nuint end = (nuint)total - width;
            if (end <= width)
            {
                // The final vector's offset is still within the first mask period.
                if (end != 0)
                    ops.Store(ops.And(ops.Load(ref source, end), ops.Load(ref mask, end)), ref destination, end);
                return;
            }

            nuint i = width - ((nuint)Unsafe.AsPointer(ref AlignedSide(ref destination, ref source)) & (width - 1));
            nuint m = i; // i % period (i <= width <= period)
            if (period == width)
            {
                var v = ops.Load(ref mask, m);
#if NET8_0_OR_GREATER
                if (AdvSimd.Arm64.IsSupported && Unsafe.SizeOf<TVector>() == 16)
                    i = CopyPairs(ref destination, ref source, i, end - 1, ops.Lower(v));
#endif
                // The final store handles the vector at end, even when it is aligned.
                for (; i + 3 * width < end; i += 4 * width)
                {
                    var a = ops.And(ops.Load(ref source, i), v);
                    var b = ops.And(ops.Load(ref source, i + width), v);
                    var c = ops.And(ops.Load(ref source, i + 2 * width), v);
                    var e = ops.And(ops.Load(ref source, i + 3 * width), v);
                    ops.Store(a, ref destination, i);
                    ops.Store(b, ref destination, i + width);
                    ops.Store(c, ref destination, i + 2 * width);
                    ops.Store(e, ref destination, i + 3 * width);
                }

                for (; i < end; i += width)
                    ops.Store(ops.And(ops.Load(ref source, i), v), ref destination, i);
            }
            else
            {
                if (m >= period)
                    m -= period;
                for (; i <= end; i += width)
                {
                    ops.Store(ops.And(ops.Load(ref source, i), ops.Load(ref mask, m)), ref destination, i);
                    m += width;
                    if (m >= period)
                        m -= period;
                }
            }

            ops.Store(ops.And(ops.Load(ref source, end), ops.Load(ref mask, TailOffset(m, i, end, period))),
                ref destination, end);
        }

        /// <summary>
        /// The side <see cref="CopyVectors{TVector, TOps}"/> aligns: the destination (unaligned vector stores straddle cache lines),
        /// but the source on Arm64, where aligned loads beat aligned stores: on GitHub's Arm64 runners (Neoverse N2, .NET
        /// 10) a 16-byte vector copy took 0.76 to 1.00 of memcpy's time for 8,000 bytes with its loads aligned, and up to
        /// 1.31 times with its stores aligned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte AlignedSide(ref byte destination, ref byte source)
        {
#if NET8_0_OR_GREATER
            if (AdvSimd.Arm64.IsSupported)
                return ref source;
#endif
            return ref destination;
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// The body of <see cref="CopyVectors{TVector, TOps}"/> on Arm64, 64 bytes at a time as two load pairs and two store pairs
        /// (ldp/stp of q registers, as the C library's memcpy copies): from <paramref name="i"/> while a whole 64
        /// bytes fits before <paramref name="end"/> + 16; returns where it stopped.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe nuint CopyPairs(ref byte destination, ref byte source, nuint i, nuint end,
            Vector128<byte> v)
        {
            fixed (byte* d = &destination, s = &source)
            {
                for (; i + 48 <= end; i += 64)
                {
                    var (a, b) = AdvSimd.Arm64.LoadPairVector128(s + i);
                    var (c, e) = AdvSimd.Arm64.LoadPairVector128(s + i + 32);
                    AdvSimd.Arm64.StorePair(d + i, a & v, b & v);
                    AdvSimd.Arm64.StorePair(d + i + 32, c & v, e & v);
                }
            }

            return i;
        }
#endif

        /// <summary>
        /// <c>end % period</c> from the loop's state, without a division (an <c>idiv</c> of about 12 cycles, 5% of
        /// copying 1,000 8-byte structs): <paramref name="m"/> is <paramref name="i"/> % <paramref name="period"/>,
        /// and the last vector at <paramref name="end"/> starts less than a vector before <paramref name="i"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint TailOffset(nuint m, nuint i, nuint end, nuint period)
        {
            nuint back = i - end;
            return m >= back ? m - back : m + period - back;
        }

        /// <summary>
        /// Zeroes the padding of <paramref name="count"/> elements in place. Only stores: reading back bytes just
        /// written by narrower field stores would stall on store forwarding (the absent values of types holding a
        /// <c>Nullable</c>, cleared after, read their flags).
        /// </summary>
        public void Clear(ref byte destination, int count)
        {
            int[] gaps = _gaps;
            for (int e = 0; e < count; e++)
            {
                ref byte element = ref Unsafe.Add(ref destination, e * _mask.Length);
                for (int g = 0; g < gaps.Length; g += 2)
                    Zero(ref Unsafe.Add(ref element, gaps[g]), gaps[g + 1]);
            }

            if (_absent != null)
                ClearAbsent(ref destination, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Zero(ref byte destination, int length)
        {
            switch (length)
            {
                case 1:
                    destination = 0;
                    return;
                case 2:
                    Unsafe.WriteUnaligned(ref destination, (ushort)0);
                    return;
                case 3:
                    Unsafe.WriteUnaligned(ref destination, (ushort)0);
                    Unsafe.Add(ref destination, 2) = 0;
                    return;
                case 4:
                    Unsafe.WriteUnaligned(ref destination, 0u);
                    return;
                case 5:
                case 6:
                case 7:
                    // Two overlapping stores, both inside the gap.
                    Unsafe.WriteUnaligned(ref destination, 0u);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, length - 4), 0u);
                    return;
                case 8:
                    Unsafe.WriteUnaligned(ref destination, 0ul);
                    return;
                default:
                    Unsafe.InitBlockUnaligned(ref destination, 0, (uint)length);
                    return;
            }
        }
    }
}
