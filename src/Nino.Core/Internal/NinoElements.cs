using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>Shared element loops for array-like formatters.</summary>
    internal static class NinoElements
    {
        /// <summary>
        /// Whether elements read into a new array write every byte of it, padding included, and leave none of it as it
        /// was when they reject the data: raw elements copied as one block (NinoUnsafe.CopyToNew), and blocks
        /// (NinoBlockCollections.ReadAll, which writes padding as 0 and clears the values when a bool rejects the
        /// data); only then is it allocated uninitialized. Raw values not copied as they are (bools, Untrusted mode's
        /// checks) take a zeroed array: stored in its slot before the read, an uninitialized one left an existing
        /// object holding stale heap memory when the data was rejected. Nothing of this is in the formatters, which
        /// recurse through nested collections: a test and a call there grew their Debug frames past a 1 MB stack.
        /// Elements read value by value (DateTimeOffsets, values Untrusted mode checks a field at a time) keep a zeroed
        /// array too: the clearing brings its lines into the cache, where such stores into fresh lines first read each
        /// one (workstation GC, AVX-512 Xeon: 1,000 DateTimeOffsets in 3.7 to 3.8 µs zeroed against 5.0 to 5.6). A
        /// JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CopiedWhole<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() && Traits<T, TFormatter>.CopiedWhole;

        /// <summary>
        /// Whether a loop that writes elements one at a time goes through a local copy of the writer, whose cursor then
        /// stays in registers: only for elements without references, whose formatters are small and inlined. The
        /// formatter of a string or a class is a call that takes the local's address anyway, and recursing through
        /// one copies the writer in and out at every level (a tree of classes holding lists of their own type
        /// serialized 30 to 40% slower, on x64 and Arm64). A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool WritesOnLocal<T>() => !NinoUnsafe.IsReferenceOrContainsReferences<T>();

        /// <summary>
        /// Whether <see cref="Read{T,TFormatter}(Span{T}, ref Reader)"/> overwrites every element
        /// whatever it held (raw elements, DateTimeOffsets and blocks are each read in full), so the slots need not
        /// be cleared or zeroed first. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadOverwrites<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() && Traits<T, TFormatter>.ReadOverwrites;

        /// <summary>
        /// The fewest bytes an element takes on the wire, to check a length against the bytes left before allocating:
        /// <see cref="BlockStride{T,TFormatter}"/> for elements read as one block, else 1. A new array read as one
        /// block is allocated uninitialized (<see cref="CopiedWhole{T,TFormatter}"/>): checked against 1 byte an
        /// element, data claiming more elements than its bytes hold left an array of stale heap memory in the caller's
        /// object when the copy then failed (Deserialize with an existing value). A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MinWireSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            // Elements with references are never one block: 1 without the static field (a shared generic's runtime
            // lookup).
            NinoUnsafe.IsReferenceOrContainsReferences<T>() ? 1 : Traits<T, TFormatter>.MinWireSize;

        /// <summary>
        /// The most memory a collection read allocates ahead of its elements, per byte of data left. A count is
        /// checked against the bytes left with the fewest bytes an element takes (<see cref="MinWireSize"/>), but an
        /// element can take far more memory than that: a list of 520-byte structs of a byte each on the wire, sized
        /// for the count, allocated 520 times the data. Where the slots would take more, a collection is grown as its
        /// elements are read (<see cref="ReadGrowing{T,TFormatter}"/>) or its capacity clamped
        /// (<see cref="Capacity"/>): what it allocates beyond this bound is then at most 4 times the memory of the
        /// elements actually read.
        /// </summary>
        public const int Amplification = 16;

        /// <summary>
        /// What a collection may allocate ahead of its elements however few bytes are left, so that a small
        /// collection of large elements is sized for its count as before.
        /// </summary>
        public const int PresizeFloor = 16 << 10;

        /// <summary>
        /// Bytes a hash set or dictionary takes per element beyond the element (or pair): its entry's hash code and
        /// next index, and its bucket.
        /// </summary>
        public const int HashSlot = 3 * sizeof(int);

        /// <summary>
        /// Whether slots of <paramref name="slotSize"/> bytes, each read from at least <paramref name="minWire"/>
        /// bytes, can take more than <see cref="Amplification"/> bytes of memory per byte of data: then a collection of
        /// them checks its count against the bound before it sizes itself for it (<see cref="Capacity"/>,
        /// <see cref="ReadGrowing{T,TFormatter}"/>). Otherwise the count check bounds them already (references, raw
        /// elements, small structs). A JIT-time constant where the sizes are: the check in the formatters is this
        /// test and an out-of-line call, so that types for which it is false compile and inline as before.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Heavy(int slotSize, int minWire) => slotSize > Amplification * minWire;

        /// <summary>
        /// <see cref="Heavy(int, int)"/> for the elements of an array or a list (<see cref="MinWireSize"/>), as a
        /// static field: the formatters test <c>Unsafe.SizeOf&lt;T&gt;() &gt; Amplification &amp;&amp;
        /// HeavyElements&lt;T, TFormatter&gt;.Value</c>, an intrinsic and a field the JIT folds while it imports the
        /// method (no load of the field in shared code for references, whose slots are 8 bytes), so the branch is not
        /// compiled where it is false. A method returning the same (an inlined call) left it compiled: the array slot
        /// it passes by reference was kept in memory, and what else the read inlines changed.
        /// </summary>
        internal static class HeavyElements<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
        {
            public static readonly bool Value = Heavy(Unsafe.SizeOf<T>(), MinWireSize<T, TFormatter>());
        }

        /// <summary>
        /// The fewest bytes an element of a hash set takes, which its count is checked with: raw elements their
        /// size, others (strings included: null is one byte) 1. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MinHashedSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            RawFormatter.Is<T, TFormatter>() ? Unsafe.SizeOf<T>() : 1;

        /// <summary>
        /// <see cref="Heavy(int, int)"/> for a hash set's entries (<see cref="HashSlot"/> bytes beyond the element,
        /// <see cref="MinHashedSize{T,TFormatter}"/>), as <see cref="HeavyElements{T,TFormatter}"/>.
        /// </summary>
        internal static class HeavyHashed<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
        {
            public static readonly bool Value = Heavy(Unsafe.SizeOf<T>() + HashSlot, MinHashedSize<T, TFormatter>());
        }

        /// <summary>
        /// Whether <paramref name="length"/> slots of <paramref name="slotSize"/> bytes fit
        /// <see cref="Amplification"/> bytes per byte left (or <see cref="PresizeFloor"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Fits(int length, int slotSize, int remaining) =>
            (ulong)(uint)length * (uint)slotSize <= Budget(remaining);

        /// <summary>
        /// The capacity to give a collection of <see cref="Heavy(int, int)"/> slots that grows as it is filled, for
        /// <paramref name="length"/> elements: the length where they <see cref="Fits"/>, else what fits the bound.
        /// Out of line: called only for such slots.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Capacity(int length, int slotSize, int remaining) =>
            Fits(length, slotSize, remaining) ? length : Clamped(slotSize, remaining);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Budget(int remaining) =>
            Math.Max((ulong)(uint)remaining * Amplification, PresizeFloor);

        /// <summary>
        /// The slots of <paramref name="slotSize"/> bytes that fit the bound (at least one); fewer than a length that
        /// does not <see cref="Fits"/>.
        /// </summary>
        internal static int Clamped(int slotSize, int remaining) =>
            (int)Math.Max(Budget(remaining) / (uint)Math.Max(slotSize, 1), 1);

        /// <summary>
        /// Reads <paramref name="length"/> elements into <paramref name="array"/>, as
        /// <see cref="ArrayFormatter{T,TFormatter}"/> does, for <see cref="HeavyElements{T,TFormatter}"/> elements: an
        /// existing array of that length is read in place, a new one of that length where it <see cref="Fits"/>;
        /// otherwise the array starts at what fits the bound (or the existing one's length) and doubles as its
        /// elements are read (<see cref="EnsureLength{T}"/> moves them over), to exactly <paramref name="length"/>.
        /// Data claiming more elements than it holds then fails after allocating at most 4 times the memory of the
        /// elements it held. The reader by value and returned, as ReadEach's.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Reader ReadGrowing<T, TFormatter>(scoped ref T[] array, int length, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            var current = array;
            int count = current != null && current.Length == length ||
                        Fits(length, Unsafe.SizeOf<T>(), reader.Remaining)
                ? length
                : Math.Min(length, Math.Max(Clamped(Unsafe.SizeOf<T>(), reader.Remaining),
                    current == null ? 0 : current.Length));
            int done = 0;
            while (true)
            {
                var items = EnsureLength(ref array, count);
                Read<T, TFormatter>(new Span<T>(items, done, count - done), ref reader);
                if (count == length)
                    return reader;
                done = count;
                count = (int)Math.Min(length, 2L * count);
            }
        }

        // The checks above as fields: one load each, where every check they combine was an inlined call, and the
        // JIT counts those against what else it inlines into the method (an ImmutableDictionary<int, int> read ran
        // out, and kept its reader in memory).
        private static class Traits<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
        {
            // Block reads write every byte of a value, padding as 0 (NinoBlockCollections.ReadEach); values read a
            // field at a time (ReadSize -1, Untrusted mode) leave their padding as it was.
            public static readonly bool CopiedWhole = RawFormatter.Is<T, TFormatter>()
                ? NinoLayout<T>.CopiesAsIs
                : NinoBlockCollections.ReadSize<T, TFormatter>() > 0;

            public static readonly bool ReadOverwrites = RawFormatter.Is<T, TFormatter>() ||
                typeof(TFormatter) == typeof(DateTimeOffsetFormatter) || NinoBlockCollections.FixedSize<T, TFormatter>() > 0;

            public static readonly int MinWireSize = Math.Max(BlockStride<T, TFormatter>(), 1);
        }

        /// <summary>
        /// Writes <paramref name="count"/> elements starting at <paramref name="first"/> (a multi-dimensional array's),
        /// as <see cref="Write{T,TFormatter}(ReadOnlySpan{T}, ref Writer)"/> writes a span of them.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ref T first, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
