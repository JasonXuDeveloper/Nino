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
    /// Serializes and deserializes values of <typeparamref name="T"/>.
    /// <para>
    /// Implement this on a <c>struct</c> to get a zero-cost formatter: generic code constrained with
    /// <c>where TFormatter : struct, INinoFormatter&lt;T&gt;</c> calls it as <c>default(TFormatter).Serialize(...)</c>,
    /// which the JIT/AOT compiler specializes and inlines.
    /// </para>
    /// <para>
    /// <see cref="Serialize"/> and <see cref="Deserialize"/> are all a formatter has to implement. The other members
    /// are capabilities with defaults that report none: values of a fixed size (<see cref="FixedSize"/>,
    /// <see cref="WriteFixed"/>) and blocks a collection moves as one run (<see cref="BlockMap"/>,
    /// <see cref="ReadBlock"/>), implemented by generated formatters and Nino.Core's own where they apply.
    /// </para>
    /// <para>
    /// The rule that keeps the defaults free: a call to a default a struct does not override boxes the struct
    /// (CoreCLR, Mono, IL2CPP). So only the queries (<see cref="FixedSize"/>, <see cref="BlockMap"/> and the slot
    /// stride) are ever called on a formatter that may lack a capability, once per type into a
    /// <c>static readonly</c> field the JIT folds; the hot members are called only where that field says the
    /// formatter has them, and a formatter that has them implements them, so each is a direct, constrained call.
    /// </para>
    /// </summary>
    public interface INinoFormatter<T>
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
        void WriteFixed(in T value, ref byte destination) => NinoThrow.MissingCapability(GetType(), nameof(WriteFixed));

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
        void ReadBlock(ref T value, ref byte source) => NinoThrow.MissingCapability(GetType(), nameof(ReadBlock));

        /// <summary>
        /// The bytes of a slot, or -1 (the default): values whose element in a collection is a slot of a fixed size,
        /// where a single value (a member, a tuple's item, a dictionary's key or value) is written another way (a raw
        /// <c>T?</c>: <see cref="UnmanagedNullableFormatter{T}"/>). Every sequence moves them as one block
        /// (<see cref="NinoElements.BlockStride{T,TFormatter}"/>), and writes or reads one that it holds apart through
        /// <see cref="NinoElements.WriteOne{T,TFormatter}"/> and <see cref="NinoElements.ReadOne{T,TFormatter}"/>,
        /// never through <see cref="Serialize"/>. Nino.Core's alone.
        /// </summary>
        internal int SlotStride => -1;

        /// <summary>Writes <paramref name="values"/> (not empty) into the slots at <paramref name="destination"/>.</summary>
        internal void WriteSlots(ref byte destination, ReadOnlySpan<T> values) =>
            NinoThrow.MissingCapability(GetType(), nameof(WriteSlots));

        /// <summary>
        /// Reads <paramref name="values"/> from their slots, which the caller checked are there, and moves the reader
        /// past them. Overwrites every value; rejects a presence byte other than 0 or 1.
        /// </summary>
        internal void ReadSlots(Span<T> values, ref Reader reader) =>
            NinoThrow.MissingCapability(GetType(), nameof(ReadSlots));

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
        internal void CheckSlots(ref byte slots, int count) => NinoThrow.MissingCapability(GetType(), nameof(CheckSlots));
    }

    /// <summary>
    /// Formatters that can tell how many bytes a value takes before writing it, cheaply: collections whose elements
    /// are written as one block (a count times a fixed size). The top-level calls then allocate the result at that size
    /// and write the value straight into it, without the scratch buffer and the copy out of it (<see cref="NinoSized"/>).
    /// </summary>
    internal interface INinoSizedFormatter<T>
    {
        /// <summary>
        /// The bytes <paramref name="value"/> is written as, or -1 when they are not known up front (for a given element
        /// type, a JIT-time constant decides which).
        /// </summary>
        int Size(T value);

        /// <summary>Writes <paramref name="value"/> into the <see cref="Size"/> bytes at <paramref name="destination"/>.</summary>
        void WriteSized(T value, ref byte destination);
    }

    /// <summary>A formatter that can fill a caller-provided buffer without a scratch copy.</summary>
    internal interface INinoExternalFormatter<T>
    {
        bool TryWriteExternal(T value, IBufferWriter<byte> buffer);
    }

    /// <summary>Top-level serialization of a value whose size its formatter knows (<see cref="INinoSizedFormatter{T}"/>).</summary>
    internal static class NinoSized
    {
        /// <summary>Whether <typeparamref name="TFormatter"/> may know sizes: a JIT-time constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Is<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
#if NET8_0_OR_GREATER
            default(TFormatter) is INinoSizedFormatter<T>;
#else
            Of<T, TFormatter>.Formatter != null;

        /// <summary>
        /// The formatter boxed once: IL2CPP (its C++ shows it) and runtimes without RyuJIT's box removal box
        /// <c>default(TFormatter)</c> at every cast to an interface, a GC allocation per top-level call, where RyuJIT
        /// calls the struct directly.
        /// </summary>
        private static class Of<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
        {
            public static readonly INinoSizedFormatter<T> Formatter = default(TFormatter) as INinoSizedFormatter<T>;
        }
#endif

        /// <summary>
        /// <paramref name="value"/> as a new array of exactly its bytes, written straight into it; null when its size is
        /// not known up front. The same bytes as a writer writes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
#if NET8_0_OR_GREATER
            int size = ((INinoSizedFormatter<T>)default(TFormatter)).Size(value);
#else
            int size = Of<T, TFormatter>.Formatter.Size(value);
#endif
            if (size < 0)
                return null;
            // Bytes are written little-endian, as Writer does (which rejects other hosts).
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            // Zeroed, not uninitialized: the clearing brings the lines into the cache (as a fast string store), where
            // vector stores into fresh lines first read each one; 1,000 (int, long) took 1.3 times as long.
            var bytes = new byte[size];
#if NET8_0_OR_GREATER
            ((INinoSizedFormatter<T>)default(TFormatter)).WriteSized(value, ref NinoUnsafe.GetArrayDataReference(bytes));
#else
            Of<T, TFormatter>.Formatter.WriteSized(value, ref NinoUnsafe.GetArrayDataReference(bytes));
#endif
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
        /// Whether <see cref="SerializeSized"/> may apply (<see cref="INinoSizedFormatter{T}"/>). Read through
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
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() && RawFormatter<T, TFormatter>.Is;
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

        internal override bool Sized => NinoSized.Is<T, TFormatter>();

        internal override byte[] SerializeSized(T value) => NinoSized.Serialize<T, TFormatter>(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override void WriteExternal(T value, IBufferWriter<byte> buffer)
        {
#if NET8_0_OR_GREATER
            if (default(TFormatter) is INinoExternalFormatter<T> external && external.TryWriteExternal(value, buffer))
#else
            if (External.Formatter is { } external && external.TryWriteExternal(value, buffer))
#endif
                return;
            base.WriteExternal(value, buffer);
        }

#if !NET8_0_OR_GREATER
        private static class External
        {
            // Keep the compatibility runtimes from boxing on each write.
            internal static readonly INinoExternalFormatter<T> Formatter = default(TFormatter) as INinoExternalFormatter<T>;
        }
#endif
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
