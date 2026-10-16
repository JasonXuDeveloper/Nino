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
        /// Whether a body nested too deep to read in place can be read later, into an instance created up front
        /// (see <see cref="Create"/>): false for structs and for classes built by their constructor or compared by
        /// value.
        /// </summary>
        bool Deferrable { get; }

        /// <summary>
        /// Whether the body may hold class bodies (through its members' formatters). A body that cannot skips the
        /// depth count: nothing nested in it would see the count, so the bytes are the same either way.
        /// </summary>
        bool Nests { get; }

        /// <summary>Creates the empty instance a deferred body is read into later.</summary>
        T Create();
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

        /// <summary>Whether the formatter writes exactly the in-memory bytes of <typeparamref name="T"/>.</summary>
        internal virtual bool IsRaw => false;

        /// <summary>Set when <typeparamref name="T"/> is an array written as its length and one raw block.</summary>
        internal virtual INinoRawArrayFormatter RawArray => null;
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

        internal override bool IsRaw => RawFormatter<TFormatter>.Is;

        internal override INinoRawArrayFormatter RawArray =>
            default(TFormatter) is INinoRawArrayFormatter raw && raw.ElementSize != 0 ? raw : null;
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
