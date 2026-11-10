using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Nino.Core.Internal
{
    /// <summary>
    /// Hash codes keyed with a secret drawn once per process (SipHash-1-3, 128-bit key): data cannot be built to make
    /// keys collide without knowing it. The comparers Nino gives the dictionaries and sets it creates in
    /// <see cref="NinoSettings.Untrusted"/> mode, for keys whose default hash code anyone can predict (integers, chars,
    /// <see cref="Guid"/>, and strings, whose default hash is randomized on .NET but not on .NET Framework, Mono or
    /// IL2CPP). Equality stays the default one.
    /// </summary>
    internal static class NinoSeededHash
    {
        private static readonly ulong K0, K1;

        static NinoSeededHash()
        {
            var key = new byte[16];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(key);
            K0 = BitConverter.ToUInt64(key, 0);
            K1 = BitConverter.ToUInt64(key, 8);
        }

        /// <summary>The seeded comparer for <typeparamref name="T"/>, or null when it has none.</summary>
        public static IEqualityComparer<T> For<T>() => Comparers<T>.Value;

        /// <summary>
        /// A new dictionary for <paramref name="capacity"/> pairs, with the seeded comparer of its keys
        /// (<see cref="Keys{T}.Comparer"/>, the default one for other keys). For <see cref="NinoSettings.Untrusted"/>
        /// mode only: callers test <see cref="NinoLimits.Untrusted"/> first and create the table themselves otherwise,
        /// so trusted code keeps its own <c>new</c> (in shared generic code, even a call folded away here would keep a
        /// runtime lookup of this method's instantiation).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Dictionary<TKey, TValue> NewDictionary<TKey, TValue>(int capacity) =>
            new Dictionary<TKey, TValue>(capacity, Keys<TKey>.Comparer);

        /// <summary>A new set, as <see cref="NewDictionary{TKey,TValue}"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static HashSet<T> NewSet<T>() => new HashSet<T>(Keys<T>.Comparer);

        /// <summary>The comparer Nino gives the tables it creates for keys of <typeparamref name="T"/>.</summary>
        internal static class Keys<T>
        {
            /// <summary>Null (the default comparer) unless <see cref="NinoSettings.Untrusted"/>.</summary>
            public static readonly IEqualityComparer<T> Comparer = NinoLimits.Untrusted ? For<T>() : null;
        }

        private static class Comparers<T>
        {
            public static readonly IEqualityComparer<T> Value = Create();

            private static IEqualityComparer<T> Create()
            {
                object comparer = null;
                if (typeof(T) == typeof(string))
                    comparer = new StringComparer();
                else if (typeof(T) == typeof(Guid))
                    comparer = new GuidComparer();
                else if (typeof(T) == typeof(int) || typeof(T) == typeof(uint) || typeof(T) == typeof(long) ||
                         typeof(T) == typeof(ulong) || typeof(T) == typeof(short) || typeof(T) == typeof(ushort) ||
                         typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte) || typeof(T) == typeof(char))
                    comparer = new IntegerComparer<T>();
                return (IEqualityComparer<T>)comparer;
            }
        }

        private sealed class IntegerComparer<T> : IEqualityComparer<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Equals(T x, T y) => EqualityComparer<T>.Default.Equals(x, y);

            public int GetHashCode(T value)
            {
                // Sign-extended to 64 bits: equal values, equal words.
                ulong word = Unsafe.SizeOf<T>() switch
                {
                    1 => typeof(T) == typeof(sbyte) ? (ulong)Unsafe.As<T, sbyte>(ref value) : Unsafe.As<T, byte>(ref value),
                    2 => typeof(T) == typeof(short) ? (ulong)Unsafe.As<T, short>(ref value) : Unsafe.As<T, ushort>(ref value),
                    4 => typeof(T) == typeof(int) ? (ulong)Unsafe.As<T, int>(ref value) : Unsafe.As<T, uint>(ref value),
                    _ => Unsafe.As<T, ulong>(ref value),
                };
                return Hash(word, (ulong)Unsafe.SizeOf<T>() << 56);
            }
        }

        private sealed class GuidComparer : IEqualityComparer<Guid>
        {
            public bool Equals(Guid x, Guid y) => x.Equals(y);

            public int GetHashCode(Guid value)
            {
                ulong v0 = K0 ^ 0x736f6d6570736575UL, v1 = K1 ^ 0x646f72616e646f6dUL;
                ulong v2 = K0 ^ 0x6c7967656e657261UL, v3 = K1 ^ 0x7465646279746573UL;
                ulong lo = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<Guid, byte>(ref value));
                ulong hi = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref Unsafe.As<Guid, byte>(ref value), 8));
                Compress(ref v0, ref v1, ref v2, ref v3, lo);
                Compress(ref v0, ref v1, ref v2, ref v3, hi);
                return Finish(v0, v1, v2, v3, 16UL << 56);
            }
        }

        private sealed class StringComparer : IEqualityComparer<string>
        {
            public bool Equals(string x, string y) => string.Equals(x, y, StringComparison.Ordinal);

            public int GetHashCode(string value)
            {
                if (value == null)
                    return 0;
                ulong v0 = K0 ^ 0x736f6d6570736575UL, v1 = K1 ^ 0x646f72616e646f6dUL;
                ulong v2 = K0 ^ 0x6c7967656e657261UL, v3 = K1 ^ 0x7465646279746573UL;
                int length = value.Length, i = 0;
                // Four chars a word.
                for (; i + 4 <= length; i += 4)
                    Compress(ref v0, ref v1, ref v2, ref v3,
                        value[i] | (ulong)value[i + 1] << 16 | (ulong)value[i + 2] << 32 | (ulong)value[i + 3] << 48);
                ulong last = (ulong)(length * 2) << 56;
                for (int shift = 0; i < length; i++, shift += 16)
                    last |= (ulong)value[i] << shift;
                return Finish(v0, v1, v2, v3, last);
            }
        }

        private static int Hash(ulong word, ulong last)
        {
            ulong v0 = K0 ^ 0x736f6d6570736575UL, v1 = K1 ^ 0x646f72616e646f6dUL;
            ulong v2 = K0 ^ 0x6c7967656e657261UL, v3 = K1 ^ 0x7465646279746573UL;
            Compress(ref v0, ref v1, ref v2, ref v3, word);
            return Finish(v0, v1, v2, v3, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Compress(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3, ulong m)
        {
            v3 ^= m;
            Round(ref v0, ref v1, ref v2, ref v3);
            v0 ^= m;
        }

        // The last word (length in its top byte), then three rounds.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Finish(ulong v0, ulong v1, ulong v2, ulong v3, ulong last)
        {
            Compress(ref v0, ref v1, ref v2, ref v3, last);
            v2 ^= 0xff;
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            Round(ref v0, ref v1, ref v2, ref v3);
            ulong hash = v0 ^ v1 ^ v2 ^ v3;
            return (int)hash ^ (int)(hash >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Round(ref ulong v0, ref ulong v1, ref ulong v2, ref ulong v3)
        {
            v0 += v1;
            v1 = Rotate(v1, 13);
            v1 ^= v0;
            v0 = Rotate(v0, 32);
            v2 += v3;
            v3 = Rotate(v3, 16);
            v3 ^= v2;
            v0 += v3;
            v3 = Rotate(v3, 21);
            v3 ^= v0;
            v2 += v1;
            v1 = Rotate(v1, 17);
            v1 ^= v2;
            v2 = Rotate(v2, 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Rotate(ulong value, int bits) => (value << bits) | (value >> (64 - bits));
    }
}
