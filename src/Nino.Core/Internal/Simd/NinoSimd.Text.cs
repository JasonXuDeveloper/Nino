#if NET8_0_OR_GREATER
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    internal static partial class NinoSimd
    {
        /// <summary>
        /// <see cref="NinoStrings.SameLatin1"/> for 1 to 63 chars, using hardware-appropriate widening blocks and
        /// overlapping tails within the checked input. Fewer than eight chars use scalar widening.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool SameShortLatin1(ref byte source, string value)
        {
            // Widened as little-endian words: big-endian machines compare through SameLatin1.
            if (NinoSimd.Has128 && BitConverter.IsLittleEndian)
            {
                nuint n = (uint)value.Length;
                ref ushort chars = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(value.AsSpan()));
                if (n >= 8)
                {
                    if (n >= 16 && (Avx512BW.IsSupported || Avx2.IsSupported))
                        return SameWideShortLatin1(ref source, ref chars, n);

                    // ReadyToRun folds the x64 baseline ISA even when it leaves the Arm64 test as a call.
                    if (n >= 24 && !Sse2.IsSupported && System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
                        return SameMediumLatin1(ref source, ref chars, n);

                    var diff = Vector128<ushort>.Zero;
                    for (nuint i = 0; i < n - 8; i += 8)
                        diff |= WidenEight(ref Unsafe.Add(ref source, (nint)i)) ^ Vector128.LoadUnsafe(ref chars, i);
                    diff |= WidenEight(ref Unsafe.Add(ref source, (nint)(n - 8))) ^ Vector128.LoadUnsafe(ref chars, n - 8);
                    return diff == Vector128<ushort>.Zero;
                }

                if (n >= 4)
                    return ((WidenFour(NinoUnsafe.Read<uint>(ref source)) ^ NinoUnsafe.Read<ulong>(ref Unsafe.As<ushort, byte>(ref chars))) |
                            (WidenFour(NinoUnsafe.Read<uint>(ref Unsafe.Add(ref source, (nint)(n - 4)))) ^
                             NinoUnsafe.Read<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref chars, (nint)(n - 4)))))) == 0;
                for (nuint i = 0; i < n; i++)
                {
                    if (Unsafe.Add(ref source, (nint)i) != Unsafe.Add(ref chars, (nint)i))
                        return false;
                }

                return true;
            }
            return NinoStrings.SameLatin1(ref source, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static bool SameMediumLatin1(ref byte source, ref ushort chars, nuint count)
        {
            nuint last = count - 16;
            var diff = CompareLatin1_128.Difference(ref source, ref chars, last);
            for (nuint offset = 0; offset < last; offset += 16)
                diff |= CompareLatin1_128.Difference(ref source, ref chars, offset);
            return diff == Vector128<ushort>.Zero;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static bool SameWideShortLatin1(ref byte source, ref ushort chars, nuint count)
        {
            if (count >= 32 && Prefer512Int16)
                return SameLatin1Blocks<CompareShortLatin1_512>(ref source, ref chars, count);
            if (Avx2.IsSupported)
                return SameLatin1Blocks<CompareShortLatin1_256>(ref source, ref chars, count);
            return SameLatin1Blocks<CompareLatin1_128>(ref source, ref chars, count);
        }

        /// <summary>The eight bytes at <paramref name="source"/> as the eight chars they are in Latin-1.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<ushort> WidenEight(ref byte source) =>
            Vector128.WidenLower(Vector128.CreateScalarUnsafe(NinoUnsafe.Read<ulong>(ref source)).AsByte());

        /// <summary>Four bytes as the four chars they are in Latin-1 (little-endian).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong WidenFour(uint bytes)
        {
            ulong x = bytes;
            x = (x | x << 16) & 0x0000_FFFF_0000_FFFF;
            return (x | x << 8) & 0x00FF_00FF_00FF_00FF;
        }

        // 8 to 16 chars (most names and ids), inline: the first eight and the last eight. A method of its own, so that
        // no part of Narrow is large for the JIT's inlining budget (see NinoUnsafe.Copy).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Narrow8To16(ref ushort chars, ref byte destination, nuint n)
        {
            var first = Vector128.LoadUnsafe(ref chars);
            var last = Vector128.LoadUnsafe(ref chars, n - 8);
            if (((first | last) & Vector128.Create((ushort)0xFF00)) != Vector128<ushort>.Zero)
                return false;
            var bytes = Vector128.Narrow(first, last).AsUInt64();
            NinoUnsafe.Write(ref destination, bytes.ToScalar());
            NinoUnsafe.Write(ref Unsafe.Add(ref destination, (nint)(n - 8)), bytes.GetElement(1));
            return true;
        }

        // n > 16. Narrow the ASCII prefix, then finish with the widest suitable Latin-1 kernel.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool NarrowVector(ref ushort source, ref byte destination, nuint n)
        {
            // ASCII through the runtime's own narrowing (512-bit and unrolled where it can be): 1,000 chars in 22 ns
            // against 40 for the loop below. Continue the Latin-1 pass from where the ASCII pass stopped, keeping
            // at least one vector of input for the overlapping tail.
            if (n >= 32)
            {
                if (System.Text.Ascii.FromUtf16(
                        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<ushort, char>(ref source), (int)n),
                        MemoryMarshal.CreateSpan(ref destination, (int)n), out int written) ==
                    System.Buffers.OperationStatus.Done)
                    return true;

                if (Unsafe.Add(ref source, written) > 0xFF)
                    return false;

                nuint start = Math.Min((nuint)written, n - 16);
                source = ref Unsafe.Add(ref source, (nint)start);
                destination = ref Unsafe.Add(ref destination, (nint)start);
                n -= start;
            }
            return NarrowRemainder(ref source, ref destination, n);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe bool NarrowRemainder(ref ushort source, ref byte destination, nuint n)
        {
            // Test the exact ISA so Tier 0 can discard the wide dispatch on narrower CPUs.
            if (Avx512BW.IsSupported && n >= 128)
            {
                var result = TryNarrow512(ref source, ref destination, n);
                if (result != WideNarrowResult.Unsupported)
                    return result == WideNarrowResult.Written;
            }
            if (Has256 && n >= 32)
            {
                nuint i = 0;
                while (true)
                {
                    if (!TryNarrow32(ref source, ref destination, i))
                        return false;
                    if (i + 32 == n)
                        return true;
                    // After the first vector, on to the destination's next 32-byte boundary (overlapping it): the
                    // bytes follow a 4-byte length, and a store across two cache lines costs two.
                    nuint next = i == 0 ? 32 - ((nuint)Unsafe.AsPointer(ref destination) & 31) : i + 32;
                    i = next + 32 <= n ? next : n - 32;
                }
            }

            // No 256-bit vectors.
            if (n >= 128)
                return NarrowLong16s(ref source, ref destination, n);
            return Narrow16s(ref source, ref destination, n);
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static bool NarrowLong16s(ref ushort source, ref byte destination, nuint n)
        {
            // Preserve early rejection before looking for a non-Latin-1 suffix.
            if (!TryNarrow16(ref source, ref destination, 0) ||
                Unsafe.Add(ref source, (nint)n - 1) > 0xFF)
                return false;
            return Narrow16s(ref Unsafe.Add(ref source, 16), ref Unsafe.Add(ref destination, 16), n - 16);
        }

        private enum WideNarrowResult
        {
            Unsupported,
            Written,
            OutsideLatin1
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WideNarrowResult TryNarrow512(ref ushort source, ref byte destination, nuint n)
        {
            if (!Prefer512)
                return WideNarrowResult.Unsupported;
            return Narrow512(ref source, ref destination, n)
                ? WideNarrowResult.Written
                : WideNarrowResult.OutsideLatin1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool Narrow512(ref ushort source, ref byte destination, nuint n)
        {
            nuint i = 0;
            while (true)
            {
                var a = Vector512.LoadUnsafe(ref source, i);
                var b = Vector512.LoadUnsafe(ref source, i + 32);
                if (((a | b) & Vector512.Create((ushort)0xFF00)) != Vector512<ushort>.Zero)
                    return false;
                Vector512.Narrow(a, b).StoreUnsafe(ref destination, i);
                if (i + 64 == n)
                    return true;
                nuint next = i == 0 ? 64 - ((nuint)Unsafe.AsPointer(ref destination) & 63) : i + 64;
                i = next + 64 <= n ? next : n - 64;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryNarrow32(ref ushort source, ref byte destination, nuint offset)
        {
            var a = Vector256.LoadUnsafe(ref source, offset);
            var b = Vector256.LoadUnsafe(ref source, offset + 16);
            if (((a | b) & Vector256.Create((ushort)0xFF00)) != Vector256<ushort>.Zero)
                return false;
            // Every lane fits in a byte. Saturating pack avoids masking before the pack.
            var packed = Avx2.IsSupported
                ? Avx2.Permute4x64(Avx2.PackUnsignedSaturate(a.AsInt16(), b.AsInt16()).AsInt64(), 0xD8).AsByte()
                : Vector256.Narrow(a, b);
            packed.StoreUnsafe(ref destination, offset);
            return true;
        }

        // n >= 16, sixteen chars at a time, then the last sixteen again overlapping the previous.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Narrow16s(ref ushort source, ref byte destination, nuint n)
        {
            nuint last = n - 16;
            for (nuint i = 0; i < last; i += 16)
                if (!TryNarrow16(ref source, ref destination, i))
                    return false;
            return TryNarrow16(ref source, ref destination, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryNarrow16(ref ushort source, ref byte destination, nuint offset)
        {
            var a = Vector128.LoadUnsafe(ref source, offset);
            var b = Vector128.LoadUnsafe(ref source, offset + 8);
            if (((a | b) & Vector128.Create((ushort)0xFF00)) != Vector128<ushort>.Zero)
                return false;
            Vector128.Narrow(a, b).StoreUnsafe(ref destination, offset);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Widen8To15(ref byte source, ref ushort destination, nuint n)
        {
            // 8 to 15 bytes: the first eight and the last eight.
            Vector128.WidenLower(Vector128.CreateScalarUnsafe(NinoUnsafe.Read<ulong>(ref source)).AsByte())
                .StoreUnsafe(ref destination);
            Vector128.WidenLower(Vector128.CreateScalarUnsafe(
                    NinoUnsafe.Read<ulong>(ref Unsafe.Add(ref source, (nint)(n - 8)))).AsByte())
                .StoreUnsafe(ref destination, n - 8);
        }

        // n >= 16, 16-byte vectors accelerated. Whole vectors, then the last one again overlapping the previous.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static unsafe void WidenVector(ref byte source, ref ushort destination, nuint n)
        {
            // Test the exact ISA here so even Tier 0 can eliminate the wide dispatch on other CPUs.
            if (Avx512BW.IsSupported && n >= 128 && TryWiden512(ref source, ref destination, n))
                return;

            nuint i = 0;
            if (Has256 && n >= 32)
            {
                while (true)
                {
                    LoadWiden16To256(ref source, i).StoreUnsafe(ref destination, i);
                    LoadWiden16To256(ref source, i + 16).StoreUnsafe(ref destination, i + 16);
                    if (i + 32 == n)
                        return;
                    // After the first 32 chars, on to the next 32-byte boundary of the string's chars (an
                    // even address, so a whole number of chars away), as in NarrowVector.
                    nuint next = i == 0 ? (32 - ((nuint)Unsafe.AsPointer(ref destination) & 31)) / 2 : i + 32;
                    i = next + 32 <= n ? next : n - 32;
                }
            }

            nuint last = n - 16;
            for (; i < last; i += 16)
                Widen16(ref source, ref destination, i);
            Widen16(ref source, ref destination, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe bool TryWidenAligned128(ref byte source, ref ushort destination, nuint n)
        {
            if (!HasArm64 || n < 512)
                return false;

            nuint misalignment = (nuint)Unsafe.AsPointer(ref destination) & 31;
            if ((misalignment & 15) == 0)
                return false;

            // The head covers the prefix to the next 32-byte boundary, at most sixteen UTF-16 characters.
            WidenAligned128(ref source, ref destination, n, (32 - misalignment) / 2);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WidenAligned128(ref byte source, ref ushort destination, nuint n, nuint start)
        {
            Widen16(ref source, ref destination, 0);
            nuint last = n - 16;
            for (nuint i = start; i < last; i += 16)
            {
                var (lower, upper) = Vector128.Widen(Vector128.LoadUnsafe(ref source, i));
                ref ushort at = ref Unsafe.Add(ref destination, (nint)i);
                lower.StoreUnsafe(ref at);
                upper.StoreUnsafe(ref at, 8);
            }
            Widen16(ref source, ref destination, last);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWiden512(ref byte source, ref ushort destination, nuint n)
        {
            if (!Prefer512)
                return false;
            Widen512(ref source, ref destination, n);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void Widen512(ref byte source, ref ushort destination, nuint n)
        {
            nuint i = 0;
            while (true)
            {
                LoadWiden32To512(ref source, i).StoreUnsafe(ref destination, i);
                LoadWiden32To512(ref source, i + 32).StoreUnsafe(ref destination, i + 32);
                if (i + 64 == n)
                    return;
                // Align both middle stores to a 64-byte boundary. A UTF-16 destination has an even address.
                nuint next = i == 0 ? (64 - ((nuint)Unsafe.AsPointer(ref destination) & 63)) / 2 : i + 64;
                i = next + 64 <= n ? next : n - 64;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<ushort> LoadWiden16To256(ref byte source, nuint offset)
        {
            var bytes = Vector128.LoadUnsafe(ref source, offset);
            return Avx2.IsSupported
                ? Avx2.ConvertToVector256Int16(bytes).AsUInt16()
                : Vector256.WidenLower(bytes.ToVector256Unsafe()); // Only the initialized lower half is read.
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<ushort> LoadWiden32To512(ref byte source, nuint offset)
        {
            var bytes = Vector256.LoadUnsafe(ref source, offset);
            return Avx512BW.IsSupported
                ? Avx512BW.ConvertToVector512UInt16(bytes)
                : Vector512.WidenLower(bytes.ToVector512Unsafe()); // Only the initialized lower half is read.
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Widen16(ref byte source, ref ushort destination, nuint offset)
        {
            var (lower, upper) = Vector128.Widen(Vector128.LoadUnsafe(ref source, offset));
            lower.StoreUnsafe(ref destination, offset);
            upper.StoreUnsafe(ref destination, offset + 8);
        }

        /// <summary>Compares Latin-1 text with a long header, whose length is at least 64.</summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static bool SameLongLatin1(ref byte source, string value)
        {
            // Compare the tail first: a same-length edit at the end need not scan the whole prefix.
            nuint n = (uint)value.Length;
            ref ushort chars = ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(value.AsSpan()));
            if (Prefer512)
                return SameLatin1Blocks<CompareLatin1_512>(ref source, ref chars, n);
            if (Has256)
                return SameLatin1Blocks<CompareLatin1_256>(ref source, ref chars, n);
            if (HasArm64)
                return SameLatin1Arm64(ref source, ref chars, n);
            if (Has128)
                return SameLatin1Blocks<CompareLatin1_128>(ref source, ref chars, n);
            return NinoStrings.SameBytes(ref source, value, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SameLatin1Arm64(ref byte source, ref ushort chars, nuint count)
        {
            nuint last = count - 16;
            if (!CompareLatin1_128.Same(ref source, ref chars, last))
                return false;

            nuint i = 0;
            if (count >= 128)
            {
                // Check two widened blocks with one horizontal reduction.
                nuint pairedEnd = last - 16;
                for (; i < pairedEnd; i += 32)
                {
                    var first = CompareLatin1_128.Difference(ref source, ref chars, i);
                    var second = CompareLatin1_128.Difference(ref source, ref chars, i + 16);
                    if ((first | second) != Vector128<ushort>.Zero)
                        return false;
                }
            }

            for (; i < last; i += 16)
            {
                if (!CompareLatin1_128.Same(ref source, ref chars, i))
                    return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool SameLatin1Blocks<TCompare>(ref byte source, ref ushort chars, nuint count)
            where TCompare : struct, ICompareLatin1
        {
            nuint width = TCompare.Width;
            nuint last = count - width;
            if (!TCompare.Same(ref source, ref chars, last))
                return false;
            for (nuint i = 0; i < last; i += width)
            {
                if (!TCompare.Same(ref source, ref chars, i))
                    return false;
            }

            return true;
        }

        private interface ICompareLatin1
        {
            static abstract nuint Width { get; }
            static abstract bool Same(ref byte source, ref ushort chars, nuint offset);
        }

        private readonly struct CompareShortLatin1_256 : ICompareLatin1
        {
            public static nuint Width => 16;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Same(ref byte source, ref ushort chars, nuint offset) =>
                LoadWiden16To256(ref source, offset) == Vector256.LoadUnsafe(ref chars, offset);
        }

        private readonly struct CompareShortLatin1_512 : ICompareLatin1
        {
            public static nuint Width => 32;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Same(ref byte source, ref ushort chars, nuint offset) =>
                LoadWiden32To512(ref source, offset) == Vector512.LoadUnsafe(ref chars, offset);
        }

        private readonly struct CompareLatin1_128 : ICompareLatin1
        {
            public static nuint Width => 16;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Same(ref byte source, ref ushort chars, nuint offset) =>
                Difference(ref source, ref chars, offset) == Vector128<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<ushort> Difference(ref byte source, ref ushort chars, nuint offset)
            {
                var (lower, upper) = Vector128.Widen(Vector128.LoadUnsafe(ref source, offset));
                var first = lower ^ Vector128.LoadUnsafe(ref chars, offset);
                var second = upper ^ Vector128.LoadUnsafe(ref chars, offset + 8);
                return first | second;
            }
        }

        private readonly struct CompareLatin1_256 : ICompareLatin1
        {
            public static nuint Width => 32;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Same(ref byte source, ref ushort chars, nuint offset)
            {
                var first = LoadWiden16To256(ref source, offset) ^ Vector256.LoadUnsafe(ref chars, offset);
                var second = LoadWiden16To256(ref source, offset + 16) ^ Vector256.LoadUnsafe(ref chars, offset + 16);
                return (first | second) == Vector256<ushort>.Zero;
            }
        }

        private readonly struct CompareLatin1_512 : ICompareLatin1
        {
            public static nuint Width => 64;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Same(ref byte source, ref ushort chars, nuint offset)
            {
                var first = LoadWiden32To512(ref source, offset) ^ Vector512.LoadUnsafe(ref chars, offset);
                var second = LoadWiden32To512(ref source, offset + 32) ^ Vector512.LoadUnsafe(ref chars, offset + 32);
                return (first | second) == Vector512<ushort>.Zero;
            }
        }
    }
}
#endif
