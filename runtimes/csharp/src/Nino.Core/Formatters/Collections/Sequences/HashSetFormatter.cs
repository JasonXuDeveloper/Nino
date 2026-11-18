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

            // An empty set skips the run (as NinoRuns.Read).
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
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinHashedSize<T, TFormatter>(), out int length))
                return;

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
            // Slots that are not their memory: read as one run into a rented array, then added, out of line.
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot && !default(TFormatter).SlotsAreMemory)
            {
                AddSlots(set, length, ref reader);
                return;
            }

            // Values the set takes as one block: the block taken here (its checks made), the values filled or added out
            // of line, so the reader's address never leaves the caller.
            if (NinoRuns.ScattersSet<T, TFormatter>())
            {
                AddBlock(set, ref NinoRuns.TakeSet<T, TFormatter>(ref reader, length), length);
                return;
            }

            // Elements whose buckets the set does not guard itself, out of line: its entries filled through their run
            // and linked, or each added with its bucket walked (HashTables.Plan).
            if (HashTables.Plan<T>.Inserts || HashTables.Plan<T>.Walks)
            {
                reader = ReadGuarded(set, length, reader);
                return;
            }

            // Elements whose buckets the set guards itself (strings on .NET), added here, inline as before.
            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                Add(set, item, insert: false, walk: false);
            }
        }

        /// <summary>
        /// Reads and adds <paramref name="length"/> elements, out of line with the reader by value: where the set is as
        /// expected (empty, default comparer, sized for them, <see cref="HashTables.CanInsert{T}"/>), its entries are
        /// filled through their run and linked (<see cref="Fill"/>, from <see cref="HashTables.FillFrom"/> on), or each is
        /// inserted with its bucket walked as it is; else each is
        /// added by the set and its bucket walked after (hash flooding, <see cref="HashTables"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadGuarded(TSet set, int length, Reader reader)
        {
            // The plan read once, into locals (HashTables.Plan).
            bool insert = HashTables.Plan<T>.Inserts && HashTables.CanInsert(set, length);
            if (insert && length >= HashTables.FillFrom)
                return FillGuarded(set, length, reader);

            bool walk = HashTables.Plan<T>.Walks;
            // One loop (the formatter inlined once), the choice made per element.
            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                Add(set, item, insert, walk);
            }

            return reader;
        }

        /// <summary>
        /// The <paramref name="length"/> values of the block at <paramref name="src"/>
        /// (<see cref="NinoRuns.TakeSet{T,TFormatter}"/>): scattered into the entries of a set
        /// <see cref="HashTables.CanInsert{T}"/> accepts and linked, else each read from the block and added, its bucket
        /// walked after (HashTables.Plan).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddBlock(TSet set, ref byte src, int length)
        {
            if (HashTables.Plan<T>.Inserts && HashTables.CanInsert(set, length))
            {
                FillGuarded(set, ref src, length);
                return;
            }

            bool walk = HashTables.Plan<T>.Walks;
            int size = NinoKind.Stride<T, TFormatter>();
            for (int i = 0; i < length; i++, src = ref Unsafe.Add(ref src, size))
                Add(set, NinoRuns.ReadItem<T, TFormatter>(ref src), insert: false, walk);
        }

        /// <summary>
        /// <see cref="AddBlock"/>'s fill: the block scattered into the entries
        /// (<see cref="NinoRuns.ScatterSet{T,TFormatter}"/>), then linked, the set left empty if the data is rejected part
        /// way (<see cref="HashTables.Unlink{T}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FillGuarded(TSet set, ref byte src, int length)
        {
            try
            {
                NinoRuns.ScatterSet<T, TFormatter>(CollectionFields.Run(HashTables.Entries<T>(set), length), ref src);
                HashTables.Link<T>(set, length);
            }
            catch
            {
                HashTables.Unlink(set);
                throw;
            }
        }

        /// <summary>
        /// <see cref="Fill"/>, with the set left empty if the data is rejected part way, holding no reference the read
        /// stored (<see cref="HashTables.Unlink{T}"/>): a method of its own, as the dictionary's
        /// (NinoDictionary.FillGuarded).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader FillGuarded(TSet set, int length, Reader reader)
        {
            try
            {
                return Fill(set, length, reader);
            }
            catch
            {
                HashTables.Unlink(set);
                throw;
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> elements into the entries of a set <see cref="HashTables.CanInsert{T}"/>
        /// accepted, through their run (<see cref="NinoRuns.ReadSet{T,TFormatter}"/>), then links them
        /// (<see cref="HashTables.Link{T}(HashSet{T}, int)"/>: hash flooding, repeated values).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader Fill(TSet set, int length, Reader reader)
        {
            NinoRuns.ReadSet<T, TFormatter>(CollectionFields.Run(HashTables.Entries<T>(set), length), ref reader);
            HashTables.Link<T>(set, length);
            return reader;
        }

        /// <summary>
        /// Adds one element as <see cref="ReadGuarded"/> chose for the run: inserted where the set is as expected, else
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
        /// Reads <paramref name="length"/> slots (raw <c>T?</c>) that are not their memory as one run into a rented
        /// array, checked and converted by the run's kernels, then adds each: one element at a time through the reader
        /// took two and a half times as long (1,000 <c>int?</c>: 12,174 ns against 4,842 before slots, local).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddSlots(TSet set, int length, ref Reader reader)
        {
            var rented = NinoCollections.ReadRented<T, TFormatter>(length, ref reader);
            try
            {
                bool insert = HashTables.Plan<T>.Inserts && HashTables.CanInsert(set, length);
                bool walk = HashTables.Plan<T>.Walks;
                for (int i = 0; i < length; i++)
                    Add(set, rented[i], insert, walk);
            }
            finally
            {
                NinoCollections.ReturnRented(rented);
            }
        }
    }
}
