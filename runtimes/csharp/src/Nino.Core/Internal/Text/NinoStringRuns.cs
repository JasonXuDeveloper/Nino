using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// An item of a run's element, at the reference it is given, in the cursor form: the bytes it takes at most,
    /// written at a reference, measured and then read from the data (#355). The items of strings are not generic
    /// (<see cref="NinoUtf16Item"/>, <see cref="NinoUtf8Item"/>), and the other item of a pair holding one is a value
    /// without references (<see cref="NinoValueItem{T,TFormatter}"/>), so a loop over them
    /// (<see cref="NinoStringRuns"/>) is never code shared over a class: each call is direct and inlined, and no
    /// static is a lookup.
    /// </summary>
    internal interface INinoRunItem
    {
        /// <summary>The most bytes <see cref="WriteAt"/> writes for the item of <paramref name="element"/>
        /// (negative: more than an <c>int</c> holds).</summary>
        int MaxSize(ref byte element);

        /// <summary>Writes the item of <paramref name="element"/> at <paramref name="destination"/> (room for
        /// <see cref="MaxSize"/> bytes); returns the bytes written.</summary>
        int WriteAt(ref byte destination, ref byte element);

        /// <summary>
        /// The bytes the item at <paramref name="source"/> takes (<paramref name="available"/> bytes there), every
        /// length in it checked against them, without allocating: what <see cref="ReadAt"/> then reads. A length out of
        /// range is invalid data (<see cref="NinoDataException"/>).
        /// </summary>
        int Measure(ref byte source, int available);

        /// <summary>
        /// Reads the item at <paramref name="source"/> (<paramref name="available"/> bytes, measured) into the item of
        /// <paramref name="element"/>; returns the bytes read. An existing string is kept when the data holds the
        /// same, unless <paramref name="fresh"/> (a constant: the one element of a store that hands each read on,
        /// <see cref="INinoRunStore.OneSlot"/>), where the item is written without looking at it.
        /// </summary>
        int ReadAt(ref byte source, int available, ref byte element, bool fresh);
    }

    /// <summary>What a read loop of <see cref="NinoStringRuns"/> does with each element it read.</summary>
    internal interface INinoRunStore
    {
        /// <summary>
        /// Whether every element is read into the run's first, the one element (a constant): the store hands each
        /// on (a set's or a dictionary's adds), so the loop keeps no stride and reads over nothing. Otherwise each is
        /// read in place, the run's elements one after another.
        /// </summary>
        bool OneSlot { get; }

        /// <summary>Takes the element read at <paramref name="element"/>.</summary>
        void Stored(ref byte element);
    }

    /// <summary>A string in the default encoding (<see cref="StringFormatter"/>, <see cref="NinoStrings"/>). No
    /// field: a run of strings alone keeps no offset in a register (a pair places its items,
    /// <see cref="NinoItemPair{TA,TB}"/>).</summary>
    internal readonly struct NinoUtf16Item : INinoRunItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref string At(ref byte element) => ref Unsafe.As<byte, string>(ref element);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => NinoStrings.MaxSize(At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element) => NinoStrings.Write(ref destination, At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Measure(ref byte source, int available) => NinoStrings.Measure(ref source, available);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            // An unchanged string (a read into an existing collection) is not stored again: no write barrier. A fresh
            // one always is (null too: the one element still holds the string read before).
            ref string slot = ref At(ref element);
            string current = fresh ? null : slot;
            string read = NinoStrings.Read(ref source, available, current, out int size);
            if (fresh || !ReferenceEquals(read, current))
                slot = read;
            return size;
        }
    }

    /// <summary>A UTF-8 string (<see cref="Utf8StringFormatter"/>), as <see cref="NinoUtf16Item"/>.</summary>
    internal readonly struct NinoUtf8Item : INinoRunItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref string At(ref byte element) => ref Unsafe.As<byte, string>(ref element);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => Writer.Utf8MaxSize(At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element) => Writer.WriteUtf8At(ref destination, At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Measure(ref byte source, int available) => Reader.MeasureUtf8(ref source, available);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            ref string slot = ref At(ref element);
            string current = fresh ? null : slot;
            SizedString read = Reader.ReadUtf8At(ref source, available, current);
            if (fresh || !ReferenceEquals(read.Value, current))
                slot = read.Value;
            return read.Size;
        }
    }

    /// <summary>
    /// A value of a fixed size: raw, or a block read as one
    /// (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/>), moved as its formatter moves it in a block. Only for
    /// values without references, so a loop over it is not shared code.
    /// </summary>
    internal readonly struct NinoValueItem<T, TFormatter> : INinoRunItem where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref T At(ref byte element) => ref Unsafe.As<byte, T>(ref element);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => NinoKind.Stride<T, TFormatter>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            NinoBlocks.WriteItem<T, TFormatter>(in At(ref element), ref destination);
            return NinoKind.Stride<T, TFormatter>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Measure(ref byte source, int available)
        {
            int size = NinoKind.Stride<T, TFormatter>();
            if ((uint)available < (uint)size)
                NinoThrow.EndOfData();
            return size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            NinoBlocks.ReadItem<T, TFormatter>(ref At(ref element), ref source);
            return NinoKind.Stride<T, TFormatter>();
        }
    }

    /// <summary>
    /// Two items of one element, <typeparamref name="TA"/> then <typeparamref name="TB"/> (a pair's key and value, a
    /// dictionary's entry), each at its own offset in the element: the element composed from its items, never a loop
    /// of its own.
    /// </summary>
    internal readonly struct NinoItemPair<TA, TB> : INinoRunItem
        where TA : struct, INinoRunItem
        where TB : struct, INinoRunItem
    {
        // nint: added to a reference as they are (Mono widens an int offset added to a reference as unsigned).
        private readonly nint _a, _b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemPair(int a, int b)
        {
            _a = a;
            _b = b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            int first = default(TA).MaxSize(ref Unsafe.AddByteOffset(ref element, _a));
            int second = default(TB).MaxSize(ref Unsafe.AddByteOffset(ref element, _b));
            return (uint)first + (ulong)(uint)second > int.MaxValue ? -1 : first + second;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            int size = default(TA).WriteAt(ref destination, ref Unsafe.AddByteOffset(ref element, _a));
            return size + default(TB).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size),
                ref Unsafe.AddByteOffset(ref element, _b));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Measure(ref byte source, int available)
        {
            int size = default(TA).Measure(ref source, available);
            return size + default(TB).Measure(ref Unsafe.Add(ref source, (nint)(uint)size), available - size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            int size = default(TA).ReadAt(ref source, available, ref Unsafe.AddByteOffset(ref element, _a), fresh);
            return size + default(TB).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size,
                ref Unsafe.AddByteOffset(ref element, _b), fresh);
        }
    }

    /// <summary>
    /// The run kernels of strings, the bottom layer of every collection of strings as the block kernels are of raw
    /// values (#355): a run (<see cref="NinoRun"/>: its first element, count, stride and in-use test) of strings in
    /// the default encoding or UTF-8, or of pairs holding one (a dictionary's entries, a list's pairs), each item at
    /// its offset in the element. Writes transcode each string with the SIMD kernels into the free part of the
    /// buffer, one check of its longest form against the bytes left; reads measure every length of the run against
    /// the data
    /// first, then allocate and transcode, storing each element in place or handing it to a store (a set's or a
    /// dictionary's adds). The string items are not generic, and a pair's other item is a value without references,
    /// so these loops are never compiled as code shared over a class: a collection of strings, which is shared code
    /// over its element, calls them once and pays no indirect call and no generic lookup per string. The writer and
    /// the reader never reach them: they take the writer by value and return it, or the data and its end.
    /// </summary>
    internal static class NinoStringRuns
    {
        /// <summary>Writes the strings of <paramref name="run"/> (not empty; the count is the caller's) in the default
        /// encoding.</summary>
        public static Writer WriteUtf16(scoped NinoRun run, Writer writer) =>
            Write(run, default(NinoUtf16Item), writer);

        /// <summary>Writes the strings of <paramref name="run"/> (not empty; the count is the caller's) as
        /// UTF-8.</summary>
        public static Writer WriteUtf8(scoped NinoRun run, Writer writer) => Write(run, default(NinoUtf8Item), writer);

        /// <summary>Reads the strings of <paramref name="run"/> (not empty, every one in use) in the default encoding
        /// in place from the data at <paramref name="data"/> (<paramref name="end"/> bytes); returns the bytes
        /// read.</summary>
        public static int ReadUtf16(scoped NinoRun run, ref byte data, int end) =>
            Read(run, default(NinoUtf16Item), ref data, end, default(InPlace));

        /// <summary>As <see cref="ReadUtf16"/>, UTF-8.</summary>
        public static int ReadUtf8(scoped NinoRun run, ref byte data, int end) =>
            Read(run, default(NinoUtf8Item), ref data, end, default(InPlace));

        /// <summary>
        /// Writes the strings of <paramref name="run"/> (not empty) through <typeparamref name="TFormatter"/>'s
        /// encoding (<see cref="NinoKind{TFormatter}.Strings"/>): one call, not generic over the element.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Writer Write<TFormatter>(scoped NinoRun run, Writer writer) =>
            typeof(TFormatter) == typeof(Utf8StringFormatter) ? WriteUtf8(run, writer) : WriteUtf16(run, writer);

        /// <summary>As <see cref="Write{TFormatter}(NinoRun, Writer)"/>, the read of <see cref="ReadUtf16"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read<TFormatter>(scoped NinoRun run, ref byte data, int end) =>
            typeof(TFormatter) == typeof(Utf8StringFormatter) ? ReadUtf8(run, ref data, end) : ReadUtf16(run, ref data, end);

        /// <summary>
        /// Writes <paramref name="run"/>'s pairs (not empty; the count is the caller's) whose items are
        /// <typeparamref name="TA"/> at <paramref name="a"/> bytes from each element and <typeparamref name="TB"/> at
        /// <paramref name="b"/> (<see cref="NinoCursor.Pairs{TA,TB,TFormatterA,TFormatterB}"/>: one a string, the other
        /// a string or a value of a fixed size). The items are picked here by JIT-time constants: the loop called is
        /// one instantiation over the items, never over the pair's types.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Writer WritePairs<TA, TB, TFormatterA, TFormatterB>(scoped NinoRun run, int a, int b,
            Writer writer)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            if (typeof(TFormatterA) == typeof(StringFormatter))
                return WriteWith<NinoUtf16Item, TB, TFormatterB>(run, a, b, writer);
            if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                return WriteWith<NinoUtf8Item, TB, TFormatterB>(run, a, b, writer);
            return WriteWith<NinoValueItem<TA, TFormatterA>, TB, TFormatterB>(run, a, b, writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Writer WriteWith<TItemA, TB, TFormatterB>(scoped NinoRun run, int a, int b, Writer writer)
            where TItemA : struct, INinoRunItem
            where TFormatterB : struct, INinoFormatter<TB>
        {
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return Write(run, new NinoItemPair<TItemA, NinoUtf16Item>(a, b), writer);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return Write(run, new NinoItemPair<TItemA, NinoUtf8Item>(a, b), writer);
            return Write(run, new NinoItemPair<TItemA, NinoValueItem<TB, TFormatterB>>(a, b), writer);
        }

        /// <summary>
        /// As <see cref="WritePairs{TA,TB,TFormatterA,TFormatterB}"/>, the read: <paramref name="run"/>'s
        /// pairs from the data at <paramref name="data"/> (<paramref name="end"/> bytes), each handed to
        /// <paramref name="store"/>; returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadPairs<TA, TB, TFormatterA, TFormatterB, TStore>(scoped NinoRun run, int a, int b,
            ref byte data, int end, TStore store)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
            where TStore : struct, INinoRunStore
        {
            if (typeof(TFormatterA) == typeof(StringFormatter))
                return ReadWith<NinoUtf16Item, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
            if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                return ReadWith<NinoUtf8Item, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
            return ReadWith<NinoValueItem<TA, TFormatterA>, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadWith<TItemA, TB, TFormatterB, TStore>(scoped NinoRun run, int a, int b, ref byte data,
            int end, TStore store)
            where TItemA : struct, INinoRunItem
            where TFormatterB : struct, INinoFormatter<TB>
            where TStore : struct, INinoRunStore
        {
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return Read(run, new NinoItemPair<TItemA, NinoUtf16Item>(a, b), ref data, end, store);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return Read(run, new NinoItemPair<TItemA, NinoUtf8Item>(a, b), ref data, end, store);
            return Read(run, new NinoItemPair<TItemA, NinoValueItem<TB, TFormatterB>>(a, b), ref data, end, store);
        }

        /// <summary>
        /// The write: <paramref name="run"/>'s elements in use (not none), each <paramref name="item"/>, written into
        /// the free part of the buffer by <see cref="WriteFitting{TItem}"/> until one's longest form does not fit,
        /// then the buffer grown for it and the loop gone on from it. Out of line, the writer by value and returned,
        /// a local here: only the free space reaches the loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer Write<TItem>(scoped NinoRun run, TItem item, Writer writer)
            where TItem : struct, INinoRunItem
        {
            int next = 0;
            while (true)
            {
                ulong left = WriteFitting(run, item, next, ref writer.FreeSpace(out int available), available);
                writer.Commit(available - (int)left);
                int entries = (int)(left >> 32);
                if (entries == 0)
                    return writer;
                next = run.Count - entries;
                writer.Ensure(RoomFor(run, item, next));
            }
        }

        /// <summary>
        /// The write loop: from the element at <paramref name="next"/> on, each element of <paramref name="run"/> in
        /// use, its <paramref name="item"/> written into the <paramref name="available"/> bytes at
        /// <paramref name="destination"/> once its longest form fits. Returns the bytes left (low half) and the
        /// elements left (high half): 0 when every one was written, else from the first that did not fit. One check
        /// per element against the bytes left, in registers: a first pass summing the run's longest forms to reserve
        /// them once took longer (1,000 strings written 1.3 to 1.4 times as long, local quick runs).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteFitting<TItem>(scoped NinoRun run, TItem item, int next, ref byte destination,
            int available)
            where TItem : struct, INinoRunItem =>
            // Twice from one body: runs without free entries (arrays, lists, most sets and dictionaries) with the
            // in-use test folded away, which leaves the loop's values the registers that hold them through the
            // string kernels' calls; with the test's rule too, the bytes left went to the stack (asm.py).
            run.InUse == NinoRun.AllInUse
                ? WriteFitting(run, item, next, ref destination, available, NinoRun.AllInUse)
                : WriteFitting(run, item, next, ref destination, available, run.InUse);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong WriteFitting<TItem>(scoped NinoRun run, TItem item, int next, ref byte destination,
            int available, int inUse)
            where TItem : struct, INinoRunItem
        {
            // Few values across the string kernels' calls, where x64 has five callee-saved registers for them: a
            // moving destination and the bytes left, and the element against the run's end.
            int stride = run.Stride;
            ref byte value = ref Unsafe.Add(ref run.First, (nint)next * stride);
            ref byte last = ref Unsafe.Add(ref run.First, (nint)run.Count * stride);
            ref byte cursor = ref destination;
            int left = available;
            for (; Unsafe.IsAddressLessThan(ref value, ref last); value = ref NinoRun.Next(ref value, stride))
            {
                if (NinoRun.Skips(ref value, inUse))
                    continue;
                // Room for the longest form, so a pair is never split across two buffers.
                if ((uint)item.MaxSize(ref value) > (uint)left)
                    break;
                int size = item.WriteAt(ref cursor, ref value);
                cursor = ref Unsafe.Add(ref cursor, (nint)(uint)size);
                left -= size;
            }

            // The elements and the bytes left, from which the caller works out where it stopped and what was written.
            int entries = (int)((long)Unsafe.ByteOffset(ref value, ref last) / stride);
            return ((ulong)(uint)entries << 32) | (uint)left;
        }

        /// <summary>
        /// The room the element at <paramref name="next"/> needs for its longest form (negative: more than an
        /// <c>int</c> holds, which the writer rejects), out of line: the write loop's rare stop.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RoomFor<TItem>(scoped NinoRun run, TItem item, int next)
            where TItem : struct, INinoRunItem =>
            item.MaxSize(ref Unsafe.Add(ref run.First, (nint)next * run.Stride));

        /// <summary>
        /// The read: <paramref name="run"/>'s count of elements (every one in use) from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes): every item's length measured against the data
        /// first (<see cref="Measure{TItem}"/>), so nothing is allocated for data that ends early or holds a length
        /// out of range, then each element read and handed to <paramref name="store"/> (<see cref="ReadMeasured"/>).
        /// Returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Read<TItem, TStore>(scoped NinoRun run, TItem item, ref byte data, int end, TStore store)
            where TItem : struct, INinoRunItem
            where TStore : struct, INinoRunStore
        {
            int size = Measure(item, run.Count, ref data, end);
            ReadMeasured(run, item, ref data, size, store);
            return size;
        }

        /// <summary>
        /// The read loop over the <paramref name="size"/> bytes at <paramref name="data"/> that
        /// <see cref="Measure{TItem}"/> found <paramref name="run"/>'s items in. Few values across the string
        /// kernels' and the stores' calls, where x64 has five callee-saved registers for them: a moving source and the
        /// bytes left, the elements left, the element and its stride (none for a store of one slot, which keeps its
        /// fields in its home on the stack: its call takes their address, <see cref="SetAdds"/>). With the data, the
        /// position and the end, and the store's fields in registers, a set's read kept its position, count and
        /// element on the stack and read 1,000 strings 1.3 to 1.5 times as long (asm.py, local quick runs).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadMeasured<TItem, TStore>(scoped NinoRun run, TItem item, ref byte data, int size,
            TStore store)
            where TItem : struct, INinoRunItem
            where TStore : struct, INinoRunStore
        {
            ref byte source = ref data;
            ref byte element = ref run.First;
            int stride = store.OneSlot ? 0 : run.Stride, available = size;
            for (int left = run.Count; left > 0; left--)
            {
                int read = item.ReadAt(ref source, available, ref element, store.OneSlot);
                source = ref Unsafe.Add(ref source, (nint)(uint)read);
                available -= read;
                store.Stored(ref element);
                if (!store.OneSlot)
                    element = ref NinoRun.Next(ref element, stride);
            }
        }

        /// <summary>
        /// Reads <paramref name="length"/> strings (not 0) into <paramref name="set"/>, new or emptied, from the data
        /// at <paramref name="data"/> (<paramref name="end"/> bytes), as UTF-8 where <paramref name="utf8"/>: the set's
        /// top layer for strings, the steps of <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>. Into the values of
        /// its entries and linked where it takes them (<see cref="HashTables.CanInsert{T}"/>, from
        /// <see cref="HashTables.FillFrom"/> on: the set left empty if the data is rejected part way), else each added
        /// (<see cref="SetAdds"/>). Returns the bytes read. Not generic: a set of strings is code shared over its
        /// element, and this is its one call.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadSet(HashSet<string> set, int length, ref byte data, int end, bool utf8)
        {
            bool insert = HashTables.CanInsert(set, length);
            if (insert && length >= HashTables.FillFrom)
                return FillSet(set, length, ref data, end, utf8);

            string value = null;
            var run = new NinoRun(ref Unsafe.As<string, byte>(ref value), length, 0, NinoRun.NoKey, NinoRun.AllInUse);
            var adds = new SetAdds(set, insert, HashTables.Plan<string>.Walks);
            return utf8
                ? Read(run, default(NinoUtf8Item), ref data, end, adds)
                : Read(run, default(NinoUtf16Item), ref data, end, adds);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FillSet(HashSet<string> set, int length, ref byte data, int end, bool utf8)
        {
            try
            {
                var run = CollectionFields.Values(HashTables.Entries(set), length);
                int read = utf8
                    ? Read(run, default(NinoUtf8Item), ref data, end, default(InPlace))
                    : Read(run, default(NinoUtf16Item), ref data, end, default(InPlace));
                HashTables.Link(set, length);
                return read;
            }
            catch
            {
                HashTables.Unlink(set);
                throw;
            }
        }

        /// <summary>
        /// Checks that the data at <paramref name="data"/> (<paramref name="end"/> bytes) holds
        /// <paramref name="count"/> items, every length in range, without allocating
        /// (<see cref="INinoRunItem.Measure"/>; throws otherwise); returns the bytes they take. Out of line, so that
        /// its position and count stay in registers: in the read loop's method, whose stores' calls take them, both
        /// went to the stack (asm.py).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Measure<TItem>(TItem item, int count, ref byte data, int end)
            where TItem : struct, INinoRunItem
        {
            int pos = 0;
            for (; count > 0; count--)
                pos += item.Measure(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos);
            return pos;
        }

        /// <summary>The elements read in place (an array's, a list's, a new set's or dictionary's entries, filled then
        /// linked): nothing more.</summary>
        internal readonly struct InPlace : INinoRunStore
        {
            public bool OneSlot
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => false;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
            }
        }

        /// <summary>
        /// The strings of a <see cref="HashSet{T}"/> of strings added one at a time as each is read into the one
        /// element (<see cref="SetTable{T}.Add"/>: inserted, or added and its bucket walked).
        /// </summary>
        internal readonly struct SetAdds : INinoRunStore
        {
            private readonly HashSet<string> _set;
            private readonly bool _insert, _walk;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public SetAdds(HashSet<string> set, bool insert, bool walk)
            {
                _set = set;
                _insert = insert;
                _walk = walk;
            }

            public bool OneSlot
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            // A call, as the set's Add was from the loop it replaces, on the store's home: inlined into the read loop
            // (the set's hashing and resize), or its fields passed from registers, the loop's state went to the
            // stack (local quick runs: 1,000 strings read 1.3 to 1.5 times as long).
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Stored(ref byte element) =>
                SetTable<string>.Add(_set, Unsafe.As<byte, string>(ref element), _insert, _walk);
        }

        /// <summary>
        /// The pairs of a <see cref="Dictionary{TKey,TValue}"/> keyed by strings added one at a time as each is read
        /// into the one entry (<see cref="MapTable{TKey,TValue}.Add(Dictionary{TKey,TValue}, TKey, TValue, bool,
        /// bool)"/>): generic over the value only, a value without references or a string
        /// (<see cref="PairAdds"/>), so never shared code.
        /// </summary>
        internal readonly struct KeyAdds<TValue> : INinoRunStore
        {
            private readonly Dictionary<string, TValue> _dictionary;
            private readonly bool _insert, _walk;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public KeyAdds(Dictionary<string, TValue> dictionary, bool insert, bool walk)
            {
                _dictionary = dictionary;
                _insert = insert;
                _walk = walk;
            }

            public bool OneSlot
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            // A call on the store's home, as <see cref="SetAdds"/>'s.
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<string, TValue>>(ref element);
                MapTable<string, TValue>.Add(_dictionary, entry.Key, entry.Value, _insert, _walk);
            }
        }

        /// <summary>As <see cref="KeyAdds{TValue}"/>, a dictionary whose values are strings and keys are not.</summary>
        internal readonly struct ValueAdds<TKey> : INinoRunStore
        {
            private readonly Dictionary<TKey, string> _dictionary;
            private readonly bool _insert, _walk;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ValueAdds(Dictionary<TKey, string> dictionary, bool insert, bool walk)
            {
                _dictionary = dictionary;
                _insert = insert;
                _walk = walk;
            }

            public bool OneSlot
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            // A call on the store's home, as <see cref="SetAdds"/>'s.
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<TKey, string>>(ref element);
                MapTable<TKey, string>.Add(_dictionary, entry.Key, entry.Value, _insert, _walk);
            }
        }

        /// <summary>As <see cref="KeyAdds{TValue}"/>, a dictionary of strings to strings: not generic.</summary>
        internal readonly struct PairAdds : INinoRunStore
        {
            private readonly Dictionary<string, string> _dictionary;
            private readonly bool _insert, _walk;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public PairAdds(Dictionary<string, string> dictionary, bool insert, bool walk)
            {
                _dictionary = dictionary;
                _insert = insert;
                _walk = walk;
            }

            public bool OneSlot
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            // A call on the store's home, as <see cref="SetAdds"/>'s.
            [MethodImpl(MethodImplOptions.NoInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<string, string>>(ref element);
                MapTable<string, string>.Add(_dictionary, entry.Key, entry.Value, _insert, _walk);
            }
        }
    }
}