#if NETSTANDARD2_0
            // No span over a reference: raw elements as one block, the others one by one.
            if (RawFormatter.Is<T, TFormatter>())
            {
                NinoLayout<T>.CopyToWire(ref writer.ReserveElements(count, Unsafe.SizeOf<T>()),
                    ref Unsafe.As<T, byte>(ref first), count);
                return;
            }

            for (int i = 0; i < count; i++)
                default(TFormatter).Serialize(Unsafe.Add(ref first, i), ref writer);
#else
            Write<T, TFormatter>(MemoryMarshal.CreateReadOnlySpan(ref first, count), ref writer);
#endif
        }

        /// <summary>
        /// Reads <paramref name="count"/> elements into the slots starting at <paramref name="first"/> (a
        /// multi-dimensional array's), as <see cref="Read{T,TFormatter}(Span{T}, ref Reader)"/> reads a span of them.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(ref T first, int count, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
#if NETSTANDARD2_0
            // Bools one by one (checked, NinoLayout<T>.CopiesAsIs).
            if (RawFormatter.Is<T, TFormatter>() && NinoLayout<T>.CopiesAsIs)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref first),
                    ref reader.ReadElements(count, Unsafe.SizeOf<T>()), (uint)count * (uint)Unsafe.SizeOf<T>());
                return;
            }

            for (int i = 0; i < count; i++)
                default(TFormatter).Deserialize(ref Unsafe.Add(ref first, i), ref reader);
