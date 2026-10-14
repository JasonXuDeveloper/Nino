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
        /// formatters reuse it where possible and pass instances they drop to <see cref="NinoRecycler{T}"/>.
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
    }

    /// <summary>
    /// Base class for class-based formatters, used where the formatter has to be resolved at runtime
    /// (<see cref="NinoFormatterCache{T}"/>) and for custom formatters that need state.
    /// </summary>
    public abstract class NinoFormatter<T> : INinoFormatter<T>
    {
        public abstract void Serialize(T value, ref Writer writer);

        public abstract void Deserialize(ref T value, ref Reader reader);

        /// <summary>Whether the formatter writes exactly the in-memory bytes of <typeparamref name="T"/>.</summary>
        internal virtual bool IsRaw => false;
    }

    /// <summary>
    /// Marker for struct formatters whose output is exactly the in-memory bytes of the value
    /// (<c>Unsafe.SizeOf&lt;T&gt;()</c> bytes). The top-level APIs copy such values directly.
    /// </summary>
    public interface INinoRawFormatter
    {
    }

    /// <summary>Wraps a struct formatter as a <see cref="NinoFormatter{T}"/>.</summary>
    public sealed class StructFormatter<T, TFormatter> : NinoFormatter<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Serialize(T value, ref Writer writer) => default(TFormatter).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Deserialize(ref T value, ref Reader reader) =>
            default(TFormatter).Deserialize(ref value, ref reader);

        internal override bool IsRaw => default(TFormatter) is INinoRawFormatter;
    }

    /// <summary>
    /// Forwards to <see cref="NinoFormatterCache{T}.Instance"/>. Used for generic type parameters and types whose
    /// formatter is only known at runtime. Costs one virtual call.
    /// </summary>
    public struct CachedFormatter<T> : INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer) => NinoFormatterCache<T>.Instance.Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T value, ref Reader reader) =>
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
    }

    /// <summary>
    /// Adapts a custom class formatter (<c>[NinoCustomFormatter(typeof(F))]</c> where <c>F : NinoFormatter&lt;T&gt;</c>)
    /// to the struct formatter pattern. <typeparamref name="TCustom"/> is instantiated once.
    /// </summary>
    public struct CustomFormatter<T, TCustom> : INinoFormatter<T> where TCustom : NinoFormatter<T>, new()
    {
        private static readonly TCustom Instance = new TCustom();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer) => Instance.Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T value, ref Reader reader) => Instance.Deserialize(ref value, ref reader);
    }
}
