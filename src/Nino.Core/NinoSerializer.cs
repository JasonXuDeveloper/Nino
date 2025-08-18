using System;
using System.Collections.Concurrent;
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

            CachedSerializer<T>.Instance.Serialize(value, ref writer);
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

            serializer.SerializeBoxed(value, ref writer);
        }
    }

    public delegate void SerializeDelegate<in TVal>(TVal value, ref Writer writer);

    public interface ICachedSerializer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SerializeBoxed(object value, ref Writer writer);
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

        public static CachedSerializer<T> Instance = new(null);

        // Cache expensive type checks
        internal static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        // ReSharper disable once StaticMemberInGenericType
        private static readonly IntPtr TypeHandle = typeof(T).TypeHandle.Value;

        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool HasBaseType = NinoTypeMetadata.HasBaseType(typeof(T));

        // ULTIMATE: JIT-eliminated constant for maximum performance
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool IsSimpleType = !IsReferenceOrContainsReferences && !HasBaseType;

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)] // Cold exception path
        private static void ThrowInvalidCast(Type actualType) =>
            throw new InvalidCastException($"Cannot cast {actualType?.FullName ?? "null"} to {typeof(T).FullName}");


        public void AddSubTypeSerializer<TSub>(SerializeDelegate<TSub> serializer)
        {
            if (typeof(TSub).IsValueType)
            {
                // cast T to TSub via boxing, T here must be interface, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
                {
                    if (val is TSub sub)
                    {
                        // Fast path: already the correct type
                        serializer(sub, ref writer);
                        return;
                    }

                    ThrowInvalidCast(val?.GetType());
                });
            }
            else
            {
                // simply cast TSub to T directly, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
                {
                    switch (val)
                    {
                        case null:
                            // Handle null case
                            serializer(default, ref writer);
                            return;
                        case TSub sub:
                            // Fast path: already the correct type
                            serializer(sub, ref writer);
                            return;
                    }

                    ThrowInvalidCast(val.GetType());
                });
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeBoxed(object value, ref Writer writer)
        {
            switch (value)
            {
                case null:
                    Serializer(default, ref writer);
                    break;
                case T val:
                    Serializer(val, ref writer);
                    break;
            }
        }

        // ULTRA-OPTIMIZED: Single core method with all paths optimized
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T val, ref Writer writer)
        {
            // FASTEST PATH: JIT-eliminated branch for simple types
            if (IsSimpleType)
            {
                writer.UnsafeWrite(val);
                return;
            }

            // OPTIMIZED: Direct serialization with polymorphism support
            if (SubTypeSerializers.Count != 0)
            {
                SerializePolymorphic(val, ref writer);
            }
            else
            {
                // DIRECT DELEGATE: Generated code path - no null check needed
                Serializer(val, ref writer);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining |
                    MethodImplOptions.NoOptimization)] // Cold path: Profile-guided optimization
        private void SerializePolymorphic(T val, ref Writer writer)
        {
            if (val == null)
            {
                writer.Write(TypeCollector.Null);
                return;
            }

            IntPtr actualTypeHandle = val.GetType().TypeHandle.Value;

            // Check if it's the same type first (most common case)
            if (actualTypeHandle == TypeHandle)
            {
                Serializer(val, ref writer);
                return;
            }

            // Handle subtype serialization
            if (!SubTypeSerializers.TryGetValue(actualTypeHandle, out var subTypeSerializer))
                throw new Exception($"Serializer not found for type {val.GetType().FullName}");
            subTypeSerializer(val, ref writer);
        }
    }
#pragma warning restore CA1000
}