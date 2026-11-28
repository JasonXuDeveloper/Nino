using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// The capabilities of every <see cref="INinoFormatter{T}"/> that do not depend on <c>T</c>: the run loop of
    /// generated formatters of polymorphic bases, the prefetch of long collections of classes, and the queries
    /// of the top-level writes of Nino.Core's own collection formatters. Never implemented alone. Their queries are read
    /// once per formatter type, into fields of a class generic over the formatter alone, which fold even where the code
    /// asking them is shared generic code over a class <c>T</c> (a field of a class generic over <c>T</c> too would be a
    /// lookup per collection there).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoFormatter
    {
        /// <summary>
        /// What a value's body does, as <see cref="NinoBodyTraits"/> flags (a constant):
        /// <see cref="NinoBodyTraits.Nests"/> (the default: a formatter that says nothing may open class bodies),
        /// <see cref="NinoBodyTraits.Deferrable"/> and <see cref="NinoBodyTraits.Dispatches"/>. Every
        /// <see cref="CompilerServices.INinoBodyFormatter{T}"/> implements it, and is asked it per value; of other
        /// formatters it is asked once per type (a sealed class whose body opens no class body is written through a
        /// local writer in collection loops). On the non-generic interface, so it is read into a field keyed on the
        /// formatter alone.
        /// </summary>
        int BodyTraits => NinoBodyTraits.Nests;

        /// <summary>
        /// Whether <see cref="WriteRun"/> writes the values of a run of a collection's storage in one loop (false, the
        /// default): generated formatters of non-generic polymorphic bases, whose loop has the subtype dispatch inline
        /// and the writer in a local. Called once.
        /// </summary>
        bool HasRunLoop => false;

        /// <summary>
        /// Writes each value of <paramref name="run"/> (a reference of the formatter's type: an array's, a list's, a
        /// ring half's, a set's or a dictionary's values, each after its key where <see cref="NinoRun.Key"/> says one
        /// is there) as <see cref="INinoFormatter{T}.Serialize"/> writes one, skipping free entries. Called only where
        /// <see cref="HasRunLoop"/> is true, with the run not empty and the count already written. Not generic: one
        /// member serves every collection and every key Nino.Core writes as its bytes or a string, through a
        /// constrained call even from code shared over a class (IL2CPP 2022.3 cannot build a generic default member,
        /// #405).
        /// </summary>
        void WriteRun(NinoRun run, ref Writer writer) => NinoThrow.MissingCapability(null, nameof(WriteRun));

        /// <summary>
        /// Whether <see cref="ReadRun"/> reads the values of a run in one loop (false, the default): generated formatters
        /// of generic polymorphic bases over a closed instance whose code is shared (a type argument holding a class),
        /// whose loop lies in that instance's own dispatch, where nothing is shared code. Called once.
        /// </summary>
        bool HasReadRun => false;

        /// <summary>
        /// Reads each value of <paramref name="run"/> (values in a row, every one in use, not empty; the count is the
        /// caller's) in place as <see cref="INinoFormatter{T}.Deserialize"/> reads one. Called only where
        /// <see cref="HasReadRun"/> is true. Not generic, as <see cref="WriteRun"/>: from code shared over a class the
        /// call is the one indirect call per collection, never one per value.
        /// </summary>
        void ReadRun(in NinoRun run, ref Reader reader) => NinoThrow.MissingCapability(null, nameof(ReadRun));

        /// <summary>
        /// Whether a run of the formatter's values is written and read by the flat kernels (false, the default):
        /// <see cref="WriteFlat"/> and <see cref="ReadFlat"/>. Generated formatters of classes and structs whose body is
        /// flat (raw members, bools, raw T?s and strings: <see cref="NinoFlat{TLayout,TBody}"/>,
        /// <see cref="NinoFlatValues{TBody}"/>, #355), a constant. Called once.
        /// </summary>
        bool HasFlat => false;

        /// <summary>
        /// Writes the values of <paramref name="run"/> (not empty, every one in use; the count is the caller's) through
        /// the flat kernels. Called only where <see cref="HasFlat"/> is true. The writer by reference, as
        /// <see cref="WriteRun"/>, and the run by <c>in</c>: a run passed by value is copied with loads wider than the
        /// stores that just built it, which the CPU cannot forward.
        /// </summary>
        void WriteFlat(in NinoRun run, ref Writer writer) => NinoThrow.MissingCapability(null, nameof(WriteFlat));

        /// <summary>
        /// Reads the values of <paramref name="run"/> (not empty, values in a row) in place through the flat kernels,
        /// as <see cref="WriteFlat"/>. Called only where <see cref="HasFlat"/> is true.
        /// </summary>
        void ReadFlat(in NinoRun run, ref Reader reader) => NinoThrow.MissingCapability(null, nameof(ReadFlat));

        /// <summary>
        /// The bytes an element of a long array or list brings into the cache (a constant: its object, and a line of
        /// each object its reference members point to), or 0 (the default): generated formatters of non-generic
        /// classes whose collections prefetch, in a project built with <c>NinoPrefetchLargeCollections</c>. An array or
        /// a list of their objects larger than a quarter of the last-level cache, whose first objects are not in
        /// allocation order, written by a top-level call or read into its existing objects by one
        /// (<c>NinoDirect.SerializePrefetched</c>, <c>DeserializePrefetched</c>), is walked in runs of 16 elements, each
        /// run prefetching the objects of the runs ahead (x64 and Arm64 on .NET 8 and later). Called once.
        /// </summary>
        int PrefetchBytes => 0;

        /// <summary>
        /// Whether <see cref="PrefetchMembers"/> prefetches the objects an element's members point to (false, the
        /// default: only the elements' own objects are prefetched). Called once.
        /// </summary>
        bool HasMemberPrefetch => false;

        /// <summary>
        /// Prefetches the objects the reference members of <paramref name="value"/> (an element, never null) point to,
        /// through <see cref="NinoPrefetched.At"/>, and returns the sum of what that returned. Called once the
        /// element's own object was prefetched, by Nino.Core's loop over a run of elements, only where
        /// <see cref="HasMemberPrefetch"/> is true.
        /// </summary>
        int PrefetchMembers(object value)
        {
            NinoThrow.MissingCapability(null, nameof(PrefetchMembers));
            return 0;
        }

        /// <summary>
        /// Whether <see cref="INinoFormatter{T}.Size"/> and <see cref="INinoFormatter{T}.WriteSized"/> may know a
        /// value's bytes before writing it (false, the default): collections whose elements are written as one block.
        /// Nino.Core's alone. Called once.
        /// </summary>
        internal bool HasSizedWrite => false;

        /// <summary>
        /// Whether <see cref="INinoFormatter{T}.TryWriteExternal"/> may write a whole payload straight into a
        /// caller's buffer (false, the default). Nino.Core's alone. Called once.
        /// </summary>
        internal bool HasExternalWrite => false;

        /// <summary>
        /// Whether the formatter's values are pairs of items the string run kernels move (false, the default):
        /// <see cref="WriteStrings"/> and <see cref="ReadStrings"/> (<see cref="NinoStringRuns"/>, #355), which take the
        /// data and the bytes left rather than the writer or the reader, so their loops keep the data, position and end
        /// in registers. Strings themselves are told by their formatters' types, raw and block values by their kind.
        /// Nino.Core's alone (<see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>). Called
        /// once.
        /// </summary>
        internal bool HasCursor => false;

        /// <summary>
        /// Writes the values of <paramref name="run"/> (not empty; the count is the caller's) through the string run
        /// kernels (<see cref="NinoItems"/>): one call per run, so in
        /// code shared over a class only the call is indirect, never a value. The writer by reference. Called
        /// only where <see cref="HasCursor"/> is true.
        /// </summary>
        internal void WriteStrings(scoped in NinoRun run, ref Writer writer) =>
            NinoThrow.MissingCapability(null, nameof(WriteStrings));

        /// <summary>
        /// Reads the values of <paramref name="run"/> (not empty, every one in use) in place from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes) through the string run kernels; returns the bytes
        /// read. Called only where <see cref="HasCursor"/> is true.
        /// </summary>
        internal int ReadStrings(scoped NinoRun run, ref byte data, int end)
        {
            NinoThrow.MissingCapability(null, nameof(ReadStrings));
            return 0;
        }

        /// <summary>
        /// Whether a dropped value's items are recycled as the items' types (false, the default): a composite of
        /// items (a pair, a value tuple), whose <see cref="INinoFormatter{T}.ItemsMayRecycle"/> and
        /// <see cref="INinoFormatter{T}.RecycleItems"/> <see cref="NinoRecycler"/> calls for the elements a collection
        /// drops. Nino.Core's alone (<c>KeyValuePair</c>, the tuples). Called once.
        /// </summary>
        internal bool RecyclesItems => false;
    }

    /// <summary>
    /// Serializes and deserializes values of <typeparamref name="T"/>.
    /// <para>
    /// Implement this on a <c>struct</c> to get a zero-cost formatter: generic code constrained with
    /// <c>where TFormatter : struct, INinoFormatter&lt;T&gt;</c> calls it as <c>default(TFormatter).Serialize(...)</c>,
    /// which the JIT/AOT compiler specializes and inlines.
    /// </para>
    /// <para>
    /// <see cref="Serialize"/> and <see cref="Deserialize"/> are all a formatter has to implement. The other members
    /// are capabilities with defaults that report none: values of a fixed size (<see cref="FixedSize"/>,
    /// <see cref="WriteFixed"/>), blocks a collection moves as one run (<see cref="BlockMap"/>,
    /// <see cref="ReadBlock"/>), what a class body does (<see cref="INinoFormatter.BodyTraits"/>), the run loop of
    /// polymorphic bases (<see cref="INinoFormatter.HasRunLoop"/>) and the
    /// prefetch of long collections of classes (<see cref="INinoFormatter.PrefetchBytes"/>), implemented by generated
    /// formatters and Nino.Core's own where they apply; Nino.Core's formatters also have internal ones (slots, sized
    /// and external top-level writes, the string run kernels of pairs).
    /// </para>
    /// <para>
    /// The rule that keeps the defaults free: a call to a default a struct does not override boxes the struct
    /// (CoreCLR, Mono, IL2CPP). The defaults that report a missing member never use the formatter itself (its
    /// <c>GetType()</c>): the box of an unused <c>this</c> is dropped where the JIT inlines them, so a branch the
    /// queries below rule out boxes nothing even where it is compiled (FullOpts code compiled before those fields
    /// are set). So only the queries (<see cref="FixedSize"/>, <see cref="BlockMap"/>,
    /// <see cref="INinoFormatter.BodyTraits"/>, <see cref="INinoFormatter.HasRunLoop"/>,
    /// <see cref="INinoFormatter.PrefetchBytes"/>, <see cref="INinoFormatter.HasMemberPrefetch"/> and the internal
    /// ones) are ever called on a formatter that may lack a capability, once per type into a
    /// <c>static readonly</c> field the JIT folds; the hot members are called only where that field says the
    /// formatter has them, and a formatter that has them implements them, so each is a direct, constrained call.
    /// </para>
    /// </summary>
    public interface INinoFormatter<T> : INinoFormatter
    {
        /// <summary>Writes <paramref name="value"/>.</summary>
        void Serialize(T value, ref Writer writer);

        /// <summary>
        /// Reads a value into <paramref name="value"/>. When <paramref name="value"/> already holds an instance,
        /// formatters reuse it where possible; what they drop follows the ownership rules of <see cref="NinoRecycler"/>
        /// (each dropped instance is recycled exactly once).
        /// </summary>
        void Deserialize(ref Reader reader, ref T value);

        /// <summary>
        /// The bytes <see cref="Serialize"/> writes for any value (any non-null one: a null sealed class is its
        /// presence byte), or -1 when values vary in size (the default). Generated formatters of structs and sealed
        /// classes whose members are all unmanaged values or such structs: there
        /// <see cref="NinoSerializer.Serialize{T}(in T)"/> allocates the result at that size and writes the value
        /// straight into it (<see cref="WriteFixed"/>). A JIT-time constant.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        int FixedSize => -1;

        /// <summary>
        /// Writes <paramref name="value"/> (not null) into the <see cref="FixedSize"/> bytes at
        /// <paramref name="destination"/>: the same bytes as <see cref="Serialize"/>. Called only where
        /// <see cref="FixedSize"/> is not -1.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        void WriteFixed(in T value, ref byte destination) => NinoThrow.MissingCapability(typeof(T), nameof(WriteFixed));

        /// <summary>
        /// Values a collection moves as blocks of <see cref="FixedSize"/> bytes, or null (the default): value tuples
        /// and pairs of raw items, generated <c>[NinoType]</c> structs of a fixed size that are not raw. A collection
        /// of them reserves or bounds-checks its whole block once and writes (<see cref="WriteFixed"/>) or reads
        /// (<see cref="ReadBlock"/>) each value at its position with no check of its own; where the map says where each
        /// byte of a block lies in a value's memory, several values at a time by vector shuffles and permutes. A map
        /// that does not (empty, or failed: padding inside an item) moves values one at a time. Called once.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        NinoBlockMap BlockMap() => null;

        /// <summary>
        /// Reads into <paramref name="value"/> the value in the <see cref="FixedSize"/> bytes at
        /// <paramref name="source"/>, with the checks a read of it does otherwise (a <c>bool</c> other than 0 or 1 is
        /// invalid data). Called only where <see cref="BlockMap"/> is not null.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        void ReadBlock(ref T value, ref byte source) => NinoThrow.MissingCapability(typeof(T), nameof(ReadBlock));

        /// <summary>
        /// The bytes of a slot, or -1 (the default): values whose element in a collection is a slot of a fixed size,
        /// where a single value (a member, a tuple's item, a dictionary's key or value) is written another way (a raw
        /// <c>T?</c>: <see cref="UnmanagedNullableFormatter{T}"/>). Every sequence moves them as one block
        /// (<see cref="NinoElementKind.Slot"/>), and writes or reads one that it holds apart through
        /// <see cref="NinoElements.WriteOne{T,TFormatter}"/> and <see cref="NinoElements.ReadOne{T,TFormatter}"/>,
        /// never through <see cref="Serialize"/>. Nino.Core's alone.
        /// </summary>
        internal int SlotStride => -1;

        /// <summary>Writes <paramref name="values"/> (not empty) into the slots at <paramref name="destination"/>.</summary>
        internal void WriteSlots(ref byte destination, ReadOnlySpan<T> values) =>
            NinoThrow.MissingCapability(typeof(T), nameof(WriteSlots));

        /// <summary>
        /// Reads <paramref name="values"/> from their slots, which the caller checked are there, and moves the reader
        /// past them. Overwrites every value; rejects a presence byte other than 0 or 1.
        /// </summary>
        internal void ReadSlots(Span<T> values, ref Reader reader) =>
            NinoThrow.MissingCapability(typeof(T), nameof(ReadSlots));

        /// <summary>
        /// Whether a slot is its value as it lies in memory on this runtime (then also <see cref="SlotStride"/> is the
        /// value's size): a collection that keeps its elements in storage of its own (a set's entries) then moves
        /// them as raw elements are moved, checked by <see cref="CheckSlots"/> on the way in.
        /// </summary>
        internal bool SlotsAreMemory => false;

        /// <summary>
        /// Whether <see cref="WriteSlots"/> may write a run over itself (values moved to the wire as they lie, then
        /// made the wire's bytes in place): <see cref="SlotsAreMemory"/>, and every step of its kernels reads only
        /// the bytes it writes.
        /// </summary>
        internal bool SlotsWriteInPlace => false;

        /// <summary>Rejects a presence byte other than 0 or 1 in <paramref name="count"/> slots (not zero).</summary>
        internal void CheckSlots(ref byte slots, int count) => NinoThrow.MissingCapability(typeof(T), nameof(CheckSlots));

        /// <summary>
        /// Writes <paramref name="value"/> as a single value (<see cref="Serialize"/>'s bytes) at
        /// <paramref name="destination"/>, which has room for <see cref="SlotStride"/> bytes; returns the bytes written:
        /// a slot's value as an item of a composite in the string run kernels' cursor form (<c>NinoSlotItem</c>: a
        /// dictionary's <c>int?</c> values beside its string keys). Called only where <see cref="SlotStride"/> is not -1.
        /// </summary>
        internal int WriteAt(ref byte destination, in T value)
        {
            NinoThrow.MissingCapability(typeof(T), nameof(WriteAt));
            return 0;
        }

        /// <summary>
        /// As <see cref="WriteAt"/>, the read (<see cref="Deserialize"/>'s checks): a single value from the
        /// <paramref name="available"/> bytes at <paramref name="source"/> into <paramref name="value"/>; returns the
        /// bytes read. Too few bytes and a presence byte other than 0 or 1 are invalid data.
        /// </summary>
        internal int ReadAt(ref byte source, int available, ref T value)
        {
            NinoThrow.MissingCapability(typeof(T), nameof(ReadAt));
            return 0;
        }

        /// <summary>
        /// Whether a dropped value has an item that may be recycled now (<see cref="INinoFormatter.RecyclesItems"/>),
        /// asked as <see cref="NinoRecycler{T}.MayRecycle"/> is, each time a collection drops elements. Called only
        /// where <see cref="INinoFormatter.RecyclesItems"/> is true.
        /// </summary>
        internal bool ItemsMayRecycle => false;

        /// <summary>
        /// Recycles the items of a dropped <paramref name="value"/>, each once as its own type. Called only where
        /// <see cref="INinoFormatter.RecyclesItems"/> is true.
        /// </summary>
        internal void RecycleItems(in T value)
        {
        }

        /// <summary>
        /// The bytes <paramref name="value"/> is written as, or -1 when they are not known up front (for a given element
        /// type, a JIT-time constant decides which): collections whose elements are written as one block (a count
        /// times a fixed size). The top-level calls then allocate the result at that size and write the value straight
        /// into it, without the scratch buffer and the copy out of it (<see cref="NinoSized"/>). Called only where
        /// <see cref="INinoFormatter.HasSizedWrite"/> is true.
        /// </summary>
        internal int Size(T value)
        {
            NinoThrow.MissingCapability(typeof(T), nameof(Size));
            return -1;
        }

        /// <summary>Writes <paramref name="value"/> into the <see cref="Size"/> bytes at <paramref name="destination"/>.</summary>
        internal void WriteSized(T value, ref byte destination) =>
            NinoThrow.MissingCapability(typeof(T), nameof(WriteSized));

        /// <summary>
        /// Writes <paramref name="value"/> as a whole payload straight into <paramref name="buffer"/> and returns true,
        /// or returns false (nothing written) where it cannot, and the caller writes it through a scratch copy. Called
        /// only where <see cref="INinoFormatter.HasExternalWrite"/> is true.
        /// </summary>
        internal bool TryWriteExternal(T value, IBufferWriter<byte> buffer)
        {
            NinoThrow.MissingCapability(typeof(T), nameof(TryWriteExternal));
            return false;
        }
    }

    /// <summary>Top-level serialization of a value whose size its formatter knows (<see cref="INinoFormatter{T}.Size"/>).</summary>
    internal static class NinoSized
    {
        /// <summary>
        /// <paramref name="value"/> as a new array of exactly its bytes, written straight into it; null when its size is
        /// not known up front. The same bytes as a writer writes. Only where
        /// <see cref="NinoKind{TFormatter}.SizedWrite"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            int size = default(TFormatter).Size(value);
            if (size < 0)
                return null;
            // Bytes are written little-endian, as Writer does (which rejects other hosts).
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            // Zeroed, not uninitialized: the clearing brings the lines into the cache (as a fast string store), where
            // vector stores into fresh lines first read each one; 1,000 (int, long) took 1.3 times as long.
            var bytes = new byte[size];
            default(TFormatter).WriteSized(value, ref NinoUnsafe.GetArrayDataReference(bytes));
            return bytes;
        }
    }

    /// <summary>The formatter of a type, for code that only has a <see cref="Type"/>.</summary>
    internal abstract class NinoFormatterBase
    {
        /// <summary>
        /// The boxed entry point of the formatter's type, created where the type is a generic argument rather than
        /// by closing <see cref="BoxedFormatter{T}"/> at run time (which NativeAOT cannot do).
        /// </summary>
        internal abstract BoxedFormatter CreateBoxed();

        /// <summary>
        /// The struct formatter this one calls, or null: the resolver uses it as an element or member formatter
        /// directly (inlined, and raw where it is), rather than through <see cref="CachedFormatter{T}"/>.
        /// </summary>
        internal virtual Type StructType => null;
    }

    /// <summary>
    /// The formatter of <typeparamref name="T"/> held by <see cref="NinoFormatterCache{T}"/>, for code that only finds
    /// it at runtime (top-level calls, <see cref="CachedFormatter{T}"/>): a <see cref="StructFormatter{T,TFormatter}"/>
    /// around a struct formatter, called virtually. Every formatter users write is a struct
    /// <see cref="INinoFormatter{T}"/>; this class is only the dispatch carrier and is not part of the API.
    /// </summary>
    internal abstract class NinoFormatterBase<T> : NinoFormatterBase, INinoFormatter<T>
    {
        internal sealed override BoxedFormatter CreateBoxed() => new BoxedFormatter<T>();

        public abstract void Serialize(T value, ref Writer writer);

        public abstract void Deserialize(ref Reader reader, ref T value);

        /// <summary>
        /// Writes <paramref name="value"/> as a whole payload into <paramref name="buffer"/> (the top-level calls): the
        /// writer is created by the formatter's class, specialized for the struct formatter, so the JIT keeps it in
        /// registers there rather than in the memory of a caller that passes its address (see
        /// <see cref="INinoTopFormatter{T}"/>).
        /// </summary>
        internal abstract void Write(T value, NinoArrayBufferWriter buffer);

        /// <summary>Writes a whole payload to an external buffer, using scratch when the size is unknown.</summary>
        internal virtual void WriteExternal(T value, IBufferWriter<byte> buffer) =>
            NinoScratch.CopyTo(value, new NinoSerializer.FormatterWriter<T>(this), buffer);

        /// <summary>
        /// Reads a whole payload into a new value (the top-level calls): the reader is created by the formatter's
        /// class, for the reasons of <see cref="Write"/>.
        /// </summary>
        internal abstract T Read(ReadOnlySpan<byte> data);

        /// <summary>Whether the formatter writes exactly the in-memory bytes of <typeparamref name="T"/>.</summary>
        internal virtual bool IsRaw => false;

        /// <summary>Set when <typeparamref name="T"/> is an array written as its length and one raw block.</summary>
        internal virtual INinoRawArrayFormatter RawArray => null;

        /// <summary>
        /// The size of every (non-null) value when it is fixed (<see cref="INinoFormatter{T}.FixedSize"/>), else -1.
        /// Read through <see cref="NinoTopFormatter{T}.Formatter"/>, whose exact class Tier1 code knows, so a constant.
        /// </summary>
        internal virtual int FixedSize => -1;

        /// <summary>A new array of exactly the value's bytes; only when <see cref="FixedSize"/> is not -1.</summary>
        internal virtual byte[] SerializeFixed(T value) => null;

        /// <summary>
        /// Whether <see cref="SerializeSized"/> may apply (<see cref="INinoFormatter.HasSizedWrite"/>). Read through
        /// <see cref="NinoTopFormatter{T}.Formatter"/>, as <see cref="FixedSize"/>, so a constant.
        /// </summary>
        internal virtual bool Sized => false;

        /// <summary>A new array of exactly the value's bytes when its size is known up front (<see cref="Sized"/>), else null.</summary>
        internal virtual byte[] SerializeSized(T value) => null;

        /// <summary>
        /// This formatter, counting each read toward the depth limit, for a type that can hold itself through
        /// collections (<see cref="NinoNesting"/>); the formatter itself where it cannot be wrapped.
        /// </summary>
        internal virtual NinoFormatterBase<T> Counted() => this;
    }

    /// <summary>Formatters of arrays whose elements are written as one raw block.</summary>
    internal interface INinoRawArrayFormatter
    {
        /// <summary>Wire size of one element; 0 when elements are not written as one block on this runtime.</summary>
        int ElementSize { get; }

        /// <summary>
        /// Writes the length and the elements of <paramref name="array"/> (a <c>T[]</c>, not null) into
        /// <paramref name="buffer"/>: one capacity check, the length and one block copy.
        /// </summary>
        void Write(NinoArrayBufferWriter buffer, System.Array array);

        /// <summary>Writes the array directly into a caller-provided buffer.</summary>
        void WriteTo(IBufferWriter<byte> buffer, System.Array array);

        /// <summary><paramref name="array"/> (not null) as a new byte array of exactly its size.</summary>
        byte[] Serialize(System.Array array);

        /// <summary>
        /// Reads a whole payload holding one array, into <paramref name="existing"/> (null or a <c>T[]</c>) where
        /// possible. Code specialized for the element type, with its own reader.
        /// </summary>
        System.Array Read(System.ReadOnlySpan<byte> data, System.Array existing);
    }

    /// <summary>
    /// Whether <typeparamref name="TFormatter"/> is an <see cref="INinoRawFormatter"/> of <typeparamref name="T"/>. A
    /// <c>static readonly</c> field, so the JIT folds checks against it; AOT runtimes read a field instead of boxing the
    /// formatter. Read it through <see cref="RawFormatter.Is{T,TFormatter}"/>.
    /// </summary>
    internal static class RawFormatter<T, TFormatter> where TFormatter : INinoFormatter<T>
    {
        public static readonly bool Is = Compute();

        // Raw only where the value's memory is its wire layout (see NinoLayout). T is the formatter's own type, so no
        // reflection over its interfaces is needed (which trimming and NativeAOT may not keep).
        private static bool Compute() =>
            typeof(INinoRawFormatter).IsAssignableFrom(typeof(TFormatter)) &&
            NinoLayout.Get(typeof(T), Unsafe.SizeOf<T>()).Canonical;
    }

    internal static class RawFormatter
    {
        /// <summary>
        /// Whether <typeparamref name="TFormatter"/> writes values of <typeparamref name="T"/> as their raw memory.
        /// Folds to a constant once inlined: raw values are unmanaged, so for a reference type (whose code is shared,
        /// where a static field of a class generic over it would be a runtime lookup) it is false without a lookup.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Is<T, TFormatter>() where TFormatter : INinoFormatter<T> =>
            !RuntimeHelpers.IsReferenceOrContainsReferences<T>() && RawFormatter<T, TFormatter>.Is;
    }

    /// <summary>
    /// The members every wrapper of a struct formatter shares: calls through to <typeparamref name="TFormatter"/>
    /// (inlined), and the top-level calls with a writer or reader of their own (<see cref="NinoDirect"/>). Not sealed,
    /// but its leaves are: <see cref="NinoTopFormatter{T}.Formatter"/> holds one of them, whose exact class Tier1 code
    /// knows, so these calls are devirtualized there as they were when each leaf declared them.
    /// </summary>
    internal abstract class StructFormatterBase<T, TFormatter> : NinoFormatterBase<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        internal sealed override Type StructType => typeof(TFormatter);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sealed override void Serialize(T value, ref Writer writer) =>
            default(TFormatter).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sealed override void Deserialize(ref Reader reader, ref T value) =>
            default(TFormatter).Deserialize(ref reader, ref value);

        internal override void Write(T value, NinoArrayBufferWriter buffer) =>
            NinoDirect.Write<T, TFormatter>(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => NinoDirect.Deserialize<T, TFormatter>(data);
    }

    /// <summary>Wraps a struct formatter as a <see cref="NinoFormatterBase{T}"/>.</summary>
    internal sealed class StructFormatter<T, TFormatter> : StructFormatterBase<T, TFormatter>
        where TFormatter : struct, INinoFormatter<T>
    {
        internal override NinoFormatterBase<T> Counted() => new NestingFormatter<T, TFormatter>();

        internal override bool IsRaw => RawFormatter.Is<T, TFormatter>();

        internal override INinoRawArrayFormatter RawArray =>
            default(TFormatter) is INinoRawArrayFormatter raw && raw.ElementSize != 0 ? raw : null;

        internal override bool Sized => NinoKind<TFormatter>.SizedWrite;

        internal override byte[] SerializeSized(T value) => NinoSized.Serialize<T, TFormatter>(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override void WriteExternal(T value, IBufferWriter<byte> buffer)
        {
            if (NinoKind<TFormatter>.ExternalWrite && default(TFormatter).TryWriteExternal(value, buffer))
                return;
            base.WriteExternal(value, buffer);
        }
    }

    /// <summary>
    /// The formatter of a type that can hold itself through collections (<see cref="NinoNesting"/>): each read
    /// counts toward <see cref="NinoSettings.MaxDepth"/>, as a class body does, so data nested deeper throws instead of
    /// overflowing the stack. The resolver reads a type reached again inside its own formatter through
    /// <see cref="CachedFormatter{T}"/>, so each level of such data passes through here, and <see cref="StructType"/>
    /// is null, so it does not inline the formatter as an element's. Generated code counts each level in a struct of
    /// its own instead (the generator's nesting formatter: <c>EnterStruct</c>, then this formatter's
    /// <typeparamref name="TFormatter"/>, which names that struct for its elements), a direct call. Writes count
    /// too (<see cref="Writer.EnterStruct"/>): an object graph nested too deep, or a collection that holds itself,
    /// throws <see cref="InvalidOperationException"/> instead of overflowing the stack.
    /// </summary>
    internal sealed class NestingFormatter<T, TFormatter> : NinoFormatterBase<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public override void Serialize(T value, ref Writer writer)
        {
            writer.EnterStruct(typeof(T));
            default(TFormatter).Serialize(value, ref writer);
            writer.ExitStruct();
        }

        public override void Deserialize(ref Reader reader, ref T value)
        {
            reader.EnterStruct(typeof(T));
            default(TFormatter).Deserialize(ref reader, ref value);
            reader.ExitStruct();
        }

        internal override void Write(T value, NinoArrayBufferWriter buffer) =>
            NinoDirect.Write<T, TFormatter>(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => NinoDirect.Deserialize<T, TFormatter>(data);
    }

    /// <summary>
    /// <see cref="StructFormatter{T,TFormatter}"/> of a formatter of fixed size, registered by generated code
    /// (<see cref="NinoTypes.RegisterFixed{T,TFormatter}"/>): the regular byte array call writes into an
    /// array of exactly that size too.
    /// </summary>
    internal sealed class FixedStructFormatter<T, TFormatter> : StructFormatterBase<T, TFormatter>
        where TFormatter : struct, INinoFormatter<T>
    {
        internal override int FixedSize => default(TFormatter).FixedSize;

        internal override byte[] SerializeFixed(T value) => NinoDirect.WriteFixed<T, TFormatter>(value);

        internal override void WriteExternal(T value, IBufferWriter<byte> buffer)
        {
            int size = default(TFormatter).FixedSize;
            if (size < 0)
            {
                base.WriteExternal(value, buffer);
                return;
            }

            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            bool absent = value == null;
            if (absent)
                size = 1;
            var span = buffer.GetSpan(size);
            if (span.Length < size)
                NinoThrow.EndOfData();
            ref byte destination = ref MemoryMarshal.GetReference(span);
            if (absent)
                destination = NinoWire.Null;
            else
                default(TFormatter).WriteFixed(in value, ref destination);
            buffer.Advance(size);
        }
    }

    /// <summary>
    /// <see cref="StructFormatter{T,TFormatter}"/> of a generated formatter with a <c>__Top</c> twin
    /// (<typeparamref name="TTop"/>), registered by generated code
    /// (<see cref="NinoTypes.RegisterTop{T,TFormatter,TTop}"/>): the regular top-level calls write through
    /// the twin, with the writer it creates (see <see cref="INinoTopFormatter{T}"/>); everything else through
    /// <typeparamref name="TFormatter"/>.
    /// </summary>
    internal sealed class TopStructFormatter<T, TFormatter, TTop> : StructFormatterBase<T, TFormatter>
        where TFormatter : struct, INinoFormatter<T>
        where TTop : struct, INinoTopFormatter<T>
    {
        internal override void Write(T value, NinoArrayBufferWriter buffer) => default(TTop).Write(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => default(TTop).Read(data);
    }

    /// <summary>
    /// The formatter Nino uses for <typeparamref name="T"/> (registered by generated code, declared with
    /// <c>[assembly: NinoFormatter]</c>, or built on first use), as a struct: it writes what
    /// <c>NinoSerializer.Serialize(value, ref writer)</c> writes. Pass it as the element formatter of a built-in
    /// formatter for an element type whose formatter you cannot name (a <c>[NinoType]</c>); generated code uses it
    /// for type parameters and types whose formatter is only known at run time. Tier1 code calls (and inlines) the
    /// formatter directly where <typeparamref name="T"/> is exact (<see cref="NinoTopFormatter{T}"/>); shared generic
    /// code and AOT runtimes make one virtual call.
    /// </summary>
    public struct CachedFormatter<T> : INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer) => NinoTopFormatter<T>.Formatter.Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref T value) =>
            NinoTopFormatter<T>.Formatter.Deserialize(ref reader, ref value);
    }
}
