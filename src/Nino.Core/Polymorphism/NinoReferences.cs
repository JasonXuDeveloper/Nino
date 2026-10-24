using System;
using System.Runtime.CompilerServices;
using Nino.Core.Generated;

namespace Nino.Core
{
    /// <summary>
    /// Objects already written in one serialization call, for types marked
    /// <c>[NinoType(trackReferences: true)]</c>. Rented on the first tracked object (one cached table per
    /// thread), so calls without tracked objects touch nothing.
    /// <para>
    /// Open addressing on the object identity hash, kept at most half full; avoids the interface calls of a
    /// <c>Dictionary</c> with a custom comparer. Each entry holds the object and its index side by side, so a hit
    /// reads one cache line; the slots in use are listed in insertion order, so returning the table clears those
    /// slots only, however large the table grew in an earlier call.
    /// </para>
    /// </summary>
    internal sealed class NinoReferenceWriteTable
    {
        [ThreadStatic] private static NinoReferenceWriteTable t_cached;

        private struct Entry
        {
            public object Key;
            public int Value;
        }

        private Entry[] _entries = new Entry[16];

        // The slot of object i + 1 (at most half the slots are used, plus the one that triggers growth).
        private int[] _slots = new int[16 / 2 + 1];
        private int _count;

        public static NinoReferenceWriteTable Rent() =>
            NinoThreadCache.Take(ref t_cached) ?? new NinoReferenceWriteTable();

        public static void Return(NinoReferenceWriteTable table)
        {
            if (table._entries.Length > NinoThreadCache.MaxRetainedTableCapacity)
                return;
            // Each of the first _count slots holds an entry's index (GetOrAdd): no bounds checks.
            ref var entries = ref NinoUnsafe.GetArrayDataReference(table._entries);
            ref int slot = ref NinoUnsafe.GetArrayDataReference(table._slots);
            for (int k = table._count; k > 0; k--, slot = ref Unsafe.Add(ref slot, 1))
                Unsafe.Add(ref entries, slot).Key = null;
            table._count = 0;
            t_cached = table;
        }

        /// <summary>Returns the 1-based index of an object already written, or 0 after adding a new one.</summary>
        public int GetOrAdd(object value)
        {
            // The slots by reference, without bounds checks: a slot is the hash masked by the length less one (a
            // power of two), and _count stays below _slots.Length (Grow), which the JIT cannot tell.
            var entries = _entries;
            ref var first = ref NinoUnsafe.GetArrayDataReference(entries);
            int mask = entries.Length - 1;
            int i = RuntimeHelpers.GetHashCode(value) & mask;
            ref var entry = ref Unsafe.Add(ref first, i);
            while (true)
            {
                var key = entry.Key;
                if (key == null)
                    break;
                if (ReferenceEquals(key, value))
                    return entry.Value;
                i = (i + 1) & mask;
                entry = ref Unsafe.Add(ref first, i);
            }

            int count = _count + 1;
            entry.Key = value;
            entry.Value = count;
            Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_slots), count - 1) = i;
            _count = count;
            if (count * 2 > entries.Length)
                Grow();
            return 0;
        }

        private void Grow()
        {
            var old = _entries;
            var entries = new Entry[old.Length * 2];
            var slots = new int[entries.Length / 2 + 1];
            int mask = entries.Length - 1;
            // In insertion order: object k + 1 is at _slots[k].
            for (int k = 0; k < _count; k++)
            {
                ref var entry = ref old[_slots[k]];
                int i = RuntimeHelpers.GetHashCode(entry.Key) & mask;
                while (entries[i].Key != null)
                    i = (i + 1) & mask;
                entries[i] = entry;
                slots[k] = i;
            }

            _entries = entries;
            _slots = slots;
        }
    }

    /// <summary>Objects read so far in one deserialization call (index i - 1 holds object i).</summary>
    internal sealed class NinoReferenceReadTable
    {
        [ThreadStatic] private static NinoReferenceReadTable t_cached;

        // An object in a struct: storing into an object[] is a covariant array store, a call that checks the
        // element type before the write barrier.
        private struct Slot
        {
            public object Value;
        }

        private Slot[] _objects = new Slot[8];
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
            At(_count++) = null;
            Pending = _count;
            return _count;
        }

        /// <summary>
        /// Records <paramref name="value"/> as the object whose instance <paramref name="refs"/> (null: no table) is
        /// waiting for, if any: back references to it may come before its body.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetPending(NinoReferenceReadTable refs, object value)
        {
            if (refs != null && refs.Pending != 0)
                refs.Set(refs.Pending, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index, object value)
        {
            // Usually called twice per object (once its instance exists, then when it is read): the second call
            // finds it stored and skips the write barrier.
            ref var slot = ref At(index - 1);
            if (!ReferenceEquals(slot, value))
                slot = value;
            if (Pending == index)
                Pending = 0;
        }

        public object Get(int index)
        {
            if ((uint)(index - 1) >= (uint)_count)
                NinoThrow.InvalidReference(index);
            var value = At(index - 1);
            if (value == null)
                NinoThrow.IncompleteReference(index);
            return value;
        }

        /// <summary>
        /// The object at <paramref name="at"/>, without a bounds check: every index here comes from Begin or was
        /// checked against _count, which stays within the array.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref object At(int at) =>
            ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(_objects), at).Value;
    }
}
