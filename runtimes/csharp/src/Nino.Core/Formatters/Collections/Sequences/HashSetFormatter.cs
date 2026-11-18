using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary><c>HashSet&lt;T&gt;</c> (or a type derived from it): count (-1 = null), then the elements.</summary>
    public struct HashSetFormatter<[NinoCreated] TSet, T, TFormatter> : INinoFormatter<TSet>
        where TSet : HashSet<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TSet value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            // An empty set skips the run (as NinoElements.Read).
            int count = value.Count;
            if (count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            // The set's own entries as a run: a loop over an array, without the enumerator's version check and reloads
            // of the set's fields on each element. The enumerator out of line, so that its state does not take
            // registers or inlining budget here.
            if (CollectionFields.Run(value, count, out var run))
                NinoRuns.WriteSet<T, TFormatter>(run, count, ref writer);
            else
                writer = WriteEnumerated(value, count, writer);
        }

        /// <summary>
        /// The count and the elements through the enumerator, where the set's fields are not known: out of line, the
        /// writer by value and returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEnumerated(TSet value, int count, Writer writer)
        {
            writer.WriteLength(count);
            int written = 0;
            foreach (var item in value)
            {
                if (++written > count)
                    NinoThrow.ModifiedDuringWrite();
                NinoElements.WriteOne<T, TFormatter>(item, ref writer);
            }

            return writer;
        }

        public void Deserialize(ref Reader reader, ref TSet value)
        {
            bool raw = NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw;
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinHashedSize<T, TFormatter>(), out int length))
                return;

            // A new plain set (default comparer) of raw elements is filled directly (HashTables).
            bool created = value == null && typeof(TSet) == typeof(HashSet<T>);
            // InterfaceSetFormatter reads into a plain HashSet<T>, which no generated code registers a creator for.
            var set = value ??= typeof(TSet) == typeof(HashSet<T>)
                ? (TSet)(NinoLimits.Untrusted ? NinoSeededHash.NewSet<T>() : new HashSet<T>())
                : NinoActivator<TSet>.Create();
            if (NinoRecycler.DiscardAll(set, set.Count))
                set.Clear();
            if (length == 0)
                return;

#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            // Sized for the elements, unless its entries would take far more memory than the data (large elements,
            // strings among them: a null or empty one takes one byte): then it grows as they are added.
            set.EnsureCapacity(Unsafe.SizeOf<T>() + NinoElements.HashSlot > NinoElements.Amplification &&
                               NinoKind<T, TFormatter>.HeavyHashed
                ? NinoElements.Capacity(length, Unsafe.SizeOf<T>() + NinoElements.HashSlot, reader.Remaining)
                : length);
