using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Nino.Core.Internal
{
    internal static partial class NinoBlockCollections
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

            Interleave<T, TZip>(ref Unsafe.As<TKey, T>(ref NinoUnsafe.GetArrayDataReference(first)),
                ref Unsafe.As<TValue, T>(ref NinoUnsafe.GetArrayDataReference(second)), ref target, count);
            return true;
        }

        /// <summary>Interleaves two checked raw arrays into one little-endian block of pairs.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Interleave<T, TZip>(ref T first, ref T second, ref byte target, int count)
            where T : unmanaged where TZip : struct, IZip<T>
        {
            int lanes = Vector128<T>.Count;
            int size = Unsafe.SizeOf<T>();
            int pairSize = size * 2;
            while (count >= lanes)
            {
                var a = Vector128.LoadUnsafe(ref first);
                var b = Vector128.LoadUnsafe(ref second);
                TZip.Low(a, b).AsByte().StoreUnsafe(ref target);
                TZip.High(a, b).AsByte().StoreUnsafe(ref target, 16);
                first = ref Unsafe.Add(ref first, lanes);
                second = ref Unsafe.Add(ref second, lanes);
                target = ref Unsafe.Add(ref target, 32);
                count -= lanes;
            }

            for (int i = 0; i < count; i++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i * pairSize), Unsafe.Add(ref first, i));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref target, i * pairSize + size), Unsafe.Add(ref second, i));
            }
        }

        internal interface IZip<T> where T : unmanaged
        {
            static abstract Vector128<T> Low(Vector128<T> a, Vector128<T> b);
            static abstract Vector128<T> High(Vector128<T> a, Vector128<T> b);
        }

        internal readonly struct Zip16 : IZip<short>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<short> Low(Vector128<short> a, Vector128<short> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<short> High(Vector128<short> a, Vector128<short> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);
        }

        internal readonly struct Zip32 : IZip<int>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<int> Low(Vector128<int> a, Vector128<int> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<int> High(Vector128<int> a, Vector128<int> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);
        }

        internal readonly struct Zip64 : IZip<long>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<long> Low(Vector128<long> a, Vector128<long> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipLow(a, b) : Sse2.UnpackLow(a, b);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<long> High(Vector128<long> a, Vector128<long> b) =>
                AdvSimd.Arm64.IsSupported ? AdvSimd.Arm64.ZipHigh(a, b) : Sse2.UnpackHigh(a, b);
        }

        // The caller has checked the full block of pairs.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool AreStrictlyIncreasingInt32(ref byte source, int count)
        {
            if (count >= 32 && (AdvSimd.Arm64.IsSupported || Ssse3.IsSupported))
                return AreStrictlyIncreasingInt32Vector(ref source, count);

            return IncreasingInt32Tail(ref Unsafe.Add(ref source, 8), count - 1, NinoUnsafe.Read<int>(ref source));
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
        internal static void DeinterleaveInt32(ref byte source, ref int first, ref int second, int count)
        {
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
