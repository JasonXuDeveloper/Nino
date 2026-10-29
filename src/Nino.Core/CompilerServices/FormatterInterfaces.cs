using System;
using System.ComponentModel;

namespace Nino.Core.CompilerServices
{
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
    /// structs. <see cref="NinoSerializer.Serialize{T}(in T)"/> allocates the result at that size and writes the value
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
    /// Formatters whose values take <see cref="BlockSize"/> bytes each when it is not -1 (a JIT-time constant): value
    /// tuples and pairs of raw items, and generated <c>[NinoType]</c> structs of a fixed size that are not raw. A
    /// collection of them reserves or bounds-checks its whole block once and writes or reads each value at its position
    /// with no check of its own; where <see cref="BlockMap"/> says where each byte of a block lies in a value's memory,
    /// several values at a time by vector shuffles and permutes (<c>NinoBlock</c>).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoBlockFormatter<T>
    {
        /// <summary>The bytes of every value, or -1 when values are not written as blocks.</summary>
        int BlockSize { get; }

        /// <summary>
        /// Where the bytes of a block lie in a value's memory (<see cref="NinoBlockMap"/>), or null (or a map that
        /// failed) when that is not known, or values hold bytes no block byte gives them (padding inside an item):
        /// values are then moved one at a time, by <see cref="WriteBlock"/> and <see cref="ReadBlock"/>. Called once.
        /// </summary>
        NinoBlockMap BlockMap();

        /// <summary>Writes <paramref name="value"/> into the <see cref="BlockSize"/> bytes at <paramref name="destination"/>.</summary>
        void WriteBlock(ref byte destination, in T value);

        /// <summary>
        /// Reads the value in the <see cref="BlockSize"/> bytes at <paramref name="source"/>, with the checks a read of
        /// it does otherwise (a <c>bool</c> other than 0 or 1 is invalid data).
        /// </summary>
        void ReadBlock(ref byte source, ref T value);
    }

    /// <summary>
    /// Generated formatters of non-generic classes whose collections prefetch: an array or a list of their objects
    /// larger than a quarter of the last-level cache (<see cref="PrefetchBytes"/> each), whose first objects are not
    /// in allocation order, written by a top-level call or read into its existing objects by one
    /// (<c>NinoDirect.SerializePrefetched</c>, <c>DeserializePrefetched</c>; generated only for a project built with
    /// <c>NinoPrefetchLargeCollections</c>), is walked in runs of 16 elements, each run calling an out-of-line
    /// helper that prefetches the objects of the runs ahead and the objects their members point to (x64 and Arm64 on
    /// .NET 8 and later).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoPrefetchFormatter
    {
        /// <summary>
        /// The bytes an element brings into the cache (a constant): its object, and a line of each object its reference
        /// members point to.
        /// </summary>
        int PrefetchBytes { get; }

        /// <summary>
        /// Prefetches the objects the reference members of the <paramref name="count"/> elements at
        /// <paramref name="first"/> (null ones skipped) point to, through <see cref="NinoPrefetched.At"/>, and returns
        /// the sum of what that returned. Called once the elements' own objects were prefetched.
        /// </summary>
        int PrefetchMembers(ref object first, int count);
    }

    /// <summary>
    /// Marker for struct formatters whose output is exactly the in-memory bytes of the value
    /// (<c>Unsafe.SizeOf&lt;T&gt;()</c> bytes). The top-level APIs copy such values directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoRawFormatter
    {
    }
}
