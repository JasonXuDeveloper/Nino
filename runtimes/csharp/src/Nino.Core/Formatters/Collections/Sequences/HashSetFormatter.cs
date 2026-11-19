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
    /// <summary>
    /// <c>HashSet&lt;T&gt;</c> (or a type derived from it): count (-1 = null), then the elements. The set's thin layer:
    /// its entries are a run whose elements are the entries themselves (<see cref="CollectionFields.SetEntry{T}"/>, its
    /// value composed at its offset by <see cref="NinoStorageFormatter{TElement,TA,TFormatterA,TFields}"/>), written by
    /// <see cref="NinoRuns.WriteEntries{TEntry,TFormatter}"/> and read by <see cref="NinoEntries{TEntry,TFormatter,
    /// TTable}"/>, as a dictionary's are; what is the set's own is reaching the entries, its slots, and the add of one
    /// value (<see cref="SetTable{T}"/>).
    /// </summary>
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
            if (!CollectionFields.Run(value, count, out var run))
            {
                writer = WriteEnumerated(value, count, writer);
                return;
            }

            // Slots whose memory is their wire form after a pass over them: out of line.
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot && run.InUse == NinoRun.AllInUse &&
                default(TFormatter).SlotsWriteInPlace)
            {
                WriteSlots(run, count, ref writer);
                return;
            }

            // Values with references are moved as the values at their offset in the entries (SetTable's item view): in
            // code shared over a class the entry formatter's accessor is a call. A polymorphic base's values: its
            // generated loop over the run of the values, through a copy of the writer (the writer's address never
            // reaches the generated loop, a call, so the caller keeps its fields in registers). The intrinsic itself,
            // as in NinoRuns.Write.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                var values = CollectionFields.Values<T>(run);
                if (NinoKind<TFormatter>.Runs)
                {
                    writer.WriteLength(count);
                    Writer copy = default;
                    writer.CopyTo(ref copy);
                    default(TFormatter).WriteRun(values, ref copy);
                    copy.CopyTo(ref writer);
                    return;
                }

                NinoRuns.WriteEntries<T, TFormatter>(values, count, ref writer, NinoCursor.Own<T, TFormatter>());
                return;
            }

            NinoRuns.WriteEntries<CollectionFields.SetEntry<T>, NinoStorageFormatter<CollectionFields.SetEntry<T>, T, TFormatter, CollectionFields.SetFields<T>>>(run, count,
                ref writer, NinoCursor.Own<T, TFormatter>());
        }

        /// <summary>
        /// The count and the slots, out of line: the values packed out of the entries by the block kernels as their
        /// memory (<see cref="NinoMemoryItem{T}"/>), then written over themselves as slots
        /// (<see cref="INinoFormatter{T}.SlotsWriteInPlace"/>), which clears their padding and absent values (1,000
        /// <c>int?</c>: 227 ns against 1,140 from a rented copy, local).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteSlots(NinoRun run, int count, ref Writer writer)
        {
            int size = Unsafe.SizeOf<T>();
            ref byte elements = ref writer.ReserveCounted(count, size);
            NinoBlockCollections.WriteAll<CollectionFields.SetEntry<T>, NinoStorageFormatter<CollectionFields.SetEntry<T>, T, NinoMemoryItem<T>, CollectionFields.SetFields<T>>>(ref elements,
                ref Unsafe.As<byte, CollectionFields.SetEntry<T>>(ref run.First), count, size);
            default(TFormatter).WriteSlots(ref elements,
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, T>(ref elements), count));
        }

        /// <summary>
        /// The count and the elements through the set's enumerator, gathered a chunk at a time, where the set's fields
        /// are not known: out of line, the writer by value and returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEnumerated(TSet value, int count, Writer writer) =>
            NinoGather.Write<T, TFormatter, NinoGather.Enumerated<T, HashSet<T>.Enumerator>>(
                new(((HashSet<T>)value).GetEnumerator()), count, writer);

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
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
            {
                // Slots that are not their memory: read as one run into a rented array, then added, out of line.
                if (!default(TFormatter).SlotsAreMemory)
                {
                    var adds = new Adds(set, length);
                    reader = NinoGather.ReadAll<T, TFormatter, Adds>(ref adds, length, reader);
                    return;
                }

                // Slots that are: their presence bytes checked on the block, then moved as their memory.
                ref byte slots = ref reader.ReadElements(length, NinoKind.Stride<T, TFormatter>());
                default(TFormatter).CheckSlots(ref slots, length);
                NinoEntries<CollectionFields.SetEntry<T>, NinoStorageFormatter<CollectionFields.SetEntry<T>, T, NinoMemoryItem<T>, CollectionFields.SetFields<T>>, SetTable<T>>.AddBlock(new SetTable<T>(set),
                    ref slots, length);
                return;
            }

            // As in Serialize: the entries of values without references, the values of those with.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                // Values whose buckets the set guards itself (strings on .NET: neither inserted nor walked), added
                // here, inline, one at a time: through NinoEntries' loops, in code shared over a class, each add was an
                // indirect call of the table (HashSet<string> of 1,000 read 20 to 30 % slower, local).
                if (!HashTables.Plan<T>.Inserts && !HashTables.Plan<T>.Walks)
                {
                    for (int i = 0; i < length; i++)
                    {
                        T item = default;
                        NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                        if (!set.Add(item))
                            NinoThrow.DuplicateKey(set.GetType());
                    }

                    return;
                }

                reader = ReadValues(set, length, reader);
            }
            else
                NinoEntries<CollectionFields.SetEntry<T>, NinoStorageFormatter<CollectionFields.SetEntry<T>, T, TFormatter, CollectionFields.SetFields<T>>, SetTable<T>>.Read(
                    new SetTable<T>(set), length, ref reader, NinoCursor.Own<T, TFormatter>());
        }

        /// <summary>
        /// The values of a set of values with references that inserts or walks (<see cref="HashTables.Plan{T}"/>),
        /// through <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>: out of line, the reader by value and returned,
        /// so that the inline loop beside it keeps the caller's reader in registers (asm.py: HashSet&lt;string&gt;'s
        /// read in code shared over a class 44 to 81 stack accesses with it inlined).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadValues(TSet set, int length, Reader reader)
        {
            NinoEntries<T, TFormatter, SetTable<T>>.Read(new SetTable<T>(set), length, ref reader,
                NinoCursor.Own<T, TFormatter>());
            return reader;
        }

        /// <summary>
        /// Slots (raw <c>T?</c>) that are not their memory, read a chunk at a time by the run's kernels (checked and
        /// converted), then added: one element at a time through the reader took two and a half times as long (1,000
        /// <c>int?</c>: 12,174 ns against 4,842 before slots, local).
        /// </summary>
        private readonly struct Adds : INinoGatherTarget<T>
        {
            private readonly TSet _set;
            private readonly bool _insert, _walk;

            public Adds(TSet set, int length)
            {
                _set = set;
                _insert = HashTables.Plan<T>.Inserts && HashTables.CanInsert(set, length);
                _walk = HashTables.Plan<T>.Walks;
            }

            public void Add(Span<T> chunk)
            {
                foreach (var item in chunk)
                    SetTable<T>.Add(_set, item, _insert, _walk);
            }
        }
    }
}
