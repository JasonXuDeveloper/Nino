using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    /// <summary>Deserializes Nino binary data. All methods are thread safe.</summary>
    public static class NinoDeserializer
    {
        /// <summary>Deserializes a new value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ReadOnlySpan<byte> data)
        {
            // Raw values inline into the caller as a length check and a load.
            if (NinoRaw.Is<T>())
                return ReadRaw<T>(data);
            return DeserializeFormatted<T>(data);
        }

        // Inlined into the caller, where T is usually exact: the formatter cache is then a static field load rather
        // than a generic dictionary lookup (T a reference type makes this method shared code).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T DeserializeFormatted<T>(ReadOnlySpan<byte> data)
        {
            var reader = new Reader(data);
            T value = default;
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
            return value;
        }

        /// <summary>
        /// Deserializes into <paramref name="value"/>, reusing the existing instance (and nested instances) where
        /// possible. Instances that are dropped are passed to <see cref="NinoRecycler{T}"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ReadOnlySpan<byte> data, ref T value)
        {
            if (NinoRaw.Is<T>())
            {
                value = ReadRaw<T>(data);
                return;
            }

            var reader = new Reader(data);
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        /// <summary>Same as <see cref="Deserialize{T}(ReadOnlySpan{byte}, ref T)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ReadOnlySpan<byte> data) => Deserialize(data, ref value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T ReadRaw<T>(ReadOnlySpan<byte> data)
        {
            if (data.Length < Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            return NinoUnsafe.Read<T>(ref MemoryMarshal.GetReference(data));
        }

        /// <summary>Same as <see cref="Deserialize{T}(ref T, ref Reader)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ref Reader reader) => Deserialize(ref value, ref reader);

        /// <summary>Deserializes into <paramref name="value"/> with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ref T value, ref Reader reader)
        {
            if (NinoRaw.Is<T>())
            {
                value = reader.UnsafeRead<T>();
                return;
            }

            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
        }

        /// <summary>Deserializes a new value with an existing reader.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ref Reader reader)
        {
            if (NinoRaw.Is<T>())
                return reader.UnsafeRead<T>();

            T value = default;
            NinoFormatterCache<T>.Instance.Deserialize(ref value, ref reader);
            return value;
        }

        /// <summary>Deserializes a value of <paramref name="type"/> and returns it boxed.</summary>
        public static object Deserialize(ReadOnlySpan<byte> data, Type type)
        {
            object value = null;
            Deserialize(data, type, ref value);
            return value;
        }

        /// <summary>Deserializes a value of <paramref name="type"/> into <paramref name="value"/>.</summary>
        public static void Deserialize(ReadOnlySpan<byte> data, Type type, ref object value)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            var reader = new Reader(data);
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
            reader.ReleaseReferences();
        }

        /// <summary>Deserializes a value of <paramref name="type"/> with an existing reader.</summary>
        public static void DeserializeBoxed(ref object value, ref Reader reader, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            BoxedFormatter.Get(type).Deserialize(ref value, ref reader);
        }
    }
}
