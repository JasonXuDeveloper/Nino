using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;
#if !NETSTANDARD2_0
using System.Threading;
using System.Threading.Tasks;
#endif

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
        // The formatter creates the reader (NinoFormatterBase.Read): a reader created here would reach its call by address.
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
            NinoTopFormatter<T>.Formatter.Deserialize(ref reader, ref value);
            reader.ReleaseReferences();
        }

        /// <summary>
        /// Deserializes a new value from data that may span several segments (a <c>PipeReader</c>'s buffer, a gRPC
        /// payload), as <see cref="Deserialize{T}(ReadOnlySpan{byte})"/> does, with the same checks. One segment is
        /// read in place; several are copied once, onto the stack up to 1,024 bytes, else into a pooled array (a
        /// <c>byte[]</c> of two segments straight into the array returned).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(in ReadOnlySequence<byte> data) =>
            data.IsSingleSegment ? Deserialize<T>(data.First.Span) : DeserializeSegments<T>(data);

        /// <summary>
        /// Deserializes into <paramref name="value"/>, as <see cref="Deserialize{T}(ReadOnlySpan{byte}, ref T)"/>,
        /// from data that may span several segments: one segment is read in place, several are copied once (see
        /// <see cref="Deserialize{T}(in ReadOnlySequence{byte})"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(in ReadOnlySequence<byte> data, ref T value)
        {
            if (data.IsSingleSegment)
                Deserialize(data.First.Span, ref value);
            else
                DeserializeSegments(data, ref value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T DeserializeSegments<T>(in ReadOnlySequence<byte> data)
        {
            T value = default;
            DeserializeSegments(data, ref value);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeSegments<T>(in ReadOnlySequence<byte> data, ref T value)
        {
            long length = data.Length;
            if (typeof(T) == typeof(byte[]) && !NinoLimits.Untrusted && NinoSequences.HasTwoSegments(data) &&
                NinoSequences.TryReadByteArray(data, length, ref Unsafe.As<T, byte[]>(ref value)))
                return;

            if (length <= NinoSequences.StackBytes)
            {
                Span<byte> stack = stackalloc byte[NinoSequences.StackBytes];
                stack = stack.Slice(0, (int)length);
                NinoSequences.CopySegments(data, stack);
                Deserialize(stack, ref value);
                return;
            }

            byte[] rented = NinoSequences.Rent(data, length);
            try
            {
                Deserialize(new ReadOnlySpan<byte>(rented, 0, (int)length), ref value);
            }
            finally
            {
                NinoSequences.Return(rented);
            }
        }

        /// <summary>
        /// Reads <paramref name="stream"/> to its end and deserializes a new value from those bytes (what
        /// <see cref="NinoSerializer.Serialize{T}(in T, Stream)"/> wrote, or any payload the span overload reads). The
        /// bytes are read into a pooled array, which grows only as bytes arrive (bytes already in memory are read in
        /// place: a <see cref="MemoryStream"/> whose buffer is exposed, not one made with <c>new MemoryStream(bytes)</c>,
        /// a pointer-backed <see cref="UnmanagedMemoryStream"/> of that exact type, or a memory-mapped view).
        /// Every check of the data is the span overload's, and a stream that ends early throws
        /// <see cref="NinoDataException"/>. Bound a stream from an untrusted source yourself (its length,
        /// a frame): Nino holds what the stream delivers.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static T Deserialize<T>(Stream stream)
        {
            T value = default;
            NinoStreams.Read(stream, ref value, default(SpanReader<T>));
            return value;
        }

        /// <summary>
        /// Reads <paramref name="stream"/> to its end and deserializes into <paramref name="value"/>, as
        /// <see cref="Deserialize{T}(ReadOnlySpan{byte}, ref T)"/> does (see <see cref="Deserialize{T}(Stream)"/>).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static void Deserialize<T>(Stream stream, ref T value) =>
            NinoStreams.Read(stream, ref value, default(SpanReader<T>));

#if !NETSTANDARD2_0
        /// <summary>
        /// <see cref="Deserialize{T}(Stream)"/> with the stream's asynchronous reads. Reads that complete at once (a
        /// <see cref="MemoryStream"/>) allocate nothing but the value. There is no form that reads into an existing
        /// instance (an async method takes no <c>ref</c>): for that, read the bytes yourself and call
        /// <see cref="Deserialize{T}(ReadOnlySpan{byte}, ref T)"/>. Not in the netstandard2.0 build (no
        /// <see cref="ValueTask{TResult}"/> there).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
        public static ValueTask<T> DeserializeAsync<T>(Stream stream, CancellationToken cancellationToken = default) =>
            NinoStreams.ReadAsync<T, SpanReader<T>>(stream, default, cancellationToken);
#endif

        /// <summary>Reads with <see cref="Deserialize{T}(ReadOnlySpan{byte}, ref T)"/> (the Stream overloads, <see cref="NinoStreams"/>).</summary>
        private readonly struct SpanReader<T> : INinoStreamReader<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ReadOnlySpan<byte> data, ref T value) => Deserialize(data, ref value);
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
        public static void Deserialize<T>(ref Reader reader, ref T value)
        {
            if (NinoRaw.IsRead<T>())
            {
                value = reader.UnsafeRead<T>();
                return;
            }

            NinoTopFormatter<T>.Formatter.Deserialize(ref reader, ref value);
        }

        /// <summary>Deserializes a new value with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ref Reader reader)
        {
            if (NinoRaw.IsRead<T>())
                return reader.UnsafeRead<T>();

            T value = default;
            NinoTopFormatter<T>.Formatter.Deserialize(ref reader, ref value);
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
            BoxedFormatter.Get(type).Deserialize(ref reader, ref value);
            reader.ReleaseReferences();
        }

        /// <summary>Deserializes a value of <paramref name="type"/> with an existing reader.</summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Deserialize&lt;TBase&gt;</c> (see <see cref="DeserializeBoxed(ReadOnlySpan{byte}, Type)"/>).
        /// </remarks>
        public static void DeserializeBoxed(ref Reader reader, ref object value, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Deserialize(ref reader, ref value);
        }

        /// <summary>
        /// Reads <paramref name="stream"/> to its end and deserializes a value of <paramref name="type"/>, as
        /// <see cref="Deserialize{T}(Stream)"/> with <c>T</c> = <paramref name="type"/> does, and returns it boxed.
        /// </summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Deserialize&lt;TBase&gt;</c> (see <see cref="DeserializeBoxed(ReadOnlySpan{byte}, Type)"/>).
        /// </remarks>
        public static object DeserializeBoxed(Stream stream, Type type)
        {
            object value = null;
            DeserializeBoxed(stream, ref value, type);
            return value;
        }

        /// <summary>
        /// Reads <paramref name="stream"/> to its end and deserializes a value of <paramref name="type"/> into
        /// <paramref name="value"/>, reusing it when it is an instance of <paramref name="type"/>.
        /// </summary>
        /// <remarks>
        /// For types known only at runtime that share no <c>[NinoType]</c> base; otherwise prefer
        /// <c>Deserialize&lt;TBase&gt;(stream, ref value)</c> (see <see cref="DeserializeBoxed(ReadOnlySpan{byte}, Type)"/>).
        /// </remarks>
        public static void DeserializeBoxed(Stream stream, ref object value, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            NinoStreams.Read(stream, ref value, new BoxedReader(type));
        }

#if !NETSTANDARD2_0
        /// <summary>
        /// <see cref="DeserializeBoxed(Stream, Type)"/> with the stream's asynchronous reads, as
        /// <see cref="DeserializeAsync{T}"/>. Not in the netstandard2.0 build.
        /// </summary>
        public static ValueTask<object> DeserializeBoxedAsync(Stream stream, Type type,
            CancellationToken cancellationToken = default)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            return NinoStreams.ReadAsync<object, BoxedReader>(stream, new BoxedReader(type), cancellationToken);
        }
#endif

        /// <summary>Reads with <see cref="DeserializeBoxed(ReadOnlySpan{byte}, ref object, Type)"/> (the Stream overloads).</summary>
        private readonly struct BoxedReader : INinoStreamReader<object>
        {
            private readonly Type _type;

            public BoxedReader(Type type) => _type = type;

            public void Read(ReadOnlySpan<byte> data, ref object value) => DeserializeBoxed(data, ref value, _type);
        }
    }
}
