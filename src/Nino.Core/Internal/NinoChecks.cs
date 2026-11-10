using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// Checks of values read from the data that Nino never writes otherwise: a <c>bool</c> other than 0 or 1, in every
    /// mode. Where the check goes decides what it costs the other types, so: a single bool by its own read
    /// (<see cref="Reader.ReadBool"/> and <see cref="NinoUnsafe.ReadBool"/>, which generated code uses for bool members),
    /// or out of line (<see cref="NinoLayout{T}.Read"/>, the top-level APIs through the formatter); a block of them in
    /// the data, before it is copied (<see cref="Bools(ref byte, int)"/>), behind a JIT-time test of the type written
    /// in place (<c>typeof(T) == typeof(bool)</c>), not in a helper: in a large method the inliner gives up on a helper,
    /// even one that folds away.
    /// </summary>
    internal static class NinoChecks
    {
        /// <summary>Throws if a byte of the <paramref name="count"/> at <paramref name="first"/> is neither 0 nor 1.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Bools(ref byte first, int count)
        {
#if NET8_0_OR_GREATER
            // Vectorized by the runtime.
            int at = MemoryMarshal.CreateReadOnlySpan(ref first, count).IndexOfAnyExcept((byte)0, (byte)1);
            if (at >= 0)
                NinoThrow.InvalidBool(Unsafe.Add(ref first, at));
#else
            // Eight at a time: any bit other than each byte's lowest.
            const ulong high = 0xFEFE_FEFE_FEFE_FEFE;
            int i = 0;
            ulong any = 0;
            for (; i + 8 <= count; i += 8)
                any |= Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref first, i));
            for (; i < count; i++)
                any |= Unsafe.Add(ref first, i);
            if ((any & high) != 0)
                NinoThrow.InvalidBool(2);
#endif
        }

        /// <summary>
        /// Throws if a presence byte of the <paramref name="count"/> raw nullables at <paramref name="first"/>,
        /// <paramref name="stride"/> bytes apart, is neither 0 nor 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Flags(ref byte first, int count, int stride)
        {
#if NET8_0_OR_GREATER
            if (count >= 128)
            {
                bool valid;
                if (Vector512.IsHardwareAccelerated && NinoUnsafe.Wide512)
                    valid = ValidFlagBlocks<Vector512<byte>, FlagVectors512>(ref first, count, stride);
                else if (Vector256.IsHardwareAccelerated)
                    valid = ValidFlagBlocks<Vector256<byte>, FlagVectors256>(ref first, count, stride);
                else
                    valid = Vector128.IsHardwareAccelerated &&
                            ValidFlagBlocks<Vector128<byte>, FlagVectors128>(ref first, count, stride);

                if (valid)
                    return;
            }
#endif
            int any = Strided(ref first, count, stride);
            if ((any & 0xFE) != 0)
                NinoThrow.InvalidPresence(any);
        }

#if NET8_0_OR_GREATER
        // Nullable payloads have a full stride after the last flag, so the vector loads stay within the block.
        // Tier 0 leaves calls inside the scan, so compile this validator with its helpers inlined from the start.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static bool ValidFlagBlocks<TVector, TOps>(ref byte first, int count, int stride)
            where TVector : struct
            where TOps : struct, IFlagVectors<TVector>
        {
            Vector128<ulong> mask;
            switch (stride)
            {
                case 2: mask = Vector128.Create(0x00FE_00FE_00FE_00FEUL); break;
                case 4: mask = Vector128.Create(0x0000_00FE_0000_00FEUL); break;
                case 8: mask = Vector128.Create(0xFEUL); break;
                case 16: mask = Vector128.Create(0xFEUL, 0UL); break;
                default: return false;
            }

            int bytes = count * stride;
            int width = Unsafe.SizeOf<TVector>();
            int end = bytes & -width;
            TVector bits = default;
            int remaining = end;
            ref byte at = ref ScanFlagGroups<TVector, TOps>(ref first, ref remaining, ref bits, 128);
            at = ref ScanFlagGroups<TVector, TOps>(ref at, ref remaining, ref bits, 64);
            if (width < 64)
                ScanFlagGroups<TVector, TOps>(ref at, ref remaining, ref bits, width);

            if (TOps.HasMaskedBits(bits, mask))
                return false;
            for (int offset = end; offset < bytes; offset += stride)
            {
                if ((Unsafe.Add(ref first, offset) & 0xFE) != 0)
                    return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte ScanFlagGroups<TVector, TOps>(ref byte at, scoped ref int remaining,
            scoped ref TVector any, int groupBytes)
            where TVector : struct
            where TOps : struct, IFlagVectors<TVector>
        {
            int width = Unsafe.SizeOf<TVector>();
            for (; remaining >= groupBytes; remaining -= groupBytes, at = ref Unsafe.Add(ref at, groupBytes))
            {
                var bits = ReadFlagGroup<TVector, TOps>(ref at, 0, Math.Min(groupBytes, 64) / width);
                if (groupBytes == 128)
                    bits = TOps.Or(bits, ReadFlagGroup<TVector, TOps>(ref at, 64, 64 / width));
                any = TOps.Or(any, bits);
            }

            return ref at;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TVector ReadFlagGroup<TVector, TOps>(ref byte at, int offset, int vectors)
            where TVector : struct
            where TOps : struct, IFlagVectors<TVector>
        {
            int width = Unsafe.SizeOf<TVector>();
            var first = TOps.Load(ref at, (nuint)offset);
            if (vectors == 1)
                return first;
            var second = TOps.Load(ref at, (nuint)(offset + width));
            var low = TOps.Or(first, second);
            if (vectors == 2)
                return low;
            var third = TOps.Load(ref at, (nuint)(offset + 2 * width));
            var fourth = TOps.Load(ref at, (nuint)(offset + 3 * width));
            var high = TOps.Or(third, fourth);
            return TOps.Or(low, high);
        }

        private interface IFlagVectors<TVector> where TVector : struct
        {
            static abstract TVector Load(ref byte at, nuint offset);
            static abstract TVector Or(TVector left, TVector right);
            static abstract bool HasMaskedBits(TVector bits, Vector128<ulong> mask);
        }

        private readonly struct FlagVectors128 : IFlagVectors<Vector128<byte>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<byte> Load(ref byte at, nuint offset) =>
                Vector128.LoadUnsafe(ref at, offset);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<byte> Or(Vector128<byte> left, Vector128<byte> right) => left | right;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasMaskedBits(Vector128<byte> bits, Vector128<ulong> mask) =>
                (bits.AsUInt64() & mask) != Vector128<ulong>.Zero;
        }

        private readonly struct FlagVectors256 : IFlagVectors<Vector256<byte>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<byte> Load(ref byte at, nuint offset) =>
                Vector256.LoadUnsafe(ref at, offset);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<byte> Or(Vector256<byte> left, Vector256<byte> right) => left | right;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasMaskedBits(Vector256<byte> bits, Vector128<ulong> mask) =>
                (bits.AsUInt64() & Vector256.Create(mask, mask)) != Vector256<ulong>.Zero;
        }

        private readonly struct FlagVectors512 : IFlagVectors<Vector512<byte>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<byte> Load(ref byte at, nuint offset) =>
                Vector512.LoadUnsafe(ref at, offset);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<byte> Or(Vector512<byte> left, Vector512<byte> right) => left | right;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasMaskedBits(Vector512<byte> bits, Vector128<ulong> mask)
            {
                var half = Vector256.Create(mask, mask);
                return (bits.AsUInt64() & Vector512.Create(half, half)) != Vector512<ulong>.Zero;
            }
        }
