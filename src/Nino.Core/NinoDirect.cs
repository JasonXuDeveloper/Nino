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
        public static byte[] Serialize<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T> =>
            SerializeToArray<T, TFormatter>(value);

        /// <summary>
        /// <see cref="NinoSerializer.Serialize{T}(T)"/> of a value whose size is fixed: the result is allocated at
        /// that size and the value written straight into it, with no scratch buffer, writer or copy. The bytes are
        /// the same.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] SerializeFixed<T, TFormatter>(T value)
            where TFormatter : struct, INinoFixedSizeFormatter<T> =>
            WriteFixed<T, TFormatter>(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static byte[] WriteFixed<T, TFormatter>(T value) where TFormatter : struct, INinoFixedSizeFormatter<T>
        {
            // A null sealed class: its null flag (0), which a new array holds.
            if (value == null)
                return new byte[1];
            var bytes = new byte[default(TFormatter).FixedSize];
            default(TFormatter).WriteFixed(value, ref NinoUnsafe.GetArrayDataReference(bytes));
            return bytes;
        }

        private static byte[] SerializeToArray<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            var buffer = NinoSerializer.RentScratch();
            try
            {
                Write<T, TFormatter>(value, buffer);
                return buffer.ToArray();
            }
            finally
            {
                NinoSerializer.ReturnScratch(buffer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(T value, IBufferWriter<byte> bufferWriter)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (bufferWriter is NinoArrayBufferWriter nino)
                Write<T, TFormatter>(value, nino);
            else
                SerializeOther(value, bufferWriter);
        }

        // Another buffer writer: the regular call, out of line so the inlined fast path stays small.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeOther<T>(T value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize(value, bufferWriter);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(T value, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T> =>
            default(TFormatter).Serialize(value, ref writer);

        private static void Write<T, TFormatter>(T value, NinoArrayBufferWriter buffer)
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
            if (!NinoRaw.Is<T>())
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
