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
        [ThreadStatic] private static NinoArrayBufferWriter _threadLocalBufferWriter;
        [ThreadStatic] private static bool _threadLocalBufferInUse;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoArrayBufferWriter GetBufferWriter()
        {
            // Fast path - use default buffer if available (single interlocked op, no branches)
            if (Interlocked.CompareExchange(ref _defaultUsed, 1, 0) == 0)
            {
                return DefaultBufferWriter;
            }

            // Fallback to thread-local buffer
            if (!_threadLocalBufferInUse)
            {
                var local = _threadLocalBufferWriter;
                if (local == null)
                {
                    local = new NinoArrayBufferWriter(2048);
                    _threadLocalBufferWriter = local;
                }

                _threadLocalBufferInUse = true;
                return local;
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

            // Fast path - return default buffer (most common case in single-threaded scenarios)
            // Direct reference comparison - JIT will optimize this to a single pointer comparison
            if (bufferWriter == DefaultBufferWriter)
            {
                Interlocked.Exchange(ref _defaultUsed, 0);
                return;
            }

            // Thread-local buffer
            if (bufferWriter == _threadLocalBufferWriter)
            {
                _threadLocalBufferInUse = false;
                return;
            }

            // Return to pool for reuse
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

            if (serializer == null)
            {
                var ns = NinoHelper.GetGeneratedNamespace(type);
                throw new Exception(NinoHelper.GetRegistrationErrorMessage(type.FullName, ns));
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
        private static SerializeDelegate<T> _serializer;
        public static readonly FastMap<IntPtr, SerializeDelegate<T>> SubTypeSerializers = new();

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

        public static void SetSerializer(SerializeDelegate<T> serializer)
        {
            _serializer = serializer;
        }

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
            private static readonly bool IsValueType = typeof(TSub).IsValueType;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void SerializeWrapper(T val, ref Writer writer)
            {
                if (IsValueType)
                {
                    SubSerializer((TSub)(object)val!, ref writer);
                }
                else
                {
                    // Runtime handle check already guaranteed T is TSub so skip casts for reference types.
                    SubSerializer(Unsafe.As<T, TSub>(ref val), ref writer);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeBoxed(object value, ref Writer writer)
        {
            if (_serializer == null)
            {
                var ns = NinoHelper.GetGeneratedNamespace(typeof(T));
                throw new Exception(NinoHelper.GetRegistrationErrorMessage(typeof(T).FullName, ns));
            }

            if (value == null)
                _serializer(default, ref writer);
            else if (value is T val) _serializer(val, ref writer);
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

            SerializePolymorphic(val, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void SerializePolymorphic(T val, ref Writer writer)
        {
            // FAST PATH 2: JIT-eliminated branch for sealed types
            // If T is sealed or a value type, it CANNOT have a different runtime type
            // This completely eliminates the need for GetType() calls
            if (IsSealed || SubTypeSerializers.Count == 0)
            {
                // DIRECT DELEGATE: Generated code path - no polymorphism possible
                if (_serializer == null)
                {
                    var ns = NinoHelper.GetGeneratedNamespace(typeof(T));
                    throw new Exception(NinoHelper.GetRegistrationErrorMessage(typeof(T).FullName, ns));
                }

                _serializer(val, ref writer);
                return;
            }

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

            // FAST PATH: Base type (common for non-polymorphic usage)
            if (actualTypeHandle == TypeHandle)
            {
                if (_serializer == null)
                {
                    var ns = NinoHelper.GetGeneratedNamespace(typeof(T));
                    throw new Exception(NinoHelper.GetRegistrationErrorMessage(typeof(T).FullName, ns));
                }

                _serializer(val, ref writer);
                return;
            }

            if (SubTypeSerializers.Count == 1)
            {
                SubTypeSerializers.FirstValue()(val, ref writer);
                return;
            }

            // Check expanded 8-entry inline cache using bitwise AND indexing
            // This significantly improves hit rate for patterns with 2-8 alternating types
            int cacheSlot = (int)actualTypeHandle & 7;  // Faster than % 8 for power-of-2
            SerializeDelegate<T> cachedSer;
            switch (cacheSlot)
            {
                case 0:
                    if (actualTypeHandle == writer.CachedTypeHandle0 &&
                        writer.CachedSerializer0 is SerializeDelegate<T> cached0)
                    {
                        cached0(val, ref writer);
                        return;
                    }
                    break;
                case 1:
                    if (actualTypeHandle == writer.CachedTypeHandle1 &&
                        writer.CachedSerializer1 is SerializeDelegate<T> cached1)
                    {
                        cached1(val, ref writer);
                        return;
                    }
                    break;
                case 2:
                    if (actualTypeHandle == writer.CachedTypeHandle2 &&
                        writer.CachedSerializer2 is SerializeDelegate<T> cached2)
                    {
                        cached2(val, ref writer);
                        return;
                    }
                    break;
                case 3:
                    if (actualTypeHandle == writer.CachedTypeHandle3 &&
                        writer.CachedSerializer3 is SerializeDelegate<T> cached3)
                    {
                        cached3(val, ref writer);
                        return;
                    }
                    break;
                case 4:
                    if (actualTypeHandle == writer.CachedTypeHandle4 &&
                        writer.CachedSerializer4 is SerializeDelegate<T> cached4)
                    {
                        cached4(val, ref writer);
                        return;
                    }
                    break;
                case 5:
                    if (actualTypeHandle == writer.CachedTypeHandle5 &&
                        writer.CachedSerializer5 is SerializeDelegate<T> cached5)
                    {
                        cached5(val, ref writer);
                        return;
                    }
                    break;
                case 6:
                    if (actualTypeHandle == writer.CachedTypeHandle6 &&
                        writer.CachedSerializer6 is SerializeDelegate<T> cached6)
                    {
                        cached6(val, ref writer);
                        return;
                    }
                    break;
                case 7:
                    if (actualTypeHandle == writer.CachedTypeHandle7 &&
                        writer.CachedSerializer7 is SerializeDelegate<T> cached7)
                    {
                        cached7(val, ref writer);
                        return;
                    }
                    break;
            }

            // Cache miss - look up in FastMap and update cache
            if (SubTypeSerializers.TryGetValue(actualTypeHandle, out var subTypeSerializer))
            {
                // Update the cache slot for this type handle
                switch (cacheSlot)
                {
                    case 0:
                        writer.CachedTypeHandle0 = actualTypeHandle;
                        writer.CachedSerializer0 = subTypeSerializer;
                        break;
                    case 1:
                        writer.CachedTypeHandle1 = actualTypeHandle;
                        writer.CachedSerializer1 = subTypeSerializer;
                        break;
                    case 2:
                        writer.CachedTypeHandle2 = actualTypeHandle;
                        writer.CachedSerializer2 = subTypeSerializer;
                        break;
                    case 3:
                        writer.CachedTypeHandle3 = actualTypeHandle;
                        writer.CachedSerializer3 = subTypeSerializer;
                        break;
                    case 4:
                        writer.CachedTypeHandle4 = actualTypeHandle;
                        writer.CachedSerializer4 = subTypeSerializer;
                        break;
                    case 5:
                        writer.CachedTypeHandle5 = actualTypeHandle;
                        writer.CachedSerializer5 = subTypeSerializer;
                        break;
                    case 6:
                        writer.CachedTypeHandle6 = actualTypeHandle;
                        writer.CachedSerializer6 = subTypeSerializer;
                        break;
                    case 7:
                        writer.CachedTypeHandle7 = actualTypeHandle;
                        writer.CachedSerializer7 = subTypeSerializer;
                        break;
                }
                subTypeSerializer(val, ref writer);
                return;
            }

            throw new Exception($"Serializer not found for type {val.GetType().FullName}");
        }
    }
#pragma warning restore CA1000
}