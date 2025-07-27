namespace Nino.Core
{
    using System;
    using System.Numerics;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;

    public sealed class FastMap<TKey, TValue> where TKey : unmanaged, IEquatable<TKey>
    {
        private TKey[] _keys;
        private TValue[] _values;
        private int _count;

        public FastMap(int capacity = 16)
        {
            int safeCap = Math.Max(4, capacity);
            _keys = new TKey[safeCap];
            _values = new TValue[safeCap];
            _count = 0;
        }

        public ReadOnlySpan<TKey> Keys => _keys.AsSpan(0, _count);

        public int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, TValue value)
        {
            int index = CustomBinarySearch(key);
            if (index >= 0)
            {
                // Key already exists, update the value
                _values[index] = value;
                return;
            }

            int insertAt = ~index;

            if (_count >= _keys.Length)
                Grow();

            if (insertAt < _count)
            {
                Array.Copy(_keys, insertAt, _keys, insertAt + 1, _count - insertAt);
                Array.Copy(_values, insertAt, _values, insertAt + 1, _count - insertAt);
            }

            _keys[insertAt] = key;
            _values[insertAt] = value;
            _count++;
        }

        private void Grow()
        {
            int newSize = Math.Max(4, _keys.Length * 2);
            Array.Resize(ref _keys, newSize);
            Array.Resize(ref _values, newSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_count == 0)
            {
                value = default!;
                return false;
            }

            int index = _count <= 16 ? LinearSearch(key) :
                _count <= 128 ? SimdSearch(key) : BinarySearch(key);

            if (index >= 0)
            {
                value = _values[index];
                return true;
            }

            value = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key) =>
            _count <= 16 ? LinearSearch(key) >= 0 :
            _count <= 128 ? SimdSearch(key) >= 0 : BinarySearch(key) >= 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int BinarySearch(TKey key)
        {
            return CustomBinarySearch(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int CustomBinarySearch(TKey key)
        {
            if (_count == 0)
                return ~0;

            int left = 0;
            int right = _count - 1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var midKey = _keys[mid];

                int comparison = CompareKeys(midKey, key);

                if (comparison == 0)
                    return mid;
                if (comparison < 0)
                    left = mid + 1;
                else
                    right = mid - 1;
            }

            return ~left;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int CompareKeys(TKey left, TKey right)
        {
            // Handle specific key types without requiring IComparable
            if (typeof(TKey) == typeof(IntPtr))
            {
                var leftPtr = Unsafe.As<TKey, IntPtr>(ref left);
                var rightPtr = Unsafe.As<TKey, IntPtr>(ref right);
                return leftPtr.ToInt64().CompareTo(rightPtr.ToInt64());
            }

            if (typeof(TKey) == typeof(int))
            {
                var leftInt = Unsafe.As<TKey, int>(ref left);
                var rightInt = Unsafe.As<TKey, int>(ref right);
                return leftInt.CompareTo(rightInt);
            }

            if (typeof(TKey) == typeof(uint))
            {
                var leftUint = Unsafe.As<TKey, uint>(ref left);
                var rightUint = Unsafe.As<TKey, uint>(ref right);
                return leftUint.CompareTo(rightUint);
            }

            if (typeof(TKey) == typeof(long))
            {
                var leftLong = Unsafe.As<TKey, long>(ref left);
                var rightLong = Unsafe.As<TKey, long>(ref right);
                return leftLong.CompareTo(rightLong);
            }

            if (typeof(TKey) == typeof(ulong))
            {
                var leftUlong = Unsafe.As<TKey, ulong>(ref left);
                var rightUlong = Unsafe.As<TKey, ulong>(ref right);
                return leftUlong.CompareTo(rightUlong);
            }

            // Fallback for other types - this should work for IComparable types
            return ((IComparable<TKey>)left).CompareTo(right);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int LinearSearch(TKey key)
        {
            var keySpan = _keys.AsSpan(0, _count);

            for (int i = 0; i < keySpan.Length; i++)
            {
                if (keySpan[i].Equals(key))
                    return i;
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SimdSearch(TKey key)
        {
            if (typeof(TKey) == typeof(IntPtr))
            {
                return SimdSearchIntPtr(Unsafe.As<TKey, IntPtr>(ref key));
            }

            // Only use long SIMD if TKey is actually 8 bytes
            if (Unsafe.SizeOf<TKey>() == 8 && (typeof(TKey) == typeof(long) || typeof(TKey) == typeof(ulong)))
            {
                return SimdSearchLong(Unsafe.As<TKey, long>(ref key));
            }

            // For 32-bit integers, use int SIMD
            if (Unsafe.SizeOf<TKey>() == 4 && (typeof(TKey) == typeof(int) || typeof(TKey) == typeof(uint)))
            {
                return SimdSearchInt(Unsafe.As<TKey, int>(ref key));
            }

            // Fallback for other types
            return LinearSearch(key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SimdSearchIntPtr(IntPtr key)
        {
            var keySpan = _keys.AsSpan(0, _count);

            // Ensure safe casting - only cast if sizes match
            if (Unsafe.SizeOf<TKey>() != IntPtr.Size)
            {
                return LinearSearch(Unsafe.As<IntPtr, TKey>(ref key));
            }

            var intPtrSpan = MemoryMarshal.Cast<TKey, IntPtr>(keySpan);

            if (Vector.IsHardwareAccelerated && intPtrSpan.Length >= Vector<IntPtr>.Count)
            {
                var keyVec = new Vector<IntPtr>(key);
                int vectorLength = Vector<IntPtr>.Count;
                int i = 0;

                for (; i <= intPtrSpan.Length - vectorLength; i += vectorLength)
                {
                    var slice = intPtrSpan.Slice(i, vectorLength);
                    var vec = new Vector<IntPtr>(slice);

                    if (Vector.EqualsAny(vec, keyVec))
                    {
                        // Linear search within this vector segment
                        for (int j = 0; j < vectorLength; j++)
                        {
                            if (intPtrSpan[i + j] == key)
                                return i + j;
                        }
                    }
                }

                // Handle remaining elements
                for (; i < intPtrSpan.Length; i++)
                {
                    if (intPtrSpan[i] == key)
                        return i;
                }
            }
            else
            {
                // Linear search for small arrays or when SIMD not available
                for (int i = 0; i < intPtrSpan.Length; i++)
                {
                    if (intPtrSpan[i] == key)
                        return i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SimdSearchLong(long key)
        {
            var keySpan = _keys.AsSpan(0, _count);

            // Ensure safe casting - only cast if sizes match (8 bytes)
            if (Unsafe.SizeOf<TKey>() != 8)
            {
                return LinearSearch(Unsafe.As<long, TKey>(ref key));
            }

            var longSpan = MemoryMarshal.Cast<TKey, long>(keySpan);

            if (Vector.IsHardwareAccelerated && longSpan.Length >= Vector<long>.Count)
            {
                var keyVec = new Vector<long>(key);
                int vectorLength = Vector<long>.Count;
                int i = 0;

                for (; i <= longSpan.Length - vectorLength; i += vectorLength)
                {
                    var slice = longSpan.Slice(i, vectorLength);
                    var vec = new Vector<long>(slice);

                    if (Vector.EqualsAny(vec, keyVec))
                    {
                        for (int j = 0; j < vectorLength; j++)
                        {
                            if (longSpan[i + j] == key)
                                return i + j;
                        }
                    }
                }

                for (; i < longSpan.Length; i++)
                {
                    if (longSpan[i] == key)
                        return i;
                }
            }
            else
            {
                for (int i = 0; i < longSpan.Length; i++)
                {
                    if (longSpan[i] == key)
                        return i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SimdSearchInt(int key)
        {
            var keySpan = _keys.AsSpan(0, _count);

            // Ensure safe casting - only cast if sizes match (4 bytes)
            if (Unsafe.SizeOf<TKey>() != 4)
            {
                return LinearSearch(Unsafe.As<int, TKey>(ref key));
            }

            var intSpan = MemoryMarshal.Cast<TKey, int>(keySpan);

            if (Vector.IsHardwareAccelerated && intSpan.Length >= Vector<int>.Count)
            {
                var keyVec = new Vector<int>(key);
                int vectorLength = Vector<int>.Count;
                int i = 0;

                for (; i <= intSpan.Length - vectorLength; i += vectorLength)
                {
                    var slice = intSpan.Slice(i, vectorLength);
                    var vec = new Vector<int>(slice);

                    if (Vector.EqualsAny(vec, keyVec))
                    {
                        for (int j = 0; j < vectorLength; j++)
                        {
                            if (intSpan[i + j] == key)
                                return i + j;
                        }
                    }
                }

                for (; i < intSpan.Length; i++)
                {
                    if (intSpan[i] == key)
                        return i;
                }
            }
            else
            {
                for (int i = 0; i < intSpan.Length; i++)
                {
                    if (intSpan[i] == key)
                        return i;
                }
            }

            return -1;
        }
    }
}