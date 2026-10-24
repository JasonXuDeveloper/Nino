using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core
{
    /// <summary>Shared element loops for array-like formatters.</summary>
    internal static class NinoElements
    {
        /// <summary>
        /// Whether elements read into a new array are copied into it as one block (NinoUnsafe.CopyToNew), which overwrites
        /// it all: only then is it allocated uninitialized. An array filled by vector or per-value stores is allocated
        /// zeroed: the clearing brings its lines into the cache, where such stores into fresh lines first read each one
        /// (workstation GC, AVX-512 Xeon: 1,000 (int, long) into a new list in 1.5 to 1.7 µs zeroed, 2.7 to 2.9
        /// uninitialized; 1,000 DateTimeOffsets in 3.7 to 3.8 µs against 5.0 to 5.6; server GC on par or faster zeroed). A
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
        /// <see cref="BlockSize{T,TFormatter}"/> for elements read as one block, else 1. A new array read as one block
        /// is allocated uninitialized (<see cref="CopiedWhole{T,TFormatter}"/>): checked against 1 byte an element,
        /// data claiming more elements than its bytes hold left an array of stale heap memory in the caller's object
        /// when the copy then failed (Deserialize with an existing value). A JIT-time constant.
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
        /// size, a string its 4-byte header, others 1. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MinHashedSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            RawFormatter.Is<T, TFormatter>() ? Unsafe.SizeOf<T>()
            : typeof(TFormatter) == typeof(StringFormatter) ? NinoWire.LengthSize
            : 1;

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
            public static readonly bool CopiedWhole = RawFormatter.Is<T, TFormatter>() ||
                NinoBlock.Size<T, TFormatter>() > 0 && NinoBlock<T, TFormatter>.IsMemory;

            public static readonly bool ReadOverwrites = RawFormatter.Is<T, TFormatter>() ||
                typeof(TFormatter) == typeof(DateTimeOffsetFormatter) || NinoBlock.Size<T, TFormatter>() > 0;

            public static readonly int MinWireSize = Math.Max(BlockSize<T, TFormatter>(), 1);
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
            // Elements of a fixed size (BlockSize): one capacity check for the block, none per element. Raw elements
            // too, as the unmanaged formatters write them: an interface, Stack or Queue of them, or any collection
            // whose formatter is not specialized for them, would otherwise check the capacity per element.
            int block = BlockSize<T, TFormatter>();
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
        public static int BlockSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            RawFormatter.Is<T, TFormatter>() ? Unsafe.SizeOf<T>()
            : typeof(TFormatter) == typeof(DateTimeOffsetFormatter) ? NinoWire.DateTimeOffsetSize
            : NinoBlock.Size<T, TFormatter>();

        /// <summary>Writes <paramref name="values"/> (not empty) as one block of <see cref="BlockSize"/> bytes each.</summary>
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
                NinoBlock.WriteAll<T, TFormatter>(ref destination, ref MemoryMarshal.GetReference(values),
                    values.Length, NinoBlock.Size<T, TFormatter>());
        }

        /// <summary>
        /// The bytes of a collection of <paramref name="count"/> elements (-1: null) written as its length and one
        /// block (<see cref="INinoSizedFormatter{T}"/>), or -1 when its elements are not, or it is too large for an
        /// array (the regular path reports it).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizedBytes<T, TFormatter>(int count) where TFormatter : struct, INinoFormatter<T>
        {
            int block = BlockSize<T, TFormatter>();
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

            int block = NinoBlock.ReadSize<T, TFormatter>();
            if (block > 0)
            {
                NinoBlock.ReadAll<T, TFormatter>(ref reader.ReadElements(values.Length, block),
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

    /// <summary><c>T[]</c>: length (-1 = null), then the elements. Reuses an existing array of the same length.</summary>
    public struct ArrayFormatter<T, TFormatter> : INinoFormatter<T[]>, INinoSizedFormatter<T[]>
        where TFormatter : struct, INinoFormatter<T>
    {
        int INinoSizedFormatter<T[]>.Size(T[] value) =>
            NinoElements.SizedBytes<T, TFormatter>(value == null ? -1 : value.Length);

        void INinoSizedFormatter<T[]>.WriteSized(T[] value, ref byte destination) =>
            NinoElements.WriteSized<T, TFormatter>(ref destination, value, value == null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer)
        {
            if (!writer.TryWriteNullLength(value))
                NinoCollections.WriteSpan<T, TFormatter>(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T[] value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            // Elements far larger in memory than on the wire: out of line, grown as they are read where the data
            // could not fill as many. A constant false for the others, and nothing compiled.
            if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
            {
                reader = NinoElements.ReadGrowing<T, TFormatter>(ref value, length, reader);
                return;
            }

            NinoElements.Read<T, TFormatter>(
                NinoElements.EnsureLength(ref value, length, NinoElements.CopiedWhole<T, TFormatter>(),
                    NinoElements.ReadOverwrites<T, TFormatter>()), ref reader);
        }
    }

    /// <summary>Unmanaged <c>T[]</c>: length (-1 = null), then one block copy.</summary>
    public struct UnmanagedArrayFormatter<[NinoRaw] T> : INinoFormatter<T[]>, INinoRawArrayFormatter where T : unmanaged
    {
        // Elements with a non-canonical layout take the formatter path, which converts them.
        int INinoRawArrayFormatter.ElementSize => NinoLayout<T>.IsCanonical ? Unsafe.SizeOf<T>() : 0;

        // The top-level calls reach these through a static readonly field of their exact type, so they are inlined
        // there with Unsafe.SizeOf<T>() a constant: no multiplication by a loaded size, no overflow branches.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoRawArrayFormatter.Write(NinoArrayBufferWriter buffer, Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            NinoLayout<T>.WriteLengthPrefixed(ref buffer.Reserve(WireSize(length)),
                ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(values)), length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        byte[] INinoRawArrayFormatter.Serialize(Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            // Every byte is written below.
            var bytes = NinoUnsafe.AllocateUninitializedArray<byte>(WireSize(length));
            NinoLayout<T>.WriteLengthPrefixed(ref NinoUnsafe.GetArrayDataReference(bytes),
                ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(values)), length);
            return bytes;
        }

        /// <summary>
        /// Whether a length read as it is (-1 for null included) is past <see cref="NinoSettings.MaxCollectionLength"/>:
        /// -1 is then too, and takes the cold path that tells null from invalid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TooLong(int length) =>
            NinoLimits.LengthCapped && (uint)length > (uint)NinoLimits.MaxCollectionLength;

        /// <summary>The length prefix and <paramref name="length"/> elements, in bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WireSize(int length)
        {
            // In 64 bits: cannot overflow; one compare rejects what does not fit an array.
            ulong size = NinoWire.LengthSize + (ulong)(uint)length * (uint)Unsafe.SizeOf<T>();
            if (size > int.MaxValue)
                NinoThrow.TooLarge();
            return (int)size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Array INinoRawArrayFormatter.Read(ReadOnlySpan<byte> data, Array existing)
        {
            // A new array (NinoDeserializer.Deserialize<T[]>(data) passes null, a constant once inlined): no slot
            // to reuse, so no local whose address reaches the recycler and stays in memory.
            if (existing == null && NinoLayout<T>.CopiesAsIs)
                return ReadNew(data);
            var reader = new Reader(data);
            var value = Unsafe.As<T[]>(existing);
            Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>The bytes of <see cref="Deserialize"/>, into a new array (null for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T[] ReadNew(ReadOnlySpan<byte> data)
        {
            if (data.Length < NinoWire.LengthSize)
                NinoThrow.EndOfData();
            ref byte start = ref MemoryMarshal.GetReference(data);
            int length = NinoUnsafe.Read<int>(ref start);
            uint size = (uint)Unsafe.SizeOf<T>();
            if ((ulong)(uint)length * size > (uint)(data.Length - NinoWire.LengthSize) || TooLong(length))
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                return null;
            }

            var array = NinoUnsafe.NewArray<T>(length, uninitialized: true);
            NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array)),
                ref Unsafe.Add(ref start, NinoWire.LengthSize), (uint)length * size);
            return array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T[] value, ref Reader reader)
        {
            // Also bools, checked there (NinoLayout<T>.CopiesAsIs).
            if (!NinoLayout<T>.CopiesAsIs)
            {
                DeserializeConverted(ref value, ref reader);
                return;
            }

            // One bounds check for the count and the elements: a negative count (null, or invalid) is a huge
            // unsigned size and takes the cold path.
            int length = reader.UnsafeRead<int>();
            uint size = (uint)Unsafe.SizeOf<T>();
            if ((ulong)(uint)length * size > (uint)reader.Remaining || TooLong(length))
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                NinoRecycler.Discard(ref value);
                return;
            }

            var array = NinoElements.EnsureLength(ref value, length, uninitialized: true);
            uint bytes = (uint)length * size;
            NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array)),
                ref reader.UnsafeAdvance((int)bytes), bytes);
        }

        private static void DeserializeConverted(ref T[] value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoLayout<T>.Size, out int length))
                return;

            reader.ReadUnmanagedRaw(NinoElements.EnsureLength(ref value, length, uninitialized: true).AsSpan());
        }
    }

    /// <summary>
    /// <c>T?[]</c> of raw <typeparamref name="T"/>: length, then every element at a fixed stride in the canonical
    /// <c>Nullable&lt;T&gt;</c> layout (see <see cref="NinoNullables{T}"/>), written into one reservation and read
    /// without per-element calls. Not the bytes of <see cref="ArrayFormatter{T,TFormatter}"/> with
    /// <see cref="NullableFormatter{T,TFormatter}"/> (a has-value byte, then the value only when present), which
    /// sequences declared as interfaces (<c>IList&lt;T?&gt;</c>, <c>IEnumerable&lt;T?&gt;</c>) write.
    /// </summary>
    public struct NullableArrayFormatter<[NinoRaw] T> : INinoFormatter<T?[]> where T : unmanaged
    {
        public void Serialize(T?[] value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            NinoNullables<T>.Write(value, ref writer);
        }

        public void Deserialize(ref T?[] value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoNullables<T>.Stride, out int length))
                return;

            // Uninitialized only for a block copy (NinoElements.CopiedWhole).
            NinoNullables<T>.Read(NinoElements.EnsureLength(ref value, length, NinoNullables<T>.Raw, overwritten: true),
                ref reader);
        }
    }

    /// <summary>
    /// <c>T?</c> sequences with unmanaged <c>T</c>, in the canonical (64-bit corefx) <c>Nullable&lt;T&gt;</c> layout:
    /// a has-value byte, zero padding up to T's alignment, then T in its canonical layout (zero when null).
    /// Where the runtime lays <c>T?</c> out the same way the elements are copied as one block (padding cleared);
    /// elsewhere (old Mono puts the value first, x86 aligns 8-byte values to 4) they are converted one by one.
    /// </summary>
    internal static class NinoNullables<T> where T : unmanaged
    {
        private static readonly int Offset = NinoLayout<T>.Info.Align > 0 ? NinoLayout<T>.Info.Align : 1;

        /// <summary>Wire size of one element.</summary>
        public static readonly int Stride = Offset + NinoLayout<T>.WireSize;

        /// <summary>Whether <c>T?</c> memory equals its wire layout on this runtime.</summary>
        // Not for bool: its values are read one by one, checked (NinoLayout<T>.CopiesAsIs).
        internal static readonly bool Raw = NinoLayout<T>.CopiesAsIs && NullableLayout<T>.HasValueOffset == 0 &&
                                           NullableLayout<T>.ValueOffset == Offset &&
                                           Unsafe.SizeOf<T?>() == Stride;

        private static readonly NinoMask Mask = BuildMask();

        private static NinoMask BuildMask()
        {
            var mask = new byte[Stride];
            mask[0] = 0xFF;
            var value = NinoLayout<T>.Info.Mask;
            for (int i = 0; i < NinoLayout<T>.WireSize; i++)
                mask[Offset + i] = value == null ? (byte)0xFF : value[i];
            return NinoMask.Create(mask);
        }

        /// <summary>Writes the count and the elements.</summary>
        public static void Write(ReadOnlySpan<T?> values, ref Writer writer)
        {
            ref byte dst = ref writer.ReserveCounted(values.Length, Stride);
            if (values.IsEmpty)
                return;
            ref T? item = ref MemoryMarshal.GetReference(values);
            if (Raw)
            {
                ref byte src = ref Unsafe.As<T?, byte>(ref item);
                if (Mask != null)
                    Mask.Copy(ref dst, ref src, values.Length);
                else
                    NinoUnsafe.Copy(ref dst, ref src, (uint)(values.Length * Stride));
                return;
            }

            Unsafe.InitBlockUnaligned(ref dst, 0, (uint)(values.Length * Stride));
            for (int i = 0; i < values.Length; i++)
            {
                ref T? element = ref Unsafe.Add(ref item, i);
                if (!element.HasValue)
                    continue;
                ref byte slot = ref Unsafe.Add(ref dst, i * Stride);
                slot = NinoWire.NotNull;
                NinoLayout<T>.Write(ref Unsafe.Add(ref slot, Offset), element.GetValueOrDefault());
            }
        }

        /// <summary>Reads <c>destination.Length</c> elements (the count has been read).</summary>
        public static void Read(Span<T?> destination, ref Reader reader)
        {
            ref byte src = ref reader.ReadElements(destination.Length, Stride);
            if (destination.IsEmpty)
                return;
            // Has-value bytes other than 0 or 1 are invalid data (copied as they are, they would make HasValue true
            // and the value unequal to any other).
            NinoChecks.Flags(ref src, destination.Length, Stride);
            ref T? item = ref MemoryMarshal.GetReference(destination);
            if (Raw)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T?, byte>(ref item), ref src, (uint)(destination.Length * Stride));
                return;
            }

            for (int i = 0; i < destination.Length; i++)
            {
                ref byte slot = ref Unsafe.Add(ref src, i * Stride);
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset))
                    : default(T?);
            }
        }
    }

    /// <summary>
    /// Field offsets of <c>Nullable&lt;T&gt;</c> in memory on the running runtime. Old Mono stores the value first
    /// while corefx stores the flag first, so the layout is probed rather than assumed.
    /// </summary>
    internal static class NullableLayout<T> where T : unmanaged
    {
        public static readonly int HasValueOffset = ProbeHasValue();

        public static readonly int ValueOffset =
            HasValueOffset == 0 ? Unsafe.SizeOf<T?>() - Unsafe.SizeOf<T>() : 0;

        private static int ProbeHasValue()
        {
            T? probe = default(T); // has value, zero value and padding
            ref byte b = ref Unsafe.As<T?, byte>(ref probe);
            for (int i = 0; i < Unsafe.SizeOf<T?>(); i++)
                if (Unsafe.Add(ref b, i) != 0)
                    return i;
            return 0;
        }
    }

    /// <summary><c>ArraySegment&lt;T&gt;</c>: written like an array; read into a new array.</summary>
    public struct ArraySegmentFormatter<T, TFormatter> : INinoFormatter<ArraySegment<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ArraySegment<T> value, ref Writer writer)
        {
            if (!writer.TryWriteNullLength(value.Array))
                NinoCollections.WriteSpan<T, TFormatter>(value.AsSpan(), ref writer);
        }

        public void Deserialize(ref ArraySegment<T> value, ref Reader reader)
        {
            var array = NinoCollections.ReadArray<T, TFormatter>(ref reader);
            value = array == null ? default : new ArraySegment<T>(array);
        }
    }

    /// <summary>The lengths of a multidimensional array (1 past its rank) and its element count.</summary>
    internal struct ArrayShape
    {
        public int A, B, C, D, Total;
    }

    /// <summary>The lengths of the multidimensional array formatters.</summary>
    internal static class MultiDim
    {
        /// <summary>Writes the length of each dimension of <paramref name="value"/> (not null).</summary>
        public static void WriteLengths(Array value, ref Writer writer)
        {
            int rank = value.Rank;
            ref byte p = ref writer.Reserve(rank * NinoWire.LengthSize);
            for (int i = 0; i < rank; i++)
                NinoUnsafe.Write(ref p, i * NinoWire.LengthSize, value.GetLength(i));
        }

        /// <summary>
        /// Reads the <paramref name="rank"/> lengths (false when the first is -1, for null) and checks them: rejects
        /// negative lengths, lengths no array can have and more elements than bytes left (every element takes at
        /// least <paramref name="minElementSize"/> bytes, <see cref="NinoElements.MinWireSize{T,TFormatter}"/>). The
        /// first length alone is not checked against the remaining bytes: another dimension may be 0, so a single
        /// length says nothing about the payload.
        /// </summary>
        public static bool TryReadShape(ref Reader reader, int rank, int minElementSize, out ArrayShape shape)
        {
            shape = new ArrayShape { B = 1, C = 1, D = 1 };
            if (!reader.TryReadLength(0, out shape.A))
                return false;
            shape.B = reader.UnsafeRead<int>();
            if (rank > 2)
                shape.C = reader.UnsafeRead<int>();
            if (rank > 3)
                shape.D = reader.UnsafeRead<int>();
            int a = shape.A, b = shape.B, c = shape.C, d = shape.D;
            if (b < 0 || c < 0 || d < 0)
                NinoThrow.InvalidLength(Math.Min(b, Math.Min(c, d)));
            // Longest dimension the runtime can allocate, also when the array is empty.
            const int max = NinoUnsafe.MaxArrayLength;
            if (a > max || b > max || c > max || d > max)
                NinoThrow.InvalidLength(Math.Max(Math.Max(a, b), Math.Max(c, d)));
            // One dimension multiplied in at a time, each product checked: a * b * c * d overflows a long (three
            // dimensions of 2^29 wrap around to 0). An empty array too: the runtime rejects one whose leading
            // dimensions multiply past 2^32 (CoreCLR: OutOfMemoryException for 65536 x 65536 x 0).
            long total = a;
            // The last product times the fewest bytes an element takes (NinoElements.MinWireSize, under 2^31):
            // under 2^63.
            if ((total *= b) > uint.MaxValue || (total *= c) > uint.MaxValue || (total *= d) > uint.MaxValue ||
                total * minElementSize > reader.Remaining || NinoLimits.LengthCapped && total > NinoLimits.MaxCollectionLength)
                NinoThrow.InvalidLength((int)Math.Min(total, int.MaxValue));
            shape.Total = (int)total;
            return true;
        }

        /// <summary>
        /// Copies <paramref name="items"/> into the elements of a multidimensional array starting at
        /// <paramref name="first"/>, as many as it holds.
        /// </summary>
        public static void Fill<T>(T[] items, ref T first)
        {
            for (int i = 0; i < items.Length; i++)
                Unsafe.Add(ref first, i) = items[i];
        }

        /// <summary>Whether <paramref name="array"/> is not null and of <paramref name="shape"/>.</summary>
        public static bool SameShape(Array array, in ArrayShape shape)
        {
            if (array == null || array.GetLength(0) != shape.A || array.GetLength(1) != shape.B)
                return false;
            int rank = array.Rank;
            return (rank < 3 || array.GetLength(2) == shape.C) && (rank < 4 || array.GetLength(3) == shape.D);
        }
    }
}
