using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// What a hashed collection (a <see cref="HashSet{T}"/>, a <see cref="Dictionary{TKey,TValue}"/>) does of its own
    /// when <see cref="NinoEntries{TEntry,TFormatter,TTable}"/> reads into it: whether its new entries are filled then
    /// linked (<see cref="HashTables"/>), and how it takes one entry read apart (inserted, or added and its bucket
    /// walked). The collection's thin layer; its key is the one thing it reads out of an entry. Entries holding
    /// strings are read by the string run kernels instead (<see cref="NinoStringRuns"/>), whose stores are not generic
    /// over a class: in code shared over a class each call of a struct of this is an indirect call.
    /// </summary>
    internal interface INinoHashTable<TEntry, TTable>
        where TTable : struct, INinoHashTable<TEntry, TTable>
    {
        /// <summary>Whether its entries take <paramref name="count"/> new ones, filled in place then linked, or each
        /// inserted (<see cref="HashTables.CanInsert{T}"/>).</summary>
        bool CanInsert(int count);

        /// <summary>Whether a key it added has its bucket walked (<see cref="HashTables.Plan{T}"/>).</summary>
        bool Walks { get; }

        /// <summary>
        /// The run of its first <paramref name="count"/> new entries, each the element (in use, for a fill
        /// <see cref="CanInsert"/> accepted): its entries, or the items of a one-item entry, a run of them with the
        /// entry's stride.
        /// </summary>
        NinoRun Run(int count);

        /// <summary>Links the first <paramref name="count"/> entries a fill wrote (<see cref="HashTables"/>).</summary>
        void Link(int count);

        /// <summary>Empties it after a fill the data rejected part way.</summary>
        void Unlink();

        /// <summary>Takes one entry read apart: inserted where <paramref name="insert"/>, else added by the collection
        /// (a null or repeated key is invalid data) and its bucket walked where <paramref name="walk"/>.</summary>
        void Add(ref TEntry entry, bool insert, bool walk);
    }

    /// <summary>
    /// The one read of a hashed collection's entries, a set's and a dictionary's alike (#355): its elements are its
    /// entries (<typeparamref name="TEntry"/>, written by <typeparamref name="TFormatter"/>, a storage element of the
    /// set's value or the dictionary's key and value), and the collection (<typeparamref name="TTable"/>) says only what
    /// it does with them. Into a new collection sized for them, the entries are filled through
    /// <see cref="NinoRuns"/>' loops and the block kernels and linked after (<see cref="HashTables.Link{T}"/>, the
    /// collection left empty when the data is rejected part way); else each is read apart and added. Blocks from one
    /// entry on, the others from <see cref="HashTables.FillFrom"/> on. Strings and pairs holding them take the same
    /// steps through the string run kernels (<see cref="NinoStringRuns.ReadSet"/>, NinoDictionary's
    /// <c>ReadStrings</c>).
    /// </summary>
    internal static class NinoEntries<TEntry, TFormatter, TTable>
        where TFormatter : struct, INinoFormatter<TEntry>
        where TTable : struct, INinoHashTable<TEntry, TTable>
    {
        /// <summary>Whether the entries are read as one block (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/>):
        /// a JIT-time constant.</summary>
        public static bool Blocks
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoRuns.ScattersItems<TEntry, TFormatter>();
        }

        /// <summary>
        /// Reads <paramref name="length"/> entries into <paramref name="table"/>: as one block (taken here, so the
        /// reader's address never leaves the caller), else value by value, out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(TTable table, int length, ref Reader reader)
        {
            if (Blocks)
            {
                ref byte source = ref reader.ReadElements(length, NinoKind.Stride<TEntry, TFormatter>());
                if (length != 0)
                    AddBlock(table, ref source, length);
                return;
            }

            // Out of line, a copy of the reader to the call; an empty collection skips it.
            if (length == 0)
                return;
            reader = ReadEach(table, length, reader);
        }

        /// <summary>
        /// The <paramref name="length"/> entries of the block at <paramref name="source"/> (its checks the caller's):
        /// scattered into the entries of a collection that takes them and linked, else each read from the block and
        /// added.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void AddBlock(TTable table, ref byte source, int length)
        {
            if (table.CanInsert(length))
            {
                FillBlock(table, ref source, length);
                return;
            }

            bool walk = table.Walks;
            int size = NinoKind.Stride<TEntry, TFormatter>();
            for (int i = 0; i < length; i++, source = ref Unsafe.Add(ref source, size))
            {
                TEntry entry = default;
                default(TFormatter).ReadBlock(ref entry, ref source);
                table.Add(ref entry, insert: false, walk);
            }
        }

        /// <summary>
        /// <see cref="AddBlock"/>'s fill: the block scattered into the entries by the block kernels (each entry's every
        /// byte written, its hash code and link 0 until linked), then linked, the collection left empty if the data is
        /// rejected part way.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FillBlock(TTable table, ref byte source, int length)
        {
            try
            {
                // Blocks are entries of values, whose runs are their entries in a row.
                NinoBlockCollections.ReadAll<TEntry, TFormatter>(ref source,
                    ref Unsafe.As<byte, TEntry>(ref table.Run(length).First), length,
                    NinoKind.Stride<TEntry, TFormatter>());
                table.Link(length);
            }
            catch
            {
                table.Unlink();
                throw;
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> entries (not 0) value by value: in place into the entries of a collection
        /// that takes them and linked (from <see cref="HashTables.FillFrom"/> on: below it the read loop and the link
        /// cost more than the inserts they save), else each read into one entry and added. Out of line with the reader
        /// by value: a local here, its cursor in registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadEach(TTable table, int length, Reader reader)
        {
            bool insert = table.CanInsert(length);
            if (insert && length >= HashTables.FillFrom)
                return FillEach(table, length, reader);

            bool walk = table.Walks;
            // One loop (the formatter inlined once), the choice made per entry: predicted, and cheaper than a second
            // copy of the loop, which kept more of its state on the stack.
            for (int i = 0; i < length; i++)
            {
                TEntry entry = default;
                default(TFormatter).Deserialize(ref reader, ref entry);
                table.Add(ref entry, insert, walk);
            }

            return reader;
        }

        /// <summary>
        /// <see cref="ReadEach"/>'s fill: the new entries read in place by the one loop of values one by one
        /// (<see cref="NinoRuns.ReadLoop{T,TFormatter}"/>; the kinds the kernels move are <see cref="AddBlock"/>'s and
        /// the string run kernels', and the kind tests of <see cref="NinoRuns"/>' read are lookups in code shared over
        /// a class), then linked. A method of its own, as an exception region in the loop above slowed it for a few
        /// entries too (one int-keyed pair 5 to 7 % slower on Neoverse-N2, #320, run 44246428900).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader FillEach(TTable table, int length, Reader reader)
        {
            try
            {
                var run = table.Run(length);
                NinoRuns.ReadLoop<TEntry, TFormatter>(ref run.First, run.Count, run.Stride, ref reader);
                table.Link(length);
                return reader;
            }
            catch
            {
                table.Unlink();
                throw;
            }
        }
    }

    /// <summary>
    /// A <see cref="HashSet{T}"/>'s own layer under <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>: its key is the
    /// entry's value, and a null value is one. Two views of its entries: the entries themselves (values without
    /// references: the storage element, a block where the value is), or the values at their offset in them, a run with
    /// the entry's stride (values with references: in code shared over a class the element's accessor was an indirect
    /// call per value, asm.py: <c>HashSet&lt;string&gt;</c>'s write loop 382 to 812 bytes; strings are read through
    /// the string run kernels, <see cref="NinoStringRuns.ReadSet"/>).
    /// </summary>
    internal readonly struct SetTable<T> : INinoHashTable<CollectionFields.SetEntry<T>, SetTable<T>>,
        INinoHashTable<T, SetTable<T>>
    {
        private readonly HashSet<T> _set;

        public SetTable(HashSet<T> set) => _set = set;

        public bool CanInsert(int count) => HashTables.CanInsert(_set, count);

        public bool Walks
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => HashTables.Plan<T>.Walks;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        NinoRun INinoHashTable<CollectionFields.SetEntry<T>, SetTable<T>>.Run(int count) =>
            CollectionFields.Run(HashTables.Entries(_set), count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        NinoRun INinoHashTable<T, SetTable<T>>.Run(int count) => CollectionFields.Values(HashTables.Entries(_set), count);

        public void Link(int count) => HashTables.Link(_set, count);

        public void Unlink() => HashTables.Unlink(_set);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ref CollectionFields.SetEntry<T> entry, bool insert, bool walk) =>
            Add(_set, entry.Value, insert, walk);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ref T value, bool insert, bool walk) => Add(_set, value, insert, walk);

        /// <summary>Adds <paramref name="value"/> as <see cref="INinoHashTable{TEntry,TTable}.Add"/> does.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add(HashSet<T> set, T value, bool insert, bool walk)
        {
            if (insert)
            {
                HashTables.Insert(set, value);
                return;
            }

            if (!set.Add(value))
                NinoThrow.DuplicateKey(set.GetType());
            if (walk)
                HashTables.Walk(set, value);
        }
    }

    /// <summary>A <see cref="Dictionary{TKey,TValue}"/>'s own layer under <see cref="NinoEntries{TEntry,TFormatter,
    /// TTable}"/>: its key is the entry's key, and a null key is invalid data.</summary>
    internal readonly struct MapTable<TKey, TValue>
        : INinoHashTable<CollectionFields.MapEntry<TKey, TValue>, MapTable<TKey, TValue>>
    {
        private readonly Dictionary<TKey, TValue> _dictionary;

        public MapTable(Dictionary<TKey, TValue> dictionary) => _dictionary = dictionary;

        public bool CanInsert(int count) => HashTables.CanInsert(_dictionary, count);

        public bool Walks
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => HashTables.Plan<TKey, TValue>.Walks;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoRun Run(int count) => CollectionFields.Run(HashTables.Entries(_dictionary), count);

        public void Link(int count) => HashTables.Link(_dictionary, count);

        public void Unlink() => HashTables.Unlink(_dictionary);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ref CollectionFields.MapEntry<TKey, TValue> entry, bool insert, bool walk) =>
            Add(_dictionary, entry.Key, entry.Value, insert, walk);

        /// <summary>Adds a pair as <see cref="INinoHashTable{TEntry,TTable}.Add"/> does.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Add(Dictionary<TKey, TValue> dictionary, TKey key, TValue value, bool insert, bool walk)
        {
            if (insert)
            {
                HashTables.Insert(dictionary, key, value);
                return;
            }

            if (key == null)
                NinoThrow.NullKey(dictionary);
            NinoCollections.AddUnique(dictionary, key, value);
            if (walk)
                HashTables.Walk(dictionary, key);
        }
    }
}
