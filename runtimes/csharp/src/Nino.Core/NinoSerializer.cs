using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
using System.Threading;
using System.Threading.Tasks;

namespace Nino.Core
{
    /// <summary>Serializes values to Nino binary data. All methods are thread safe.</summary>
    public static class NinoSerializer
    {
        /// <summary>Serializes <paramref name="value"/> into a new byte array.</summary>
        /// <remarks>
        /// <typeparamref name="T"/> is the static type of the argument. For a polymorphic value, pass the common
        /// <c>[NinoType]</c> base (<c>Serialize&lt;TBase&gt;(value)</c>): a type id is written and read back with
        /// <c>Deserialize&lt;TBase&gt;</c>. With <typeparamref name="T"/> = <see cref="object"/> (as when the
        /// argument is a variable of type <c>object</c>) only <c>[NinoType]</c> values are accepted; a type id is
        /// written and dispatched through the global registry, which is slower than a common base. For a type known
        /// only as a <see cref="Type"/>, see <see cref="SerializeBoxed(object, Type)"/>.
        /// </remarks>
        // The public entry points inline into the caller, where T is usually exact: the formatter and the raw-array
        // check are then static field loads instead of generic dictionary lookups (T a reference type makes any
        // method generic over it shared code), and the out-of-line rest receives the formatter as an argument.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(in T value)
        {
            if (NinoRaw.Is<T>())
                return SerializeRaw(in value);

            // The formatter is a static readonly field: Tier1 code calls (and inlines) it as its exact type.
            if (NinoRawArray<T>.Formatter is { } raw && value != null)
                return raw.Serialize(Unsafe.As<Array>(value));

            // A value of fixed size: written into an array of that size (a constant check in Tier1 code).
            if (NinoTopFormatter<T>.Formatter.FixedSize >= 0)
                return NinoTopFormatter<T>.Formatter.SerializeFixed(value);

            // A collection whose size is known up front (a count times a fixed element size): written into an array of
            // that size, with no scratch buffer to copy out of.
            if (NinoTopFormatter<T>.Formatter.Sized &&
                NinoTopFormatter<T>.Formatter.SerializeSized(value) is { } sized)
                return sized;

            return NinoScratch.ToArray(value, new FormatterWriter<T>(NinoTopFormatter<T>.Formatter));
        }

        /// <summary>
        /// The number of bytes <see cref="Serialize{T}(in T)"/> returns for every value of <typeparamref name="T"/>
        /// when that number is the same for all of them, else -1. Fixed are raw values and generated structs and
        /// sealed classes whose members are all unmanaged values or such structs (not version tolerant, keyed or
        /// tracked); a null instance of such a sealed class is 1 byte (its presence byte), every other one this size.
        /// </summary>
        public static int GetFixedSize<T>()
        {
            if (NinoRaw.Is<T>())
                return Unsafe.SizeOf<T>();
            return NinoTopFormatter<T>.Formatter.FixedSize;
        }

        // Inlined: the allocation and one store, as MemoryPack's (a call cost 1.6 ns of 14 for a 40-byte struct).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] SerializeRaw<T>(in T value)
        {
            int size = Unsafe.SizeOf<T>();
#if NET8_0_OR_GREATER
            // Keep the direct small allocation. A large canonical layout write fills every byte, padding included.
            var bytes = size < NinoUnsafe.MinUninitializedArrayBytes
                ? new byte[size]
                : NinoUnsafe.NewArray<byte>(size, uninitialized: true);
#else
            var bytes = new byte[size];
#endif
            NinoLayout<T>.Write(ref bytes[0], in value);
            return bytes;
        }

        /// <summary>Writes <paramref name="value"/> with its formatter, then ends the call's reference tracking.</summary>
        // The formatter creates the writer (NinoFormatterBase.Write): a writer created here would reach its call by address.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFormatted<T>(T value, NinoArrayBufferWriter buffer, NinoFormatterBase<T> formatter) =>
            formatter.Write(value, buffer);

        /// <summary>Writes with a formatter known only as <see cref="NinoFormatterBase{T}"/> (<see cref="NinoScratch"/>).</summary>
        internal readonly struct FormatterWriter<T> : INinoScratchWriter<T>
        {
            private readonly NinoFormatterBase<T> _formatter;

            public FormatterWriter(NinoFormatterBase<T> formatter) => _formatter = formatter;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(T value, NinoArrayBufferWriter buffer) => WriteFormatted(value, buffer, _formatter);
        }

