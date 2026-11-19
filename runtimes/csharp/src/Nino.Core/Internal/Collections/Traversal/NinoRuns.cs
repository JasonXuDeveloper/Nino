using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The loops over a run of a collection's storage (<see cref="NinoRun"/>), one per element kind
    /// (<see cref="NinoKind"/>) and direction: every contiguous collection writes its elements through these, and
    /// reads them into its storage through the same view (#355). A run of values in a row (an array, a list, a span,
    /// each half of a ring) is <see cref="Write{T,TFormatter}(NinoRun, ref Writer)"/> and
    /// <see cref="Read{T,TFormatter}(NinoRun, ref Reader, bool)"/>; the entries of a set are
    /// <see cref="WriteSet{T,TFormatter}"/> and <see cref="ReadSet{T,TFormatter}"/>, and of a dictionary, whose element
    /// is the pair, <see cref="WritePairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/> and
    /// <see cref="ReadPairs{TKey,TValue,TKeyFormatter,TValueFormatter}"/> (new entries, which the caller links after:
    /// <see cref="HashTables"/>). In each:
    /// <list type="bullet">
    /// <item>raw elements and blocks: one block, copied, or gathered out of the entries by the block kernels through
    /// the entries' one block formatter (<see cref="CollectionFields.EntryBlock{TEntry,TItems}"/>);</item>
    /// <item>slots: one block, through the slot kernels;</item>
    /// <item>a polymorphic base's values: its generated loop over the run (<see cref="INinoFormatter.WriteRun"/>),
    /// the dispatch inline and the writer in a local;</item>
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
            // formatter on every target. Only classes have one.
            if (!typeof(T).IsValueType && NinoKind<TFormatter>.Runs)
            {
                if (count != 0)
                    default(TFormatter).WriteRun(run, ref writer);
                return;
            }

            // Values with the cursor form (strings): the element loop, the pair loop without keys.
            if (NinoCursor.Own<T, TFormatter>())
            {
                if (count != 0)
                    WriteCursors<T, TFormatter>(run, ref writer);
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

            if (!typeof(T).IsValueType && NinoKind<TFormatter>.Runs)
            {
                writer.WriteLength(count);
                default(TFormatter).WriteRun(run, ref writer);
                return;
            }

            // Values whose formatter has the cursor form (strings): the element loop. Raw values and blocks with free
            // entries between them go value by value, as before: the loop's setup in every set's write cost more code
            // and stack than it saves them (asm.py: HashSet<NinoNullable<int>> 61 to 91 stack accesses).
            if (NinoCursor.Own<T, TFormatter>())
            {
                writer.WriteLength(count);
                WriteCursors<T, TFormatter>(run, ref writer);
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
        private static Writer WriteEach<T, TFormatter>(NinoRun run, int count, Writer writer)
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
        /// of a dictionary's run with: a string in the default encoding, or a key whose memory is its wire form (raw
        /// without padding, or a block that is its memory, <see cref="NinoBlockPlan{T,TFormatter}.IsMemory"/>) as its
        /// bytes; else <see cref="NinoRun.NoKey"/>, and the dictionary is written value by value. A JIT-time constant
        /// for strings and keys with references.
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
        /// Writes the count (<paramref name="count"/>, not 0: the dictionary's) and the pairs of
        /// <paramref name="run"/>, a dictionary's entries (<see cref="CollectionFields.Run{TKey,TValue}"/>, its key
        /// <see cref="KeyOf{TKey,TKeyFormatter}"/> where the values have <see cref="INinoFormatter.HasRunLoop"/>). The
        /// pair is written as its formatter writes it: key, then value.
        /// </summary>
        // Inlined, with the loops out of line: the writer's address never reaches a call that is not inlined (blocks
        // are written into one reservation taken here; other pairs go through a copy of the writer), so the caller
        // keeps the writer's fields in registers.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WritePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, int count,
            ref Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            var keyKind = NinoKind.Of<TKey, TKeyFormatter>();
            var valueKind = NinoKind.Of<TValue, TValueFormatter>();
            if (keyKind is NinoElementKind.Raw or NinoElementKind.Block &&
                valueKind is NinoElementKind.Raw or NinoElementKind.Block && run.InUse == NinoRun.AllInUse)
            {
                // A pair of raw items or blocks is a block (KeyValuePairFormatter's): several entries at a time where
                // the CPU permutes vectors, each key and value packed out of its entry.
                int size = NinoKind.Stride<TKey, TKeyFormatter>() + NinoKind.Stride<TValue, TValueFormatter>();
                WritePairBlocks<TKey, TValue, TKeyFormatter, TValueFormatter>(run, ref writer.ReserveCounted(count, size),
                    count, size);
                return;
            }

            // A polymorphic value whose key the run says how to write: the base's generated loop over the run.
            if (!typeof(TValue).IsValueType && NinoKind<TValueFormatter>.Runs && run.Key != NinoRun.NoKey)
            {
                writer.WriteLength(count);
                Writer pairs = default;
                writer.CopyTo(ref pairs);
                default(TValueFormatter).WriteRun(run, ref pairs);
                pairs.CopyTo(ref writer);
                return;
            }

            // Keys and values with the cursor form, a string among them (raw values and blocks alone are blocks, or with
            // free entries value by value, as for sets): the pair loop.
            if (CursorPairs<TKey, TValue, TKeyFormatter, TValueFormatter>())
            {
                writer.WriteLength(count);
                writer = WriteCursors<TKey, TValue, TKeyFormatter, TValueFormatter>(run, MapKey<TKey, TValue>(), writer);
                return;
            }

            Writer copy = default;
            writer.CopyTo(ref copy);
            WritePairsEach<TKey, TValue, TKeyFormatter, TValueFormatter>(run, count, ref copy);
            copy.CopyTo(ref writer);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WritePairBlocks<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run,
            ref byte pairs, int count, int size)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            NinoBlockCollections.WriteAll<CollectionFields.MapEntry<TKey, TValue>,
                CollectionFields.EntryBlock<CollectionFields.MapEntry<TKey, TValue>,
                    CollectionFields.MapPair<TKey, TValue, TKeyFormatter, TValueFormatter>>>(ref pairs,
                ref CollectionFields.MapEntryOf<TKey, TValue>(ref run.First), count, size);

        /// <summary>The count and the pairs one by one, each key and value through its formatter.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WritePairsEach<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, int count,
            ref Writer target)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            // A local writer, so its fields stay in registers inside the loop.
            Writer writer = default;
            target.CopyTo(ref writer);
            writer.WriteLength(count);
            ref byte value = ref run.First;
            int stride = run.Stride, inUse = run.InUse;
            nint key = MapKey<TKey, TValue>();
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
            {
                if (NinoRun.Skips(ref value, inUse))
                    continue;
                default(TKeyFormatter).Serialize(Unsafe.As<byte, TKey>(ref Unsafe.AddByteOffset(ref value, key)), ref writer);
                default(TValueFormatter).Serialize(Unsafe.As<byte, TValue>(ref value), ref writer);
            }

            writer.CopyTo(ref target);
        }

        /// <summary>
        /// Whether a dictionary's pairs take the pair loop: keys and values with the cursor form
        /// (<see cref="NinoCursor.Has{T,TFormatter}"/>), one of them a formatter's own (a string). A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool CursorPairs<TKey, TValue, TKeyFormatter, TValueFormatter>()
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            (NinoCursor.Own<TKey, TKeyFormatter>() || NinoCursor.Own<TValue, TValueFormatter>()) &&
            NinoCursor.Has<TKey, TKeyFormatter>() && NinoCursor.Has<TValue, TValueFormatter>();

        /// <summary>The offset of a dictionary entry's key from its value (<see cref="CollectionFields.MapLayout{TKey,TValue}"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static nint MapKey<TKey, TValue>() =>
            CollectionFields.MapLayout<TKey, TValue>.Key - CollectionFields.MapLayout<TKey, TValue>.Value;

        /// <summary>
        /// Writes the values of <paramref name="run"/> (the count is the caller's) through their cursor form: the
        /// element loop, the pair loop without keys.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteCursors<T, TFormatter>(NinoRun run, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            writer = WriteCursors<NinoCursor.None, T, NinoCursor.NoneFormatter, TFormatter>(run, 0, writer);

        /// <summary>
        /// Writes the pairs of <paramref name="run"/> (the count is the caller's), each key at <paramref name="key"/>
        /// bytes from its value, through the pair loop
        /// (<see cref="WriteCursors{TKey,TValue,TKeyFormatter,TValueFormatter}(NinoRun, nint, int, ref byte, int)"/>)
        /// into the free part of the buffer, which grows when a pair does not fit and the loop goes on from it. Only
        /// the free space goes to the loop, never the writer. Out of line with the writer by value and returned, as
        /// <see cref="WriteOnLocal{T,TFormatter}"/>: a reference to it would keep the caller's in memory, and this
        /// driver inline cost every caller more than the call (asm.py: AppData's lookups 117 to 301 stack accesses
        /// with the whole driver inline; string arrays' callers 15 to 47 with only the first pass inline, against 29
        /// out of line).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteCursors<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, nint key,
            Writer writer)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            int next = 0;
            while (true)
            {
                ulong left = WriteCursors<TKey, TValue, TKeyFormatter, TValueFormatter>(run, key, next,
                    ref writer.FreeSpace(out int available), available);
                writer.Commit(available - (int)left);
                int entries = (int)(left >> 32);
                if (entries == 0)
                    return writer;
                next = run.Count - entries;
                writer.Ensure(CursorSize<TKey, TValue, TKeyFormatter, TValueFormatter>(run, key, next));
            }
        }

        /// <summary>
        /// The pair loop of writes: from the entry at <paramref name="next"/> on, each entry of <paramref name="run"/>
        /// in use, its key (at <paramref name="key"/> bytes from the value; none where <typeparamref name="TKey"/> is
        /// <see cref="NinoCursor.None"/>) then its value, each in its cursor form (<see cref="NinoCursor"/>), into the
        /// <paramref name="available"/> bytes at <paramref name="destination"/>, the free part of the buffer. The data,
        /// position and end are locals in registers. Returns the bytes left (low half) and the entries left (high
        /// half): 0 when every pair was written, else from the first whose longest form did not fit.
        /// The loop of strings, UTF-8 strings, raw values and blocks in any pairing, in sets, arrays, lists and
        /// dictionaries alike (#347; it replaces the string pair loop, which wrote raw keys and strings, or strings and
        /// raw values, only, and gave up on the whole dictionary when one did not fit).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong WriteCursors<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, nint key,
            int next, ref byte destination, int available)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            // Twice from one body: runs without free entries (arrays, lists, most sets and dictionaries) with the
            // in-use test folded away, which leaves their loop's six values (the entry, the end, the stride, the
            // destination, the bytes left, a dictionary's key offset) the registers that hold them through the string
            // kernels' calls; with the test's rule too, the bytes left went to the stack (asm.py).
            return run.InUse == NinoRun.AllInUse
                ? WriteCursors<TKey, TValue, TKeyFormatter, TValueFormatter>(run, key, next, ref destination, available,
                    NinoRun.AllInUse)
                : WriteCursors<TKey, TValue, TKeyFormatter, TValueFormatter>(run, key, next, ref destination, available,
                    run.InUse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong WriteCursors<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, nint key,
            int next, ref byte destination, int available, int inUse)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            // Few values across the string kernels' calls, where x64 has five callee-saved registers for them: a
            // moving destination and the bytes left (not a start, a size and a position), and the entry against the
            // run's end (not an index and a count, which the caller works out). With nine, the position and the entry
            // were kept on the stack (asm.py); now a run of values in a row keeps all five in registers, a dictionary's
            // pairs all but the bytes left (its key's offset is the sixth).
            int stride = run.Stride;
            ref byte value = ref Unsafe.Add(ref run.First, (nint)next * stride);
            ref byte last = ref Unsafe.Add(ref run.First, (nint)run.Count * stride);
            ref byte cursor = ref destination;
            int left = available;
            for (; Unsafe.IsAddressLessThan(ref value, ref last); value = ref NinoRun.Next(ref value, stride))
            {
                if (NinoRun.Skips(ref value, inUse))
                    continue;
                ref TKey item = ref Unsafe.As<byte, TKey>(ref Unsafe.AddByteOffset(ref value, key));
                ref TValue element = ref Unsafe.As<byte, TValue>(ref value);
                // Room for the longest form of both, so a pair is never split across two buffers.
                if ((ulong)(uint)NinoCursor.MaxSize<TKey, TKeyFormatter>(in item) +
                    (uint)NinoCursor.MaxSize<TValue, TValueFormatter>(in element) > (uint)left)
                    break;
                // Past each item as it is written: a sum of the two would live across the value's write.
                int size = NinoCursor.WriteAt<TKey, TKeyFormatter>(ref cursor, in item);
                cursor = ref Unsafe.Add(ref cursor, (nint)(uint)size);
                left -= size;
                size = NinoCursor.WriteAt<TValue, TValueFormatter>(ref cursor, in element);
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
        /// The room the pair at <paramref name="next"/> of a run needs for its longest form (negative: more than an
        /// <c>int</c> holds, which the writer rejects), out of line: the pair loop's rare stop.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int CursorSize<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, nint key, int next)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            ref byte value = ref Unsafe.Add(ref run.First, (nint)next * run.Stride);
            ulong size = (ulong)(uint)NinoCursor.MaxSize<TKey, TKeyFormatter>(
                             in Unsafe.As<byte, TKey>(ref Unsafe.AddByteOffset(ref value, key))) +
                         (uint)NinoCursor.MaxSize<TValue, TValueFormatter>(in Unsafe.As<byte, TValue>(ref value));
            return size > int.MaxValue ? -1 : (int)size;
        }

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

            // Values whose formatter has the cursor form (strings): the element loop, the pair loop without keys. Raw
            // values and blocks that reach here are read one by one with their checks, as before.
            if (NinoCursor.Own<T, TFormatter>())
            {
                ReadCursors<T, TFormatter>(run, ref reader);
                return;
            }

            reader = ReadEach<T, TFormatter>(ref first, count, reader);
        }

        /// <summary>
        /// Reads the values of <paramref name="run"/> in place through their cursor form (the element loop:
        /// <see cref="ReadCursors{TKey,TValue,TKeyFormatter,TValueFormatter,TStore}"/> without keys). The data and the
        /// bytes left go to the call, not the reader, which stays in the caller's registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadCursors<T, TFormatter>(scoped NinoRun run, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T> =>
            reader.UnsafeAdvance((int)ReadCursors<NinoCursor.None, T, NinoCursor.NoneFormatter, TFormatter,
                NinoCursor.Entries>(run, ref reader.Peek(), reader.Remaining, default));

        /// <summary>
        /// The pair loop of reads: <paramref name="run"/>'s count of pairs from the data at <paramref name="data"/>
        /// (<paramref name="end"/> bytes), each key (none where <typeparamref name="TKey"/> is
        /// <see cref="NinoCursor.None"/>) then value in its cursor form (<see cref="NinoCursor"/>), into the places
        /// <paramref name="store"/> gives and then handed to it: the entries of the run (a new dictionary's, filled then
        /// linked; an array's, a list's or a new set's values: <see cref="NinoCursor.Entries"/>), a dictionary's adds
        /// (<see cref="NinoCursor.Adds"/>), or its merge into the pairs there (<see cref="NinoCursor.Merges"/>). The
        /// data, position and end are locals in registers. Returns the bytes read (low half) and, for a merge, the keys
        /// already there (high half). The store comes by value: by reference its address and a copy back kept two
        /// registers through the loop and its caller's store in memory (asm.py). Strings, UTF-8 strings, raw values
        /// and blocks in any pairing (#347; it replaces the string loops, which read strings in a row, raw keys and
        /// strings, and pairs of strings, only).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static ulong ReadCursors<TKey, TValue, TKeyFormatter, TValueFormatter, TStore>(scoped NinoRun run,
            ref byte data, int end, TStore store)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
            where TStore : struct
        {
            nint keyAt = NinoCursor.KeyAt(ref store);
            ref byte entry = ref run.First;
            int stride = run.Stride, pos = 0;
            for (int left = run.Count; left > 0; left--, entry = ref NinoRun.Next(ref entry, stride))
            {
                ref TKey key = ref Unsafe.As<byte, TKey>(ref Unsafe.AddByteOffset(ref entry, keyAt));
                pos += NinoCursor.ReadAt<TKey, TKeyFormatter>(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos,
                    ref key);
                ref TValue value = ref NinoCursor.ValueAt<TKey, TValue, TStore>(ref store, ref entry, ref key);
                pos += NinoCursor.ReadAt<TValue, TValueFormatter>(ref Unsafe.Add(ref data, (nint)(uint)pos), end - pos,
                    ref value);
                NinoCursor.Stored<TKey, TValue, TStore>(ref store, ref key, ref value);
            }

            // Only a merge counts anything.
            ulong matched = typeof(TStore) == typeof(NinoCursor.Merges)
                ? (uint)Unsafe.As<TStore, NinoCursor.Merges>(ref store).Matched
                : 0;
            return (matched << 32) | (uint)pos;
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
        /// Whether a set's or a dictionary's entries take <typeparamref name="T"/>'s values as one block, scattered
        /// into them by the block kernels (<see cref="CollectionFields.EntryBlock{TEntry,TItems}"/>): raw values (their
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

        /// <summary>
        /// As <see cref="TakeSet{T,TFormatter}"/>, for the pairs of a dictionary whose keys and values are both
        /// <see cref="ScattersItems{T,TFormatter}"/>: each pair the key's item bytes, then the value's.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte TakePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(ref Reader reader, int count)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            int keySize = NinoKind.Stride<TKey, TKeyFormatter>();
            int size = keySize + NinoKind.Stride<TValue, TValueFormatter>();
            ref byte source = ref reader.ReadElements(count, size);
            CheckRaw<TKey, TKeyFormatter>(ref source, count, size);
            CheckRaw<TValue, TValueFormatter>(ref Unsafe.Add(ref source, keySize), count, size);
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
        /// Reads a value of a block <see cref="TakeSet{T,TFormatter}"/> or <see cref="TakePairs"/> took at
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
        /// Reads the values of <paramref name="run"/>, a set's new entries (every one in use, <see cref="CollectionFields
        /// .Run{T}(CollectionFields.SetEntry{T}[], int)"/>), from the data: the values a set takes as one block
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
        private static Reader ReadSetEach<T, TFormatter>(NinoRun run, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            ref byte value = ref run.First;
            int stride = run.Stride;
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref Unsafe.As<byte, T>(ref value));
            return reader;
        }

        /// <summary>
        /// Reads the pairs of <paramref name="run"/>, a dictionary's new entries (every one in use,
        /// <see cref="CollectionFields.Run{TKey,TValue}(CollectionFields.MapEntry{TKey,TValue}[], int)"/>), from the data,
        /// as its formatter wrote them, key then value: pairs whose keys and values are both
        /// <see cref="ScattersItems{T,TFormatter}"/> scattered into the entries by the block kernels, the others pair by
        /// pair into their places. The entries' hash codes and links are the caller's
        /// (<see cref="HashTables.Link{TKey,TValue}(System.Collections.Generic.Dictionary{TKey,TValue}, int)"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, ref Reader reader)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            if (ScattersItems<TKey, TKeyFormatter>() && ScattersItems<TValue, TValueFormatter>())
            {
                ScatterPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(run,
                    ref TakePairs<TKey, TValue, TKeyFormatter, TValueFormatter>(ref reader, run.Count));
                return;
            }

            reader = ReadPairsEach<TKey, TValue, TKeyFormatter, TValueFormatter>(run, reader);
        }

        /// <summary>
        /// The pairs of <see cref="TakePairs"/>' block at <paramref name="source"/> scattered into a dictionary's
        /// entries: the gather of <see cref="WritePairBlocks"/> the other way. A caller that takes the block itself
        /// passes it here, as with <see cref="ScatterSet{T,TFormatter}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ScatterPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run,
            ref byte source)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue> =>
            NinoBlockCollections.ReadAll<CollectionFields.MapEntry<TKey, TValue>,
                CollectionFields.EntryBlock<CollectionFields.MapEntry<TKey, TValue>,
                    CollectionFields.MapPair<TKey, TValue, TKeyFormatter, TValueFormatter>>>(ref source,
                ref CollectionFields.MapEntryOf<TKey, TValue>(ref run.First), run.Count,
                NinoKind.Stride<TKey, TKeyFormatter>() + NinoKind.Stride<TValue, TValueFormatter>());

        /// <summary>
        /// The pairs of a dictionary's run one by one into their entries, each key and value through its formatter
        /// with nothing else in the loop. A key added one at a time (HashTables.Insert, a call per pair that loaded
        /// the table's fields again each time) read a <c>Dictionary&lt;int, IFoo&gt;</c> of 100 pairs about 10 %
        /// slower than LuminPack, which fills the entries this way (#320). The reader by value and returned: its
        /// address reaching no call, its fields stay in registers in the loop.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadPairsEach<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, Reader reader)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            ref byte value = ref run.First;
            int stride = run.Stride;
            nint key = MapKey<TKey, TValue>();
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
            {
                default(TKeyFormatter).Deserialize(ref reader,
                    ref Unsafe.As<byte, TKey>(ref Unsafe.AddByteOffset(ref value, key)));
                default(TValueFormatter).Deserialize(ref reader, ref Unsafe.As<byte, TValue>(ref value));
            }

            return reader;
        }
    }
}
