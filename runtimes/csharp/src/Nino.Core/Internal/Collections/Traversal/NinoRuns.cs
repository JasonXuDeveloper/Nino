using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The loops over a run of a collection's storage (<see cref="NinoRun"/>), one per element kind
    /// (<see cref="NinoKind"/>) and direction, over <c>&lt;T, TFormatter&gt;</c> only: every contiguous collection writes
    /// its elements through these, and reads them into its storage through the same view (#355). A run of values in a
    /// row (an array, a list, a span, each half of a ring) is <see cref="Write{T,TFormatter}(NinoRun, ref Writer)"/>
    /// and <see cref="Read{T,TFormatter}(NinoRun, ref Reader, bool)"/>; the entries of a set and of a dictionary are
    /// <see cref="WriteSet{T,TFormatter}"/> and <see cref="ReadSet{T,TFormatter}"/> (new entries, which the caller links
    /// after: <see cref="HashTables"/>). A dictionary's element is its pair, one value of a composite whose formatter
    /// composes its key and value (<see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>, or
    /// the entry's, <see cref="CollectionFields.MapEntryFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>): never
    /// a loop shape of its own. In each:
    /// <list type="bullet">
    /// <item>raw elements and blocks: one block, copied, or gathered out of the entries by the block kernels through
    /// the entries' one block formatter (<see cref="CollectionFields.EntryBlock{TEntry,TItems}"/>);</item>
    /// <item>slots: one block, through the slot kernels;</item>
    /// <item>a polymorphic base's values: its generated loop over the run (<see cref="INinoFormatter.WriteRun"/>),
    /// the dispatch inline and the writer in a local; a pair of them, its value formatter's loop over the run of its
    /// values with their keys (<see cref="WritePairRun{TKey,TValue,TKeyFormatter,TValueFormatter}"/>);</item>
    /// <item>values with the cursor form (strings, pairs holding them): the cursor loop;</item>
    /// <item>others: value by value through the formatter, a constrained call.</item>
    /// </list>
    /// Entries a set or a dictionary freed are skipped (<see cref="NinoRun.Skips"/>); the kinds moved as one block take
    /// a run without free entries, else they go value by value. Reads fill a run whose every entry is in use, and read
    /// a polymorphic base's values through its formatter's <c>Deserialize</c> (its subtype switch inlined there).
    /// </summary>
    internal static class NinoRuns
    {
        /// <summary>Writes the elements of <paramref name="values"/> (the count is the caller's).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ReadOnlySpan<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(NinoRun.Of(values), ref writer);

        /// <summary>
        /// Writes <paramref name="count"/> elements in a row starting at <paramref name="first"/> (a multi-dimensional
        /// array's; the count is the caller's).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ref T first, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref first, count), ref writer);

        /// <summary>
        /// Writes the elements of <paramref name="run"/>, a run of values in a row (the count is the caller's).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(NinoRun run, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = run.Count;
            ref T first = ref Unsafe.As<byte, T>(ref run.First);
            // Elements of a fixed size (NinoKind.Stride): one capacity check for the block, none per element. Raw
            // elements too, as the unmanaged formatters write them: an interface, Stack or Queue of them, or any
            // collection whose formatter is not specialized for them, would otherwise check the capacity per element.
            int block = NinoKind.Stride<T, TFormatter>();
            if (block > 0)
            {
                if (count != 0)
                    NinoElements.WriteBlock<T, TFormatter>(ref writer.ReserveElements(count, block), ref first, count);
                return;
            }

            // The loop stays in this method: a helper of its own was not inlined where the element's formatter
            // writes a collection of its own type (a tree), which added a call per level (SelfTree serialize 0.86x on
            // EPYC 9V74, runner A/B of the rc.10 review head against main 51a7bd18).
            // A polymorphic base: its generated loop over the run, with the dispatch inline, a constrained call on the
            // formatter on every target; a pair of them, its value's loop (KeyValuePairFormatter). The intrinsic itself,
            // not NinoUnsafe's: one inlinee more in this method, inlined into every collection's write, left the writer of
            // block arrays' top-level writes in memory (asm.py: ByteIntPairs32 10 to 38 stack accesses).
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && NinoKind<TFormatter>.Runs)
            {
                if (count != 0)
                    default(TFormatter).WriteRun(run, ref writer);
                return;
            }

            // Values with the cursor form (strings, pairs holding them): the cursor loop.
            if (NinoCursor.Own<T, TFormatter>())
            {
                if (count != 0)
                    writer = WriteCursors<T, TFormatter>(run, writer);
                return;
            }

            if (NinoElements.WritesOnLocal<T, TFormatter>())
            {
                if (count != 0)
                    writer = WriteOnLocal<T, TFormatter>(ref first, count, writer);
                return;
            }

            // By reference, as in WriteOnLocal.
            for (int left = count; left > 0; left--, first = ref Unsafe.Add(ref first, 1))
                default(TFormatter).Serialize(first, ref writer);
        }

        /// <summary>
        /// The elements one by one through a local copy of the writer (as reads do), out of line: Write is inlined
        /// into every collection formatter, and a second loop there slows down the code around it (as
        /// HashSetFormatter's raw loop was).
        /// </summary>
        private static Writer WriteOnLocal<T, TFormatter>(ref T item, int count, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            // By reference, not by index: a span's length is not known to the JIT to be positive (a list's count, for
            // one), and over a loop with a call in it each element kept a bounds check of its own.
            for (int left = count; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Serialize(item, ref writer);
            return writer;
        }

        /// <summary>
        /// Writes the count (<paramref name="count"/>, not 0: the set's) and the values of <paramref name="run"/>, a
        /// set's entries (<see cref="CollectionFields.Run{T}(System.Collections.Generic.HashSet{T}, int, out
        /// NinoRun)"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSet<T, TFormatter>(NinoRun run, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            var kind = NinoKind.Of<T, TFormatter>();
            bool whole = run.InUse == NinoRun.AllInUse;
            if (kind is NinoElementKind.Raw or NinoElementKind.Block && whole)
            {
                // Several entries at a time where the CPU permutes vectors, each value packed out of its entry.
                int size = NinoKind.Stride<T, TFormatter>();
                NinoBlockCollections.WriteAll<CollectionFields.SetEntry<T>,
                    CollectionFields.EntryBlock<CollectionFields.SetEntry<T>,
                        CollectionFields.SetValue<T, TFormatter>>>(ref writer.ReserveCounted(count, size),
                    ref CollectionFields.SetEntryOf<T>(ref run.First), count, size);
                return;
            }

            if (kind == NinoElementKind.Slot && whole && default(TFormatter).SlotsWriteInPlace)
            {
                WriteSlots<T, TFormatter>(run, count, ref writer);
                return;
            }

            // Through a copy of the writer, as the dictionaries' did: the writer's address never reaches the generated
            // loop, a call, so the caller keeps its fields in registers. The intrinsic itself, as in Write.
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && NinoKind<TFormatter>.Runs)
            {
                writer.WriteLength(count);
                Writer copy = default;
                writer.CopyTo(ref copy);
                default(TFormatter).WriteRun(run, ref copy);
                copy.CopyTo(ref writer);
                return;
            }

            // Values whose formatter has the cursor form (strings, pairs holding them): the cursor loop. Raw values and
            // blocks with free entries between them go value by value, as before: the loop's setup in every set's write
            // cost more code and stack than it saves them (asm.py: HashSet<NinoNullable<int>> 61 to 91 stack accesses).
            if (NinoCursor.Own<T, TFormatter>())
            {
                writer.WriteLength(count);
                writer = WriteCursors<T, TFormatter>(run, writer);
                return;
            }

            // Elements with references through the writer parameter, which their formatters' calls take anyway: a copy
            // in and out of WriteEach for each set cost more (NinoElements.WritesOnLocal).
            if (NinoElements.WritesOnLocal<T>())
                writer = WriteEach<T, TFormatter>(run, count, writer);
            else
                WriteEach<T, TFormatter>(run, count, ref writer);
        }

        /// <summary>
        /// The count and the slots, out of line: the values packed out of the entries as raw elements are, then
        /// written over themselves as slots (<see cref="INinoFormatter{T}.SlotsWriteInPlace"/>), which clears their
        /// padding and absent values (1,000 <c>int?</c>: 227 ns against 1,140 from a rented copy, local).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteSlots<T, TFormatter>(NinoRun run, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = Unsafe.SizeOf<T>();
            ref byte elements = ref writer.ReserveCounted(count, size);
            NinoBlockCollections.WriteAll<CollectionFields.SetEntry<T>,
                CollectionFields.EntryBlock<CollectionFields.SetEntry<T>, CollectionFields.SetValueBytes<T>>>(
                ref elements, ref CollectionFields.SetEntryOf<T>(ref run.First), count, size);
            default(TFormatter).WriteSlots(ref elements,
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, T>(ref elements),
                    count));
        }

        /// <summary>
        /// The count and the elements one by one: one loop, out of the caller (a second loop there made the raw one's
        /// code slower: HashSet&lt;int&gt; of 1,000 serialized 40% slower on x64 and Arm64). The writer by value and
        /// returned: a local here, its cursor in registers (HashSet&lt;int?&gt; of 1,000: 5 to 10% faster), where one
        /// passed by reference would be kept in memory, the caller's too.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer WriteEach<T, TFormatter>(NinoRun run, int count, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            WriteEach<T, TFormatter>(run, count, ref writer);
            return writer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteEach<T, TFormatter>(NinoRun run, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            writer.WriteLength(count);
            ref byte value = ref run.First;
            int stride = run.Stride, inUse = run.InUse;
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
            {
                if (!NinoRun.Skips(ref value, inUse))
                    NinoElements.WriteOne<T, TFormatter>(Unsafe.As<byte, T>(ref value), ref writer);
            }
        }

        /// <summary>
        /// The <see cref="NinoRun.Key"/> (without its offset) that a polymorphic value's generated loop writes the keys
        /// of a pair's run with: a string in the default encoding, or a key whose memory is its wire form (raw without
        /// padding, or a block that is its memory, <see cref="NinoBlockPlan{T,TFormatter}.IsMemory"/>) as its bytes;
        /// else <see cref="NinoRun.NoKey"/>, and the pairs are written value by value. A JIT-time constant for strings
        /// and keys with references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int KeyOf<TKey, TKeyFormatter>() where TKeyFormatter : struct, INinoFormatter<TKey> =>
            NinoKind<TKeyFormatter>.IsString ? NinoRun.KeyAt(0, -1)
            : NinoUnsafe.IsReferenceOrContainsReferences<TKey>() ? NinoRun.NoKey
            : Keys<TKey, TKeyFormatter>.Key;

        private static class Keys<TKey, TKeyFormatter> where TKeyFormatter : struct, INinoFormatter<TKey>
        {
            public static readonly int Key = Decide();

            private static int Decide()
            {
                var kind = NinoKind.Of<TKey, TKeyFormatter>();
                int size = NinoKind.Stride<TKey, TKeyFormatter>();
                bool memory = kind == NinoElementKind.Raw && !NinoLayout<TKey>.HasPadding ||
                              kind == NinoElementKind.Block && NinoBlockPlan<TKey, TKeyFormatter>.IsMemory;
                return BitConverter.IsLittleEndian && memory && size is > 0 and < 0xFF ? NinoRun.KeyAt(0, size) : NinoRun.NoKey;
            }
        }

        /// <summary>
        /// Whether a pair of <typeparamref name="TKey"/> and <typeparamref name="TValue"/> writes a run in one loop
        /// (<see cref="INinoFormatter.HasRunLoop"/> of a pair's formatter): its values are a polymorphic base's with a
        /// run loop, and its keys are written by that loop (<see cref="KeyOf{TKey,TKeyFormatter}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool PairRuns<TKey, TValue, TKeyFormatter, TValueFormatter>()
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            !typeof(TValue).IsValueType && NinoKind<TValueFormatter>.Runs &&
            KeyOf<TKey, TKeyFormatter>() != NinoRun.NoKey;

        /// <summary>
        /// Writes a run of pairs (<see cref="PairRuns{TKey,TValue,TKeyFormatter,TValueFormatter}"/>; the count is the
        /// caller's), each value at <paramref name="value"/> bytes from its pair and its key at <paramref name="key"/>:
        /// the value's generated loop over the run of the values (<see cref="NinoRun.Values"/>), each written after its
        /// key. A pair's formatter composes onto its value's: the one place a key is written apart from its value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WritePairRun<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, int value, int key,
            ref Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            default(TValueFormatter).WriteRun(run.Values(value, key, KeyOf<TKey, TKeyFormatter>()), ref writer);

        /// <summary>
        /// Writes the values of <paramref name="run"/> (the count is the caller's) through their cursor form
        /// (<see cref="WriteCursors{T,TFormatter}(NinoRun, int, ref byte, int)"/>) into the free part of the buffer,
        /// which grows when a value does not fit and the loop goes on from it. Only the free space goes to the loop,
        /// never the writer. Out of line with the writer by value and returned, as
        /// <see cref="WriteOnLocal{T,TFormatter}"/>: a reference to it would keep the caller's in memory, and this
        /// driver inline cost every caller more than the call (asm.py: AppData's lookups 117 to 301 stack accesses
        /// with the whole driver inline; string arrays' callers 15 to 47 with only the first pass inline, against 29
        /// out of line).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer WriteCursors<T, TFormatter>(NinoRun run, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int next = 0;
            while (true)
            {
                ulong left = WriteCursors<T, TFormatter>(run, next, ref writer.FreeSpace(out int available), available);
                writer.Commit(available - (int)left);
                int entries = (int)(left >> 32);
                if (entries == 0)
                    return writer;
                next = run.Count - entries;
                writer.Ensure(CursorSize<T, TFormatter>(run, next));
            }
        }

        /// <summary>
        /// The cursor loop of writes: from the entry at <paramref name="next"/> on, each value of <paramref name="run"/>
        /// in use, in its cursor form (<see cref="NinoCursor"/>; a pair's key then value, composed by its formatter),
        /// into the <paramref name="available"/> bytes at <paramref name="destination"/>, the free part of the buffer.
        /// The data, position and end are locals in registers. Returns the bytes left (low half) and the entries left
        /// (high half): 0 when every value was written, else from the first whose longest form did not fit. The loop of
        /// strings, UTF-8 strings, raw values and blocks, and pairs of them in any pairing, in sets, arrays, lists and
        /// dictionaries alike (#347).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong WriteCursors<T, TFormatter>(NinoRun run, int next, ref byte destination, int available)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Twice from one body: runs without free entries (arrays, lists, most sets and dictionaries) with the
            // in-use test folded away, which leaves their loop's values (the entry, the end, the stride, the
            // destination, the bytes left) the registers that hold them through the string kernels' calls; with the
            // test's rule too, the bytes left went to the stack (asm.py).
            return run.InUse == NinoRun.AllInUse
                ? WriteCursors<T, TFormatter>(run, next, ref destination, available, NinoRun.AllInUse)
                : WriteCursors<T, TFormatter>(run, next, ref destination, available, run.InUse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong WriteCursors<T, TFormatter>(NinoRun run, int next, ref byte destination, int available,
            int inUse)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Few values across the string kernels' calls, where x64 has five callee-saved registers for them: a
            // moving destination and the bytes left (not a start, a size and a position), and the entry against the
            // run's end (not an index and a count, which the caller works out). With nine, the position and the entry
            // were kept on the stack (asm.py).
            int stride = run.Stride;
            ref byte value = ref Unsafe.Add(ref run.First, (nint)next * stride);
            ref byte last = ref Unsafe.Add(ref run.First, (nint)run.Count * stride);
            ref byte cursor = ref destination;
            int left = available;
            for (; Unsafe.IsAddressLessThan(ref value, ref last); value = ref NinoRun.Next(ref value, stride))
            {
                if (NinoRun.Skips(ref value, inUse))
                    continue;
                ref T element = ref Unsafe.As<byte, T>(ref value);
                // Room for the longest form, so a value (a pair) is never split across two buffers.
                if ((uint)NinoCursor.MaxSize<T, TFormatter>(in element) > (uint)left)
                    break;
                int size = NinoCursor.WriteAt<T, TFormatter>(ref cursor, in element);
                cursor = ref Unsafe.Add(ref cursor, (nint)(uint)size);
                left -= size;
            }

            // Nothing is kept through the loop for this: the entries and the bytes left, from which the caller, which
            // knows the count and the bytes it passed, works out where it stopped and what was written (with the
            // count or the first value read here, the JIT kept one in a register through the loop and the bytes left
            // went to the stack).
            int entries = (int)((long)Unsafe.ByteOffset(ref value, ref last) / stride);
            return ((ulong)(uint)entries << 32) | (uint)left;
        }

        /// <summary>
        /// The room the value at <paramref name="next"/> of a run needs for its longest form (negative: more than an
        /// <c>int</c> holds, which the writer rejects), out of line: the cursor loop's rare stop.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CursorSize<T, TFormatter>(NinoRun run, int next) where TFormatter : struct, INinoFormatter<T> =>
            NinoCursor.MaxSize<T, TFormatter>(in Unsafe.As<byte, T>(ref Unsafe.Add(ref run.First, (nint)next * run.Stride)));

        /// <summary>Reads into the elements of <paramref name="values"/>, reusing the existing values.</summary>
        /// <param name="prechecked">The collection length already proved its entire fixed-stride payload fits.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(Span<T> values, ref Reader reader, bool prechecked = false)
            where TFormatter : struct, INinoFormatter<T> =>
            Read<T, TFormatter>(ref MemoryMarshal.GetReference(values), values.Length, ref reader, prechecked);

        /// <summary>
        /// Reads <paramref name="count"/> elements into the slots starting at <paramref name="first"/> (a
        /// multi-dimensional array's), as <see cref="Read{T,TFormatter}(Span{T}, ref Reader, bool)"/> reads a span.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(ref T first, int count, ref Reader reader, bool prechecked = false)
            where TFormatter : struct, INinoFormatter<T> =>
            Read<T, TFormatter>(new NinoRun(ref Unsafe.As<T, byte>(ref first), count, Unsafe.SizeOf<T>(), NinoRun.NoKey,
                NinoRun.AllInUse), ref reader, prechecked);

        /// <summary>
        /// Reads into the elements of <paramref name="run"/>, a run of values in a row (an array's, a list's backing
        /// array, a ring's from its start), reusing the existing values: the dense half of the one view reads fill
        /// (#355, design §2.4). The caller publishes what it filled (a list's count, a ring's head and tail).
        /// </summary>
        /// <param name="prechecked">The collection length already proved its entire fixed-stride payload fits.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(scoped NinoRun run, ref Reader reader, bool prechecked)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = run.Count;
            ref T first = ref Unsafe.As<byte, T>(ref run.First);
            // Raw elements: one bounds check and one block copy (see Write). Bools one by one (checked,
            // NinoLayout<T>.CopiesAsIs).
            var kind = NinoKind.Of<T, TFormatter>();
            if (kind == NinoElementKind.Raw && NinoLayout<T>.CopiesAsIs)
            {
                ref byte destination = ref Unsafe.As<T, byte>(ref first);
                ref byte source = ref TakeElements(ref reader, count, Unsafe.SizeOf<T>(), prechecked);
                uint bytes = (uint)count * (uint)Unsafe.SizeOf<T>();
#if NET8_0_OR_GREATER
                if (Unsafe.SizeOf<T>() == 1 && NinoUnsafe.IsArm64Short(bytes))
                    NinoUnsafe.CopyArm64ShortMayOverlap(ref destination, ref source, bytes);
                else
#endif
                    NinoUnsafe.CopyToNew(ref destination, ref source, bytes);
                return;
            }

            if (kind == NinoElementKind.Slot)
            {
                // The kernels check the presence bytes and take the slots in one pass over the block.
                if (count != 0)
                    ReadSlots<T, TFormatter>(ref first, count, ref reader, prechecked);
                return;
            }

            // Blocks as one run, except in Untrusted mode where their fields need checks (ReadSize -1): one by one.
            int block = NinoBlockCollections.ReadSize<T, TFormatter>();
            if (kind == NinoElementKind.Block && block > 0)
            {
                NinoBlockCollections.ReadAll<T, TFormatter>(ref TakeElements(ref reader, count, block, prechecked),
                    ref first, count, block);
                return;
            }

            // Empty collections (the leaves of a tree) skip the call and the copies of the reader in and out.
            if (count == 0)
                return;

            // Values whose formatter has the cursor form (strings, pairs holding them): the cursor loop. Raw values and
            // blocks that reach here are read one by one with their checks, as before.
            if (NinoCursor.Own<T, TFormatter>())
            {
                ReadCursors<T, TFormatter>(run, ref reader);
                return;
            }

            reader = ReadEach<T, TFormatter>(ref first, count, reader);
        }

        /// <summary>
        /// Reads the values of <paramref name="run"/> in place through their cursor form
        /// (<see cref="ReadCursors{T,TFormatter,TStore}"/>). The data and the bytes left go to the call, not the reader,
        /// which stays in the caller's registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadCursors<T, TFormatter>(scoped NinoRun run, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T> =>
            reader.UnsafeAdvance(ReadCursors<T, TFormatter, NinoCursor.InPlace<T>>(run, ref reader.Peek(),
                reader.Remaining, default));

        /// <summary>
        /// The cursor loop of reads: <paramref name="run"/>'s count of values from the data at <paramref name="data"/>
        /// (<paramref name="end"/> bytes), each in its cursor form (<see cref="NinoCursor"/>; a pair's key then value,
        /// composed by its formatter) into its entry, then handed to <paramref name="store"/>
        /// (<see cref="NinoCursor.IStore{T}"/>): read in place (an array's, a list's, a new set's or a new dictionary's
        /// entries, filled then linked), or added one at a time by a dictionary's own layer, which reads each into the
        /// one pair of a run with no stride. The data, position and end are locals in registers. Returns the bytes
        /// read. The store comes by value: by reference its address and a copy back kept two registers through the
        /// loop and its caller's store in memory (asm.py). Strings, UTF-8 strings, raw values and blocks, and pairs of
        /// them in any pairing (#347).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int ReadCursors<T, TFormatter, TStore>(scoped NinoRun run, ref byte data, int end, TStore store)
            where TFormatter : struct, INinoFormatter<T>
            where TStore : struct, NinoCursor.IStore<T>
        {
            ref byte entry = ref run.First;
            int stride = run.Stride, pos = 0;
            for (int left = run.Count; left > 0; left--, entry = ref NinoRun.Next(ref entry, stride))
            {
                ref T value = ref Unsafe.As<byte, T>(ref entry);
                pos += NinoCursor.ReadAt<T, TFormatter>(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos, ref value);
                store.Stored(ref value);
            }

            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte TakeElements(ref Reader reader, int count, int stride, bool prechecked) =>
            ref (prechecked ? ref reader.UnsafeAdvance(count * stride) : ref reader.ReadElements(count, stride));

        /// <summary>Reads <paramref name="count"/> slots (not 0), their bytes checked first unless
        /// <paramref name="prechecked"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ReadSlots<T, TFormatter>(ref T first, int count, ref Reader reader, bool prechecked)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (!prechecked && (ulong)(uint)count * (uint)NinoKind.Stride<T, TFormatter>() > (uint)reader.Remaining)
                NinoThrow.EndOfData();
            default(TFormatter).ReadSlots(MemoryMarshal.CreateSpan(ref first, count), ref reader);
        }

        /// <summary>
        /// The elements one by one, out of line: inlined into a read (also where they are blocks and it is never
        /// reached), the loop and the element's formatter used up what the JIT inlines into the method, and helpers as
        /// small as Reader.Skip were left calls. The reader by value and returned, not by reference: its address passed
        /// to a call would keep the caller's reader in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadEach<T, TFormatter>(scoped ref T item, int count, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            // The reader a local, its cursor in registers, where through a reference every element would store it and
            // load it back (List<(int, long)>: 2x faster). By reference, see WriteOnLocal.
            for (int left = count; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Deserialize(ref reader, ref item);
            return reader;
        }

        /// <summary>
        /// Whether a set's or a dictionary's entries take <typeparamref name="T"/>'s values (a dictionary's pairs) as
        /// one block, scattered into them by the block kernels (<see cref="CollectionFields.EntryBlock{TEntry,TItems}"/>): raw values (their
        /// checks made first on the block, <see cref="TakeSet{T,TFormatter}"/>) and blocks read as one
        /// (<see cref="NinoBlockCollections.ReadSize{T,TFormatter}"/>; not in Untrusted mode where their fields need
        /// checks). Their item bytes are <see cref="NinoKind.Stride{T,TFormatter}"/>. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ScattersItems<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw ||
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Block && NinoBlockCollections.ReadSize<T, TFormatter>() > 0;

        /// <summary>
        /// <see cref="ScattersItems{T,TFormatter}"/> for a set's values, and slots whose memory is their wire form
        /// (<see cref="INinoFormatter{T}.SlotsAreMemory"/>), checked on the block and moved as bytes. A JIT-time
        /// constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ScattersSet<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            ScattersItems<T, TFormatter>() ||
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot && default(TFormatter).SlotsAreMemory;

        /// <summary>
        /// Takes the block of <paramref name="count"/> values a set reads as one (<see cref="ScattersSet"/>), with the
        /// checks of their bytes a read of each makes: a raw bool other than 0 or 1 (and in Untrusted mode other values
        /// no constructor makes), a presence byte of a slot other than 0 or 1. Blocks are checked as they are read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte TakeSet<T, TFormatter>(ref Reader reader, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            int size = NinoKind.Stride<T, TFormatter>();
            ref byte source = ref reader.ReadElements(count, size);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                default(TFormatter).CheckSlots(ref source, count);
            else
                CheckRaw<T, TFormatter>(ref source, count, size);
            return ref source;
        }

        // Raw values the block moves without a look: a bool other than 0 or 1, and in Untrusted mode other values no
        // constructor makes, are invalid data (NinoLayout<T>.CopiesAsIs, a constant).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckRaw<T, TFormatter>(ref byte source, int count, int stride)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw && !NinoLayout<T>.CopiesAsIs)
                NinoChecks.Values<T>(ref source, count, stride);
        }

        /// <summary>
        /// Reads a value of a block <see cref="TakeSet{T,TFormatter}"/> took at
        /// <paramref name="source"/>: a set or dictionary that is not filled through its entries adds them one by one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T ReadItem<T, TFormatter>(ref byte source) where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                return NinoUnsafe.Read<T>(ref source);
            T value = default;
            NinoBlocks.ReadItem<T, TFormatter>(ref value, ref source);
            return value;
        }

        /// <summary>
        /// Reads the values of <paramref name="run"/>, a set's or a dictionary's new entries (every one in use,
        /// <see cref="CollectionFields.Run{T}(CollectionFields.SetEntry{T}[], int)"/>), from the data: the values a set takes as one block
        /// (<see cref="ScattersSet{T,TFormatter}"/>) scattered into the entries by the block kernels, the others value by
        /// value into their places. The entries' hash codes and links are the caller's
        /// (<see cref="HashTables.Link{T}(System.Collections.Generic.HashSet{T}, int)"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadSet<T, TFormatter>(NinoRun run, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (ScattersSet<T, TFormatter>())
            {
                ScatterSet<T, TFormatter>(run, ref TakeSet<T, TFormatter>(ref reader, run.Count));
                return;
            }

            if (NinoCursor.Own<T, TFormatter>())
            {
                ReadCursors<T, TFormatter>(run, ref reader);
                return;
            }

            reader = ReadSetEach<T, TFormatter>(run, reader);
        }

        /// <summary>
        /// The values a set takes as one block (<see cref="TakeSet{T,TFormatter}"/>'s, at <paramref name="source"/>),
        /// scattered into its entries: the gather of <see cref="WriteSet{T,TFormatter}"/> the other way. A caller that
        /// takes the block itself (to keep its reader out of a call) passes it here.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ScatterSet<T, TFormatter>(NinoRun run, ref byte source)
            where TFormatter : struct, INinoFormatter<T>
        {
            ref var entries = ref CollectionFields.SetEntryOf<T>(ref run.First);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                NinoBlockCollections.ReadAll<CollectionFields.SetEntry<T>,
                    CollectionFields.EntryBlock<CollectionFields.SetEntry<T>, CollectionFields.SetValueBytes<T>>>(
                    ref source, ref entries, run.Count, Unsafe.SizeOf<T>());
            else
                NinoBlockCollections.ReadAll<CollectionFields.SetEntry<T>,
                    CollectionFields.EntryBlock<CollectionFields.SetEntry<T>, CollectionFields.SetValue<T, TFormatter>>>(
                    ref source, ref entries, run.Count, NinoKind.Stride<T, TFormatter>());
        }

        /// <summary>
        /// The values of a set's run one by one into their entries, out of line with the reader by value (as
        /// <see cref="ReadEach{T,TFormatter}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Reader ReadSetEach<T, TFormatter>(NinoRun run, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            ref byte value = ref run.First;
            int stride = run.Stride;
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref Unsafe.As<byte, T>(ref value));
            return reader;
        }
    }
}
