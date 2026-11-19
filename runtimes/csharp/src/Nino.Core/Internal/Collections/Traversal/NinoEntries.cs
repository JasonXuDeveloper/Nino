using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// What a hashed collection (a <see cref="HashSet{T}"/>, a <see cref="Dictionary{TKey,TValue}"/>) does of its own
    /// when <see cref="NinoEntries{TEntry,TFormatter,TTable}"/> reads into it: whether its new entries are filled then
    /// linked (<see cref="HashTables"/>), and how it takes one entry read apart (inserted, or added and its bucket
    /// walked). The collection's thin layer; its key is the one thing it reads out of an entry. It is the store of
    /// the cursor loop that adds (<see cref="Adding"/>): in code shared over a class each call of a struct of it is an
    /// indirect call, so a store wrapping it made two per entry.
    /// </summary>
    internal interface INinoHashTable<TEntry, TTable> : NinoCursor.IStore<TEntry>
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

        /// <summary>Itself as the store of entries read apart (<see cref="NinoCursor.IStore{T}.Stored"/>: each
        /// added as <see cref="Add"/> does, then cleared, so the next is read into empty places).</summary>
        TTable Adding(bool insert, bool walk);
    }

    /// <summary>
    /// The one read of a hashed collection's entries, a set's and a dictionary's alike (#355): its elements are its
    /// entries (<typeparamref name="TEntry"/>, written by <typeparamref name="TFormatter"/>, a storage element of the
    /// set's value or the dictionary's key and value), and the collection (<typeparamref name="TTable"/>) says only what
    /// it does with them. Into a new collection sized for them, the entries are filled through
    /// <see cref="NinoRuns"/>' loops and the block kernels and linked after (<see cref="HashTables.Link{T}"/>, the
    /// collection left empty when the data is rejected part way); else each is read apart and added. Blocks from one
    /// entry on, the others from <see cref="HashTables.FillFrom"/> on.
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
        /// reader's address never leaves the caller), through the cursor loop where <paramref name="cursor"/> (which
        /// the caller asks of the items: in code shared over a class the element formatter's own answer is a lookup),
        /// else value by value, each out of line.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read(TTable table, int length, ref Reader reader, bool cursor)
        {
            if (Blocks)
            {
                ref byte source = ref reader.ReadElements(length, NinoKind.Stride<TEntry, TFormatter>());
                if (length != 0)
                    AddBlock(table, ref source, length);
                return;
            }

            // Out of line, the data and the bytes left or a copy of the reader to the call; an empty collection skips
            // both.
            if (length == 0)
                return;
            if (cursor)
            {
                reader.UnsafeAdvance(ReadCursors(table, length, ref reader.Peek(), reader.Remaining));
                return;
            }

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
        /// Reads <paramref name="length"/> entries (not 0) through the cursor loop from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes): in place into the entries of a collection that takes
        /// them and linked (from <see cref="HashTables.FillFrom"/> on), else each read into one entry (a run of it with
        /// no stride) and added (the table the store, <see cref="INinoHashTable{TEntry,TTable}.Adding"/>). Returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadCursors(TTable table, int length, ref byte data, int end)
        {
            bool insert = table.CanInsert(length);
            if (insert && length >= HashTables.FillFrom)
                return FillCursors(table, length, ref data, end);

            TEntry entry = default;
            return NinoRuns.ReadCursors<TEntry, TFormatter, TTable>(
                new NinoRun(ref Unsafe.As<TEntry, byte>(ref entry), length, 0, NinoRun.NoKey, NinoRun.AllInUse),
                ref data, end, table.Adding(insert, table.Walks));
        }

        /// <summary><see cref="ReadCursors"/>' fill, the collection left empty if the data is rejected part
        /// way.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FillCursors(TTable table, int length, ref byte data, int end)
        {
            try
            {
                int read = NinoRuns.ReadCursors<TEntry, TFormatter, NinoCursor.InPlace<TEntry>>(
                    table.Run(length), ref data, end, default);
                table.Link(length);
                return read;
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
        /// <see cref="ReadEach"/>'s fill: a method of its own, as an exception region in the loop above slowed it for
        /// a few entries too (one int-keyed pair 5 to 7 % slower on Neoverse-N2, #320, run 44246428900).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader FillEach(TTable table, int length, Reader reader)
        {
            try
            {
                reader = ReadEach(table.Run(length), reader);
                table.Link(length);
                return reader;
            }
            catch
            {
                table.Unlink();
                throw;
            }
        }

        /// <summary>
        /// The elements of <paramref name="run"/> (new entries) read in place one by one, the reader by value (a local,
        /// its cursor in registers).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Reader ReadEach(NinoRun run, Reader reader)
        {
            ref byte value = ref run.First;
            int stride = run.Stride;
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
                default(TFormatter).Deserialize(ref reader, ref Unsafe.As<byte, TEntry>(ref value));
            return reader;
        }
    }

    /// <summary>
    /// A <see cref="HashSet{T}"/>'s own layer under <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>: its key is the
    /// entry's value, and a null value is one. Two views of its entries: the entries themselves (values without
    /// references: the storage element, a block where the value is), or the values at their offset in them, a run with
    /// the entry's stride (values with references: in code shared over a class the element's accessor was an indirect
    /// call per value in the cursor loops, asm.py: <c>HashSet&lt;string&gt;</c>'s write loop 382 to 812 bytes).
    /// </summary>
    internal readonly struct SetTable<T> : INinoHashTable<CollectionFields.SetEntry<T>, SetTable<T>>,
        INinoHashTable<T, SetTable<T>>
    {
        private readonly HashSet<T> _set;
        private readonly bool _insert, _walk;

        public SetTable(HashSet<T> set) : this(set, false, false)
        {
        }

        private SetTable(HashSet<T> set, bool insert, bool walk)
        {
            _set = set;
            _insert = insert;
            _walk = walk;
        }

        public SetTable<T> Adding(bool insert, bool walk) => new(_set, insert, walk);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Stored(ref CollectionFields.SetEntry<T> entry)
        {
            Add(_set, entry.Value, _insert, _walk);
            entry = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Stored(ref T value)
        {
            Add(_set, value, _insert, _walk);
            value = default;
        }

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
        private readonly bool _insert, _walk;

        public MapTable(Dictionary<TKey, TValue> dictionary) : this(dictionary, false, false)
        {
        }

        private MapTable(Dictionary<TKey, TValue> dictionary, bool insert, bool walk)
        {
            _dictionary = dictionary;
            _insert = insert;
            _walk = walk;
        }

        public MapTable<TKey, TValue> Adding(bool insert, bool walk) => new(_dictionary, insert, walk);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Stored(ref CollectionFields.MapEntry<TKey, TValue> entry)
        {
            Add(ref entry, _insert, _walk);
            entry = default;
        }

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
        public void Add(ref CollectionFields.MapEntry<TKey, TValue> entry, bool insert, bool walk)
        {
            if (insert)
            {
                HashTables.Insert(_dictionary, entry.Key, entry.Value);
                return;
            }

            if (entry.Key == null)
                NinoThrow.NullKey(_dictionary);
            NinoCollections.AddUnique(_dictionary, entry.Key, entry.Value);
            if (walk)
                HashTables.Walk(_dictionary, entry.Key);
        }
    }
}