        /// <summary>
        /// Serializes <paramref name="value"/> into <paramref name="bufferWriter"/>. Writing into a
        /// <see cref="NinoArrayBufferWriter"/> or writing a raw value, raw array, raw list or fixed-size value is done
        /// in place.
        /// The .NET Standard 2.1 and modern .NET builds also write raw array-backed slices and single-segment
        /// sequences in place.
        /// Modern .NET also writes zero-based raw multidimensional arrays in place.
        /// Other values are copied from a scratch buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(in T value, IBufferWriter<byte> bufferWriter)
        {
            if (NinoRaw.Is<T>())
            {
                WriteRaw(in value, bufferWriter);
                return;
            }

            if (NinoRawArray<T>.Formatter is { } raw)
            {
                // An array of raw elements: one capacity check and one block copy, inlined with the element size
                // a constant (the formatter is a static readonly field: Tier1 code knows its exact type).
                if (value != null && bufferWriter is NinoArrayBufferWriter target)
                {
                    raw.Write(target, Unsafe.As<Array>(value));
                    return;
                }
            }
            else if (bufferWriter is NinoArrayBufferWriter nino)
            {
                WriteFormatted(value, nino, NinoTopFormatter<T>.Formatter);
                return;
            }

            SerializeOther(value, bufferWriter, NinoTopFormatter<T>.Formatter);
        }

        // Out of line: inlined, it would take the caller's inlining budget for paths the calls above do not take.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeOther<T>(T value, IBufferWriter<byte> bufferWriter, NinoFormatterBase<T> formatter)
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
            {
                WriteFormatted(value, nino, formatter);
                return;
            }

            if (value != null && NinoRawArray<T>.Formatter is { } raw)
            {
                raw.WriteTo(bufferWriter, Unsafe.As<Array>(value));
                return;
            }

            formatter.WriteExternal(value, bufferWriter);
        }

        /// <summary>
        /// Writes <paramref name="value"/> to <paramref name="stream"/>: exactly the bytes
        /// <see cref="Serialize{T}(in T)"/> returns, with no length or other framing (read them back with
        /// <see cref="NinoDeserializer.Deserialize{T}(Stream)"/>, which reads to the end of the stream). The value is
        /// written into a pooled per-thread buffer first, then to the stream in one write: no array is allocated per
        /// call.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static void Serialize<T>(in T value, Stream stream) =>
            NinoStreams.Write(value, default(BufferWriter<T>), stream);

        /// <summary>
        /// <see cref="Serialize{T}(in T, Stream)"/> with the stream's asynchronous write. The value is serialized
        /// before the method returns, so it may change while the write is pending; a write that completes at once
        /// allocates nothing.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static ValueTask SerializeAsync<T>(T value, Stream stream, CancellationToken cancellationToken = default) =>
            NinoStreams.WriteAsync(value, default(BufferWriter<T>), stream, cancellationToken);

