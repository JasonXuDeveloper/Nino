using System;
using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
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
            // A null sealed class: its null flag (0), which a new array holds.
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(ReadOnlySpan<byte> data) where TFormatter : struct, INinoFormatter<T>
        {
            var reader = new Reader(data);
            T value = default;
            default(TFormatter).Deserialize(ref value, ref reader);
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
            default(TFormatter).Read(data);

        /// <summary>
        /// <see cref="NinoDeserializer.Deserialize{T}(ReadOnlySpan{byte})"/> of a value the generator found is copied
        /// as its raw bytes: a length check and a load. One return, whose value is loaded in the block that returns
        /// it: an inlinee with several returns (or a value that crosses a branch) passes a struct through a stack
        /// temporary, which a struct the JIT cannot promote (a raw struct with <c>Pack</c>) keeps in memory, a load
        /// and a store more per 8 bytes. A value that is not raw after all (a runtime whose layout is not the wire's) is read by the regular call into a heap copy, which it is loaded from.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T DeserializeRaw<T>(ReadOnlySpan<byte> data)
        {
            ref byte source = ref MemoryMarshal.GetReference(data);
            // Folded to one of the two branches.
            if (!NinoRaw.IsRead<T>())
                source = ref DeserializeOther<T>(data);
            else if (data.Length < Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            return NinoUnsafe.Read<T>(ref source);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ref byte DeserializeOther<T>(ReadOnlySpan<byte> data)
        {
            var copy = new T[1];
            copy[0] = NinoDeserializer.Deserialize<T>(data);
            return ref Unsafe.As<T, byte>(ref copy[0]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ReadOnlySpan<byte> data, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            var reader = new Reader(data);
            default(TFormatter).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ref T value, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T> =>
            default(TFormatter).Deserialize(ref value, ref reader);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T value = default;
            Deserialize<T, TFormatter>(ref value, ref reader);
            return value;
        }
    }
}