#endif
            if (raw)
            {
                int size = Unsafe.SizeOf<T>();
                ref byte src = ref reader.ReadElements(length, size);
                // Filled directly where it can be, else added out of line (each checks bools: 0 or 1).
                if (TryFill(set, ref src, length, created))
                    return;
                AddRaw(set, ref src, length);
                return;
            }

            // Slots (raw T?), out of line.
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
            {
                ReadSlots(set, length, created, ref reader);
                return;
            }

            // Elements whose buckets the set guards itself (strings on .NET) are added here, inline as before;
            // others out of line, where each is inserted or its bucket walked (HashTables.Plan).
            if (HashTables.Plan<T>.Inserts || HashTables.Plan<T>.Walks)
            {
                reader = AddEach(set, length, reader);
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                Add(set, item, insert: false, walk: false);
            }
        }

        /// <summary>
        /// Fills the entries of <paramref name="set"/> directly from <paramref name="length"/> raw values at
        /// <paramref name="src"/> where it can (<see cref="HashTables"/>): a new plain set (default comparer), or a
        /// plain one read into, empty once cleared. False where the set was left to add them.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryFill(TSet set, ref byte src, int length, bool created) =>
            HashTables.Set && (created
                ? HashTables.TryFill(set, ref src, length, Unsafe.SizeOf<T>())
                : typeof(TSet) == typeof(HashSet<T>) && set.Count == 0 &&
                  HashTables.TryRefill(set, ref src, length, Unsafe.SizeOf<T>()));

        /// <summary>
        /// Reads and adds <paramref name="length"/> elements, out of line with the reader by value (see
        /// <see cref="AddRaw"/>): each added here, its bucket walked as it is, where the set is as expected (empty,
        /// default comparer, sized for them), else added by the set and its bucket walked after (hash flooding,
        /// <see cref="HashTables"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader AddEach(TSet set, int length, Reader reader)
        {
            // One loop (the formatter inlined once), the choice made per element.
            // The plan read once, into locals (HashTables.Plan).
            bool insert = HashTables.Plan<T>.Inserts && HashTables.CanInsert(set, length);
            bool walk = HashTables.Plan<T>.Walks;
            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                Add(set, item, insert, walk);
            }

            return reader;
        }

        /// <summary>
        /// Adds one element as <see cref="AddEach"/> chose for the run: inserted where the set is as expected, else
        /// added by the set, its bucket walked after where the plan says so.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Add(TSet set, T item, bool insert, bool walk)
        {
            if (insert)
            {
                HashTables.Insert(set, item);
                return;
            }

            if (!set.Add(item))
                NinoThrow.DuplicateKey(set.GetType());
            if (walk)
                HashTables.Walk(set, item);
        }

        /// <summary>
        /// Reads <paramref name="length"/> slots (raw <c>T?</c>). Where a slot is its value's memory they are checked
        /// in the data and added from it as raw elements are, the entries filled directly where they can be
        /// (<see cref="TryFill"/>); else <see cref="AddSlots"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadSlots(TSet set, int length, bool created, ref Reader reader)
        {
            if (!default(TFormatter).SlotsAreMemory)
            {
                AddSlots(set, length, ref reader);
                return;
            }

            int size = Unsafe.SizeOf<T>();
            ref byte src = ref reader.ReadElements(length, size);
            default(TFormatter).CheckSlots(ref src, length);
            if (TryFill(set, ref src, length, created))
                return;
            AddFrom(set, ref src, length);
        }

        /// <summary>
        /// Adds <paramref name="length"/> elements from their checked bytes as <see cref="AddEach"/> adds them: inserted
        /// where the set is empty, with the default comparer and sized for them (an existing set read into, cleared),
        /// else added by the set, its bucket walked after.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void AddFrom(TSet set, ref byte src, int length)
        {
            int size = Unsafe.SizeOf<T>();
            bool insert = HashTables.Plan<T>.Inserts && HashTables.CanInsert(set, length);
            bool walk = HashTables.Plan<T>.Walks;
            for (int i = 0; i < length; i++)
            {
                Add(set, NinoUnsafe.Read<T>(ref src), insert, walk);
                src = ref Unsafe.Add(ref src, size);
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> slots (raw <c>T?</c>) as one run into a rented array, checked and
        /// converted by the run's kernels, then adds each as <see cref="AddEach"/> does: one element at a time
        /// through the reader took two and a half times as long (1,000 <c>int?</c>: 12,174 ns against 4,842 before
        /// slots, local).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddSlots(TSet set, int length, ref Reader reader)
        {
            var rented = NinoCollections.ReadRented<T, TFormatter>(length, ref reader);
            try
            {
                AddFrom(set, ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(rented)), length);
            }
            finally
            {
                NinoCollections.ReturnRented(rented);
            }
        }

        /// <summary>
        /// Adds the <paramref name="length"/> raw elements at <paramref name="src"/> one by one, where the set was not
        /// filled directly (<see cref="HashTables"/>). Out of line: the element loop and the bool check (a byte other
        /// than 0 or 1 is invalid data) cost the caller nothing.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddRaw(TSet set, ref byte src, int length)
        {
            if (!NinoLayout<T>.CopiesAsIs)
                NinoChecks.Values<T>(ref src, length, Unsafe.SizeOf<T>());
            AddFrom(set, ref src, length);
        }
    }
}
