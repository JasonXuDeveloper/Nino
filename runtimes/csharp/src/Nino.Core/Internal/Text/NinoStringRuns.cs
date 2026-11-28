using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
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

    /// <summary>
    /// The run kernels of strings, the bottom layer of every collection of strings as the block kernels are of raw
    /// values (#355): a run (<see cref="NinoRun"/>: its first element, count, stride and in-use test) of strings in
    /// the default encoding or UTF-8, or of pairs holding one (a dictionary's entries, a list's pairs), each item at
    /// its offset in the element. Writes transcode each string with the SIMD kernels into the free part of the
    /// buffer, one check of its longest form against the bytes left; reads check each string's lengths against the
    /// bytes left, then allocate and transcode it, storing each element in place or handing it to a store (a set's
    /// or a dictionary's adds). The string items are not generic, and a pair's other item is a value without references,
    /// so these loops are never compiled as code shared over a class: a collection of strings, which is shared code
    /// over its element, calls them once and pays no indirect call and no generic lookup per string. The writer and
    /// the reader never reach them: they take the writer by value and return it, or the data and its end.
    /// </summary>
    internal static class NinoStringRuns
    {
        /// <summary>Reads the strings of <paramref name="run"/> (not empty, every one in use) in the default encoding
        /// in place from the data at <paramref name="data"/> (<paramref name="end"/> bytes); returns the bytes
        /// read.</summary>
        public static int ReadUtf16(scoped NinoRun run, ref byte data, int end) =>
            Read(run, default(NinoUtf16Item), ref data, end, default(InPlace));

        /// <summary>As <see cref="ReadUtf16"/>, UTF-8.</summary>
        public static int ReadUtf8(scoped NinoRun run, ref byte data, int end) =>
            Read(run, default(NinoUtf8Item), ref data, end, default(InPlace));

        /// <summary>
        /// <see cref="WriteFree{TItem}"/> of the strings of <paramref name="run"/> (not empty) in
        /// <typeparamref name="TFormatter"/>'s encoding (<see cref="NinoKind{TFormatter}.Strings"/>), not generic over
        /// the element: the bytes left (low half) and the strings left (high half).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong WriteFree<TFormatter>(scoped in NinoRun run, ref byte destination, int available)
        {
            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                return WriteFree(run, default(NinoUtf8Item), ref destination, available);
            return WriteFree(run, default(NinoUtf16Item), ref destination, available);
        }

        /// <summary>
        /// The strings of <paramref name="run"/> from the one at <paramref name="next"/> (which did not fit the free
        /// space, <see cref="WriteFree{TFormatter}"/>) on, the buffer grown for each that does not fit.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteFrom<TFormatter>(scoped in NinoRun run, int next, ref Writer writer)
        {
            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                WriteGrown(ref run.First, run.Count, run.Stride, run.InUse, default(NinoUtf8Item), next, ref writer);
            else
                WriteGrown(ref run.First, run.Count, run.Stride, run.InUse, default(NinoUtf16Item), next, ref writer);
        }

        /// <summary>As <see cref="WriteFree{TFormatter}"/>, the read of <see cref="ReadUtf16"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read<TFormatter>(scoped NinoRun run, ref byte data, int end) =>
            typeof(TFormatter) == typeof(Utf8StringFormatter) ? ReadUtf8(run, ref data, end) : ReadUtf16(run, ref data, end);

        /// <summary>
        /// The write: <paramref name="run"/>'s elements in use (not none), each <paramref name="item"/>, written into
        /// the free part of the buffer by the write loop until one's longest form does not fit, then the buffer grown
        /// for it and the loop gone on from it (<see cref="WriteGrown{TItem}"/>). Inline, the loop too: every caller is
        /// one call out of line already (a dictionary's entries, a collection's strings, a pair's formatter), so a loop
        /// of its own was a second call per run, its prolog and the run's fields loaded again (a top-level
        /// Dictionary&lt;int, string&gt; of one pair written in 1.5 times main's time). Only the free space reaches
        /// the loop, never the writer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Write<TItem>(scoped in NinoRun run, TItem item, ref Writer writer)
            where TItem : struct, INinoItem
        {
            ulong left = WriteFree(run, item, ref writer.FreeSpace(out int available), available);
            writer.Commit(available - (int)left);
            int entries = (int)(left >> 32);
            if (entries != 0)
                WriteGrown(ref run.First, run.Count, run.Stride, run.InUse, item, run.Count - entries, ref writer);
        }

        /// <summary>
        /// <see cref="Write{TItem}"/>'s first pass, the writer's free space alone: <paramref name="run"/>'s elements
        /// in use (not none) written into the <paramref name="available"/> bytes at <paramref name="destination"/>
        /// until one's longest form does not fit. Returns the bytes left (low half) and the elements left (high half,
        /// 0 when every one fit), as the write loop does. What a top layer calls that hands the run kernels the free
        /// space in registers and takes the result back in one (a dictionary's entries,
        /// <see cref="NinoItems.WriteFree{TA,TB,TFormatterA,TFormatterB}"/>), falling back to its writer's path when
        /// the run did not fit.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong WriteFree<TItem>(scoped in NinoRun run, TItem item, ref byte destination, int available)
            where TItem : struct, INinoItem
        {
            // The run's fields as values from here on: a run whose address reached a call (the rare growth) stayed in
            // memory, built field by field and loaded back in the caller (a dictionary's entries).
            ref byte first = ref run.First;
            int count = run.Count, stride = run.Stride, inUse = run.InUse;
            // Twice from one body: runs without free entries (arrays, lists, most sets and dictionaries) with the
            // in-use test folded away, which leaves the loop's values the registers that hold them through the string
            // kernels' calls; with the test's rule too, the bytes left went to the stack (asm.py).
            return inUse == NinoRun.AllInUse
                ? WriteFitting(ref first, count, stride, NinoRun.AllInUse, item, 0, ref destination, available)
                : WriteFitting(ref first, count, stride, inUse, item, 0, ref destination, available);
        }

        /// <summary>
        /// <see cref="Write{TItem}"/> from the element at <paramref name="next"/>, which did not fit: the buffer grown
        /// for each element that does not fit and the loop gone on from it. Out of line: the rare path. The writer by
        /// reference and the run as its fields: a struct passed by value is copied with loads wider than the stores
        /// that just built it, which the CPU cannot forward (a store-forwarding stall per copy). As one out-of-line call
        /// taking and returning the writer by value, over a run passed by value, a top-level Dictionary&lt;int,
        /// string&gt; of one pair was written in 68 ns against main's 37 (perf: the copies of the writer and the run
        /// held most of its samples).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteGrown<TItem>(ref byte first, int count, int stride, int inUse, TItem item, int next,
            ref Writer writer)
            where TItem : struct, INinoItem
        {
            while (true)
            {
                writer.Ensure(RoomFor(item, ref Unsafe.Add(ref first, (nint)next * stride)));
                ref byte destination = ref writer.FreeSpace(out int available);
                ulong left = inUse == NinoRun.AllInUse
                    ? WriteFitting(ref first, count, stride, NinoRun.AllInUse, item, next, ref destination, available)
                    : WriteFitting(ref first, count, stride, inUse, item, next, ref destination, available);
                writer.Commit(available - (int)left);
                int entries = (int)(left >> 32);
                if (entries == 0)
                    return;
                next = count - entries;
            }
        }

        /// <summary>
        /// The write loop: from the element at <paramref name="next"/> on, each element of the run of
        /// <paramref name="count"/> at <paramref name="first"/> (<paramref name="stride"/> bytes apart) in use (the
        /// test <paramref name="inUse"/>, <see cref="NinoRun.InUse"/>), its <paramref name="item"/> written into the
        /// <paramref name="available"/> bytes at <paramref name="destination"/> once its longest form fits. Returns the
        /// bytes left (low half) and the elements left (high half): 0 when every one was written, else from the first
        /// that did not fit. One check per element against the bytes left, in registers: a first pass summing the run's
        /// longest forms to reserve them once took longer (1,000 strings written 1.3 to 1.4 times as long, local quick
        /// runs).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong WriteFitting<TItem>(ref byte first, int count, nint stride, int inUse, TItem item,
            int next, ref byte destination, int available)
            where TItem : struct, INinoItem
        {
            // Few values across the string kernels' calls, where x64 has five callee-saved registers for them: the
            // element and the destination move, the rest (the run's end, its stride, the destination's end) only read,
            // so whichever goes to the stack is a load, never a store and a reload carried from one element to the
            // next. The bytes left are the distance to the destination's end, not a count of their own: as a count the
            // bytes left and the destination were both carried through the stack (Dictionary<int, string>'s loop: a
            // store and a reload of each per pair, SimpleClass written in 1.18 times main's time).
            ref byte value = ref Unsafe.Add(ref first, next * stride);
            ref byte last = ref Unsafe.Add(ref first, count * stride);
            ref byte cursor = ref destination;
            ref byte end = ref Unsafe.Add(ref destination, (nint)(uint)available);
            for (; Unsafe.IsAddressLessThan(ref value, ref last); value = ref Unsafe.Add(ref value, stride))
            {
                if (NinoRun.Skips(ref value, inUse))
                    continue;
                // Room for the longest form, so a pair is never split across two buffers.
                if ((ulong)item.MaxSize(ref value) > (ulong)Unsafe.ByteOffset(ref cursor, ref end))
                    return Stopped(ref value, ref last, stride, (int)Unsafe.ByteOffset(ref cursor, ref end));
                cursor = ref Unsafe.Add(ref cursor, (nint)(uint)item.WriteAt(ref cursor, ref value));
            }

            // Every element written: none left, only the bytes.
            return (uint)(int)Unsafe.ByteOffset(ref cursor, ref end);
        }

        /// <summary>
        /// The elements left from <paramref name="value"/> to <paramref name="last"/> (high half) and the bytes left
        /// (low half), from which the caller works out where the write loop stopped: out of line, the loop's rare stop.
        /// The stride is data, so this is a division (a 64-bit idiv): only where the loop stopped early, never after a
        /// run that fit.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong Stopped(ref byte value, ref byte last, nint stride, int left)
        {
            int entries = (int)((long)Unsafe.ByteOffset(ref value, ref last) / stride);
            return ((ulong)(uint)entries << 32) | (uint)left;
        }

        /// <summary>
        /// The room the element at <paramref name="element"/> needs for its longest form (negative: more than an
        /// <c>int</c> holds, which the writer rejects), out of line: the write loop's rare stop.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int RoomFor<TItem>(TItem item, ref byte element)
            where TItem : struct, INinoItem
        {
            // The long bound narrowed here, out of line: in the driver it cost 8 stack accesses more (asm.py).
            long room = item.MaxSize(ref element);
            return room < 0 ? int.MinValue : room > int.MaxValue ? -1 : (int)room;
        }

        /// <summary>
        /// The read: <paramref name="run"/>'s count of elements (every one in use) from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes), each read and handed to <paramref name="store"/>;
        /// returns the bytes read. Each item checks its lengths against the bytes left before it allocates
        /// (<see cref="INinoItem.ReadAt"/>), and the caller sized the run by the data (a count of at least a byte
        /// each), so a rejected read has allocated in proportion to the bytes it read. A first pass that measured every
        /// length of the run before reading any took longer (a chain of dependent loads, one per string: 1,000
        /// strings read 1.1 to 1.3 times as long, local quick runs). Few values across the string kernels' and the
        /// stores' calls, where x64 has five callee-saved registers for them: a moving source and the bytes left, the
        /// elements left, the element and its stride (none for a store of one slot, which keeps its fields in its home
        /// on the stack: its call takes their address, <see cref="SetAdds"/>). With the data, the position and the end,
        /// and the store's fields in registers, a set's read kept its position, count and element on the stack and
        /// read 1,000 strings 1.3 to 1.5 times as long (asm.py, local quick runs).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Read<TItem, TStore>(scoped in NinoRun run, TItem item, ref byte data, int end, TStore store)
            where TItem : struct, INinoItem
            where TStore : struct, INinoRunStore
        {
            ref byte source = ref data;
            ref byte element = ref run.First;
            int stride = store.OneSlot ? 0 : run.Stride, available = end;
            for (int left = run.Count; left > 0; left--)
            {
                int read = item.ReadAt(ref source, available, ref element, store.OneSlot);
                source = ref Unsafe.Add(ref source, (nint)(uint)read);
                available -= read;
                store.Stored(ref element);
                if (!store.OneSlot)
                    element = ref NinoRun.Next(ref element, stride);
            }

            return end - available;
        }

        /// <summary>
        /// Reads <paramref name="length"/> strings (not 0) into <paramref name="set"/>, new or emptied, from the data
        /// at <paramref name="data"/> (<paramref name="end"/> bytes), as UTF-8 where <paramref name="utf8"/>: the set's
        /// top layer for strings, the steps of <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>. Into the values of
        /// its entries and linked where it takes them (<see cref="HashTables.CanInsert{T}"/>, from
        /// <see cref="HashTables.FillFrom"/> on: the set left empty if the data is rejected part way), else each added
        /// (<see cref="Add{TItem}"/>, or <see cref="SetAdds"/> where the set inserts or walks). Returns the bytes read. Not generic: a set of strings is code shared over its
        /// element, and this is its one call.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadSet(HashSet<string> set, int length, ref byte data, int end, bool utf8)
        {
            bool insert = HashTables.CanInsert(set, length);
            if (insert && length >= HashTables.FillFrom)
                return FillSet(set, length, ref data, end, utf8);

            // A set that guards its own buckets (strings on .NET: neither inserted nor walked): each string added
            // through the set's Add.
            if (!insert && !HashTables.Plan<string>.Walks)
                return utf8
                    ? Add<NinoUtf8Item>(set, length, ref data, end)
                    : Add<NinoUtf16Item>(set, length, ref data, end);

            string value = null;
            var run = new NinoRun(ref Unsafe.As<string, byte>(ref value), length, 0, NinoRun.NoKey, NinoRun.AllInUse);
            var adds = new SetAdds(set, insert, HashTables.Plan<string>.Walks);
            return utf8
                ? Read(run, default(NinoUtf8Item), ref data, end, adds)
                : Read(run, default(NinoUtf16Item), ref data, end, adds);
        }

        /// <summary>
        /// <see cref="ReadSet"/>'s adds into a set that guards its own buckets: <paramref name="length"/> strings, each
        /// <typeparamref name="TItem"/>, from the data at <paramref name="data"/> (<paramref name="end"/> bytes), each
        /// added through the set (a repeated one is invalid data); returns the bytes read. A loop of its own beside
        /// <see cref="Read{TItem,TStore}"/>'s: the set's Add inline after each read, where that loop's store
        /// (<see cref="SetAdds"/>, an out-of-line call that adds) read 1,000 strings 1.1 times as long (local quick
        /// runs).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Add<TItem>(HashSet<string> set, int length, ref byte data, int end)
            where TItem : struct, INinoItem
        {
            ref byte source = ref data;
            int available = end;
            for (int left = length; left > 0; left--)
            {
                string value = null;
                int read = default(TItem).ReadAt(ref source, available, ref Unsafe.As<string, byte>(ref value), true);
                source = ref Unsafe.Add(ref source, (nint)(uint)read);
                available -= read;
                if (!set.Add(value))
                    NinoThrow.DuplicateKey(set.GetType());
            }

            return end - available;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int FillSet(HashSet<string> set, int length, ref byte data, int end, bool utf8)
        {
            try
            {
                var run = CollectionFields.Run(HashTables.Entries(set), length)
                    .Items(CollectionFields.SetEntry<string>.ValueOffset);
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

        /// <summary>
        /// As <see cref="PairAdds"/>, into a dictionary that guards its own buckets (strings on .NET: neither inserted
        /// nor walked, <see cref="HashTables.Plan{TKey,TValue}"/>): each pair added through the dictionary, a repeated
        /// key invalid data, inline in the read loop. Its one field, the dictionary, leaves the loop its source, bytes
        /// left, count and slot in the callee-saved registers; through <see cref="PairAdds"/>' call on its home each
        /// pair took a call more and its key and value back from memory (CommonTypesTest's StringDictionary: 1,000
        /// pairs read in 1.16 to 1.50 times main's time on runners, run 46116965014).
        /// </summary>
        internal readonly struct GuardedPairAdds : INinoRunStore
        {
            private readonly Dictionary<string, string> _dictionary;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public GuardedPairAdds(Dictionary<string, string> dictionary) => _dictionary = dictionary;

            public bool OneSlot
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<string, string>>(ref element);
                if (entry.Key == null)
                    NinoThrow.NullKey(_dictionary);
                NinoCollections.AddUnique(_dictionary, entry.Key, entry.Value);
            }
        }

        /// <summary>As <see cref="GuardedPairAdds"/>, a dictionary keyed by strings whose values are not strings
        /// (<see cref="KeyAdds{TValue}"/>).</summary>
        internal readonly struct GuardedKeyAdds<TValue> : INinoRunStore
        {
            private readonly Dictionary<string, TValue> _dictionary;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public GuardedKeyAdds(Dictionary<string, TValue> dictionary) => _dictionary = dictionary;

            public bool OneSlot
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref byte element)
            {
                ref var entry = ref Unsafe.As<byte, CollectionFields.MapEntry<string, TValue>>(ref element);
                if (entry.Key == null)
                    NinoThrow.NullKey(_dictionary);
                NinoCollections.AddUnique(_dictionary, entry.Key, entry.Value);
            }
        }
    }
}
