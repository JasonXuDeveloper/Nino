using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// An item of a run's element at its offset, in the cursor form: the bytes it takes at most, written at a
    /// reference, measured and then read from the data (#355). The items of strings are not generic
    /// (<see cref="NinoUtf16Item"/>, <see cref="NinoUtf8Item"/>), and the other item of a pair holding one is a value
    /// without references (<see cref="NinoValueItem{T,TFormatter}"/>), so a loop over them (<see cref="NinoStringRuns"/>)
    /// is never code shared over a class: each call is direct and inlined, and no static is a lookup.
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
        /// <paramref name="element"/>, an existing string kept when the data holds the same; returns the bytes read.
        /// </summary>
        int ReadAt(ref byte source, int available, ref byte element);
    }

    /// <summary>What a read loop of <see cref="NinoStringRuns"/> does with each element it read.</summary>
    internal interface INinoRunStore
    {
        /// <summary>Takes the element read at <paramref name="element"/>.</summary>
        void Stored(ref byte element);
    }

    /// <summary>A string in the default encoding (<see cref="StringFormatter"/>, <see cref="NinoStrings"/>) at its
    /// offset in the element.</summary>
    internal readonly struct NinoUtf16Item : INinoRunItem
    {
        // nint: added to a reference as it is (Mono widens an int offset added to a reference as unsigned).
        private readonly nint _offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoUtf16Item(int offset) => _offset = offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref string At(ref byte element) =>
            ref Unsafe.As<byte, string>(ref Unsafe.AddByteOffset(ref element, _offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => NinoStrings.MaxSize(At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element) => NinoStrings.Write(ref destination, At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Measure(ref byte source, int available) => NinoStrings.Measure(ref source, available);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element)
        {
            // An unchanged string (a read into an existing collection) is not stored again: no write barrier.
            ref string slot = ref At(ref element);
            string current = slot;
            string read = NinoStrings.Read(ref source, available, current, out int size);
            if (!ReferenceEquals(read, current))
                slot = read;
            return size;
        }
    }

    /// <summary>A UTF-8 string (<see cref="Utf8StringFormatter"/>) at its offset in the element.</summary>
    internal readonly struct NinoUtf8Item : INinoRunItem
    {
        private readonly nint _offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoUtf8Item(int offset) => _offset = offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref string At(ref byte element) =>
            ref Unsafe.As<byte, string>(ref Unsafe.AddByteOffset(ref element, _offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => Writer.Utf8MaxSize(At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element) => Writer.WriteUtf8At(ref destination, At(ref element));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Measure(ref byte source, int available) => Reader.MeasureUtf8(ref source, available);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element)
        {
            ref string slot = ref At(ref element);
            string current = slot;
            SizedString read = Reader.ReadUtf8At(ref source, available, current);
            if (!ReferenceEquals(read.Value, current))
                slot = read.Value;
            return read.Size;
        }
    }

    /// <summary>
    /// A value of a fixed size at its offset in the element: raw, or a block read as one
    /// (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/>), moved as its formatter moves it in a block. Only for
    /// values without references, so a loop over it is not shared code.
    /// </summary>
    internal readonly struct NinoValueItem<T, TFormatter> : INinoRunItem where TFormatter : struct, INinoFormatter<T>
    {
        private readonly nint _offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoValueItem(int offset) => _offset = offset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref T At(ref byte element) => ref Unsafe.As<byte, T>(ref Unsafe.AddByteOffset(ref element, _offset));

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
        public int ReadAt(ref byte source, int available, ref byte element)
        {
            NinoBlocks.ReadItem<T, TFormatter>(ref At(ref element), ref source);
            return NinoKind.Stride<T, TFormatter>();
        }
    }

    /// <summary>
    /// Two items of one element, <typeparamref name="TA"/> then <typeparamref name="TB"/> (a pair's key and value, a
    /// dictionary's entry), each at its own offset: the element composed from its items, never a loop of its own.
    /// </summary>
    internal readonly struct NinoItemPair<TA, TB> : INinoRunItem
        where TA : struct, INinoRunItem
        where TB : struct, INinoRunItem
    {
        private readonly TA _a;
        private readonly TB _b;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemPair(TA a, TB b)
        {
            _a = a;
            _b = b;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            int first = _a.MaxSize(ref element);
            int second = _b.MaxSize(ref element);
            return (uint)first + (ulong)(uint)second > int.MaxValue ? -1 : first + second;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            int size = _a.WriteAt(ref destination, ref element);
            return size + _b.WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref element);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Measure(ref byte source, int available)
        {
            int size = _a.Measure(ref source, available);
            return size + _b.Measure(ref Unsafe.Add(ref source, (nint)(uint)size), available - size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element)
        {
            int size = _a.ReadAt(ref source, available, ref element);
            return size + _b.ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref element);
        }
    }

    /// <summary>
    /// The run kernels of strings, the bottom layer of every collection of strings as the block kernels are of raw
    /// values (#355): a run (<see cref="NinoRun"/>: its first element, count, stride and in-use test) of strings in
    /// the default encoding or UTF-8, or of pairs holding one (a dictionary's entries, a list's pairs), each item at
    /// its offset in the element. Writes reserve the whole run's longest form once, then transcode each string with
    /// the SIMD kernels and no capacity check of its own; reads measure every length of the run against the data
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
        public static Writer WriteUtf16(scoped NinoRun run, Writer writer) => Write(run, new NinoUtf16Item(0), writer);

        /// <summary>Writes the strings of <paramref name="run"/> (not empty; the count is the caller's) as
        /// UTF-8.</summary>
        public static Writer WriteUtf8(scoped NinoRun run, Writer writer) => Write(run, new NinoUtf8Item(0), writer);

        /// <summary>Reads the strings of <paramref name="run"/> (not empty, every one in use) in the default encoding
        /// in place from the data at <paramref name="data"/> (<paramref name="end"/> bytes); returns the bytes
        /// read.</summary>
        public static int ReadUtf16(scoped NinoRun run, ref byte data, int end) =>
            Read(run, new NinoUtf16Item(0), ref data, end, default(InPlace));

        /// <summary>As <see cref="ReadUtf16"/>, UTF-8.</summary>
        public static int ReadUtf8(scoped NinoRun run, ref byte data, int end) =>
            Read(run, new NinoUtf8Item(0), ref data, end, default(InPlace));

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
        public static Writer WritePairs<TA, TB, TFormatterA, TFormatterB>(scoped NinoRun run, int a, int b, Writer writer)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            if (typeof(TFormatterA) == typeof(StringFormatter))
                return WriteWith<NinoUtf16Item, TB, TFormatterB>(run, new NinoUtf16Item(a), b, writer);
            if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                return WriteWith<NinoUtf8Item, TB, TFormatterB>(run, new NinoUtf8Item(a), b, writer);
            return WriteWith<NinoValueItem<TA, TFormatterA>, TB, TFormatterB>(run,
                new NinoValueItem<TA, TFormatterA>(a), b, writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Writer WriteWith<TItemA, TB, TFormatterB>(scoped NinoRun run, TItemA first, int b, Writer writer)
            where TItemA : struct, INinoRunItem
            where TFormatterB : struct, INinoFormatter<TB>
        {
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return Write(run, new NinoItemPair<TItemA, NinoUtf16Item>(first, new NinoUtf16Item(b)), writer);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return Write(run, new NinoItemPair<TItemA, NinoUtf8Item>(first, new NinoUtf8Item(b)), writer);
            return Write(run, new NinoItemPair<TItemA, NinoValueItem<TB, TFormatterB>>(first,
                new NinoValueItem<TB, TFormatterB>(b)), writer);
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
                return ReadWith<NinoUtf16Item, TB, TFormatterB, TStore>(run, new NinoUtf16Item(a), b, ref data, end,
                    store);
            if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                return ReadWith<NinoUtf8Item, TB, TFormatterB, TStore>(run, new NinoUtf8Item(a), b, ref data, end,
                    store);
            return ReadWith<NinoValueItem<TA, TFormatterA>, TB, TFormatterB, TStore>(run,
                new NinoValueItem<TA, TFormatterA>(a), b, ref data, end, store);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadWith<TItemA, TB, TFormatterB, TStore>(scoped NinoRun run, TItemA first, int b,
            ref byte data, int end, TStore store)
            where TItemA : struct, INinoRunItem
            where TFormatterB : struct, INinoFormatter<TB>
            where TStore : struct, INinoRunStore
        {
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return Read(run, new NinoItemPair<TItemA, NinoUtf16Item>(first, new NinoUtf16Item(b)), ref data, end,
                    store);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return Read(run, new NinoItemPair<TItemA, NinoUtf8Item>(first, new NinoUtf8Item(b)), ref data, end,
                    store);
            return Read(run, new NinoItemPair<TItemA, NinoValueItem<TB, TFormatterB>>(first,
                new NinoValueItem<TB, TFormatterB>(b)), ref data, end, store);
        }

        /// <summary>
        /// The write loop: <paramref name="run"/>'s elements in use (not none), each <paramref name="item"/>, written
        /// into one reservation of their longest forms' sum with no check per element; past what an <c>int</c> holds,
        /// one reservation each. Out of line, the writer by value and returned, a local here.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer Write<TItem>(scoped NinoRun run, TItem item, Writer writer)
            where TItem : struct, INinoRunItem
        {
            int stride = run.Stride, inUse = run.InUse;
            ref byte last = ref Unsafe.Add(ref run.First, (nint)run.Count * stride);
            long total = 0;
            // The longest forms first (a string's length, read again by the copy right after, from the same line),
            // a negative one (more than an int) counted as more than an int.
            for (ref byte value = ref run.First; Unsafe.IsAddressLessThan(ref value, ref last);
                 value = ref NinoRun.Next(ref value, stride))
            {
                if (!NinoRun.Skips(ref value, inUse))
                    total += (uint)item.MaxSize(ref value);
            }

            if (total > int.MaxValue)
                return WriteEach(run, item, writer);

            ref byte start = ref writer.Ensure((int)total);
            ref byte cursor = ref start;
            for (ref byte value = ref run.First; Unsafe.IsAddressLessThan(ref value, ref last);
                 value = ref NinoRun.Next(ref value, stride))
            {
                if (!NinoRun.Skips(ref value, inUse))
                    cursor = ref Unsafe.Add(ref cursor, (nint)(uint)item.WriteAt(ref cursor, ref value));
            }

            writer.Commit((int)Unsafe.ByteOffset(ref start, ref cursor));
            return writer;
        }

        /// <summary>
        /// <see cref="Write{TItem}"/> of a run whose longest forms take more than an <c>int</c> holds: a reservation
        /// per element (one that cannot be reserved is rejected by the writer).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEach<TItem>(scoped NinoRun run, TItem item, Writer writer)
            where TItem : struct, INinoRunItem
        {
            int stride = run.Stride, inUse = run.InUse;
            ref byte value = ref run.First;
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
            {
                if (NinoRun.Skips(ref value, inUse))
                    continue;
                ref byte at = ref writer.Ensure(item.MaxSize(ref value));
                writer.Commit(item.WriteAt(ref at, ref value));
            }

            return writer;
        }

        /// <summary>
        /// The read loop: <paramref name="run"/>'s count of elements (every one in use; a stride of 0 reads each into
        /// the one element) from the data at <paramref name="data"/> (<paramref name="end"/> bytes): every item's
        /// length measured against the data first, so nothing is allocated for data that ends early or holds a length
        /// out of range, then each element read and handed to <paramref name="store"/>. Returns the bytes read. The
        /// store comes by value (by reference, its address and a copy back kept two registers through the loop).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Read<TItem, TStore>(scoped NinoRun run, TItem item, ref byte data, int end, TStore store)
            where TItem : struct, INinoRunItem
            where TStore : struct, INinoRunStore
        {
            int count = run.Count, pos = 0;
            for (int left = count; left > 0; left--)
                pos += item.Measure(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos);

            ref byte element = ref run.First;
            int stride = run.Stride;
            pos = 0;
            for (int left = count; left > 0; left--, element = ref NinoRun.Next(ref element, stride))
            {
                pos += item.ReadAt(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos, ref element);
                store.Stored(ref element);
            }

            return pos;
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
                ? Read(run, new NinoUtf8Item(0), ref data, end, adds)
                : Read(run, new NinoUtf16Item(0), ref data, end, adds);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FillSet(HashSet<string> set, int length, ref byte data, int end, bool utf8)
        {
            try
            {
                var run = CollectionFields.Values(HashTables.Entries(set), length);
                int read = utf8
                    ? Read(run, new NinoUtf8Item(0), ref data, end, default(InPlace))
                    : Read(run, new NinoUtf16Item(0), ref data, end, default(InPlace));
                HashTables.Link(set, length);
                return read;
            }
            catch
            {
                HashTables.Unlink(set);
                throw;
            }
        }

        /// <summary>The elements read in place (an array's, a list's, a new set's or dictionary's entries, filled then
        /// linked): nothing more.</summary>
        internal readonly struct InPlace : INinoRunStore
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
            }
        }

        /// <summary>
        /// The strings of a <see cref="HashSet{T}"/> of strings added one at a time as each is read into the one
        /// element (<see cref="SetTable{T}.Add"/>: inserted, or added and its bucket walked), then cleared.
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
                ref string value = ref Unsafe.As<byte, string>(ref element);
                SetTable<string>.Add(_set, value, _insert, _walk);
                value = null;
            }
        }

        /// <summary>
        /// The pairs of a <see cref="Dictionary{TKey,TValue}"/> keyed by strings added one at a time as each is read
        /// into the one entry (<see cref="MapTable{TKey,TValue}.Add(Dictionary{TKey,TValue}, TKey, TValue, bool,
        /// bool)"/>), then cleared: generic over the value only, a value without references or a string
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<string, TValue>>(ref element);
                MapTable<string, TValue>.Add(_dictionary, entry.Key, entry.Value, _insert, _walk);
                entry = default;
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<TKey, string>>(ref element);
                MapTable<TKey, string>.Add(_dictionary, entry.Key, entry.Value, _insert, _walk);
                entry = default;
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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<string, string>>(ref element);
                MapTable<string, string>.Add(_dictionary, entry.Key, entry.Value, _insert, _walk);
                entry = default;
            }
        }
    }
}
