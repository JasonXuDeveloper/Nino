using System;
using System.ComponentModel;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// Formatter for the body of a <c>[NinoType]</c>: the members, without the null/type header.
    /// Implemented by generated formatters and used for polymorphic dispatch. Each implements
    /// <see cref="INinoFormatter{T}.BodyTraits"/> too, which the dispatch asks per value.
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
        void ReadBody(ref Reader reader, ref T value);

        /// <summary>Creates the empty instance a deferred body is read into later.</summary>
        T Create();
    }

    /// <summary>The flags of <see cref="INinoFormatter{T}.BodyTraits"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoBodyTraits
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
    /// JIT-time constant). The type id or presence byte in front of it is written into the same reservation (one
    /// capacity check rather than two: see <see cref="NinoObject"/>'s <c>...Raw</c> methods).
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
        /// its own, then commits it (the writer's <see cref="Writer.Flush"/> and
        /// <see cref="Writer.ReleaseReferences"/>). The same bytes as <see cref="INinoFormatter{T}.Serialize"/> with a
        /// new writer.
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
    /// Marker for struct formatters whose output is exactly the in-memory bytes of the value
    /// (<c>Unsafe.SizeOf&lt;T&gt;()</c> bytes). The top-level APIs copy such values directly. A marker with no members,
    /// so nothing is ever called through it: it is tested as a type, by reflection once per type
    /// (<c>RawFormatter&lt;T, TFormatter&gt;.Is</c>, the resolver) and on .NET 8+ by a test of the formatter's struct
    /// type parameter that the JIT folds while importing the call, without boxing (<c>NullableFormatter</c>).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoRawFormatter
    {
    }
}
