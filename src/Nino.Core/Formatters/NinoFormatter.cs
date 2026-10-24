using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Generated;

namespace Nino.Core
{
    /// <summary>
    /// Serializes and deserializes values of <typeparamref name="T"/>.
    /// <para>
    /// Implement this on a <c>struct</c> to get a zero-cost formatter: generic code constrained with
    /// <c>where TFormatter : struct, INinoFormatter&lt;T&gt;</c> calls it as <c>default(TFormatter).Serialize(...)</c>,
    /// which the JIT/AOT compiler specializes and inlines.
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
        void Deserialize(ref T value, ref Reader reader);
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

    /// <summary>Top-level serialization of a value whose size its formatter knows (<see cref="INinoSizedFormatter{T}"/>).</summary>
    internal static class NinoSized
    {
        /// <summary>Whether <typeparamref name="TFormatter"/> may know sizes: a JIT-time constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Is<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            default(TFormatter) is INinoSizedFormatter<T>;

        /// <summary>
        /// <paramref name="value"/> as a new array of exactly its bytes, written straight into it; null when its size is
        /// not known up front. The same bytes as a writer writes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            int size = ((INinoSizedFormatter<T>)default(TFormatter)).Size(value);
            if (size < 0)
                return null;
            // Bytes are written little-endian, as Writer does (which rejects other hosts).
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            // Zeroed, not uninitialized: the clearing brings the lines into the cache (as a fast string store), where
            // vector stores into fresh lines first read each one; 1,000 (int, long) took 1.3 times as long.
            var bytes = new byte[size];
            ((INinoSizedFormatter<T>)default(TFormatter)).WriteSized(value, ref NinoUnsafe.GetArrayDataReference(bytes));
            return bytes;
        }
    }

    /// <summary>The formatter of a type, for code that only has a <see cref="Type"/>.</summary>
    internal abstract class NinoFormatter
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
    internal abstract class NinoFormatter<T> : NinoFormatter, INinoFormatter<T>
    {
        internal sealed override BoxedFormatter CreateBoxed() => new BoxedFormatter<T>();

        public abstract void Serialize(T value, ref Writer writer);

        public abstract void Deserialize(ref T value, ref Reader reader);

        /// <summary>
        /// Writes <paramref name="value"/> as a whole payload into <paramref name="buffer"/> (the top-level calls): the
        /// writer is created by the formatter's class, specialized for the struct formatter, so the JIT keeps it in
        /// registers there rather than in the memory of a caller that passes its address (see
        /// <see cref="INinoTopFormatter{T}"/>).
        /// </summary>
        internal abstract void Write(T value, NinoArrayBufferWriter buffer);

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
        /// The size of every (non-null) value when it is fixed (<see cref="INinoFixedSizeFormatter{T}"/>), else -1.
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
        internal virtual NinoFormatter<T> Counted() => this;
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
            NinoLayout.Get(typeof(T), -1).Canonical;
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
    internal abstract class StructFormatterBase<T, TFormatter> : NinoFormatter<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        internal sealed override Type StructType => typeof(TFormatter);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sealed override void Serialize(T value, ref Writer writer) =>
            default(TFormatter).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sealed override void Deserialize(ref T value, ref Reader reader) =>
            default(TFormatter).Deserialize(ref value, ref reader);

        internal override void Write(T value, NinoArrayBufferWriter buffer) =>
            NinoDirect.Write<T, TFormatter>(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => NinoDirect.Deserialize<T, TFormatter>(data);
    }

    /// <summary>Wraps a struct formatter as a <see cref="NinoFormatter{T}"/>.</summary>
    internal sealed class StructFormatter<T, TFormatter> : StructFormatterBase<T, TFormatter>
        where TFormatter : struct, INinoFormatter<T>
    {
        internal override NinoFormatter<T> Counted() => new NestingFormatter<T, TFormatter>();

        internal override bool IsRaw => RawFormatter.Is<T, TFormatter>();

        internal override INinoRawArrayFormatter RawArray =>
            default(TFormatter) is INinoRawArrayFormatter raw && raw.ElementSize != 0 ? raw : null;

        internal override bool Sized => NinoSized.Is<T, TFormatter>();

        internal override byte[] SerializeSized(T value) => NinoSized.Serialize<T, TFormatter>(value);
    }

    /// <summary>
    /// The formatter of a type that can hold itself through collections (<see cref="NinoNesting"/>): each read
    /// counts toward <see cref="NinoSettings.MaxDepth"/>, as a class body does, so data nested deeper throws instead of
    /// overflowing the stack. Each level of such data passes through here: a type reached again inside its own
    /// formatter is read through <see cref="CachedFormatter{T}"/> (the generator's and the resolver's rule), and
    /// <see cref="StructType"/> is null, so the resolver does not inline the formatter as an element's. Writes count
    /// too (<see cref="Writer.EnterStruct"/>): an object graph nested too deep, or a collection that holds itself,
    /// throws <see cref="InvalidOperationException"/> instead of overflowing the stack.
    /// </summary>
    internal sealed class NestingFormatter<T, TFormatter> : NinoFormatter<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public override void Serialize(T value, ref Writer writer)
        {
            writer.EnterStruct(typeof(T));
            default(TFormatter).Serialize(value, ref writer);
            writer.ExitStruct();
        }

        public override void Deserialize(ref T value, ref Reader reader)
        {
            reader.EnterStruct(typeof(T));
            default(TFormatter).Deserialize(ref value, ref reader);
            reader.ExitStruct();
        }

        internal override void Write(T value, NinoArrayBufferWriter buffer) =>
            NinoDirect.Write<T, TFormatter>(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => NinoDirect.Deserialize<T, TFormatter>(data);
    }

    /// <summary>
    /// <see cref="StructFormatter{T,TFormatter}"/> of a formatter of fixed size, registered by generated code
    /// (<see cref="NinoFormatterCache{T}.TryRegisterFixed{TFormatter}"/>): the regular byte array call writes into an
    /// array of exactly that size too.
    /// </summary>
    internal sealed class FixedStructFormatter<T, TFormatter> : StructFormatterBase<T, TFormatter>
        where TFormatter : struct, INinoFixedSizeFormatter<T>
    {
        internal override int FixedSize => default(TFormatter).FixedSize;

        internal override byte[] SerializeFixed(T value) => NinoDirect.WriteFixed<T, TFormatter>(value);
    }

    /// <summary>
    /// <see cref="StructFormatter{T,TFormatter}"/> of a generated formatter with a <c>__Top</c> twin
    /// (<typeparamref name="TTop"/>), registered by generated code
    /// (<see cref="NinoFormatterCache{T}.TryRegisterTop{TFormatter,TTop}"/>): the regular top-level calls write through
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
    /// Forwards to <see cref="NinoFormatterCache{T}.Instance"/>. Used for generic type parameters and types whose
    /// formatter is only known at runtime. Costs one virtual call.
    /// </summary>
    public struct CachedFormatter<T> : INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer) => NinoTopFormatter<T>.Get.Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T value, ref Reader reader) =>
            NinoTopFormatter<T>.Get.Deserialize(ref value, ref reader);
    }
}