        /// <summary>Writes with <see cref="Serialize{T}(in T, IBufferWriter{byte})"/> (the Stream overloads, <see cref="NinoStreams"/>).</summary>
        private readonly struct BufferWriter<T> : INinoScratchWriter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(T value, NinoArrayBufferWriter buffer) => Serialize(value, buffer);
        }

        /// <summary>Serializes <paramref name="value"/> with an existing writer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(in T value, ref Writer writer)
        {
            if (NinoRaw.Is<T>())
            {
                writer.WriteValue(in value);
                return;
            }

            NinoTopFormatter<T>.Formatter.Serialize(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRaw<T>(in T value, IBufferWriter<byte> bufferWriter)
        {
            int size = Unsafe.SizeOf<T>();
            if (bufferWriter is NinoArrayBufferWriter nino)
            {
                NinoLayout<T>.Write(ref nino.Reserve(size), in value);
                return;
            }

            var span = bufferWriter.GetSpan(size);
            if (span.Length < size)
                NinoThrow.EndOfData();
            NinoLayout<T>.Write(ref MemoryMarshal.GetReference(span), in value);
            bufferWriter.Advance(size);
        }

        /// <summary>
        /// Whether Nino can serialize and deserialize values of <paramref name="type"/>: a <c>[NinoType]</c>, a
        /// built-in type, or a type with a registered formatter. For integrations that only have a
        /// <see cref="Type"/> and must decide whether to handle it (an ASP.NET Core formatter, for example).
        /// </summary>
        public static bool IsSupported(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (type.ContainsGenericParameters || type.IsByRef || type.IsPointer)
                return false;
            try
            {
                return BoxedFormatter.Get(type).HasFormatter;
            }
            catch (InvalidOperationException)
            {
                // Native AOT without a generated formatter for the type.
                return false;
            }
            catch (ArgumentException)
            {
                // Not a valid type argument (a ref struct or void).
                return false;
            }
        }

        /// <summary>
        /// Serializes a boxed value as <paramref name="type"/>, exactly as <c>Serialize&lt;T&gt;</c> with
        /// <c>T</c> = <paramref name="type"/> would (null included, for a type that can be null). Read it back with
        /// <see cref="NinoDeserializer.DeserializeBoxed(ReadOnlySpan{byte}, Type)"/> and the same type.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null, or <paramref name="value"/> is
        /// null and <paramref name="type"/> is a value type other than <see cref="Nullable{T}"/>.</exception>
        /// <remarks>
        /// <para>
        /// Use the boxed API only when the type is known only at runtime, as a <see cref="Type"/>, and the types
        /// share no common <c>[NinoType]</c> base: reflection-driven tooling over arbitrary types, for example. It
        /// finds the formatter by <see cref="Type"/> (reflection on first use, a dictionary lookup afterward) and
        /// boxes value types.
        /// </para>
        /// <para>
        /// For RPC or message dispatch, plugin systems and editor tooling, give the types a common <c>[NinoType]</c>
        /// base class or interface and call <c>Serialize&lt;TBase&gt;</c> / <c>Deserialize&lt;TBase&gt;</c>
        /// instead: the generated code writes a type id and dispatches on it, which is faster and works with reuse
        /// deserialization and pooling (0 GC).
        /// </para>
        /// </remarks>
        public static byte[] SerializeBoxed(object value, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            return BoxedFormatter.Get(type).Serialize(value);
        }

        /// <summary>Serializes a boxed value as <paramref name="type"/> with an existing writer.</summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Serialize&lt;TBase&gt;</c> (see <see cref="SerializeBoxed(object, Type)"/>).
        /// </remarks>
        public static void SerializeBoxed(object value, ref Writer writer, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Serialize(value, ref writer);
        }

        /// <summary>Serializes a boxed value as <paramref name="type"/> into <paramref name="bufferWriter"/>.</summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Serialize&lt;TBase&gt;</c> (see <see cref="SerializeBoxed(object, Type)"/>).
        /// </remarks>
        public static void SerializeBoxed(object value, IBufferWriter<byte> bufferWriter, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Serialize(value, bufferWriter);
        }

        /// <summary>
        /// Writes a boxed value as <paramref name="type"/> to <paramref name="stream"/>, as
        /// <see cref="Serialize{T}(in T, Stream)"/> with <c>T</c> = <paramref name="type"/> does.
        /// </summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Serialize&lt;TBase&gt;</c> (see <see cref="SerializeBoxed(object, Type)"/>).
        /// </remarks>
        public static void SerializeBoxed(object value, Stream stream, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            NinoStreams.Write(value, new BoxedWriter(BoxedFormatter.Get(type)), stream);
        }

        /// <summary>
        /// <see cref="SerializeBoxed(object, Stream, Type)"/> with the stream's asynchronous write, as
        /// <see cref="SerializeAsync{T}"/>.
        /// </summary>
        public static ValueTask SerializeBoxedAsync(object value, Stream stream, Type type,
            CancellationToken cancellationToken = default)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            return NinoStreams.WriteAsync(value, new BoxedWriter(BoxedFormatter.Get(type)), stream, cancellationToken);
        }

        /// <summary>Writes a boxed value with its type's <see cref="BoxedFormatter"/> (the Stream overloads).</summary>
        private readonly struct BoxedWriter : INinoScratchWriter<object>
        {
            private readonly BoxedFormatter _formatter;

            public BoxedWriter(BoxedFormatter formatter) => _formatter = formatter;

            public void Write(object value, NinoArrayBufferWriter buffer) => _formatter.Serialize(value, buffer);
        }
    }
}
