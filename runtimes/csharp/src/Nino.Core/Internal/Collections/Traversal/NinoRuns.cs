using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The one write and the one read of a run of a collection's storage (<see cref="NinoRun"/>), over
    /// <c>&lt;T, TFormatter&gt;</c> only, for a run of any stride (#355): <see cref="Write{T,TFormatter}(NinoRun, bool,
    /// bool, int, ref Writer)"/> and <see cref="Read{T,TFormatter}(Span{T}, ref Reader, bool)"/>. Every collection
    /// writes its elements through them and reads them into its storage through the same view: values in a row (an
    /// array, a list, a span, each half of a ring, a gathered chunk), the entries of a set and of a dictionary, each
    /// the element (a storage element whose formatter composes its items at their offsets,
    /// <see cref="NinoMapEntryFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>, filled then linked by
    /// <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>), and the values of a set's entries at the entries' stride:
    /// never a loop shape of its own. Each element kind is moved by its kernel, and what no kernel moves by the one
    /// loop of values one by one per direction (<see cref="WriteLoop{T,TFormatter}"/>,
    /// <see cref="ReadLoop{T,TFormatter}"/>), inlined into each entry point with its constants, so a run in a row steps
    /// by a constant (a row's write out of line by value spells the loop out, <see cref="WriteEach{T,TFormatter}(ref
    /// byte, int, int, Writer)"/>):
    /// <list type="bullet">
    /// <item>raw elements and blocks (a storage element of raw items or blocks among them): one block, copied, or
    /// moved by the block kernels, which skip the bytes of an element that are not on the wire;</item>
    /// <item>slots: one block, through the slot kernels;</item>
    /// <item>a polymorphic base's values: its generated loop over the run (<see cref="INinoFormatter.WriteRun"/>),
    /// the dispatch inline and the writer in a local; a pair of them, its value formatter's loop over the run of its
    /// values with their keys (<see cref="NinoItems.WriteRun{TA,TB,TFormatterA,TFormatterB}"/>);</item>
    /// <item>strings and pairs holding them: the string run kernels (<see cref="NinoStringRuns"/>), one call not
    /// generic over the element;</item>
    /// <item>others: value by value through the formatter, a constrained call.</item>
    /// </list>
    /// Entries a set or a dictionary freed are skipped (<see cref="NinoRun.Skips"/>); the kinds moved as one block take
    /// a run of values in a row without free entries, else they go value by value. Reads fill a run whose every entry is in use, and read
    /// a polymorphic base's values through its formatter's <c>Deserialize</c> (its subtype switch inlined there).
    /// </summary>
    internal static class NinoRuns
    {
        /// <summary>
        /// The <c>length</c> of <see cref="Write{T,TFormatter}(NinoRun, int, ref Writer)"/> where the caller writes
        /// the collection's length itself (a ring in two runs, the chunks of a gather).
        /// </summary>
        public const int NoLength = -1;

        /// <summary>Writes the elements of <paramref name="values"/> (the count is the caller's).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ReadOnlySpan<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(NinoRun.Of(values), true, false, NoLength, ref writer);

        /// <summary>Writes <paramref name="length"/> (not <see cref="NoLength"/>), then the elements of
        /// <paramref name="values"/>: an array's, a span's.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ReadOnlySpan<T> values, int length, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(NinoRun.Of(values), true, true, length, ref writer);

        /// <summary>
        /// Writes the length of a run in two segments, then the <paramref name="firstCount"/> elements in a row from
        /// <paramref name="first"/> and the <paramref name="secondCount"/> from <paramref name="second"/>: a ring that wraps (a <c>Queue</c>'s, head to the end of its array,
        /// then from its start), the two-segment form of the one write. Elements of a fixed size take one reservation
        /// and one capacity check for both, each segment its own block (each its own alignment and, on AMD, its own
        /// 4K-aliasing test: the second starts at the array's start); the others write each segment as a run in a row.
        /// A form of the write rather than a second segment in <see cref="NinoRun"/>, whose fields the JIT does not
        /// fold: two more would be read by every loop over a run.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ref T first, int firstCount, ref T second, int secondCount,
            ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = firstCount + secondCount;
            int block = NinoKind.Stride<T, TFormatter>();
            if (block > 0)
            {
                ref byte target = ref writer.ReserveCounted(count, block);
                if (firstCount != 0)
                    NinoElements.WriteBlock<T, TFormatter>(ref target, ref first, firstCount);
                if (secondCount != 0)
                    NinoElements.WriteBlock<T, TFormatter>(ref Unsafe.Add(ref target, firstCount * block), ref second,
                        secondCount);
                return;
            }

            writer.WriteLength(count);
            Write<T, TFormatter>(ref first, firstCount, ref writer);
            Write<T, TFormatter>(ref second, secondCount, ref writer);
        }

        /// <summary>
        /// Writes <paramref name="count"/> elements in a row starting at <paramref name="first"/> (a multi-dimensional
        /// array's; the count is the caller's).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ref T first, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(MemoryMarshal.CreateReadOnlySpan(ref first, count), ref writer);

        /// <summary>
        /// Writes <paramref name="length"/> (not <see cref="NoLength"/>: the elements in use), then the elements of
        /// <paramref name="run"/>, a run of any stride whose free entries are skipped: a set's or a dictionary's
        /// entries, each the element (a storage element, <see cref="NinoSetEntryFormatter{T,TFormatter}"/>: its items
        /// at their offsets, the rest of its stride skipped), or the values of a set's entries
        /// at the entries' stride (values with references). Never empty: the caller writes an empty collection itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(NinoRun run, int length, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            Write<T, TFormatter>(run, false, true, length, ref writer);

        /// <summary>
        /// The one write of a run (#355), every entry point's: writes <paramref name="length"/> first where
        /// <paramref name="counted"/> (a constant: a collection's length), then the entries of <paramref name="run"/>,
        /// those its in-use test says are free (<see cref="NinoRun.InUse"/>) skipped; <paramref name="row"/> (a
        /// constant) where they are values in a row, every one in use, so that nothing reads the run's stride or
        /// in-use test: the JIT does not fold a run's five fields, and the code of a run in a row is that of a span.
        /// By kind: elements of a fixed size in a row without free entries (an entry's stride is its size) as one
        /// block, one capacity check with the length; a polymorphic base's values through its generated loop; strings
        /// and pairs holding them through the string run kernels; others one by one
        /// (<see cref="WriteLoop{T,TFormatter}"/>). Statements, never a conditional expression (?:), here and in every
        /// path inlined into a top-level write: one choosing between two calls that return the writer, or between two
        /// arguments of one, kept the writer of a top-level write of an array of blocks in memory, though that write
        /// never takes the path (asm.py: KeyValuePairs 5 to 38 stack accesses).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Write<T, TFormatter>(NinoRun run, bool row, bool counted, int length, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = run.Count;
            // Elements of a fixed size (NinoKind.Stride): one capacity check for the block, none per element. Raw
            // elements too, as the unmanaged formatters write them: an interface, Stack or Queue of them, or any
            // collection whose formatter is not specialized for them, would otherwise check the capacity per element.
            // A set's entries: the block kernels pack the items out of each.
            // A run of any stride whose values have no references is a set's or a dictionary's entries, each at its
            // own size (values with references have no fixed size): the block kernels take it whole where no entry is
            // free. Its callers write an empty collection themselves, so it never tests for none (a test of the run's
            // count, and the stride compared with the entry's size, cost the sets' writes 160 bytes and 11 stack
            // accesses, asm.py: HashSet<int>).
            int block = NinoKind.Stride<T, TFormatter>();
            if (block > 0 && (row || run.InUse == NinoRun.AllInUse))
            {
                if (counted)
                {
                    ref byte elements = ref writer.ReserveCounted(length, block);
                    if (!row || count != 0)
                        NinoElements.WriteBlock<T, TFormatter>(ref elements, ref Unsafe.As<byte, T>(ref run.First),
                            count);
                }
                else if (count != 0)
                    NinoElements.WriteBlock<T, TFormatter>(ref writer.ReserveElements(count, block),
                        ref Unsafe.As<byte, T>(ref run.First), count);
                return;
            }

            // A polymorphic base: its generated loop over the run, with the dispatch inline, a constrained call on the
            // formatter on every target; a pair of them, its value's loop (KeyValuePairFormatter). The intrinsic
            // itself, not NinoUnsafe's: one inlinee more in this method, inlined into every collection's write, left
            // the writer of block arrays' top-level writes in memory (asm.py: ByteIntPairs32 10 to 38 stack accesses).
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>() && NinoKind<TFormatter>.Runs)
            {
                if (counted)
                    writer.WriteLength(length);
                if (!row || count != 0)
                    default(TFormatter).WriteRun(run, ref writer);
                return;
            }

            // Strings and pairs holding them: the string run kernels, one call not generic over the element (a pair's
            // through its formatter, once per run), free entries skipped.
            if (NinoCursor.Own<T, TFormatter>() && !ShortFlatValues<T, TFormatter>(count))
            {
                if (counted)
                    writer.WriteLength(length);
                if (!row || count != 0)
                {
                    // Strings: the free space to one call and the bytes written back, in registers. Through a copy of
                    // the writer each step of the cursor went through memory (the caller's stores loaded back in the
                    // call, the call's loaded back here: a store-forwarding round trip each, as a dictionary's pairs
                    // had, NinoDictionary.WriteFree). The strings that did not fit through the copy (below), from the
                    // first of them.
                    int next = 0;
                    if (!typeof(T).IsValueType)
                    {
                        ulong left = WriteStringsFree<TFormatter>(run, ref writer.FreeSpace(out int available),
                            available);
                        writer.Commit(available - (int)left);
                        if (left >> 32 == 0)
                            return;
                        next = run.Count - (int)(left >> 32);
                    }

                    // Out of line on a copy of the cursor alone, field by field: strings and the items beside them
                    // open no class body, defer nothing and track no reference. A writer passed and returned by value
                    // is copied with loads wider than the stores that built it (store forwarding fails), and the
                    // whole writer copied each way cost the caller twice the stack accesses.
                    Writer copy = default;
                    writer.CopyCursorTo(ref copy);
                    WriteStrings<T, TFormatter>(run, next, ref copy);
                    copy.CopyCursorTo(ref writer);
                }

                return;
            }

            // A class whose generated body is flat (#355): the flat kernels, at every length (their entry is as cheap as
            // the loop below at one element); runs with free entries (a set's) take that loop. After the kinds above, before the local writes, which
            // sealed flat classes take too (NinoKind.FlatClass), where the kernels went unused and ScatteredTest's write of a
            // million items took 1.33 times the base's: a static readonly of NinoKind<TFormatter> read in code shared over
            // classes is no constant, and read first it turned a string array's and a polymorphic base's writes from 4 to 29
            // and 86 to 234 stack accesses (asm.py).
            if (!typeof(T).IsValueType && NinoKind<TFormatter>.Flat && count != 0 &&
                (row || run.InUse == NinoRun.AllInUse))
            {
                if (counted)
                    writer.WriteLength(length);
                default(TFormatter).WriteFlat(run, ref writer);
                return;
            }

            // Values without references through a local copy of the writer, out of line, the length too; others by
            // reference, as their formatters' calls take it anyway (NinoElements.WritesOnLocal). Empty collections (the
            // leaves of a tree) skip the call and the copies of the writer in and out.
            if (NinoElements.WritesOnLocal<T, TFormatter>())
            {
                if (row && count == 0)
                {
                    if (counted)
                        writer.WriteLength(length);
                }
                else if (row)
                    writer = WriteEach<T, TFormatter>(ref run.First, count, length, writer);
                else
                    writer = WriteEach<T, TFormatter>(run, length, writer);
                return;
            }

            // The loop stays in this method: a helper of its own was not inlined where the element's formatter
            // writes a collection of its own type (a tree), which added a call per level (SelfTree serialize 0.86x on
            // EPYC 9V74, runner A/B of the rc.10 review head against main 51a7bd18).
            if (counted)
                writer.WriteLength(length);
            if (row)
                WriteLoop<T, TFormatter>(ref run.First, count, Unsafe.SizeOf<T>(), NinoRun.NoKey, NinoRun.AllInUse,
                    ref writer);
            else
                WriteLoop<T, TFormatter>(ref run.First, count, run.Stride, NinoRun.NoKey, run.InUse, ref writer);
        }

        /// <summary>
        /// Writes <paramref name="length"/> (unless <see cref="NoLength"/>) and the <paramref name="count"/> values
        /// (not 0) in a row at <paramref name="first"/> one by one, the writer by value and returned: a
        /// local here, its cursor in registers (HashSet&lt;int?&gt; of 1,000: 5 to 10% faster), where one passed by
        /// reference would be kept in memory, the caller's too; and a second loop in the caller made the raw one's
        /// code slower (HashSet&lt;int&gt; of 1,000 serialized 40% slower on x64 and Arm64). Left to the JIT, as it
        /// always was: inlined where it pays (a top-level write of an array of sealed classes of raw members inlines
        /// the loop, its writer in registers; out of line it kept that writer in memory, asm.py: FixedClasses 8 to 30
        /// stack accesses). Its loop is <see cref="WriteLoop{T,TFormatter}"/> with a row's constants written out, the
        /// one place it is: a value in a row is never free, has no key and is no slot (slots go as a block), and the
        /// level of inlining WriteLoop adds made a top-level write of sealed classes holding a raw array run out of
        /// inlining budget, its writer in memory (asm.py: AppData Events 19 to 86 stack accesses).
        /// </summary>
        private static Writer WriteEach<T, TFormatter>(scoped ref byte first, int count, int length, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (length != NoLength)
                writer.WriteLength(length);
            for (ref T item = ref Unsafe.As<byte, T>(ref first); count > 0; count--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Serialize(item, ref writer);
            return writer;
        }

        /// <summary>
        /// As <see cref="WriteEach{T,TFormatter}(ref byte, int, int, Writer)"/>, the entries of <paramref name="run"/>
        /// (any stride, free entries skipped): a set's or a dictionary's; <paramref name="length"/> written first.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer WriteEach<T, TFormatter>(scoped in NinoRun run, int length, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (length != NoLength)
                writer.WriteLength(length);
            WriteLoop<T, TFormatter>(ref run.First, run.Count, run.Stride, NinoRun.NoKey, run.InUse, ref writer);
            return writer;
        }

        /// <summary>
        /// The one loop that writes a run's values one by one (the other kinds are kernels: blocks, slots, strings, a
        /// polymorphic base's generated loop): <paramref name="count"/> entries <paramref name="stride"/> bytes apart
        /// from <paramref name="value"/>, those <paramref name="inUse"/> says are free skipped, each value after its
        /// key where <paramref name="key"/> is not <see cref="NinoRun.NoKey"/> (<see cref="NinoRun.WriteEach{T,
        /// TFormatter}"/>, the deep path of a pair's generated loop), a slot as a run of slots writes it. Inlined into
        /// each entry point, whose constants fold its tests: a run in a row steps by the value's size.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WriteLoop<T, TFormatter>(scoped ref byte value, int count, int stride, int key, int inUse,
            ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            // By reference, not by index: a span's length is not known to the JIT to be positive (a list's count, for
            // one), and over a loop with a call in it each element kept a bounds check of its own.
            // Each piece tested here, against the constants the callers pass, before it is called: a call whose
            // test folds inside it is still imported, and takes from the caller's inlining budget (an array of
            // sealed classes holding a raw array, inlined into its top-level write, ran out of budget and called its
            // writer's properties: asm.py, AppData Events 19 to 109 stack accesses).
            for (int left = count; left > 0; left--, value = ref Unsafe.Add(ref value, stride))
            {
                if (inUse != NinoRun.AllInUse && NinoRun.Skips(ref value, inUse))
                    continue;
                if (key != NinoRun.NoKey)
                    NinoRun.WriteKey(ref value, key, ref writer);
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    default(TFormatter).Serialize(Unsafe.As<byte, T>(ref value), ref writer);
                else
                    NinoElements.WriteOne<T, TFormatter>(in Unsafe.As<byte, T>(ref value), ref writer);
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
            : RuntimeHelpers.IsReferenceOrContainsReferences<TKey>() ? NinoRun.NoKey
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
            Read<T, TFormatter>(new NinoRun(ref Unsafe.As<T, byte>(ref first), count, Unsafe.SizeOf<T>(),
                NinoRun.NoKey, NinoRun.AllInUse), ref reader, prechecked);

        /// <summary>
        /// The one read of values in a row (#355), every entry point's: an array's, a list's backing array, a ring's
        /// from its start, a chunk, reusing the existing values; the caller publishes what it filled (a list's count,
        /// a ring's head and tail). By kind, as <see cref="Write{T,TFormatter}(NinoRun, bool, bool, int, ref Writer)"/>:
        /// raw values, slots and blocks as one block, strings and pairs holding them through the string run kernels,
        /// others one by one (<see cref="ReadLoop{T,TFormatter}"/>, which a set's and a dictionary's entries, a run of
        /// any stride, take from <see cref="NinoEntries{TEntry,TFormatter,TTable}"/>).
        /// </summary>
        /// <param name="prechecked">The collection length already proved its entire fixed-stride payload fits.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Read<T, TFormatter>(scoped NinoRun run, ref Reader reader, bool prechecked)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count = run.Count;
            ref T first = ref Unsafe.As<byte, T>(ref run.First);
            // Raw elements: one bounds check and one block copy (see Write). Values Nino checks (bools, Untrusted
            // mode's fields: NinoLayout<T>.CopiesAsIs false) are checked in the data first, out of line, then copied
            // the same way: the copy never stores a value the check rejects, so every collection reading raw values
            // block-checks them (an ArraySegment, a Queue or an ImmutableArray of bools read one by one before).
            var kind = NinoKind.Of<T, TFormatter>();
            if (kind == NinoElementKind.Raw)
            {
                ref byte destination = ref Unsafe.As<T, byte>(ref first);
                ref byte source = ref TakeElements(ref reader, count, Unsafe.SizeOf<T>(), prechecked);
                uint bytes = (uint)count * (uint)Unsafe.SizeOf<T>();
                if (!NinoLayout<T>.CopiesAsIs && count != 0)
                    CheckRaw<T>(ref source, count);
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

            // Strings and pairs holding them: the string run kernels, with the data and the bytes left, not the reader,
            // which stays in the caller's registers. Raw values and blocks that reach here are read one by one with
            // their checks, as before.
            if (NinoCursor.Own<T, TFormatter>() && !ShortFlatValues<T, TFormatter>(count))
            {
                ReadStrings<T, TFormatter>(run, ref reader);
                return;
            }

            // Classes apart (a constant): the flat kernels' branch in the loop of values with no class (a union's) kept
            // the JIT from inlining their body there (PolymorphicTest's TaggedUnionArray read in 1.06 times the base's
            // time, UnionArray 1.11).
            if (typeof(T).IsValueType)
                reader = ReadEach<T, TFormatter>(ref first, count, reader);
            else
                reader = ReadClasses<T, TFormatter>(ref first, count, reader);
        }

        /// <summary>
        /// Whether a run of <paramref name="count"/> values is a flat struct's (#355) too short for the flat kernels
        /// (under <see cref="NinoFlatRuns.MinRun"/>): it takes the loops of values one at a time.
        /// Through the kernels, out of line twice, a run of one flat struct wrote in 2.1 times main's time and one of a
        /// version-tolerant struct 2.8 (their fixed cost; Tier1, local, alternated). The count first: the static
        /// after it only for short runs.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShortFlatValues<T, TFormatter>(int count) where TFormatter : struct, INinoFormatter<T> =>
            typeof(T).IsValueType && count < NinoFlatRuns.MinRun && NinoKind<TFormatter>.Flat;

        /// <summary>
        /// Writes the strings, or the pairs holding them, of <paramref name="run"/> (not empty; the count is the
        /// caller's) through the string run kernels (<see cref="NinoStringRuns"/>): a run of strings by the kernel of
        /// its encoding, a run of pairs through its formatter (<see cref="INinoFormatter.WriteStrings"/>, once per run).
        /// Out of line, one call in the loops inlined into every collection: the kernels' dispatch inline there spent
        /// their callers' inlining budgets (asm.py: KeyValuePairs, NullableGridInts and BlockingCollection
        /// serialized with their writers in memory).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void WriteStrings<T, TFormatter>(scoped in NinoRun run, int next, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Strings: those WriteStringsFree left, from the element at next, the first that did not fit.
            if (!typeof(T).IsValueType)
                NinoStringRuns.WriteFrom<TFormatter>(run, next, ref writer);
            // A generated struct whose body is flat (#355): its chain of items, in place (from NinoFlatRuns.MinRun
            // elements on: ShortFlatValues).
            else if (NinoKind<TFormatter>.Flat)
                default(TFormatter).WriteFlat(NinoRun.Unscoped(run), ref writer);
            else
                default(TFormatter).WriteStrings(run, ref writer);
        }

        /// <summary>
        /// Writes the strings of <paramref name="run"/> (not empty) into the <paramref name="available"/> bytes at
        /// <paramref name="destination"/> until one's longest form does not fit (<see cref="NinoStringRuns.WriteFree{TFormatter}"/>):
        /// the bytes left (low half) and the strings left (high half, 0 when every one fit). Out of line, the free
        /// space and the result in registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteStringsFree<TFormatter>(scoped in NinoRun run, ref byte destination, int available) =>
            NinoStringRuns.WriteFree<TFormatter>(run, ref destination, available);

        /// <summary>
        /// As <see cref="WriteStrings{T,TFormatter}"/>, the read in place. The data and the bytes left go to the call,
        /// not the reader, which stays in the caller's registers.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadStrings<T, TFormatter>(scoped NinoRun run, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            // A generated struct whose body is flat (#355): its chain of items, in place, the reader by value and
            // returned (ReadEach's way).
            if (typeof(T).IsValueType && NinoKind<TFormatter>.Flat)
                reader = ReadFlatValues<T, TFormatter>(NinoRun.Unscoped(run), reader);
            else
                reader.UnsafeAdvance(ReadStrings<T, TFormatter>(run, ref reader.Peek(), reader.Remaining));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadFlatValues<T, TFormatter>(NinoRun run, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            default(TFormatter).ReadFlat(run, ref reader);
            return reader;
        }

        /// <summary>The read of <see cref="ReadStrings{T,TFormatter}(NinoRun, ref Reader)"/> from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes); returns the bytes read.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadStrings<T, TFormatter>(scoped NinoRun run, ref byte data, int end)
            where TFormatter : struct, INinoFormatter<T> =>
            typeof(T).IsValueType
                ? default(TFormatter).ReadStrings(run, ref data, end)
                : NinoStringRuns.Read<TFormatter>(run, ref data, end);

        /// <summary>
        /// Checks <paramref name="count"/> raw values (not 0) in the data at <paramref name="source"/> before they are
        /// copied (<see cref="NinoChecks.Values{T}"/>: bools, Untrusted mode's fields). Out of line: rare, and the
        /// checks are large.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CheckRaw<T>(ref byte source, int count) =>
            NinoChecks.Values<T>(ref source, count, Unsafe.SizeOf<T>());

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
        /// The <paramref name="count"/> values (not 0) in a row from <paramref name="item"/> read one by one, out of
        /// line: inlined into a read (also where they are blocks and it is never reached), the loop and the element's
        /// formatter used up what the JIT inlines into the method, and helpers as small as Reader.Skip were left calls.
        /// The reader by value and returned, not by reference: its address passed to a call would keep the caller's
        /// reader in memory. The one loop with the stride a constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadEach<T, TFormatter>(scoped ref T item, int count, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            ReadLoop<T, TFormatter>(ref Unsafe.As<T, byte>(ref item), count, Unsafe.SizeOf<T>(), ref reader);
            return reader;
        }

        /// <summary>
        /// <see cref="ReadEach{T,TFormatter}"/> for classes: a class whose generated body is flat (#355) through the flat
        /// kernels, at every length. Here, out of line, not in Read: the reader's
        /// address passed there kept it in memory in every collection's read of such a class, short runs included
        /// (asm.py: 8 to 21 stack accesses, ChatMessage, ScatteredItem).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadClasses<T, TFormatter>(scoped ref T item, int count, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoKind<TFormatter>.Flat)
            {
                default(TFormatter).ReadFlat(NinoRun.Unscoped(new NinoRun(ref Unsafe.As<T, byte>(ref item), count,
                    Unsafe.SizeOf<T>(), NinoRun.NoKey, NinoRun.AllInUse)), ref reader);
                return reader;
            }

            // A generic polymorphic base over a class (#441): the loop of its closed instance's dispatch, where nothing
            // is shared code, one call for the run instead of a lookup and calls per value here.
            if (NinoKind<TFormatter>.ReadRuns)
            {
                default(TFormatter).ReadRun(NinoRun.Unscoped(new NinoRun(ref Unsafe.As<T, byte>(ref item), count,
                    Unsafe.SizeOf<T>(), NinoRun.NoKey, NinoRun.AllInUse)), ref reader);
                return reader;
            }

            ReadLoop<T, TFormatter>(ref Unsafe.As<T, byte>(ref item), count, Unsafe.SizeOf<T>(), ref reader);
            return reader;
        }

        /// <summary>
        /// The one loop that reads a run's values one by one (the other kinds are kernels): <paramref name="count"/>
        /// entries <paramref name="stride"/> bytes apart from <paramref name="value"/>, each read in place as a run of
        /// its kind reads it (a slot as a slot). Inlined into each entry point, a run in a row stepping by a constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ReadLoop<T, TFormatter>(scoped ref byte value, int count, int stride, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            // The callers' reader a local, its cursor in registers, where through a reference every element would
            // store it and load it back (List<(int, long)>: 2x faster); by reference here, which inlined is that local
            // (by value, each caller copied its reader parameter in its prolog). By reference, see WriteLoop.
            for (int left = count; left > 0; left--, value = ref Unsafe.Add(ref value, stride))
            {
                if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                    default(TFormatter).Deserialize(ref reader, ref Unsafe.As<byte, T>(ref value));
                else
                    NinoElements.ReadOne<T, TFormatter>(ref reader, ref Unsafe.As<byte, T>(ref value));
            }
        }

        /// <summary>
        /// Whether a run of <typeparamref name="T"/>'s values is read as one block: raw values and blocks read as one
        /// (<see cref="NinoBlockCollections.ReadSize{T,TFormatter}"/>; not in Untrusted mode where their fields need
        /// checks). Their bytes are <see cref="NinoKind.Stride{T,TFormatter}"/>. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ScattersItems<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw ||
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Block && NinoBlockCollections.ReadSize<T, TFormatter>() > 0;
    }
}
