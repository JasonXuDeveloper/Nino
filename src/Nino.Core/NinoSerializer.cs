using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>Serializes values to Nino binary data. All methods are thread safe.</summary>
    public static class NinoSerializer
    {
        [ThreadStatic] private static NinoArrayBufferWriter t_scratch;

        /// <summary>Largest scratch buffer kept per thread between calls.</summary>
        private const int MaxRetainedScratch = 1024 * 1024;

        // The thread's scratch buffer stays in its slot and is marked in use (a byte store on the instance) instead
        // of being taken out and put back: returning it then neither reads the thread-static again nor stores a
        // reference into it (a write barrier). A nested call on the same thread (from a custom formatter) finds it in
        // use and gets a buffer of its own.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static NinoArrayBufferWriter RentScratch()
        {
            var buffer = t_scratch;
            if (buffer == null || buffer.ScratchState != NinoArrayBufferWriter.ScratchIdle)
                return RentOther();
            buffer.ScratchState = NinoArrayBufferWriter.ScratchInUse;
            buffer.Index = 0;
            return buffer;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoArrayBufferWriter RentOther()
        {
            var buffer = new NinoArrayBufferWriter();
            if (t_scratch == null)
            {
                buffer.ScratchState = NinoArrayBufferWriter.ScratchInUse;
                t_scratch = buffer;
            }

            return buffer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ReturnScratch(NinoArrayBufferWriter buffer)
        {
            if (buffer.ScratchState == NinoArrayBufferWriter.ScratchInUse && buffer.Buffer.Length <= MaxRetainedScratch)
                buffer.ScratchState = NinoArrayBufferWriter.ScratchIdle;
            else
                ReleaseScratch(buffer);
        }

        /// <summary>A buffer that is not kept: one a nested call created, or the scratch buffer grown too large.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReleaseScratch(NinoArrayBufferWriter buffer)
        {
            if (ReferenceEquals(t_scratch, buffer))
                t_scratch = null;
            buffer.ScratchState = 0;
            buffer.Dispose();
        }

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
            if (RawArray<T>.Formatter is { } raw && value != null)
                return raw.Serialize(Unsafe.As<Array>(value));

            // A value of fixed size: written into an array of that size (a constant check in Tier1 code).
            if (NinoTopFormatter<T>.Formatter is { FixedSize: >= 0 } fixedSize)
                return fixedSize.SerializeFixed(value);

            // A collection whose size is known up front (a count times a fixed element size): written into an array of
            // that size, with no scratch buffer to copy out of.
            if (NinoTopFormatter<T>.Formatter is { Sized: true } sizedFormatter &&
                sizedFormatter.SerializeSized(value) is { } sized)
                return sized;

            return SerializeToArray(value, NinoTopFormatter<T>.Get);
        }

        /// <summary>
        /// The number of bytes <see cref="Serialize{T}(in T)"/> returns for every value of <typeparamref name="T"/>
        /// when that number is the same for all of them, else -1. Fixed are raw values and generated structs and
        /// sealed classes whose members are all unmanaged values or such structs (not version tolerant, keyed or
        /// tracked); a null instance of such a sealed class is 1 byte (its null flag), every other one this size.
        /// </summary>
        public static int GetFixedSize<T>()
        {
            if (NinoRaw.Is<T>())
                return Unsafe.SizeOf<T>();
            return NinoTopFormatter<T>.Get.FixedSize;
        }

        // Inlined: the allocation and one store, as MemoryPack's (a call cost 1.6 ns of 14 for a 40-byte struct).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] SerializeRaw<T>(in T value)
        {
            var bytes = new byte[Unsafe.SizeOf<T>()];
            NinoLayout<T>.Write(ref bytes[0], in value);
            return bytes;
        }

        private static byte[] SerializeToArray<T>(T value, NinoFormatter<T> formatter)
        {
            var buffer = RentScratch();
            try
            {
                WriteFormatted(value, buffer, formatter);
                return buffer.ToArray();
            }
            finally
            {
                ReturnScratch(buffer);
            }
        }

        /// <summary>Writes <paramref name="value"/> with its formatter, then ends the call's reference tracking.</summary>
        // The formatter creates the writer (NinoFormatter.Write): a writer created here would reach its call by address.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteFormatted<T>(T value, NinoArrayBufferWriter buffer, NinoFormatter<T> formatter) =>
            formatter.Write(value, buffer);

        /// <summary>
        /// The formatter of <typeparamref name="T"/> when it is an array of raw elements (null otherwise). A static
        /// readonly field: Tier1 code knows its exact type and calls it directly (inlined, with the element size a
        /// constant), without a type guard or interface dispatch. Arrays are reference types, so code that is not
        /// inlined into a caller of the exact type is shared, and this field is a generic dictionary lookup there.
        /// </summary>
        internal static class RawArray<T>
        {
            public static readonly INinoRawArrayFormatter Formatter = Create();

            private static INinoRawArrayFormatter Create()
            {
                // A big-endian host takes the formatter, whose writer or reader rejects it.
                if (!typeof(T).IsArray || typeof(T).GetArrayRank() != 1 || !BitConverter.IsLittleEndian)
                    return null;
                // Through the snapshot (a use of T's formatter); null without a formatter: the regular path reports it.
                return NinoTopFormatter<T>.Formatter?.RawArray;
            }
        }

        /// <summary>
        /// Serializes <paramref name="value"/> into <paramref name="bufferWriter"/>. Writing into a
        /// <see cref="NinoArrayBufferWriter"/> is done in place; other buffer writers receive one copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(in T value, IBufferWriter<byte> bufferWriter)
        {
            if (NinoRaw.Is<T>())
            {
                WriteRaw(in value, bufferWriter);
                return;
            }

            if (RawArray<T>.Formatter is { } raw)
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
                WriteFormatted(value, nino, NinoTopFormatter<T>.Get);
                return;
            }

            SerializeOther(value, bufferWriter, NinoTopFormatter<T>.Get);
        }

        // Out of line: inlined, it would take the caller's inlining budget for paths the calls above do not take.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeOther<T>(T value, IBufferWriter<byte> bufferWriter, NinoFormatter<T> formatter)
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
            {
                WriteFormatted(value, nino, formatter);
                return;
            }

            var buffer = RentScratch();
            try
            {
                WriteFormatted(value, buffer, formatter);
                bufferWriter.Write(buffer.WrittenSpan);
            }
            finally
            {
                ReturnScratch(buffer);
            }
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

            NinoTopFormatter<T>.Get.Serialize(value, ref writer);
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
    }
}
