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
        private static readonly NinoArrayBufferWriter DefaultBufferWriter = new(2048); // Slightly larger but not excessive
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
            // Fast path - use default buffer if available
            if (Interlocked.CompareExchange(ref _defaultUsed, 1, 0) == 0)
            {
                return DefaultBufferWriter;
            }

            // Try to reuse from pool
            if (BufferWriters.TryDequeue(out var bufferWriter))
            {
                return bufferWriter;
            }

            // Create new with reasonable initial capacity
            return new NinoArrayBufferWriter(2048);
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

        // ULTIMATE: Zero-overhead single entry point
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T value, ref Writer writer)
        {
            // JIT completely eliminates this branch for unmanaged types
            if (CachedSerializer<T>.IsSimpleType)
            {
                writer.UnsafeWrite(value);
                return;
            }

            CachedSerializer<T>.Instance.SerializeCore(value, ref writer);
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
            if (value == null)
            {
                writer.Write(TypeCollector.Null);
                return;
            }
            
            if (type == null)
                throw new ArgumentNullException(nameof(type), "Type cannot be null when serializing boxed objects.");

            if (!NinoTypeMetadata.Serializers.TryGetValue(type.TypeHandle.Value.ToInt64(), out var serializer))
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

    public class CustomSerializer<T> : ICachedSerializer
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
        public readonly FastMap<long, SerializeDelegate<T>> SubTypeSerializers = new();

        public CachedSerializer(SerializeDelegate<T> serializer)
        {
            Serializer = serializer;
        }

        public static CachedSerializer<T> Instance = new(null);

        // Cache expensive type checks
        internal static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        // ReSharper disable once StaticMemberInGenericType
        private static readonly long TypeHandle = typeof(T).TypeHandle.Value.ToInt64();

        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool HasBaseType = NinoTypeMetadata.HasBaseType(typeof(T));

        // ULTIMATE: JIT-eliminated constant for maximum performance
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool IsSimpleType = !IsReferenceOrContainsReferences && !HasBaseType;

        public void AddSubTypeSerializer<TSub>(SerializeDelegate<TSub> serializer)
        {
            if (typeof(TSub).IsValueType)
            {
                // cast TSub to T via boxing, T here must be interface, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value.ToInt64(), (T val, ref Writer writer) =>
                    serializer((TSub)(object)val, ref writer));
            }
            else
            {
                // simply cast TSub to T directly, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value.ToInt64(), (T val, ref Writer writer) =>
                    serializer(Unsafe.As<T, TSub>(ref val), ref writer));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeBoxed(object value, ref Writer writer)
        {
            Serialize((T)value, ref writer);
        }

        // ULTRA-OPTIMIZED: Single core method with all paths optimized
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T val, ref Writer writer) => SerializeCore(val, ref writer);
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SerializeCore(T val, ref Writer writer)
        {
            // FASTEST PATH: JIT-eliminated branch for simple types
            if (IsSimpleType)
            {
                writer.UnsafeWrite(val);
                return;
            }
            
            // ULTRA-OPTIMIZED: Compile-time specialization based on type characteristics
            if (CustomSerializer<T>.Instance != null)
            {
                CustomSerializer<T>.Instance.Serializer(val, ref writer);
            }
            else if (SubTypeSerializers.Count != 0)
            {
                SerializePolymorphic(val, ref writer);
            }
            else
            {
                // DIRECT DELEGATE: Generated code path - no null check needed
                Serializer(val, ref writer);
            }
        }
        
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)] // Cold path: Profile-guided optimization
        private void SerializePolymorphic(T val, ref Writer writer)
        {
            if (val == null)
            {
                Serializer(default, ref writer);
                return;
            }

            long actualTypeHandle = val.GetType().TypeHandle.Value.ToInt64();

            // Check if it's the same type first (most common case)
            if (actualTypeHandle == TypeHandle)
            {
                Serializer(val, ref writer);
                return;
            }

            // Handle subtype serialization
            if (SubTypeSerializers.TryGetValue(actualTypeHandle, out var subTypeSerializer))
            {
                subTypeSerializer(val, ref writer);
                return;
            }

            throw new Exception($"Serializer not found for type {val.GetType().FullName}");
        }
    }
#pragma warning restore CA1000

    // REMOVED: UltraFastSerializers class - redundant with optimized generic paths
    // The main serializer now achieves the same zero-overhead performance for all unmanaged types
}