#endif

        /// <summary>
        /// Throws if a bool of the <paramref name="count"/> at <paramref name="first"/>, <paramref name="stride"/>
        /// bytes apart (the keys or values of raw pairs), is neither 0 nor 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Bools(ref byte first, int count, int stride)
        {
            int any = Strided(ref first, count, stride);
            if ((any & 0xFE) != 0)
                NinoThrow.InvalidBool(any);
        }

        /// <summary>
        /// Checks <paramref name="count"/> raw values of <typeparamref name="T"/> at <paramref name="first"/>,
        /// <paramref name="stride"/> bytes apart, laid out as in memory: a <c>bool</c> other than 0 or 1, and in
        /// <see cref="NinoSettings.Untrusted"/> mode the fields of <see cref="NinoLayout{T}.Checks"/>. For the raw
        /// paths that copy values (<see cref="NinoLayout{T}.CopiesAsIs"/> false: call only then).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Values<T>(ref byte first, int count, int stride)
        {
            if (typeof(T) == typeof(bool))
            {
                if (stride == 1)
                    Bools(ref first, count);
                else
                    Bools(ref first, count, stride);
            }
            else if (NinoLayout<T>.Checks is { } checks)
            {
                NinoLayout.Check(checks, ref first, count, stride, typeof(T));
            }
        }

        /// <summary>
        /// <paramref name="value"/>, after its fields of <see cref="NinoLayout{T}.Checks"/> are checked (generated code,
        /// in <see cref="NinoSettings.Untrusted"/> mode). By value: the caller's copy never has its address taken.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static T Checked<T>(T value)
        {
            NinoLayout.Check(NinoLayout<T>.Checks, ref Unsafe.As<T, byte>(ref value), 1, 0, typeof(T));
            return value;
        }

        // Branch-free: the bytes are or-ed together, eight a step into four accumulators, so that the loads issue
        // together instead of one a cycle behind each or (1,000 bytes 15 apart: 155 ns instead of 535 on a 2.1 GHz
        // AVX-512 Xeon; four a step, 195 ns).
        private static int Strided(ref byte first, int count, int stride)
        {
            int a = 0, b = 0, c = 0, d = 0;
            nint s = stride, s3 = 3 * s, s4 = 4 * s, s5 = 5 * s, s6 = 6 * s, s7 = 7 * s, s8 = 8 * s;
            ref byte at = ref first;
            int left = count;
            for (; left >= 8; left -= 8)
            {
                a |= at | Unsafe.Add(ref at, s4);
                b |= Unsafe.Add(ref at, s) | Unsafe.Add(ref at, s5);
                c |= Unsafe.Add(ref at, 2 * s) | Unsafe.Add(ref at, s6);
                d |= Unsafe.Add(ref at, s3) | Unsafe.Add(ref at, s7);
                at = ref Unsafe.Add(ref at, s8);
            }

            for (; left > 0; left--)
            {
                a |= at;
                at = ref Unsafe.Add(ref at, s);
            }

            return a | b | c | d;
        }
    }
}