#else
            Read<T, TFormatter>(MemoryMarshal.CreateSpan(ref first, count), ref reader);
#endif
        }

        /// <summary>Writes the elements of a span.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ReadOnlySpan<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            // Elements of a fixed size (BlockStride): one capacity check for the block, none per element. Raw elements
            // too, as the unmanaged formatters write them: an interface, Stack or Queue of them, or any collection
            // whose formatter is not specialized for them, would otherwise check the capacity per element.
            int block = BlockStride<T, TFormatter>();
            if (block > 0)
            {
                if (!values.IsEmpty)
                    WriteBlock<T, TFormatter>(ref writer.ReserveElements(values.Length, block), values);
                return;
            }

            if (WritesOnLocal<T>())
            {
                if (!values.IsEmpty)
                    writer = WriteOnLocal<T, TFormatter>(values, writer);
                return;
            }

            // By reference, as in WriteOnLocal.
            ref T item = ref MemoryMarshal.GetReference(values);
            for (int left = values.Length; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Serialize(item, ref writer);
        }

        /// <summary>
        /// The elements one by one through a local copy of the writer (as Read does), out of line: Write is inlined
        /// into every collection formatter, and a second loop there slows down the code around it (as
        /// HashSetFormatter's raw loop was).
        /// </summary>
        private static Writer WriteOnLocal<T, TFormatter>(ReadOnlySpan<T> values, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            // By reference, not by index: a span's length is not known to the JIT to be positive (a list's count, for
            // one), and over a loop with a call in it each element kept a bounds check of its own.
            ref T item = ref MemoryMarshal.GetReference(values);
            for (int left = values.Length; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Serialize(item, ref writer);
            return writer;
        }

        /// <summary>
        /// The wire size of each element when a span of them is written as one block, else -1: raw elements
        /// (unmanaged values and raw [NinoType] structs), DateTimeOffsets and tuples of raw items. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int BlockStride<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            RawFormatter.Is<T, TFormatter>() ? Unsafe.SizeOf<T>()
            : typeof(TFormatter) == typeof(DateTimeOffsetFormatter) ? NinoWire.DateTimeOffsetSize
            : NinoBlockCollections.FixedSize<T, TFormatter>();

        /// <summary>Writes <paramref name="values"/> (not empty) as one block of <see cref="BlockStride"/> bytes
        /// each.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBlock<T, TFormatter>(ref byte destination, ReadOnlySpan<T> values)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (RawFormatter.Is<T, TFormatter>())
                NinoLayout<T>.CopyToWire(ref destination,
                    ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), values.Length);
            else if (typeof(TFormatter) == typeof(DateTimeOffsetFormatter))
                DateTimeOffsetFormatter.WriteAll(ref destination,
                    ref Unsafe.As<T, DateTimeOffset>(ref MemoryMarshal.GetReference(values)), values.Length);
            else
                NinoBlockCollections.WriteAll<T, TFormatter>(ref destination, ref MemoryMarshal.GetReference(values),
                    values.Length, NinoBlockCollections.FixedSize<T, TFormatter>());
        }

        /// <summary>
        /// The bytes of a collection of <paramref name="count"/> elements (-1: null) written as its length and one
        /// block (<see cref="INinoSizedFormatter{T}"/>), or -1 when its elements are not, or it is too large for an
        /// array (the regular path reports it).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizedBytes<T, TFormatter>(int count) where TFormatter : struct, INinoFormatter<T>
        {
            int block = BlockStride<T, TFormatter>();
            if (block <= 0)
                return -1;
            long size = NinoWire.LengthSize + (long)Math.Max(count, 0) * block;
            return size <= int.MaxValue ? (int)size : -1;
        }

        /// <summary>
        /// Writes a collection's length (the null length when <paramref name="isNull"/>) and its elements as one block
        /// into the <see cref="SizedBytes"/> at <paramref name="destination"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSized<T, TFormatter>(ref byte destination, ReadOnlySpan<T> values, bool isNull)
            where TFormatter : struct, INinoFormatter<T>
        {
            NinoUnsafe.Write(ref destination, isNull ? NinoWire.NullLength : values.Length);
            if (!values.IsEmpty)
                WriteBlock<T, TFormatter>(ref Unsafe.Add(ref destination, NinoWire.LengthSize), values);
        }

        /// <summary>Reads into the elements of a span, reusing the existing values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(Span<T> values, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (typeof(TFormatter) == typeof(StringFormatter))
            {
                // One loop with the position in a register (see Reader.ReadStrings).
                reader.ReadStrings(ref Unsafe.As<T, string>(ref MemoryMarshal.GetReference(values)), values.Length);
                return;
            }

            // Raw elements: one bounds check and one block copy (see Write). Bools one by one (checked,
            // NinoLayout<T>.CopiesAsIs).
            if (RawFormatter.Is<T, TFormatter>() && NinoLayout<T>.CopiesAsIs)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)),
                    ref reader.ReadElements(values.Length, Unsafe.SizeOf<T>()),
                    (uint)values.Length * (uint)Unsafe.SizeOf<T>());
                return;
            }

            if (typeof(TFormatter) == typeof(DateTimeOffsetFormatter))
            {
                DateTimeOffsetFormatter.ReadAll(
                    ref reader.ReadElements(values.Length, NinoWire.DateTimeOffsetSize),
                    ref Unsafe.As<T, DateTimeOffset>(ref MemoryMarshal.GetReference(values)), values.Length);
                return;
            }

            int block = NinoBlockCollections.ReadSize<T, TFormatter>();
            if (block > 0)
            {
                NinoBlockCollections.ReadAll<T, TFormatter>(ref reader.ReadElements(values.Length, block),
                    ref MemoryMarshal.GetReference(values), values.Length, block);
                return;
            }

            // Empty collections (the leaves of a tree) skip the call and the copies of the reader in and out.
            if (values.Length != 0)
                reader = ReadEach<T, TFormatter>(values, reader);
        }

        /// <summary>
        /// The elements one by one, out of line: inlined into a read (also where they are blocks and it is never
        /// reached), the loop and the element's formatter used up what the JIT inlines into the method, and helpers as
        /// small as Reader.Skip were left calls. The reader by value and returned, not by reference: its address passed
        /// to a call would keep the caller's reader in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadEach<T, TFormatter>(Span<T> values, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            // The reader a local, its cursor in registers, where through a reference every element would store it and
            // load it back (List<(int, long)>: 2x faster).
            ref T item = ref MemoryMarshal.GetReference(values); // by reference, see WriteOnLocal
            for (int left = values.Length; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TFormatter).Deserialize(ref item, ref reader);
            return reader;
        }

        /// <summary>
        /// Returns <paramref name="array"/> when it has <paramref name="length"/> elements; otherwise replaces it with
        /// an array of that length (allocated uninitialized when <paramref name="uninitialized"/>, see
        /// <see cref="NinoUnsafe.NewArray{T}"/>), moving over the elements that fit (unless the caller overwrites every
        /// element: <paramref name="overwritten"/>, or <paramref name="uninitialized"/>) and recycling the rest (and
        /// the old array).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] EnsureLength<T>(ref T[] array, int length, bool uninitialized = false,
            bool overwritten = false)
        {
            var current = array;
            if (current == null)
            {
                // No array yet (a new object): allocated here, the JIT's allocation helper called directly rather
                // than through Resize (a call and a frame).
                var result = NinoUnsafe.NewArray<T>(length, uninitialized);
                array = result;
                return result;
            }

            if (current.Length == length)
                return current;
            // Resize takes the old array by value and the slot is stored here: the slot's address reaching a call
            // would keep it in memory when it is a local (the value of a property, read into a local and stored back
            // after), with a store and a reload around the call, and zeroed in the prolog.
            var resized = Resize(current, length, uninitialized, overwritten | uninitialized);
            array = resized;
            // As NinoRecycler.Replace: the old array is recycled once the new one took the slot (the shared empty
            // array never is).
            if (current.Length != 0)
                NinoRecycler<T[]>.Recycle(current);
            return resized;
        }

        /// <summary>
        /// The array a read of <paramref name="length"/> unmanaged elements that writes every one of them fills:
        /// <paramref name="current"/> when it has that length, else a new one allocated uninitialized
        /// (<see cref="NinoUnsafe.NewArray{T}"/>), which the caller stores with <see cref="Publish{T}"/> once the read
        /// has filled it, so that data the read rejects half way never leaves stale heap memory in the caller's object
        /// (a has-value byte, a raw bool, a value <see cref="NinoSettings.Untrusted"/> mode checks).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] NewOrSame<T>(T[] current, int length) =>
            current != null && current.Length == length ? current : NinoUnsafe.NewArray<T>(length, uninitialized: true);

        /// <summary>
        /// Stores <paramref name="filled"/> (from <see cref="NewOrSame{T}"/> of <paramref name="current"/>) in
        /// <paramref name="array"/> and recycles the array it replaced, as <see cref="EnsureLength{T}"/> does.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Publish<T>(ref T[] array, T[] current, T[] filled)
        {
            if (filled == current)
                return;
            array = filled;
            // The shared empty array is never recycled.
            if (current != null && current.Length != 0)
                NinoRecycler<T[]>.Recycle(current);
        }

        /// <summary>
        /// A new array of <paramref name="length"/> elements for <paramref name="old"/> (null, or of another length):
        /// moves over the elements that fit (unless <paramref name="overwritten"/>), recycles the rest and clears
        /// the old array. The caller stores the result and recycles the old array.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T[] Resize<T>(T[] old, int length, bool uninitialized, bool overwritten)
        {
            var result = NinoUnsafe.NewArray<T>(length, uninitialized);
            // Nothing to move or drop (the shared empty array is never recycled).
            if (old == null || old.Length == 0)
                return result;

            // The elements that fit move to the new array (which now owns them; one about to be overwritten drops
            // them too), the rest are dropped, and the emptied old array is replaced.
            int keep = overwritten ? 0 : Math.Min(old.Length, length);
            Array.Copy(old, result, keep);
            NinoRecycler.DiscardAll(old.AsSpan(keep));
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                Array.Clear(old, 0, old.Length);
            return result;
        }
    }
}
