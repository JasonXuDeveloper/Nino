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

        internal virtual INinoRawArrayFormatter RawArrayPadding => null;

        /// <summary>Element size when <typeparamref name="T"/> is an array written as one raw block, else 0.</summary>
        internal virtual int RawArrayElementSize => 0;
    }

    /// <summary>Formatters of arrays whose elements are written as one raw block.</summary>
    internal interface INinoRawArrayFormatter
    {
        int ElementSize { get; }

        /// <summary>The formatter itself when elements have padding to clear after the copy, else null.</summary>
        INinoRawArrayFormatter Padded { get; }

        void CopyToWire(ref byte destination, ref byte source, int count);
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
    public sealed class StructFormatter<T, TFormatter> : NinoFormatter<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Serialize(T value, ref Writer writer) => default(TFormatter).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Deserialize(ref T value, ref Reader reader) =>
            default(TFormatter).Deserialize(ref value, ref reader);

        internal override bool IsRaw => RawFormatter<TFormatter>.Is;

        internal override int RawArrayElementSize =>
            default(TFormatter) is INinoRawArrayFormatter raw ? raw.ElementSize : 0;

        internal override INinoRawArrayFormatter RawArrayPadding =>
            default(TFormatter) is INinoRawArrayFormatter raw ? raw.Padded : null;
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
