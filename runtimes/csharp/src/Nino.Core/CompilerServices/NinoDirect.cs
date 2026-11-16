using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.Internal;
using System.Threading;
using System.Threading.Tasks;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// For generated code: the top-level <see cref="NinoSerializer"/> and <see cref="NinoDeserializer"/> calls with
    /// the formatter the generator resolved for <typeparamref name="T"/> (<typeparamref name="TFormatter"/>), which
    /// it intercepts them with on compilers that support interceptors. The formatter is then called directly
    /// instead of through <see cref="NinoFormatterCache{T}"/> (a virtual call). The bytes are the same.
    /// Raw values and arrays of raw elements are never intercepted: the regular calls copy them directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoDirect
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T, TFormatter>(in T value) where TFormatter : struct, INinoFormatter<T> =>
            NinoSized.Is<T, TFormatter>() && NinoSized.Serialize<T, TFormatter>(value) is { } sized
                ? sized
                : NinoScratch.ToArray(value, default(DirectWriter<T, TFormatter>));

        /// <summary>
        /// <see cref="NinoSerializer.Serialize{T}(in T)"/> of a value whose size is fixed: the result is allocated at
        /// that size and the value written straight into it, with no scratch buffer, writer or copy. The bytes are
        /// the same.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] SerializeFixed<T, TFormatter>(in T value)
            where TFormatter : struct, INinoFixedSizeFormatter<T> =>
            WriteFixed<T, TFormatter>(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte[] WriteFixed<T, TFormatter>(in T value) where TFormatter : struct, INinoFixedSizeFormatter<T>
        {
            // Bytes are written little-endian, as Writer does (which rejects other hosts); a JIT-time constant.
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            // A null sealed class: its presence byte (0), which a new array holds.
            if (value == null)
                return new byte[1];
            var bytes = new byte[default(TFormatter).FixedSize];
            default(TFormatter).WriteFixed(value, ref NinoUnsafe.GetArrayDataReference(bytes));
            return bytes;
        }

        /// <summary>Writes with <typeparamref name="TFormatter"/> (<see cref="NinoScratch"/>).</summary>
        private readonly struct DirectWriter<T, TFormatter> : INinoScratchWriter<T>
            where TFormatter : struct, INinoFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(T value, NinoArrayBufferWriter buffer) => Write<T, TFormatter>(value, buffer);
        }

        /// <summary>Writes with the <c>__Top</c> twin <typeparamref name="TFormatter"/> (<see cref="NinoScratch"/>).</summary>
        private readonly struct TopWriter<T, TFormatter> : INinoScratchWriter<T>
            where TFormatter : struct, INinoTopFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(T value, NinoArrayBufferWriter buffer) => default(TFormatter).Write(value, buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(in T value, IBufferWriter<byte> bufferWriter)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
                Write<T, TFormatter>(value, nino);
            else
                SerializeOther(value, bufferWriter);
        }

        // Another buffer writer: the regular call, out of line so the inlined fast path stays small.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeOther<T>(in T value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize(value, bufferWriter);

        /// <summary>
        /// <see cref="Serialize{T,TFormatter}(in T)"/> with a <c>__Top</c> twin that creates its own writer
        /// (<see cref="INinoTopFormatter{T}"/>). The bytes are the same.
        /// </summary>
        // Inlined: the caller calls the out-of-line scratch write directly.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] SerializeTop<T, TFormatter>(in T value) where TFormatter : struct, INinoTopFormatter<T> =>
            NinoScratch.ToArray(value, default(TopWriter<T, TFormatter>));

        /// <summary>
        /// <see cref="Serialize{T,TFormatter}(in T, IBufferWriter{byte})"/> with a <c>__Top</c> twin that creates its own
        /// writer (<see cref="INinoTopFormatter{T}"/>). The bytes are the same.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeTop<T, TFormatter>(in T value, IBufferWriter<byte> bufferWriter)
            where TFormatter : struct, INinoTopFormatter<T>
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
                default(TFormatter).Write(value, nino);
            else
                SerializeOther(value, bufferWriter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(in T value, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            default(TFormatter).Serialize(value, ref writer);

        /// <summary>
        /// Writes a null class with a presence byte as a whole payload into <paramref name="buffer"/>: the byte alone,
        /// where a generated <c>__Top</c> twin's <c>Write</c> finds the value null, without calling its writer (a
        /// writer created and flushed out of line took longer than v3's inlined null, 3.5 ns against 2.6 in
        /// v4.0.0-rc.10's NullableTest.NinoSerializeNullableObject). Out of line: inlined, its reservation (and the
        /// buffer's growth) nearly doubled every top-level call of such a class (SimpleClass's 88 to 161 bytes).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteNull(NinoArrayBufferWriter buffer) => buffer.Reserve(1) = NinoWire.Null;

        /// <summary>
        /// Writes <paramref name="value"/> as a whole payload into <paramref name="buffer"/> with a writer created
        /// here (inlined: in the caller's frame, where the JIT keeps it in registers if no call takes its address).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(in T value, NinoArrayBufferWriter buffer)
            where TFormatter : struct, INinoFormatter<T>
        {
            var writer = new Writer(buffer);
            default(TFormatter).Serialize(value, ref writer);
            writer.Flush();
            writer.ReleaseReferences();
        }

        /// <summary>
        /// <see cref="Serialize{T,TFormatter}(in T)"/> of an array of a class whose formatter prefetches
        /// (<see cref="INinoPrefetchFormatter"/>), called by the interceptors of a
        /// project built with <c>NinoPrefetchLargeCollections</c>: a long one of scattered objects is written with
        /// the objects ahead prefetched (<see cref="NinoPrefetch"/>), any other as by the plain call. The bytes are the
        /// same.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] SerializePrefetched<T, TFormatter>(in T[] value)
            where TFormatter : struct, INinoFormatter<T> =>
            value != null && NinoPrefetch.Long<TFormatter>(value.Length)
                ? NinoScratch.ToArray(value, default(PrefetchArrayWriter<T, TFormatter>))
                : Serialize<T[], ArrayFormatter<T, TFormatter>>(value);

        /// <summary>The list <paramref name="value"/> as <see cref="SerializePrefetched{T,TFormatter}(in T[])"/> writes an array.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] SerializePrefetched<T, TFormatter>(in List<T> value)
            where TFormatter : struct, INinoFormatter<T> =>
            value != null && NinoPrefetch.Long<TFormatter>(value.Count)
                ? NinoScratch.ToArray(value, default(PrefetchListWriter<T, TFormatter>))
                : Serialize<List<T>, ListFormatter<List<T>, T, TFormatter>>(value);

        /// <summary>
        /// <see cref="SerializePrefetched{T,TFormatter}(in T[])"/> into <paramref name="bufferWriter"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializePrefetched<T, TFormatter>(in T[] value, IBufferWriter<byte> bufferWriter)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (value != null && NinoPrefetch.Long<TFormatter>(value.Length) &&
                bufferWriter is NinoArrayBufferWriter nino && NinoPrefetch.Write<T, TFormatter>(value, nino))
                return;

            Serialize<T[], ArrayFormatter<T, TFormatter>>(value, bufferWriter);
        }

        /// <summary>
        /// <see cref="SerializePrefetched{T,TFormatter}(in List{T})"/> into <paramref name="bufferWriter"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializePrefetched<T, TFormatter>(in List<T> value, IBufferWriter<byte> bufferWriter)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (value != null && NinoPrefetch.Long<TFormatter>(value.Count) &&
                bufferWriter is NinoArrayBufferWriter nino && NinoPrefetch.Write<T, TFormatter>(value, nino))
                return;

            Serialize<List<T>, ListFormatter<List<T>, T, TFormatter>>(value, bufferWriter);
        }

        /// <summary>Writes a long array prefetched, or as the plain call does when its objects are in order.</summary>
        private readonly struct PrefetchArrayWriter<T, TFormatter> : INinoScratchWriter<T[]>
            where TFormatter : struct, INinoFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(T[] value, NinoArrayBufferWriter buffer)
            {
                if (!NinoPrefetch.Write<T, TFormatter>(value, buffer))
                    Write<T[], ArrayFormatter<T, TFormatter>>(value, buffer);
            }
        }

        /// <summary>Writes a long list prefetched, or as the plain call does when its objects are in order.</summary>
        private readonly struct PrefetchListWriter<T, TFormatter> : INinoScratchWriter<List<T>>
            where TFormatter : struct, INinoFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Write(List<T> value, NinoArrayBufferWriter buffer)
            {
                if (!NinoPrefetch.Write<T, TFormatter>(value, buffer))
                    Write<List<T>, ListFormatter<List<T>, T, TFormatter>>(value, buffer);
            }
        }

        /// <summary>
        /// <see cref="Deserialize{T,TFormatter}(ReadOnlySpan{byte}, ref T)"/> into an array of a class whose formatter
        /// prefetches, called by the interceptors of a project built with <c>NinoPrefetchLargeCollections</c>: into
        /// a long one of scattered objects, as long as the data's, with the objects ahead prefetched.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializePrefetched<T, TFormatter>(ReadOnlySpan<byte> data, ref T[] value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoLimits.Untrusted || value == null || !NinoPrefetch.Long<TFormatter>(value.Length) ||
                !NinoPrefetch.Read<T, TFormatter>(data, value))
                Deserialize<T[], ArrayFormatter<T, TFormatter>>(data, ref value);
        }

        /// <summary>The list <paramref name="value"/> as <see cref="DeserializePrefetched{T,TFormatter}(ReadOnlySpan{byte}, ref T[])"/> reads into an array.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializePrefetched<T, TFormatter>(ReadOnlySpan<byte> data, ref List<T> value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoLimits.Untrusted || value == null || !NinoPrefetch.Long<TFormatter>(value.Count) ||
                !NinoPrefetch.Read<T, TFormatter>(data, value))
                Deserialize<List<T>, ListFormatter<List<T>, T, TFormatter>>(data, ref value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(ReadOnlySpan<byte> data) where TFormatter : struct, INinoFormatter<T>
        {
            // NinoSettings.Untrusted: a constant once compiled, and its own path (NinoUntrusted).
            if (NinoLimits.Untrusted)
                return NinoUntrusted.Read<T, TFormatter>(data);
            var reader = new Reader(data);
            T value = default;
            default(TFormatter).Deserialize(ref reader, ref value);
            reader.ReleaseReferences();
            return value;
        }

        /// <summary>
        /// <see cref="Deserialize{T,TFormatter}(ReadOnlySpan{byte})"/> with a <c>__Top</c> twin that creates its own
        /// reader (<see cref="INinoTopFormatter{T}.Read"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T DeserializeTop<T, TFormatter>(ReadOnlySpan<byte> data)
            where TFormatter : struct, INinoTopFormatter<T> =>
            NinoLimits.Untrusted ? NinoUntrusted.Read<T, TFormatter>(data) : default(TFormatter).Read(data);

        /// <summary>
        /// <see cref="NinoDeserializer.Deserialize{T}(ReadOnlySpan{byte})"/> of a value the generator found is copied
        /// as its raw bytes: a length check and a load. When that path is unavailable, small values use a stack
        /// local. Large values in untrusted mode return directly from the validated read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T DeserializeRaw<T>(ReadOnlySpan<byte> data)
        {
            // Keep the stack local in a separate method: Tier0 would reserve it for large T even on a dead branch.
            if (Unsafe.SizeOf<T>() <= MaxStackRawCopy)
                return DeserializeSmallRaw<T>(data);

            // Fold the mode check before the layout check. Combining them gives the trusted read a large
            // temporary return buffer even after the fallback is folded out.
            if (NinoLimits.Untrusted)
                return NinoUntrusted.Read<T>(data);

            ref byte source = ref MemoryMarshal.GetReference(data);
            if (!NinoRaw.IsRead<T>())
                source = ref DeserializeLargeRaw<T>(data);
            else if (data.Length < Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            return NinoUnsafe.Read<T>(ref source);
        }

        private const int MaxStackRawCopy = 256;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T DeserializeSmallRaw<T>(ReadOnlySpan<byte> data)
        {
            scoped ref byte source = ref MemoryMarshal.GetReference(data);
            T copy;
            if (!NinoRaw.IsRead<T>() || NinoLimits.Untrusted)
            {
                DeserializeOther(data, out copy);
                source = ref Unsafe.As<T, byte>(ref copy);
            }
            else if (data.Length < Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            return NinoUnsafe.Read<T>(ref source);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeOther<T>(ReadOnlySpan<byte> data, out T value) =>
            value = NinoDeserializer.Deserialize<T>(data);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ref byte DeserializeLargeRaw<T>(ReadOnlySpan<byte> data)
        {
            var copy = new T[1];
            copy[0] = NinoDeserializer.Deserialize<T>(data);
            return ref Unsafe.As<T, byte>(ref copy[0]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ReadOnlySpan<byte> data, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoLimits.Untrusted)
            {
                NinoUntrusted.Read<T, TFormatter>(data, ref value);
                return;
            }

            var reader = new Reader(data);
            default(TFormatter).Deserialize(ref reader, ref value);
            reader.ReleaseReferences();
        }

        // The ReadOnlySequence<byte> forms of the reads above: one segment is read in place, several are copied once
        // into a pooled array out of line (NinoSequences), so the inlined part is the span call and one branch.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(in ReadOnlySequence<byte> data)
            where TFormatter : struct, INinoFormatter<T> =>
            data.IsSingleSegment ? Deserialize<T, TFormatter>(data.First.Span) : DeserializeSegments<T, TFormatter>(data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T DeserializeTop<T, TFormatter>(in ReadOnlySequence<byte> data)
            where TFormatter : struct, INinoTopFormatter<T> =>
            data.IsSingleSegment ? DeserializeTop<T, TFormatter>(data.First.Span) : DeserializeSegments<T, TFormatter>(data);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(in ReadOnlySequence<byte> data, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (data.IsSingleSegment)
                Deserialize<T, TFormatter>(data.First.Span, ref value);
            else
                DeserializeSegments<T, TFormatter>(data, ref value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializePrefetched<T, TFormatter>(in ReadOnlySequence<byte> data, ref T[] value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (data.IsSingleSegment)
                DeserializePrefetched<T, TFormatter>(data.First.Span, ref value);
            else
                DeserializeSegments<T[], ArrayFormatter<T, TFormatter>>(data, ref value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializePrefetched<T, TFormatter>(in ReadOnlySequence<byte> data, ref List<T> value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (data.IsSingleSegment)
                DeserializePrefetched<T, TFormatter>(data.First.Span, ref value);
            else
                DeserializeSegments<List<T>, ListFormatter<List<T>, T, TFormatter>>(data, ref value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T DeserializeSegments<T, TFormatter>(in ReadOnlySequence<byte> data)
            where TFormatter : struct, INinoFormatter<T>
        {
            T value = default;
            DeserializeSegments<T, TFormatter>(data, ref value);
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeSegments<T, TFormatter>(in ReadOnlySequence<byte> data, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            long length = data.Length;
            if (length <= NinoSequences.StackBytes)
            {
                Span<byte> stack = stackalloc byte[NinoSequences.StackBytes];
                stack = stack.Slice(0, (int)length);
                NinoSequences.CopySegments(data, stack);
                Deserialize<T, TFormatter>(stack, ref value);
                return;
            }

            byte[] rented = NinoSequences.Rent(data, length);
            try
            {
                Deserialize<T, TFormatter>(new ReadOnlySpan<byte>(rented, 0, (int)length), ref value);
            }
            finally
            {
                NinoSequences.Return(rented);
            }
        }

        // The Stream forms of the writes and reads above: the I/O is NinoStreams' (shared with the regular calls), out
        // of line; what the interceptor inlines is the call, with the writer or reader struct that names the formatter.
        // The asynchronous forms keep their state machines there too.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(in T value, Stream stream)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoStreams.Write(value, default(DirectWriter<T, TFormatter>), stream);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeTop<T, TFormatter>(in T value, Stream stream)
            where TFormatter : struct, INinoTopFormatter<T> =>
            NinoStreams.Write(value, default(TopWriter<T, TFormatter>), stream);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializePrefetched<T, TFormatter>(in T[] value, Stream stream)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (value != null && NinoPrefetch.Long<TFormatter>(value.Length))
                NinoStreams.Write(value, default(PrefetchArrayWriter<T, TFormatter>), stream);
            else
                Serialize<T[], ArrayFormatter<T, TFormatter>>(value, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializePrefetched<T, TFormatter>(in List<T> value, Stream stream)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (value != null && NinoPrefetch.Long<TFormatter>(value.Count))
                NinoStreams.Write(value, default(PrefetchListWriter<T, TFormatter>), stream);
            else
                Serialize<List<T>, ListFormatter<List<T>, T, TFormatter>>(value, stream);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(Stream stream) where TFormatter : struct, INinoFormatter<T>
        {
            T value = default;
            NinoStreams.Read(stream, ref value, default(DirectReader<T, TFormatter>));
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T DeserializeTop<T, TFormatter>(Stream stream) where TFormatter : struct, INinoTopFormatter<T>
        {
            T value = default;
            NinoStreams.Read(stream, ref value, default(TopReader<T, TFormatter>));
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(Stream stream, ref T value)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoStreams.Read(stream, ref value, default(DirectReader<T, TFormatter>));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializePrefetched<T, TFormatter>(Stream stream, ref T[] value)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoStreams.Read(stream, ref value, default(PrefetchArrayReader<T, TFormatter>));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializePrefetched<T, TFormatter>(Stream stream, ref List<T> value)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoStreams.Read(stream, ref value, default(PrefetchListReader<T, TFormatter>));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueTask SerializeAsync<T, TFormatter>(T value, Stream stream,
            CancellationToken cancellationToken = default)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoStreams.WriteAsync(value, default(DirectWriter<T, TFormatter>), stream, cancellationToken);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ValueTask<T> DeserializeAsync<T, TFormatter>(Stream stream,
            CancellationToken cancellationToken = default)
            where TFormatter : struct, INinoFormatter<T> =>
            NinoStreams.ReadAsync<T, DirectReader<T, TFormatter>>(stream, default, cancellationToken);

        /// <summary>Reads with <typeparamref name="TFormatter"/> (<see cref="NinoStreams"/>).</summary>
        private readonly struct DirectReader<T, TFormatter> : INinoStreamReader<T>
            where TFormatter : struct, INinoFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ReadOnlySpan<byte> data, ref T value) => Deserialize<T, TFormatter>(data, ref value);
        }

        /// <summary>Reads a new value with the <c>__Top</c> twin <typeparamref name="TFormatter"/> (<see cref="NinoStreams"/>).</summary>
        private readonly struct TopReader<T, TFormatter> : INinoStreamReader<T>
            where TFormatter : struct, INinoTopFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ReadOnlySpan<byte> data, ref T value) => value = DeserializeTop<T, TFormatter>(data);
        }

        /// <summary>Reads into an array as <see cref="DeserializePrefetched{T,TFormatter}(ReadOnlySpan{byte}, ref T[])"/>.</summary>
        private readonly struct PrefetchArrayReader<T, TFormatter> : INinoStreamReader<T[]>
            where TFormatter : struct, INinoFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ReadOnlySpan<byte> data, ref T[] value) => DeserializePrefetched<T, TFormatter>(data, ref value);
        }

        /// <summary>Reads into a list as <see cref="DeserializePrefetched{T,TFormatter}(ReadOnlySpan{byte}, ref List{T})"/>.</summary>
        private readonly struct PrefetchListReader<T, TFormatter> : INinoStreamReader<List<T>>
            where TFormatter : struct, INinoFormatter<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Read(ReadOnlySpan<byte> data, ref List<T> value) =>
                DeserializePrefetched<T, TFormatter>(data, ref value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ref Reader reader, ref T value)
            where TFormatter : struct, INinoFormatter<T> =>
            default(TFormatter).Deserialize(ref reader, ref value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T value = default;
            Deserialize<T, TFormatter>(ref reader, ref value);
            return value;
        }
    }
}
