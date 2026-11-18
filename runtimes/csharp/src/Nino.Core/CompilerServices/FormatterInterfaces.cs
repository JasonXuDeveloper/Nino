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
        void ReadBody(ref Reader reader, ref T value);

        /// <summary>
        /// What the body does, as <see cref="NinoBodyTraits"/> flags (a constant):
        /// <see cref="NinoBodyTraits.Nests"/> and <see cref="NinoBodyTraits.Deferrable"/>. One member for both:
        /// generated formatters implement it for every type.
        /// </summary>
        int Traits { get; }

        /// <summary>Creates the empty instance a deferred body is read into later.</summary>
        T Create();
    }

    /// <summary>
    /// Generated formatters of polymorphic bases: the elements of an array or a list of the base written in one loop
    /// (the dispatch inline, the writer in a local), the bytes of <see cref="INinoFormatter{T}.Serialize"/> for each.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoElementsFormatter<T>
    {
        /// <summary>Writes every element of <paramref name="values"/> (not empty) as Serialize writes one.</summary>
        void WriteElements(ReadOnlySpan<T> values, ref Writer writer);
    }

    /// <summary>
    /// Generated formatters of polymorphic bases: the pairs of a <c>Dictionary&lt;TKey, T&gt;</c> (T the base, the
    /// dictionary not empty, its count already written) in one loop (the dispatch inline, the writer in a local), each
    /// value as <see cref="INinoFormatter{T}.Serialize"/> writes it. Neither the interface nor its methods are generic:
    /// a test for it folds where the dictionary's loop is shared code over a class T, and a call through it on the
    /// formatter is direct, where a generic method is looked up per call (the JIT does not devirtualize those).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoPairsFormatter
    {
        /// <summary>
        /// Pairs whose keys are 4 bytes written as they lie in memory (a <c>Dictionary&lt;int, T&gt;</c>, or one whose
        /// keys are another primitive or enum of that size, read as an <c>int</c>).
        /// </summary>
        void WritePairs32(object dictionary, ref Writer writer);

        /// <summary>As <see cref="WritePairs32"/>, for keys of 8 bytes (read as a <c>long</c>).</summary>
        void WritePairs64(object dictionary, ref Writer writer);

        /// <summary>Pairs whose keys are strings (<c>Dictionary&lt;string, T&gt;</c>), in the default encoding.</summary>
        void WriteStringPairs(object dictionary, ref Writer writer);
    }

    /// <summary>The flags of <see cref="INinoBodyFormatter{T}.Traits"/>.</summary>
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
        /// Prefetches the objects the reference members of <paramref name="value"/> (an element, never null) point to,
        /// through <see cref="NinoPrefetched.At"/>, and returns the sum of what that returned. Called once the
        /// element's own object was prefetched, by Nino.Core's loop over a run of elements.
        /// </summary>
        int PrefetchMembers(object value);
    }

    /// <summary>Prefetches the collection's objects without walking their members.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoObjectPrefetchFormatter
    {
        /// <summary>The bytes of one element's object.</summary>
        int PrefetchBytes { get; }
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
