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
    /// instead of through <see cref="NinoFormatterCache{T}"/> (a virtual call). The bytes are the same; a formatter
    /// registered at run time with <see cref="NinoFormatterCache{T}.Register{TFormatter}"/> still takes precedence.
    /// Raw values and arrays of raw elements are never intercepted: the regular calls copy them directly.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoDirect
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T, TFormatter>(T value) where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoTopFormatter<T>.Replaced)
                return SerializeReplaced(value);
            return SerializeToArray<T, TFormatter>(value);
        }

        // The paths taken when a formatter was registered at run time are calls: inlined, they would use up the
        // JIT's inlining budget of the caller before the generated formatter's own helpers are inlined.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] SerializeReplaced<T>(T value) => NinoSerializer.Serialize(value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeReplaced<T>(T value, IBufferWriter<byte> bufferWriter) =>
            NinoSerializer.Serialize(value, bufferWriter);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SerializeReplaced<T>(T value, ref Writer writer) =>
            NinoSerializer.Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeReplaced<T>(ReadOnlySpan<byte> data, ref T value) =>
            NinoDeserializer.Deserialize(data, ref value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeReplaced<T>(ref T value, ref Reader reader) =>
            NinoDeserializer.Deserialize(ref value, ref reader);

        /// <summary>
        /// <see cref="NinoSerializer.Serialize{T}(T)"/> of a value whose size is fixed: the result is allocated at
        /// that size and the value written straight into it, with no scratch buffer, writer or copy. The bytes are
        /// the same.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] SerializeFixed<T, TFormatter>(T value) where TFormatter : struct, INinoFixedSizeFormatter<T>
        {
            if (NinoTopFormatter<T>.Replaced)
                return SerializeReplaced(value);
            return WriteFixed<T, TFormatter>(value);
        }

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
            if (!NinoTopFormatter<T>.Replaced && bufferWriter is NinoArrayBufferWriter nino)
                Write<T, TFormatter>(value, nino);
            else
                SerializeReplaced(value, bufferWriter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(T value, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoTopFormatter<T>.Replaced)
            {
                // Through a copy: the writer's address reaching a call would keep its fields in memory.
                var copy = writer;
                SerializeReplaced(value, ref copy);
                writer = copy;
            }
            else
                default(TFormatter).Serialize(value, ref writer);
        }

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
            // The replaced-formatter path is out of line: inlined, it takes the address of value and of a second
            // Reader, which keeps value in memory and zeroes both in the prolog of every call.
            if (NinoTopFormatter<T>.Replaced)
                return DeserializeReplaced<T>(data);

            var reader = new Reader(data);
            T value = default;
            default(TFormatter).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
            return value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T DeserializeReplaced<T>(ReadOnlySpan<byte> data) => NinoDeserializer.Deserialize<T>(data);

        /// <summary>
        /// <see cref="NinoDeserializer.Deserialize{T}(ReadOnlySpan{byte})"/> of a value the generator found is copied
        /// as its raw bytes: a length check and a load. One return, whose value is loaded in the block that returns
        /// it: an inlinee with several returns (or a value that crosses a branch) passes a struct through a stack
        /// temporary, which a struct the JIT cannot promote (a raw struct with <c>Pack</c>) keeps in memory, a load
        /// and a store more per 8 bytes. A value that is not raw after all (a formatter registered at run time, a runtime
        /// whose layout is not the wire's) is read by the regular call into a heap copy, which it is loaded from.
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
            if (NinoTopFormatter<T>.Replaced)
            {
                DeserializeReplaced(data, ref value);
                return;
            }

            var reader = new Reader(data);
            default(TFormatter).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ref T value, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoTopFormatter<T>.Replaced)
            {
                // Through a copy, as in Serialize.
                var copy = reader;
                DeserializeReplaced(ref value, ref copy);
                reader = copy;
            }
            else
                default(TFormatter).Deserialize(ref value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T, TFormatter>(ref Reader reader) where TFormatter : struct, INinoFormatter<T>
        {
            T value = default;
            Deserialize<T, TFormatter>(ref value, ref reader);
            return value;
        }
    }
}
