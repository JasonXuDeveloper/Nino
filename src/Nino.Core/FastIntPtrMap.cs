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
        private const int SimdThreshold = 128;

        public FastMap(int capacity = 16)
        {
            int safeCap = Math.Max(4, capacity);
            _keys = new TKey[safeCap];
            _values = new TValue[safeCap];
            _count = 0;
        }

        public int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(TKey key, TValue value)
        {
            int index = Array.BinarySearch(_keys, 0, _count, key);
            if (index >= 0)
                return;

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
            
            int index = (_count <= SimdThreshold && Vector.IsHardwareAccelerated)
                ? SimdSearch(key)
                : BinarySearch(key);

            if (index >= 0)
            {
                value = _values[index];
                return true;
            }

            value = default!;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(TKey key) =>
            (_count <= SimdThreshold && Vector.IsHardwareAccelerated)
                ? SimdSearch(key) >= 0
                : BinarySearch(key) >= 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int BinarySearch(TKey key)
        {
            return Array.BinarySearch(_keys, 0, _count, key);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int SimdSearch(TKey key)
        {
            int i = 0;
            int simdSize = Vector<TKey>.Count;
            int simdEnd = _count - (_count % simdSize);

            var span = _keys.AsSpan();
            ref TKey baseRef = ref MemoryMarshal.GetReference(span);
            var keyVec = new Vector<TKey>(key);

            while (i < simdEnd)
            {
                var vec = new Vector<TKey>(Unsafe.Add(ref baseRef, i));
                if (Vector.EqualsAny(vec, keyVec))
                {
                    for (int j = 0; j < simdSize; j++)
                    {
                        if (_keys[i + j].Equals(key))
                            return i + j;
                    }
                }

                i += simdSize;
            }

            // Scalar tail
            for (; i < _count; i++)
            {
                if (_keys[i].Equals(key))
                    return i;
            }

            return -1;
        }
    }
}