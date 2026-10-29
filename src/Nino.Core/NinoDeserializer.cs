using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>Deserializes Nino binary data. All methods are thread safe.</summary>
    public static class NinoDeserializer
    {
        /// <summary>Deserializes a new value.</summary>
        /// <remarks>
        /// Use the <typeparamref name="T"/> the data was serialized with. Data written as a common <c>[NinoType]</c>
        /// base reads back as the subtype that was written. <typeparamref name="T"/> = <see cref="object"/> reads
        /// only <c>[NinoType]</c> values and dispatches through the global registry, which is slower than a common
        /// base.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ReadOnlySpan<byte> data)
        {
            // NinoSettings.Untrusted: a constant once compiled, and its own path (NinoUntrusted).
            if (NinoLimits.Untrusted)
                return NinoUntrusted.Read<T>(data);
            // Raw values inline into the caller as a length check and a load.
            if (NinoRaw.IsRead<T>())
                return ReadRaw<T>(data);
            // Arrays of raw elements: read by code specialized for the element type, as the serializer writes them.
            if (NinoRawArray<T>.Formatter is { } raw)
            {
                object array = raw.Read(data, null);
                return Unsafe.As<object, T>(ref array);
            }

            return DeserializeFormatted<T>(data);
        }

        // Inlined into the caller, where T is usually exact: the formatter cache is then a static field load rather
        // than a generic dictionary lookup (T a reference type makes this method shared code).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // The formatter creates the reader (NinoFormatter.Read): a reader created here would reach its call by address.
        private static T DeserializeFormatted<T>(ReadOnlySpan<byte> data) => NinoTopFormatter<T>.Formatter.Read(data);

        /// <summary>
        /// Deserializes into <paramref name="value"/>, reusing the existing instance (and nested instances) where
        /// possible. Instances that are dropped are passed to <see cref="NinoRecycler{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ReadOnlySpan<byte> data, ref T value)
        {
            if (NinoLimits.Untrusted)
            {
                NinoUntrusted.Read(data, ref value);
                return;
            }

            if (NinoRaw.IsRead<T>())
            {
                value = ReadRaw<T>(data);
                return;
            }

            if (NinoRawArray<T>.Formatter is { } raw)
            {
                object array = raw.Read(data, Unsafe.As<T, Array>(ref value));
                value = Unsafe.As<object, T>(ref array);
                return;
            }

            var reader = new Reader(data);
            NinoTopFormatter<T>.Formatter.Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T ReadRaw<T>(ReadOnlySpan<byte> data)
        {
            if (data.Length < Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            return NinoUnsafe.Read<T>(ref MemoryMarshal.GetReference(data));
        }

        /// <summary>Deserializes into <paramref name="value"/> with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ref T value, ref Reader reader)
        {
            if (NinoRaw.IsRead<T>())
            {
                value = reader.UnsafeRead<T>();
                return;
            }

            NinoTopFormatter<T>.Formatter.Deserialize(ref value, ref reader);
        }

        /// <summary>Deserializes a new value with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ref Reader reader)
        {
            if (NinoRaw.IsRead<T>())
                return reader.UnsafeRead<T>();

            T value = default;
            NinoTopFormatter<T>.Formatter.Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>
        /// Deserializes a value of <paramref name="type"/> (written by <c>Serialize&lt;T&gt;</c> with
        /// <c>T</c> = <paramref name="type"/>, or <see cref="NinoSerializer.SerializeBoxed(object, Type)"/>) and
        /// returns it boxed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Use the boxed API only when the type is known only at runtime, as a <see cref="Type"/>, and the types
        /// share no common <c>[NinoType]</c> base: reflection-driven tooling over arbitrary types, for example. It
        /// finds the formatter by <see cref="Type"/> (reflection on first use, a dictionary lookup afterward) and
        /// boxes value types.
        /// </para>
        /// <para>
        /// For RPC or message dispatch, plugin systems and editor tooling, give the types a common <c>[NinoType]</c>
        /// base class or interface and call <c>Deserialize&lt;TBase&gt;</c> instead: the generated code
        /// dispatches on the type id, which is faster and works with reuse deserialization and pooling (0 GC).
        /// </para>
        /// </remarks>
        public static object DeserializeBoxed(ReadOnlySpan<byte> data, Type type)
        {
            object value = null;
            DeserializeBoxed(data, ref value, type);
            return value;
        }

        /// <summary>
        /// Deserializes a value of <paramref name="type"/> into <paramref name="value"/>, reusing it when it is an
        /// instance of <paramref name="type"/>.
        /// </summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Deserialize&lt;TBase&gt;(data, ref value)</c> (see <see cref="DeserializeBoxed(ReadOnlySpan{byte}, Type)"/>).
        /// </remarks>
        public static void DeserializeBoxed(ReadOnlySpan<byte> data, ref object value, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (NinoLimits.Untrusted)
            {
                NinoUntrusted.ReadBoxed(data, ref value, type);
                return;
            }

            var reader = new Reader(data);
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        /// <summary>Deserializes a value of <paramref name="type"/> with an existing reader.</summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Deserialize&lt;TBase&gt;</c> (see <see cref="DeserializeBoxed(ReadOnlySpan{byte}, Type)"/>).
        /// </remarks>
        public static void DeserializeBoxed(ref object value, ref Reader reader, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
        }
    }
}
