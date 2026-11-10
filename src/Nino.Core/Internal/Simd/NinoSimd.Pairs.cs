using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.Internal
{
    internal static partial class NinoSimd
    {
#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool CanInterleave<T>(int count) where T : unmanaged =>
            count >= Vector128<T>.Count && (AdvSimd.Arm64.IsSupported || Sse2.IsSupported);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryInterleave<T, TZip, TKey, TValue>(TKey[] first, TValue[] second, ref byte target,
            int count) where T : unmanaged where TZip : struct, IZip<T>
        {
            if (typeof(TKey) != typeof(T) || typeof(TValue) != typeof(T) ||
                !CanInterleave<T>(count))
                return false;

            InterleaveBest<T, TZip>(ref Unsafe.As<TKey, T>(ref NinoUnsafe.GetArrayDataReference(first)),
                ref Unsafe.As<TValue, T>(ref NinoUnsafe.GetArrayDataReference(second)), ref target, count);
            return true;
        }

        /// <summary>Interleaves two checked raw arrays into one little-endian block of pairs.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void InterleaveBest<T, TZip>(ref T first, ref T second, ref byte target, int count)
            where T : unmanaged where TZip : struct, IZip<T>
        {
            if (Avx2.IsSupported && count >= Vector256<T>.Count * 2)
                InterleaveWide<T, TZip>(ref first, ref second, ref target, count);
            else
                Interleave<T, TZip>(ref first, ref second, ref target, count);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Interleave<T, TZip>(ref T first, ref T second, ref byte target, int count)
            where T : unmanaged where TZip : struct, IZip<T>
        {
            int lanes = Vector128<T>.Count;
            int pairSize = 2 * Unsafe.SizeOf<T>();
            nuint offset = 0;
            int complete = count & -lanes;
            for (; offset < (uint)complete; offset += (uint)lanes)
            {
                var a = Vector128.LoadUnsafe(ref first, offset);
                var b = Vector128.LoadUnsafe(ref second, offset);
                nuint bytes = offset * (uint)pairSize;
                TZip.Low(a, b).AsByte().StoreUnsafe(ref target, bytes);
                TZip.High(a, b).AsByte().StoreUnsafe(ref target, bytes + 16);
            }

            if (complete == count)
                return;

            if (Unsafe.SizeOf<T>() == sizeof(long))
            {
                // A 128-bit vector holds two 64-bit lanes, so at most one pair remains.
                ref byte tailTarget = ref Unsafe.Add(ref target, complete * pairSize);
                Unsafe.WriteUnaligned(ref tailTarget, Unsafe.Add(ref first, complete));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref tailTarget, sizeof(long)), Unsafe.Add(ref second, complete));
                return;
            }

            // At least one whole vector has already been written, possibly by InterleaveWide.
            // Rewrite its overlapping suffix so the final stores end at the last pair.
            int tail = count - lanes;
            Zip128<T, TZip>(ref Unsafe.Add(ref first, tail), ref Unsafe.Add(ref second, tail),
                ref Unsafe.Add(ref target, tail * pairSize));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Zip128<T, TZip>(ref T first, ref T second, ref byte target)
            where T : unmanaged where TZip : struct, IZip<T>
        {
            var a = Vector128.LoadUnsafe(ref first);
            var b = Vector128.LoadUnsafe(ref second);
            TZip.Low(a, b).AsByte().StoreUnsafe(ref target);
            TZip.High(a, b).AsByte().StoreUnsafe(ref target, 16);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InterleaveWide<T, TZip>(ref T first, ref T second, ref byte target, int count)
            where T : unmanaged where TZip : struct, IZip<T>
        {
            int pairSize = 2 * Unsafe.SizeOf<T>();
            if (Avx512F.IsSupported && (typeof(T) != typeof(short) || Avx512BW.IsSupported) &&
                count >= Vector512<T>.Count * 2 && Prefer512)
            {
                int widePairs = count & -Vector512<T>.Count;
                Interleave512<T, TZip>(ref first, ref second, ref target, widePairs);
                if (widePairs != count)
                    Interleave<T, TZip>(ref Unsafe.Add(ref first, widePairs), ref Unsafe.Add(ref second, widePairs),
                        ref Unsafe.Add(ref target, widePairs * pairSize), count - widePairs);
                return;
            }

            int pairs = count & -Vector256<T>.Count;
            Interleave256<T, TZip>(ref first, ref second, ref target, pairs);
            if (pairs != count)
            {
                int lanes = Vector128<T>.Count;
                // At most two narrow vectors can cover the tail. End the last one at the final pair.
                if (count - pairs > lanes)
                    Zip128<T, TZip>(ref Unsafe.Add(ref first, pairs), ref Unsafe.Add(ref second, pairs),
                        ref Unsafe.Add(ref target, pairs * pairSize));

                int last = count - lanes;
                Zip128<T, TZip>(ref Unsafe.Add(ref first, last), ref Unsafe.Add(ref second, last),
                    ref Unsafe.Add(ref target, last * pairSize));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Interleave512<T, TZip>(ref T first, ref T second, ref byte target, int count)
            where T : unmanaged where TZip : struct, IZip<T>
        {
            int lanes = Vector512<T>.Count;
            var lower = TZip.PairIndices;
            var upper = lower + Vector512.Create(TZip.HalfLanes);
            for (int i = 0; i < count; i += lanes)
            {
                var a = Vector512.LoadUnsafe(ref first, (nuint)i);
                var b = Vector512.LoadUnsafe(ref second, (nuint)i);
                TZip.Permute(a, lower, b).AsByte().StoreUnsafe(ref target);
                TZip.Permute(a, upper, b).AsByte().StoreUnsafe(ref target, 64);
                target = ref Unsafe.Add(ref target, 128);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Interleave256<T, TZip>(ref T first, ref T second, ref byte target, int count)
            where T : unmanaged where TZip : struct, IZip<T>
        {
            int lanes = Vector256<T>.Count;
            for (int i = 0; i < count; i += lanes)
            {
                var a = Vector256.LoadUnsafe(ref first, (nuint)i);
                var b = Vector256.LoadUnsafe(ref second, (nuint)i);
                var low = TZip.Low(a, b).AsByte();
                var high = TZip.High(a, b).AsByte();
                // Unpack interleaves within each 128-bit lane. Join the lanes in pair order.
                Avx2.Permute2x128(low, high, 0x20).StoreUnsafe(ref target);
                Avx2.Permute2x128(low, high, 0x31).StoreUnsafe(ref target, 32);
                target = ref Unsafe.Add(ref target, 64);
            }
        }

        internal interface IZip<T> where T : unmanaged
        {
            static abstract Vector128<T> Low(Vector128<T> a, Vector128<T> b);
            static abstract Vector128<T> High(Vector128<T> a, Vector128<T> b);
            static abstract Vector256<T> Low(Vector256<T> a, Vector256<T> b);
            static abstract Vector256<T> High(Vector256<T> a, Vector256<T> b);
            static abstract Vector512<T> PairIndices { get; }
            static abstract T HalfLanes { get; }
            static abstract Vector512<T> Permute(Vector512<T> a, Vector512<T> indices, Vector512<T> b);
        }

        internal readonly struct Zip16 : IZip<short>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<short> Low(Vector128<short> a, Vector128<short> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<short> High(Vector128<short> a, Vector128<short> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<short> Low(Vector256<short> a, Vector256<short> b) => Avx2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<short> High(Vector256<short> a, Vector256<short> b) => Avx2.UnpackHigh(a, b);

            public static Vector512<short> PairIndices
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Vector512.Create((short)0, 32, 1, 33, 2, 34, 3, 35, 4, 36, 5, 37, 6, 38, 7, 39,
                    8, 40, 9, 41, 10, 42, 11, 43, 12, 44, 13, 45, 14, 46, 15, 47);
            }

            public static short HalfLanes => 16;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<short> Permute(Vector512<short> a, Vector512<short> indices, Vector512<short> b) =>
                Avx512BW.PermuteVar32x16x2(a, indices, b);
        }

        internal readonly struct Zip32 : IZip<int>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<int> Low(Vector128<int> a, Vector128<int> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<int> High(Vector128<int> a, Vector128<int> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<int> Low(Vector256<int> a, Vector256<int> b) => Avx2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<int> High(Vector256<int> a, Vector256<int> b) => Avx2.UnpackHigh(a, b);

            public static Vector512<int> PairIndices
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Vector512.Create(0, 16, 1, 17, 2, 18, 3, 19, 4, 20, 5, 21, 6, 22, 7, 23);
            }

            public static int HalfLanes => 8;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<int> Permute(Vector512<int> a, Vector512<int> indices, Vector512<int> b) =>
                Avx512F.PermuteVar16x32x2(a, indices, b);
        }

        internal readonly struct Zip64 : IZip<long>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<long> Low(Vector128<long> a, Vector128<long> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<long> High(Vector128<long> a, Vector128<long> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<long> Low(Vector256<long> a, Vector256<long> b) => Avx2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<long> High(Vector256<long> a, Vector256<long> b) => Avx2.UnpackHigh(a, b);

            public static Vector512<long> PairIndices
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Vector512.Create(0L, 8, 1, 9, 2, 10, 3, 11);
            }

            public static long HalfLanes => 4;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<long> Permute(Vector512<long> a, Vector512<long> indices, Vector512<long> b) =>
                Avx512F.PermuteVar8x64x2(a, indices, b);
        }

        // The caller has checked the full block of pairs.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool AreStrictlyIncreasingInt32(ref byte source, int count)
        {
            if (count >= 32 && (AdvSimd.Arm64.IsSupported || Ssse3.IsSupported))
                return AreStrictlyIncreasingInt32Vector(ref source, count);

            return IncreasingInt32Tail(ref Unsafe.Add(ref source, 8), count - 1, NinoUnsafe.Read<int>(ref source));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool CanSplitOrderedPairs(int count) => AdvSimd.Arm64.IsSupported && count >= 32;

        // The destination arrays belong to an empty list. Publish its count only after the whole split succeeds.
        // Optimize from the first call so the Arm64 loop keeps the preceding group out of the OSR spill slot.
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static bool SplitOrderedPairs<T>(ref byte source, ref T first, ref T second, int count)
            where T : unmanaged, System.IComparable<T>
        {
            // The caller selects a signed or unsigned integer lane, including for an enum's underlying type.
            T previous = NinoUnsafe.Read<T>(ref source);
            first = previous;
            second = NinoUnsafe.Read<T>(ref Unsafe.Add(ref source, Unsafe.SizeOf<T>()));
            var precedingGroup = Vector128.Create(previous);
            int i = 1;
            // Keep the size and lane count intrinsic in the loop. OSR otherwise imports them as live locals.
            for (; i <= count - Vector128<T>.Count; i += Vector128<T>.Count)
            {
                nuint offset = (nuint)i * (uint)(2 * Unsafe.SizeOf<T>());
                var a = Vector128.LoadUnsafe(ref source, offset);
                var b = Vector128.LoadUnsafe(ref source, offset + 16);
                var keys = Unsafe.SizeOf<T>() switch
                {
                    1 => AdvSimd.Arm64.UnzipEven(a, b).As<byte, T>(),
                    2 => AdvSimd.Arm64.UnzipEven(a.AsInt16(), b.AsInt16()).As<short, T>(),
                    4 => AdvSimd.Arm64.UnzipEven(a.AsInt32(), b.AsInt32()).As<int, T>(),
                    _ => AdvSimd.Arm64.UnzipEven(a.AsInt64(), b.AsInt64()).As<long, T>(),
                };
                var previousBytes = precedingGroup.AsByte();
                var keyBytes = keys.AsByte();
                var preceding = (Unsafe.SizeOf<T>() switch
                {
                    1 => AdvSimd.ExtractVector128(previousBytes, keyBytes, 15),
                    2 => AdvSimd.ExtractVector128(previousBytes, keyBytes, 14),
                    4 => AdvSimd.ExtractVector128(previousBytes, keyBytes, 12),
                    _ => AdvSimd.ExtractVector128(previousBytes, keyBytes, 8),
                }).As<byte, T>();
                if (!Vector128.GreaterThanAll(keys, preceding))
                    return false;

                keys.StoreUnsafe(ref first, (nuint)i);
                var values = Unsafe.SizeOf<T>() switch
                {
                    1 => AdvSimd.Arm64.UnzipOdd(a, b).As<byte, T>(),
                    2 => AdvSimd.Arm64.UnzipOdd(a.AsInt16(), b.AsInt16()).As<short, T>(),
                    4 => AdvSimd.Arm64.UnzipOdd(a.AsInt32(), b.AsInt32()).As<int, T>(),
                    _ => AdvSimd.Arm64.UnzipOdd(a.AsInt64(), b.AsInt64()).As<long, T>(),
                };
                values.StoreUnsafe(ref second, (nuint)i);
                precedingGroup = keys;
            }

            previous = precedingGroup.GetElement(Vector128<T>.Count - 1);
            for (; i < count; i++)
            {
                ref byte pair = ref Unsafe.Add(ref source, i * (2 * Unsafe.SizeOf<T>()));
                T key = NinoUnsafe.Read<T>(ref pair);
                if (key.CompareTo(previous) <= 0)
                    return false;

                Unsafe.Add(ref first, i) = previous = key;
                Unsafe.Add(ref second, i) = NinoUnsafe.Read<T>(ref Unsafe.Add(ref pair, Unsafe.SizeOf<T>()));
            }

            return true;
        }

        // Compare four adjacent keys at a time, carrying the previous group across the boundary.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool AreStrictlyIncreasingInt32Vector(ref byte source, int count)
        {
            var previous = Vector128.Create(NinoUnsafe.Read<int>(ref source));
            source = ref Unsafe.Add(ref source, 8);
            count--;
            while (count >= 4)
            {
                var a = Vector128.LoadUnsafe(ref source).AsInt32();
                var b = Vector128.LoadUnsafe(ref source, 16).AsInt32();
                var keys = EvenInt32(a, b);
                var preceding = AdvSimd.Arm64.IsSupported
                    ? AdvSimd.ExtractVector128(previous, keys, 3)
                    : Ssse3.AlignRight(keys.AsByte(), previous.AsByte(), 12).AsInt32();
                if (!Vector128.GreaterThanAll(keys, preceding))
                    return false;

                previous = keys;
                source = ref Unsafe.Add(ref source, 32);
                count -= 4;
            }

            return IncreasingInt32Tail(ref source, count, previous.GetElement(3));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IncreasingInt32Tail(ref byte source, int count, int previous)
        {
            for (int i = 0; i < count; i++)
            {
                int key = NinoUnsafe.Read<int>(ref Unsafe.Add(ref source, i * 8));
                if (key <= previous)
                    return false;
                previous = key;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<int> EvenInt32(Vector128<int> a, Vector128<int> b) =>
            AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipEven(a, b)
                : Sse.Shuffle(a.AsSingle(), b.AsSingle(), 0x88).AsInt32();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeinterleaveInt32Wide(ref byte source, ref int first, ref int second, int count)
        {
            int pairs;
            if (Avx512F.IsSupported && count >= 2 * Vector512<int>.Count && Prefer512)
            {
                pairs = count & -Vector512<int>.Count;
                DeinterleaveInt32_512(ref source, ref first, ref second, pairs);
            }
            else
            {
                pairs = count & -Vector256<int>.Count;
                DeinterleaveInt32_256(ref source, ref first, ref second, pairs);
            }

            // The remainder is below the wide dispatch threshold.
            if (pairs != count)
                DeinterleaveInt32(ref Unsafe.Add(ref source, pairs * 8), ref Unsafe.Add(ref first, pairs),
                    ref Unsafe.Add(ref second, pairs), count - pairs);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DeinterleaveInt32_512(ref byte source, ref int first, ref int second, int count)
        {
            var even = Vector512.Create(0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30);
            var odd = even + Vector512.Create(1);
            for (int i = 0; i < count; i += Vector512<int>.Count)
            {
                var a = Vector512.LoadUnsafe(ref source, (nuint)i * 8).AsInt32();
                var b = Vector512.LoadUnsafe(ref source, (nuint)i * 8 + 64).AsInt32();
                Avx512F.PermuteVar16x32x2(a, even, b).StoreUnsafe(ref first, (nuint)i);
                Avx512F.PermuteVar16x32x2(a, odd, b).StoreUnsafe(ref second, (nuint)i);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DeinterleaveInt32_256(ref byte source, ref int first, ref int second, int count)
        {
            for (int i = 0; i < count; i += Vector256<int>.Count)
            {
                var a = Vector256.LoadUnsafe(ref source, (nuint)i * 8).AsSingle();
                var b = Vector256.LoadUnsafe(ref source, (nuint)i * 8 + 32).AsSingle();
                // Shuffle selects alternating values per 128-bit lane. Reorder the resulting 64-bit groups.
                var keys = Avx.Shuffle(a, b, 0x88).AsInt64();
                var values = Avx.Shuffle(a, b, 0xDD).AsInt64();
                Avx2.Permute4x64(keys, 0xD8).AsInt32().StoreUnsafe(ref first, (nuint)i);
                Avx2.Permute4x64(values, 0xD8).AsInt32().StoreUnsafe(ref second, (nuint)i);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void DeinterleaveInt32(ref byte source, ref int first, ref int second, int count)
        {
            if (Avx2.IsSupported && count >= 2 * Vector256<int>.Count)
            {
                DeinterleaveInt32Wide(ref source, ref first, ref second, count);
                return;
            }

            if (AdvSimd.Arm64.IsSupported || Sse2.IsSupported)
            {
                while (count >= Vector128<int>.Count)
                {
                    var a = Vector128.LoadUnsafe(ref source).AsInt32();
                    var b = Vector128.LoadUnsafe(ref source, 16).AsInt32();
                    var keys = EvenInt32(a, b);
                    var values = OddInt32(a, b);
                    keys.StoreUnsafe(ref first);
                    values.StoreUnsafe(ref second);
                    source = ref Unsafe.Add(ref source, 32);
                    first = ref Unsafe.Add(ref first, 4);
                    second = ref Unsafe.Add(ref second, 4);
                    count -= 4;
                }
            }

            for (int i = 0; i < count; i++)
            {
                Unsafe.Add(ref first, i) = NinoUnsafe.Read<int>(ref Unsafe.Add(ref source, i * 8));
                Unsafe.Add(ref second, i) = NinoUnsafe.Read<int>(ref Unsafe.Add(ref source, i * 8 + 4));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Vector128<int> OddInt32(Vector128<int> a, Vector128<int> b) =>
            AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.UnzipOdd(a, b)
                : Sse.Shuffle(a.AsSingle(), b.AsSingle(), 0xDD).AsInt32();

#else
        /// <summary>Interleaves full vectors from checked, canonical four-byte arrays without padding.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int TryInterleave32<TKey, TValue>(ref TKey keys, ref TValue values, ref byte target, int count)
        {
            if (count < NinoKernelHook.MinInterleave || !IsPackedLane32<TKey>() || !IsPackedLane32<TValue>())
                return 0;

            return NinoKernelHook.TryInterleave32(ref Unsafe.As<TKey, int>(ref keys),
                ref Unsafe.As<TValue, int>(ref values), ref target, count);
        }

#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsPackedLane32<T>() => Unsafe.SizeOf<T>() == 4 && PackedLane32<T>.Value;

        private static class PackedLane32<T>
        {
            internal static readonly bool Value = typeof(T).IsPrimitive || !NinoLayout<T>.HasPadding;
        }
    }
}
