using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
    /// Formatter for the body of a <c>[NinoType]</c>: the members, without the null/type header.
    /// Implemented by generated formatters and used for polymorphic dispatch.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoBodyFormatter<T> : INinoFormatter<T>
    {
        /// <summary>The type id written in front of the body when the declared type is not sealed.</summary>
        int TypeId { get; }

        /// <summary>Writes the members of a non-null <paramref name="value"/>.</summary>
        void WriteBody(T value, ref Writer writer);

        /// <summary>
        /// Reads the members into <paramref name="value"/>, which is either <c>null</c> or an instance of exactly
        /// <typeparamref name="T"/>. Creates the instance when needed.
        /// </summary>
        void ReadBody(ref T value, ref Reader reader);

        /// <summary>
        /// What the body does, as <see cref="NinoBody"/> flags (a constant): <see cref="NinoBody.Nests"/> and
        /// <see cref="NinoBody.Deferrable"/>. One member for both: generated formatters implement it for every type.
        /// </summary>
        int Traits { get; }

        /// <summary>Creates the empty instance a deferred body is read into later.</summary>
        T Create();
    }

    /// <summary>The flags of <see cref="INinoBodyFormatter{T}.Traits"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoBody
    {
        /// <summary>
        /// The body may hold class bodies (through its members' formatters). A body that cannot skips the depth count:
        /// nothing nested in it would see the count, so the bytes are the same either way.
        /// </summary>
        public const int Nests = 1;

        /// <summary>
        /// A body nested too deep to read in place can be read later, into an instance created up front (see
        /// <see cref="INinoBodyFormatter{T}.Create"/>): not for structs, nor for classes built by their constructor or
        /// compared by value.
        /// </summary>
        public const int Deferrable = 2;
    }

    /// <summary>
    /// Generated formatters of classes whose body is one block of raw members (<see cref="BodySize"/> bytes, a
    /// JIT-time constant). The type id or null flag in front of it is written into the same reservation (one capacity
    /// check rather than two: see <see cref="NinoObject"/>'s <c>...Raw</c> methods).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoRawBodyFormatter<T> : INinoBodyFormatter<T>
    {
        /// <summary>The bytes of every body.</summary>
        int BodySize { get; }

        /// <summary>
        /// Writes the body of <paramref name="value"/> (not null) into the <see cref="BodySize"/> bytes at
        /// <paramref name="offset"/> from <paramref name="destination"/>: the bytes of
        /// <see cref="INinoBodyFormatter{T}.WriteBody"/>. The offset (a constant) is added to each member's, so the
        /// stores address the block directly rather than through a second reference the JIT computes and keeps.
        /// </summary>
        void WriteRawBody(T value, ref byte destination, int offset);
    }

    /// <summary>
    /// Generated <c>__Top</c> formatters: the top-level calls that create their own writer hand it the buffer
    /// instead, so the writer is created in the method that writes the value. Passed by address to a method that is
    /// not inlined (an out-of-line body), a writer lives in memory: zeroed and stored by the caller, loaded and stored
    /// back by the callee, and each write a load and a store of its cursor. Created in that method, the JIT keeps it
    /// in registers, and knows its depth, regions and context start empty.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoTopFormatter<T> : INinoFormatter<T>
    {
        /// <summary>
        /// Writes <paramref name="value"/> as a whole payload at the end of <paramref name="buffer"/> with a writer of
        /// its own, then commits it (the writer's <see cref="Writer.Flush"/> and <see cref="Writer.ReleaseReferences"/>).
        /// The same bytes as <see cref="INinoFormatter{T}.Serialize"/> with a new writer.
        /// </summary>
        void Write(T value, NinoArrayBufferWriter buffer);

        /// <summary>
        /// Reads a whole payload from <paramref name="data"/> into a new value with a reader of its own, then ends
        /// the call's reference tracking (the reader's <see cref="Reader.ReleaseReferences"/>): what
        /// <see cref="INinoFormatter{T}.Deserialize"/> into a null slot with a new reader reads.
        /// </summary>
        T Read(ReadOnlySpan<byte> data);
    }

    /// <summary>
    /// Generated formatters of types whose values all take the same number of bytes (<see cref="FixedSize"/>, a
    /// non-null one for a sealed class): structs and sealed classes whose members are all unmanaged values or such
    /// structs. <see cref="NinoSerializer.Serialize{T}(T)"/> allocates the result at that size and writes the value
    /// straight into it (see <see cref="NinoDirect.SerializeFixed{T,TFormatter}"/>).
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public interface INinoFixedSizeFormatter<T> : INinoFormatter<T>
    {
        /// <summary>
        /// The bytes <see cref="INinoFormatter{T}.Serialize"/> writes for any value (any non-null one: a null
        /// sealed class is its 1-byte null flag). A JIT-time constant.
        /// </summary>
        int FixedSize { get; }

        /// <summary>
        /// Writes <paramref name="value"/> (not null) into the <see cref="FixedSize"/> bytes at
        /// <paramref name="destination"/>: the same bytes as <see cref="INinoFormatter{T}.Serialize"/>.
        /// </summary>
        void WriteFixed(T value, ref byte destination);
    }

    /// <summary>
    /// The formatter of <typeparamref name="T"/> held by <see cref="NinoFormatterCache{T}"/>, for code that only finds
    /// it at runtime (top-level calls, <see cref="CachedFormatter{T}"/>): a <see cref="StructFormatter{T,TFormatter}"/>
    /// around a struct formatter, called virtually. Every formatter users write is a struct
    /// <see cref="INinoFormatter{T}"/>; this class is only the dispatch carrier and is not part of the API.
    /// </summary>
    internal abstract class NinoFormatter<T> : INinoFormatter<T>
    {
        public abstract void Serialize(T value, ref Writer writer);

        public abstract void Deserialize(ref T value, ref Reader reader);

        /// <summary>
        /// Writes <paramref name="value"/> as a whole payload into <paramref name="buffer"/> (the top-level calls): the
        /// writer is created by the formatter's class, specialized for the struct formatter, so the JIT keeps it in
        /// registers there rather than in the memory of a caller that passes its address (see
        /// <see cref="INinoTopFormatter{T}"/>).
        /// </summary>
        internal virtual void Write(T value, NinoArrayBufferWriter buffer)
        {
            var writer = new Writer(buffer);
            Serialize(value, ref writer);
            writer.Flush();
            writer.ReleaseReferences();
        }

        /// <summary>
        /// Reads a whole payload into a new value (the top-level calls): the reader is created by the formatter's
        /// class, for the reasons of <see cref="Write"/>.
        /// </summary>
        internal virtual T Read(ReadOnlySpan<byte> data)
        {
            var reader = new Reader(data);
            T value = default;
            Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
            return value;
        }

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
    /// Marker for struct formatters whose output is exactly the in-memory bytes of the value
    /// (<c>Unsafe.SizeOf&lt;T&gt;()</c> bytes). The top-level APIs copy such values directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoRawFormatter
    {
    }

    /// <summary>
    /// Whether <typeparamref name="TFormatter"/> is an <see cref="INinoRawFormatter"/>. A <c>static readonly</c>
    /// field, so the JIT folds checks against it; AOT runtimes read a field instead of boxing the formatter.
    /// </summary>
    internal static class RawFormatter<TFormatter>
    {
        public static readonly bool Is = Compute();

        // Raw only where the value's memory is its wire layout (see NinoLayout).
        private static bool Compute()
        {
            if (!typeof(INinoRawFormatter).IsAssignableFrom(typeof(TFormatter)))
                return false;
            foreach (var i in typeof(TFormatter).GetInterfaces())
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INinoFormatter<>))
                    return NinoLayout.Get(i.GetGenericArguments()[0], -1).Canonical;
            return true;
        }
    }

    /// <summary>Wraps a struct formatter as a <see cref="NinoFormatter{T}"/>.</summary>
    internal sealed class StructFormatter<T, TFormatter> : NinoFormatter<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Serialize(T value, ref Writer writer) => default(TFormatter).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Deserialize(ref T value, ref Reader reader) =>
            default(TFormatter).Deserialize(ref value, ref reader);

        internal override void Write(T value, NinoArrayBufferWriter buffer) =>
            NinoDirect.Write<T, TFormatter>(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => NinoDirect.Deserialize<T, TFormatter>(data);

        internal override bool IsRaw => RawFormatter<TFormatter>.Is;

        internal override INinoRawArrayFormatter RawArray =>
            default(TFormatter) is INinoRawArrayFormatter raw && raw.ElementSize != 0 ? raw : null;
    }

    /// <summary>
    /// <see cref="StructFormatter{T,TFormatter}"/> of a formatter of fixed size, registered by generated code
    /// (<see cref="NinoFormatterCache{T}.TryRegisterFixed{TFormatter}"/>): the regular byte array call writes into an
    /// array of exactly that size too.
    /// </summary>
    internal sealed class FixedStructFormatter<T, TFormatter> : NinoFormatter<T>
        where TFormatter : struct, INinoFixedSizeFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Serialize(T value, ref Writer writer) => default(TFormatter).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Deserialize(ref T value, ref Reader reader) =>
            default(TFormatter).Deserialize(ref value, ref reader);

        internal override void Write(T value, NinoArrayBufferWriter buffer) =>
            NinoDirect.Write<T, TFormatter>(value, buffer);

        internal override T Read(ReadOnlySpan<byte> data) => NinoDirect.Deserialize<T, TFormatter>(data);

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
    internal sealed class TopStructFormatter<T, TFormatter, TTop> : NinoFormatter<T>
        where TFormatter : struct, INinoFormatter<T>
        where TTop : struct, INinoTopFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Serialize(T value, ref Writer writer) => default(TFormatter).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Deserialize(ref T value, ref Reader reader) =>
            default(TFormatter).Deserialize(ref value, ref reader);

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
