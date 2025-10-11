using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
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
        
        public ReadOnlySpan<TValue> Values => _values.AsSpan(0, _count);

        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _count;
        }

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

        public void Remove(TKey key)
        {
            if (_count == 0)
                return;

            int index = CustomBinarySearch(key);
            if (index < 0)
                return; // Key not found

            // Shift elements to remove the key
            if (index < _count - 1)
            {
                Array.Copy(_keys, index + 1, _keys, index, _count - index - 1);
                Array.Copy(_values, index + 1, _values, index, _count - index - 1);
            }

            _keys[--_count] = default; // Clear the last element
            _values[_count] = default;
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

            // For typical subtype counts (10-200), linear search is often faster
            // than binary search due to better cache locality and branch prediction
            int index = _count <= 64 ? LinearSearch(key) : BinarySearch(key);

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
            _count <= 64 ? LinearSearch(key) >= 0 : BinarySearch(key) >= 0;

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
            // Optimized linear search avoiding Equals() call overhead
            var keys = _keys;
            int count = _count;

            // Fast path for common key types - avoid virtual/interface calls
            if (typeof(TKey) == typeof(IntPtr))
            {
                var targetKey = Unsafe.As<TKey, IntPtr>(ref key);
                var intPtrKeys = Unsafe.As<TKey[], IntPtr[]>(ref keys);
                for (int i = 0; i < count; i++)
                {
                    if (intPtrKeys[i] == targetKey)
                        return i;
                }

                return -1;
            }

            if (typeof(TKey) == typeof(int))
            {
                var targetKey = Unsafe.As<TKey, int>(ref key);
                var intKeys = Unsafe.As<TKey[], int[]>(ref keys);
                for (int i = 0; i < count; i++)
                {
                    if (intKeys[i] == targetKey)
                        return i;
                }

                return -1;
            }

            if (typeof(TKey) == typeof(uint))
            {
                var targetKey = Unsafe.As<TKey, uint>(ref key);
                var uintKeys = Unsafe.As<TKey[], uint[]>(ref keys);
                for (int i = 0; i < count; i++)
                {
                    if (uintKeys[i] == targetKey)
                        return i;
                }

                return -1;
            }

            if (typeof(TKey) == typeof(long))
            {
                var targetKey = Unsafe.As<TKey, long>(ref key);
                var longKeys = Unsafe.As<TKey[], long[]>(ref keys);
                for (int i = 0; i < count; i++)
                {
                    if (longKeys[i] == targetKey)
                        return i;
                }

                return -1;
            }

            if (typeof(TKey) == typeof(ulong))
            {
                var targetKey = Unsafe.As<TKey, ulong>(ref key);
                var ulongKeys = Unsafe.As<TKey[], ulong[]>(ref keys);
                for (int i = 0; i < count; i++)
                {
                    if (ulongKeys[i] == targetKey)
                        return i;
                }

                return -1;
            }

            // Fallback for other types
            for (int i = 0; i < count; i++)
            {
                if (keys[i].Equals(key))
                    return i;
            }

            return -1;
        }
    }
}
