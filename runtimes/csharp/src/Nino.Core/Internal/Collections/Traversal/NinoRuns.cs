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

            if ((keyKind == NinoElementKind.Raw && NinoKind<TValueFormatter>.IsString) ||
                (NinoKind<TKeyFormatter>.IsString && valueKind == NinoElementKind.Raw))
            {
                // Raw keys and strings, or strings and raw values: written into the free part of the buffer by value;
                // only when they do not fit does the buffer grow, through the general loop.
                int written = WriteStringPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(run, count,
                    ref writer.FreeSpace(out int available), available);
                if (written >= 0)
                {
                    writer.Commit(written);
                    return;
                }
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
            nint key = CollectionFields.MapLayout<TKey, TValue>.Key - CollectionFields.MapLayout<TKey, TValue>.Value;
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
        /// The bytes of <see cref="WritePairsEach"/> for raw keys and strings, or strings and raw values, written into
        /// <paramref name="dst"/> (<paramref name="available"/> bytes). Returns the number of bytes written, or -1 when
        /// they do not fit.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int WriteStringPairs<TKey, TValue, TKeyFormatter, TValueFormatter>(NinoRun run, int count,
            ref byte dst, int available)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            if (available < NinoWire.LengthSize)
                return -1;
            NinoUnsafe.Write(ref dst, count);
            nuint pos = NinoWire.LengthSize;
            nuint end = (nuint)(uint)available;
            ref byte value = ref run.First;
            int stride = run.Stride, inUse = run.InUse;
            nint key = CollectionFields.MapLayout<TKey, TValue>.Key - CollectionFields.MapLayout<TKey, TValue>.Value;
            for (int left = run.Count; left > 0; left--, value = ref NinoRun.Next(ref value, stride))
            {
                if (NinoRun.Skips(ref value, inUse))
                    continue;
                if (!TryWriteStringPair<TKey, TValue, TKeyFormatter>(ref dst, ref pos, end,
                        Unsafe.As<byte, TKey>(ref Unsafe.AddByteOffset(ref value, key)), Unsafe.As<byte, TValue>(ref value)))
                    return -1;
            }

            return (int)pos;
        }

        /// <summary>
        /// A pair of <see cref="WriteStringPairs"/> at <paramref name="pos"/>, when the longest form of its string fits:
        /// a string key and its raw value, or a raw key and its string.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryWriteStringPair<TKey, TValue, TKeyFormatter>(ref byte dst, ref nuint pos, nuint end,
            TKey key, TValue item)
            where TKeyFormatter : struct, INinoFormatter<TKey>
        {
            if (NinoKind<TKeyFormatter>.IsString)
            {
                string s = Unsafe.As<TKey, string>(ref key);
                nuint size = (nuint)(uint)(NinoStrings.MaxSize(s) + Unsafe.SizeOf<TValue>());
                if (size > end - pos)
                    return false;
                ref byte entry = ref Unsafe.Add(ref dst, (nint)pos);
                int length = NinoStrings.Write(ref entry, s);
                NinoUnsafe.Write(ref entry, length, item);
                NinoLayout<TValue>.ClearPadding(ref Unsafe.Add(ref entry, length));
                pos += (nuint)(uint)(length + Unsafe.SizeOf<TValue>());
                return true;
            }

            {
                string s = Unsafe.As<TValue, string>(ref item);
                int keySize = Unsafe.SizeOf<TKey>();
                // Room for the longest form of the string; only what it takes is kept.
                nuint size = (nuint)(uint)(keySize + NinoStrings.MaxSize(s));
                if (size > end - pos)
                    return false;
                ref byte entry = ref Unsafe.Add(ref dst, (nint)pos);
                NinoUnsafe.Write(ref entry, 0, key);
                NinoLayout<TKey>.ClearPadding(ref entry);
                pos += (nuint)(uint)(keySize + NinoStrings.Write(ref Unsafe.Add(ref entry, keySize), s));
                return true;
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
            if (NinoKind<TFormatter>.IsString)
            {
                // One loop with the position in a register (see Reader.ReadStrings).
                reader.ReadStrings(ref Unsafe.As<T, string>(ref first), count);
                return;
            }

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
            if (count != 0)
                reader = ReadEach<T, TFormatter>(ref first, count, reader);
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
            nint key = CollectionFields.MapLayout<TKey, TValue>.Key - CollectionFields.MapLayout<TKey, TValue>.Value;
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
