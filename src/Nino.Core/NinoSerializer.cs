using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    public static class NinoSerializer
    {
        private static readonly ConcurrentQueue<NinoArrayBufferWriter> BufferWriters = new();

        private static readonly NinoArrayBufferWriter
            DefaultBufferWriter = new(2048); // Slightly larger but not excessive

        private static int _defaultUsed;

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

            CachedSerializer<T>.Serialize(value, ref writer);
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
        public static void SerializeBoxed(object value, ref Writer writer, Type type)
        {
            if (value == null)
            {
                writer.Write(TypeCollector.Null);
                return;
            }

            if (type == null)
                throw new ArgumentNullException(nameof(type), "Type cannot be null when serializing boxed objects.");

            if (!NinoTypeMetadata.Serializers.TryGetValue(type.TypeHandle.Value, out var serializer))
            {
                throw new Exception(
                    $"Serializer not found for type {type.FullName}, if this is an unmanaged type, please use Serialize<T>(T value, ref Writer writer) instead.");
            }

            serializer(value, ref writer);
        }
    }

    public delegate void SerializeDelegate<TVal>(TVal value, ref Writer writer);

    public delegate void SerializeDelegateBoxed(object value, ref Writer writer);


#pragma warning disable CA1000 // Do not declare static members on generic types
    [SuppressMessage("ReSharper", "StaticMemberInGenericType")]
    public static class CachedSerializer<T>
    {
        public static SerializeDelegate<T> Serializer;
        public static readonly FastMap<IntPtr, SerializeDelegate<T>> SubTypeSerializers = new();

        // Inline cache for polymorphic serialization
        private static IntPtr _cachedTypeHandle;
        private static SerializeDelegate<T> _cachedSerializer;

        // Cache expensive type checks
        internal static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        // ReSharper disable once StaticMemberInGenericType
        private static readonly IntPtr TypeHandle = typeof(T).TypeHandle.Value;

        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool HasBaseType = NinoTypeMetadata.HasBaseType(typeof(T));

        // ReSharper disable once StaticMemberInGenericType
        private static readonly bool IsSealed = typeof(T).IsSealed || typeof(T).IsValueType;

        // ULTIMATE: JIT-eliminated constant for maximum performance
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool IsSimpleType = !IsReferenceOrContainsReferences && !HasBaseType;

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)] // Cold exception path
        private static void ThrowInvalidCast(Type actualType) =>
            throw new InvalidCastException($"Cannot cast {actualType?.FullName ?? "null"} to {typeof(T).FullName}");

        public static void AddSubTypeSerializer<TSub>(SerializeDelegate<TSub> serializer) where TSub : T
        {
            // Use a static generic helper class to create an inlineable wrapper
            SubTypeSerializerWrapper<TSub>.SubSerializer = serializer;
            SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value, SubTypeSerializerWrapper<TSub>.SerializeWrapper);
        }

        // Static wrapper class per TSub - allows better inlining than lambda
        private static class SubTypeSerializerWrapper<TSub> where TSub : T
        {
            public static SerializeDelegate<TSub> SubSerializer;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SerializeWrapper(T val, ref Writer writer)
            {
                if (val is TSub sub)
                {
                    // This can be inlined by JIT since it's a static method call with known target
                    SubSerializer(sub, ref writer);
                }
                else
                {
                    ThrowInvalidCast(val?.GetType());
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeBoxed(object value, ref Writer writer)
        {
            if (value == null)
                Serializer(default, ref writer);
            else if (value is T val) Serializer(val, ref writer);
        }

        // ULTRA-OPTIMIZED: Single core method with all paths optimized
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(T val, ref Writer writer)
        {
            // FASTEST PATH: JIT-eliminated branch for simple types
            if (IsSimpleType)
            {
                writer.UnsafeWrite(val);
                return;
            }

            // FAST PATH 2: JIT-eliminated branch for sealed types
            // If T is sealed or a value type, it CANNOT have a different runtime type
            // This completely eliminates the need for GetType() calls
            if (IsSealed || SubTypeSerializers.Count == 0)
            {
                // DIRECT DELEGATE: Generated code path - no polymorphism possible
                Serializer(val, ref writer);
            }
            else
            {
                SerializePolymorphic(val, ref writer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SerializePolymorphic(T val, ref Writer writer)
        {
            if (val == null)
            {
                writer.Write(TypeCollector.Null);
                return;
            }

            // Get type handle - optimized for .NET 5.0+ to avoid expensive GetType() call
            // Read RuntimeTypeHandle.Value directly from object header via double pointer dereference
#if NET5_0_OR_GREATER
            // Object header at offset 0 contains a pointer to the type handle
            // Double dereference: first * gets the pointer, second * gets the RuntimeTypeHandle.Value
            IntPtr actualTypeHandle = **(IntPtr**)Unsafe.AsPointer(ref val);
#else
            // Fallback to GetType() for older runtimes
            IntPtr actualTypeHandle = val.GetType().TypeHandle.Value;
#endif

            // FAST PATH 1: Inline cache hit (most common for homogeneous batches)
            // Check this FIRST - single pointer comparison, very cheap
            if (actualTypeHandle == _cachedTypeHandle)
            {
                _cachedSerializer(val, ref writer);
                return;
            }

            // FAST PATH 2: Base type (common for non-polymorphic usage)
            if (actualTypeHandle == TypeHandle)
            {
                Serializer(val, ref writer);
                return;
            }

            // SLOW PATH: Full lookup in subtype map
            // Handle subtype serialization
            if (SubTypeSerializers.TryGetValue(actualTypeHandle, out var subTypeSerializer))
            {
                (_cachedTypeHandle, _cachedSerializer) = (actualTypeHandle, subTypeSerializer);
                subTypeSerializer(val, ref writer);
                return;
            }

            throw new Exception($"Serializer not found for type {val.GetType().FullName}");
        }
    }
#pragma warning restore CA1000
}