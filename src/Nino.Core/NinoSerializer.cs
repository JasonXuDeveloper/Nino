using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    public static class NinoSerializer
    {
        private static readonly object Lock = new();

        private static readonly ConcurrentQueue<NinoArrayBufferWriter> BufferWriters = new();
        private static readonly NinoArrayBufferWriter DefaultBufferWriter = new(1024);
        private static int _defaultUsed;

        /// <summary>
        /// Registers a custom serializer for a type.
        /// This method allows you to provide a custom serialization logic for a specific type.
        /// The serializer will be used when serializing instances of that type.
        /// </summary>
        /// <param name="serializer"></param>
        /// <typeparam name="T"></typeparam>
        public static void AddCustomSerializer<T>(SerializeDelegate<T> serializer)
        {
            lock (Lock)
            {
                CustomSerializer<T>.Instance = new CustomSerializer<T>(serializer);
            }
        }

        /// <summary>
        /// Removes a custom serializer for a type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public static void RemoveCustomSerializer<T>()
        {
            lock (Lock)
            {
                CustomSerializer<T>.Instance = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoArrayBufferWriter GetBufferWriter()
        {
            // Fast path
            if (Interlocked.CompareExchange(ref _defaultUsed, 1, 0) == 0)
            {
                return DefaultBufferWriter;
            }

            if (BufferWriters.Count == 0)
            {
                return new NinoArrayBufferWriter(1024);
            }

            if (BufferWriters.TryDequeue(out var bufferWriter))
            {
                return bufferWriter;
            }

            return new NinoArrayBufferWriter(1024);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReturnBufferWriter(NinoArrayBufferWriter bufferWriter)
        {
#if NET8_0_OR_GREATER
            bufferWriter.ResetWrittenCount();
#else
            bufferWriter.Clear();
#endif
            // Check if the buffer writer is the default buffer writer
            if (bufferWriter == DefaultBufferWriter)
            {
                // Ensure it is in use, otherwise throw an exception
                if (Interlocked.CompareExchange(ref _defaultUsed, 0, 1) == 0)
                {
                    throw new InvalidOperationException("The returned buffer writer is not in use.");
                }

                return;
            }

            BufferWriters.Enqueue(bufferWriter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T value)
        {
            var bufferWriter = GetBufferWriter();
            try
            {
                var writer = new Writer(bufferWriter);
                Serialize(value, ref writer);
                return bufferWriter.WrittenSpan.ToArray();
            }
            finally
            {
                ReturnBufferWriter(bufferWriter);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T value, INinoBufferWriter bufferWriter)
        {
            var writer = new Writer(bufferWriter);
            Serialize(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T value, ref Writer writer)
        {
            // Check if T is a custom serializer
            var customSerializer = CustomSerializer<T>.Instance;
            if (customSerializer != null)
            {
                customSerializer.Serializer(value, ref writer);
                return;
            }

            // Fast path for simple types
            if (!CachedSerializer<T>.IsReferenceOrContainsReferences && !CachedSerializer<T>.HasBaseType)
            {
                writer.UnsafeWrite(value);
                return;
            }

            var cachedSerializer = CachedSerializer<T>.Instance;
            if (cachedSerializer.SubTypeSerializers.Count == 0)
            {
                // No polymorphism - direct serialization
                cachedSerializer.Serializer(value, ref writer);
                return;
            }

            // Handle polymorphism
            cachedSerializer.Serialize(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize(object value)
        {
            var bufferWriter = GetBufferWriter();
            try
            {
                var writer = new Writer(bufferWriter);
                SerializeBoxed(value, ref writer, value?.GetType());
                return bufferWriter.WrittenSpan.ToArray();
            }
            finally
            {
                ReturnBufferWriter(bufferWriter);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(object value, INinoBufferWriter bufferWriter)
        {
            Writer writer = new Writer(bufferWriter);
            SerializeBoxed(value, ref writer, value?.GetType());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SerializeBoxed(object value, ref Writer writer, Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type), "Type cannot be null when serializing boxed objects.");

            if (!NinoTypeMetadata.Serializers.TryGetValue(type.TypeHandle.Value, out var serializer))
            {
                throw new Exception(
                    $"Serializer not found for type {type.FullName}, if this is an unmanaged type, please use Serialize<T>(T value, ref Writer writer) instead.");
            }

            serializer.SerializeBoxed(value, ref writer);
        }
    }

    public delegate void SerializeDelegate<in TVal>(TVal value, ref Writer writer);

    public interface ICachedSerializer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SerializeBoxed(object value, ref Writer writer);
    }

    internal class CustomSerializer<T> : ICachedSerializer
    {
        public readonly SerializeDelegate<T> Serializer;

        public static CustomSerializer<T> Instance;

        public CustomSerializer(SerializeDelegate<T> serializer)
        {
            Serializer = serializer;
        }

        public void SerializeBoxed(object value, ref Writer writer)
        {
            if (value == null)
            {
                writer.Write(TypeCollector.Null);
                return;
            }

            Serializer((T)value, ref writer);
        }
    }

#pragma warning disable CA1000 // Do not declare static members on generic types
    public class CachedSerializer<T> : ICachedSerializer
    {
        public readonly SerializeDelegate<T> Serializer;
        public readonly FastMap<IntPtr, SerializeDelegate<T>> SubTypeSerializers = new();

        public CachedSerializer(SerializeDelegate<T> serializer)
        {
            Serializer = serializer;
        }

        internal static CachedSerializer<T> Instance = new(null);

        // Cache expensive type checks
        internal static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        // ReSharper disable once StaticMemberInGenericType
        private static readonly IntPtr TypeHandle = typeof(T).TypeHandle.Value;

        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool HasBaseType = NinoTypeMetadata.HasBaseType(typeof(T));

        public void AddSubTypeSerializer<TSub>(SerializeDelegate<TSub> serializer)
        {
            if (typeof(TSub).IsValueType)
            {
                // cast TSub to T via boxing, T here must be interface, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
                    serializer((TSub)(object)val, ref writer));
            }
            else
            {
                // simply cast TSub to T directly, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
                    serializer(Unsafe.As<T, TSub>(ref val), ref writer));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeBoxed(object value, ref Writer writer)
        {
            Serialize((T)value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T val, ref Writer writer)
        {
            // Check if T is a custom serializer
            var customSerializer = CustomSerializer<T>.Instance;
            if (customSerializer != null)
            {
                customSerializer.Serializer(val, ref writer);
                return;
            }

            // Fast path for simple types
            if (!IsReferenceOrContainsReferences && !HasBaseType)
            {
                writer.UnsafeWrite(val);
                return;
            }

            // Only check for polymorphism if we have subtypes registered
            if (SubTypeSerializers.Count > 0 && val != null)
            {
                IntPtr actualTypeHandle = val.GetType().TypeHandle.Value;

                // Check if it's the same type (most common case)
                if (actualTypeHandle == TypeHandle)
                {
                    Serializer(val, ref writer);
                    return;
                }

                // Handle subtype serialization
                if (!SubTypeSerializers.TryGetValue(actualTypeHandle, out var subTypeSerializer))
                {
                    throw new Exception(
                        $"Serializer not found for type {val.GetType().FullName}");
                }

                subTypeSerializer(val, ref writer);
                return;
            }

            // No polymorphism - direct serialization
            Serializer(val, ref writer);
        }
    }
#pragma warning restore CA1000
}