using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The read policies of array-like formatters (what a new array is allocated as, what a count is checked with,
    /// how far a collection is sized ahead of its elements) and the one-value write and read of a sequence not held
    /// in a run; the loops themselves are <see cref="NinoRuns"/>.
    /// </summary>
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
        /// Elements read value by value (blocks without a map, such as DateTimeOffsets, and values Untrusted mode checks
        /// a field at a time) keep a zeroed array too: the clearing brings its lines into the cache, where such stores
        /// into fresh lines first read each one (workstation GC, AVX-512 Xeon: 1,000 DateTimeOffsets in 3.7 to 3.8 µs
        /// zeroed against 5.0 to 5.6). A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool CopiedWhole<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoKind<T, TFormatter>.CopiedWhole;

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
        /// <see cref="WritesOnLocal{T}"/>, and also for sealed classes whose generated body opens no class body
        /// (<see cref="NinoBodyTraits.Nests"/> clear: raw members and strings), which inline into the loop and never
        /// recurse through it. Through the caller's writer by reference, the cursor was loaded from and stored to
        /// memory for every member (LuminPack's ActiveBuff[], four raw members and a string: four reservations an
        /// element, each a load, a compare and two stores; #305). A JIT-time constant at Tier1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool WritesOnLocal<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            WritesOnLocal<T>() || NinoKind<T, TFormatter>.FlatClass;

        /// <summary>
        /// Whether <see cref="NinoRuns.Read{T,TFormatter}(Span{T}, ref Reader, bool)"/> overwrites every element
        /// whatever it held (raw elements and blocks are each read in full), so the slots need not
        /// be cleared or zeroed first. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadOverwrites<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() && NinoKind<T, TFormatter>.ReadOverwrites;

        /// <summary>
        /// The fewest bytes an element takes on the wire, to check a length against the bytes left before allocating:
        /// <see cref="NinoKind.Stride{T,TFormatter}"/> for elements read as one block, else 1. A new array read as one
        /// block is allocated uninitialized (<see cref="CopiedWhole{T,TFormatter}"/>): checked against 1 byte an
        /// element, data claiming more elements than its bytes hold left an array of stale heap memory in the caller's
        /// object when the copy then failed (Deserialize with an existing value). A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MinWireSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            // Elements with references are never one block: 1 without the static field (a shared generic's runtime
            // lookup).
            NinoUnsafe.IsReferenceOrContainsReferences<T>() ? 1 : NinoKind<T, TFormatter>.MinWire;

        /// <summary>
        /// <see cref="MinWireSize{T,TFormatter}"/> of a value written through its formatter rather than as an element
        /// of a sequence (a dictionary's key or value, a pair's item): 1 for a slot, whose single value is a presence
        /// byte when absent. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MinSingleSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot ? 1 : MinWireSize<T, TFormatter>();

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
        /// The fewest bytes an element of a hash set takes, which its count is checked with: raw elements their
        /// size, slots theirs (<see cref="NinoKind.Stride{T,TFormatter}"/> of both), others (strings included: null
        /// is one byte; blocks, which a set reads one by one) 1. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MinHashedSize<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind.Of<T, TFormatter>() is NinoElementKind.Raw or NinoElementKind.Slot
                ? NinoKind<T, TFormatter>.Stride
                : 1;

        /// <summary>
        /// Whether <paramref name="length"/> slots of <paramref name="slotSize"/> bytes fit
        /// <see cref="Amplification"/> bytes per byte left (or <see cref="PresizeFloor"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Fits(int length, int slotSize, int remaining) =>
            (ulong)(uint)length * (uint)slotSize <= Budget(remaining);

#if NET8_0_OR_GREATER
        // CoreLib's shared pool starts at 16 slots and rounds larger requests up to a power of two.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool FitsRented(int length, int slotSize, int remaining) =>
            (ulong)Math.Max(2u * (uint)length, 16u) * (uint)slotSize <= Budget(remaining);
#endif

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
        /// <see cref="ArrayFormatter{T,TFormatter}"/> does, for <see cref="NinoKind{T,TFormatter}.Heavy"/> elements: an
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
                NinoRuns.Read<T, TFormatter>(new Span<T>(items, done, count - done), ref reader);
                if (count == length)
                    return reader;
                done = count;
                count = (int)Math.Min(length, 2L * count);
            }
        }

        /// <summary>
        /// Writes one element of a sequence that is not written from a span (an enumerator, a node): its slot where
        /// elements are slots, so that it is the bytes <see cref="NinoRuns.Write{T,TFormatter}(ReadOnlySpan{T}, ref Writer)"/>
        /// writes for it, else through its formatter.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteOne<T, TFormatter>(T item, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                default(TFormatter).WriteSlots(ref writer.Reserve(NinoKind.Stride<T, TFormatter>()),
                    MemoryMarshal.CreateReadOnlySpan(ref item, 1));
            else
                default(TFormatter).Serialize(item, ref writer);
        }

        /// <summary><see cref="WriteOne{T,TFormatter}"/> the other way.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadOne<T, TFormatter>(ref Reader reader, ref T item)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                NinoRuns.ReadSlots<T, TFormatter>(ref item, 1, ref reader, prechecked: false);
            else
                default(TFormatter).Deserialize(ref reader, ref item);
        }

        /// <summary>Writes <paramref name="values"/> (not empty) as one block of <see cref="NinoKind.Stride"/> bytes
        /// each.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBlock<T, TFormatter>(ref byte destination, ReadOnlySpan<T> values)
            where TFormatter : struct, INinoFormatter<T> =>
            WriteBlock<T, TFormatter>(ref destination, ref MemoryMarshal.GetReference(values), values.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WriteBlock<T, TFormatter>(ref byte destination, ref T first, int count)
            where TFormatter : struct, INinoFormatter<T>
        {
            var kind = NinoKind.Of<T, TFormatter>();
            if (kind == NinoElementKind.Raw)
                NinoLayout<T>.CopyToWire(ref destination,
                    ref Unsafe.As<T, byte>(ref first), count);
            else if (kind == NinoElementKind.Slot)
                default(TFormatter).WriteSlots(ref destination,
                    MemoryMarshal.CreateReadOnlySpan(ref first, count));
            else
                NinoBlockCollections.WriteAll<T, TFormatter>(ref destination, ref first,
                    count, NinoBlockCollections.FixedSize<T, TFormatter>());
        }

        /// <summary>
        /// The bytes of a collection of <paramref name="count"/> elements (-1: null) written as its length and one
        /// block (<see cref="INinoSizedFormatter{T}"/>), or -1 when its elements are not, or it is too large for an
        /// array (the regular path reports it).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizedBytes<T, TFormatter>(int count) where TFormatter : struct, INinoFormatter<T>
        {
            int block = NinoKind.Stride<T, TFormatter>();
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
        /// (a presence byte, a raw bool, a value <see cref="NinoSettings.Untrusted"/> mode checks).
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
