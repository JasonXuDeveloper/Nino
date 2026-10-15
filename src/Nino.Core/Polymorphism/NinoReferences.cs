using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Objects already written in one serialization call, for types marked
    /// <c>[NinoType(trackReferences: true)]</c>. Rented on the first tracked object (one cached table per
    /// thread), so calls without tracked objects touch nothing.
    /// <para>
    /// Open addressing on the object identity hash, kept at most half full; avoids the interface calls of a
    /// <c>Dictionary</c> with a custom comparer.
    /// </para>
    /// </summary>
    internal sealed class NinoReferenceWriteTable
    {
        [ThreadStatic] private static NinoReferenceWriteTable t_cached;

        private object[] _keys = new object[16];
        private int[] _values = new int[16];
        private int _count;

        public static NinoReferenceWriteTable Rent() =>
            NinoThreadCache.Take(ref t_cached) ?? new NinoReferenceWriteTable();

        public static void Return(NinoReferenceWriteTable table)
        {
            if (table._keys.Length > NinoThreadCache.MaxRetainedTableCapacity)
                return;
            if (table._count != 0)
            {
                Array.Clear(table._keys, 0, table._keys.Length);
                table._count = 0;
            }

            t_cached = table;
        }

        /// <summary>Returns the 1-based index of an object already written, or 0 after adding a new one.</summary>
        public int GetOrAdd(object value)
        {
            var keys = _keys;
            int mask = keys.Length - 1;
            int i = RuntimeHelpers.GetHashCode(value) & mask;
            while (true)
            {
                var key = keys[i];
                if (key == null)
                    break;
                if (ReferenceEquals(key, value))
                    return _values[i];
                i = (i + 1) & mask;
            }

            keys[i] = value;
            _values[i] = ++_count;
            if (_count * 2 > keys.Length)
                Grow();
            return 0;
        }

        private void Grow()
        {
            var oldKeys = _keys;
            var oldValues = _values;
            var keys = new object[oldKeys.Length * 2];
            var values = new int[keys.Length];
            int mask = keys.Length - 1;
            for (int j = 0; j < oldKeys.Length; j++)
            {
                var key = oldKeys[j];
                if (key == null)
                    continue;
                int i = RuntimeHelpers.GetHashCode(key) & mask;
                while (keys[i] != null)
                    i = (i + 1) & mask;
                keys[i] = key;
                values[i] = oldValues[j];
            }

            _keys = keys;
            _values = values;
        }
    }

    /// <summary>Objects read so far in one deserialization call (index i - 1 holds object i).</summary>
    internal sealed class NinoReferenceReadTable
    {
        [ThreadStatic] private static NinoReferenceReadTable t_cached;

        private object[] _objects = new object[8];
        private int _count;

        // Index of the object whose instance has not been recorded yet (0 = none).
        public int Pending;

        public static NinoReferenceReadTable Rent() =>
            NinoThreadCache.Take(ref t_cached) ?? new NinoReferenceReadTable();

        public static void Return(NinoReferenceReadTable table)
        {
            if (table._objects.Length > NinoThreadCache.MaxRetainedTableCapacity)
                return;
            Array.Clear(table._objects, 0, table._count);
            table._count = 0;
            table.Pending = 0;
            t_cached = table;
        }

        public int Begin()
        {
            if (_count == _objects.Length)
                Array.Resize(ref _objects, _count * 2);
            _objects[_count++] = null;
            Pending = _count;
            return _count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index, object value)
        {
            _objects[index - 1] = value;
            if (Pending == index)
                Pending = 0;
        }

        public object Get(int index)
        {
            if ((uint)(index - 1) >= (uint)_count)
                NinoThrow.InvalidReference(index);
            var value = _objects[index - 1];
            if (value == null)
                NinoThrow.IncompleteReference(index);
            return value;
        }
    }
}
