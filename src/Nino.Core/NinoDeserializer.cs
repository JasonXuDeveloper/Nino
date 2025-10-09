using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public static class NinoDeserializer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ReadOnlySpan<byte> data)
        {
            var reader = new Reader(data);
            Deserialize(out T value, ref reader);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ReadOnlySpan<byte> data, ref T value)
        {
            var reader = new Reader(data);
            DeserializeRef(ref value, ref reader);
        }

        // ULTIMATE: Zero-overhead single entry point
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ref Reader reader)
        {
            // EOF check once for all paths - branch predictor friendly
            if (reader.Eof)
            {
                value = default;
                return;
            }

            // JIT completely eliminates this for unmanaged types
            if (CachedDeserializer<T>.IsSimpleType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            CachedDeserializer<T>.DeserializeRef(ref value, ref reader);
        }

        // ULTIMATE: Zero-overhead single entry point
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(out T value, ref Reader reader)
        {
            // Single branch check - branch predictor optimized
            if (reader.Eof)
            {
                value = default;
                return;
            }

            // JIT completely eliminates this branch for unmanaged types
            if (CachedDeserializer<T>.IsSimpleType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            CachedDeserializer<T>.Deserialize(out value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object Deserialize(ReadOnlySpan<byte> data, Type type)
        {
            var reader = new Reader(data);
            return DeserializeBoxed(ref reader, type);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize(ReadOnlySpan<byte> data, Type type, ref object value)
        {
            var reader = new Reader(data);
            DeserializeRefBoxed(ref value, ref reader, type);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object DeserializeBoxed(ref Reader reader, Type type = null)
        {
            // Check for null value first
            reader.Peak(out int typeId);
            if (typeId == TypeCollector.Null)
            {
                reader.Advance(4);
                return null;
            }

            // If type is null, resolve it from the type ID in the stream
            if (type == null)
            {
                if (!NinoTypeMetadata.TypeIdToDeserializer.TryGetValue(typeId, out var deserializer))
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}");
                }

                return deserializer.outOverload(ref reader);
            }

            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var typeDeserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            return typeDeserializer.outOverload(ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRefBoxed(ref object val, ref Reader reader, Type type = null)
        {
            // Check for null value first
            reader.Peak(out int typeId);
            if (typeId == TypeCollector.Null)
            {
                val = null;
                reader.Advance(4);
                return;
            }


            // If type is null, resolve it from the type ID in the stream
            if (type == null)
            {
                if (!NinoTypeMetadata.TypeIdToDeserializer.TryGetValue(typeId, out var deserializer))
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}");
                }

                deserializer.refOverload(ref val, ref reader);
                return;
            }

            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var typeDeserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            typeDeserializer.refOverload(ref val, ref reader);
        }
    }

    public delegate void DeserializeDelegate<T>(out T result, ref Reader reader);

    public delegate void DeserializeDelegateRef<T>(ref T result, ref Reader reader);

    public delegate object DeserializeDelegateBoxed(ref Reader reader);

    public delegate void DeserializeDelegateRefBoxed(ref object value, ref Reader reader);


#pragma warning disable CA1000 // Do not declare static members on generic types
    [SuppressMessage("ReSharper", "StaticMemberInGenericType")]
    public static class CachedDeserializer<T>
    {
        private static DeserializeDelegate<T> _deserializer;
        private static DeserializeDelegateRef<T> _deserializerRef;
        private static readonly FastMap<int, DeserializeDelegate<T>> SubTypeDeserializers = new();
        private static readonly FastMap<int, DeserializeDelegateRef<T>> SubTypeDeserializerRefs = new();

        // Inline cache for polymorphic deserialization (separate caches for out/ref)
        private static int _cachedTypeIdOut = -1;
        private static DeserializeDelegate<T> _cachedDeserializer;
        private static int _cachedTypeIdRef = -1;
        private static DeserializeDelegateRef<T> _cachedDeserializerRef;

        // Cache expensive type checks
        private static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        // ReSharper disable once StaticMemberInGenericType
        private static readonly bool HasBaseType = NinoTypeMetadata.HasBaseType(typeof(T));

        // ReSharper disable once StaticMemberInGenericType
        private static readonly bool IsSealed = typeof(T).IsSealed || typeof(T).IsValueType;

        // ULTIMATE: JIT-eliminated constants for maximum performance
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool IsSimpleType = !IsReferenceOrContainsReferences && !HasBaseType;

        public static void SetDeserializer(int typeId, DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef)
        {
            _deserializer = deserializer;
            _deserializerRef = deserializerRef;
            SubTypeDeserializers.Add(typeId, _deserializer);
            SubTypeDeserializerRefs.Add(typeId, _deserializerRef);
        }

        public static void AddSubTypeDeserializer<TSub>(int subTypeId,
            DeserializeDelegate<TSub> deserializer,
            DeserializeDelegateRef<TSub> deserializerRef)
        {
            SubTypeDeserializers.Add(subTypeId, (out T value, ref Reader reader) =>
            {
                deserializer(out TSub subValue, ref reader);
                value = subValue is T val ? val : default;
            });
            SubTypeDeserializerRefs.Add(subTypeId, (ref T value, ref Reader reader) =>
            {
                if (value is TSub val)
                {
                    deserializerRef(ref val, ref reader);
                }
                else
                {
                    throw new Exception($"Cannot cast {value.GetType().FullName} to {typeof(T).FullName}");
                }
            });
        }

        // ULTRA-OPTIMIZED: Single boxed method with aggressive inlining and branch elimination
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object DeserializeBoxed(ref Reader reader)
        {
            // Zero-allocation path for simple types
            if (IsSimpleType)
            {
                reader.UnsafeRead(out T value);
                return value;
            }

            // Fallback path for complex types
            Deserialize(out T result, ref reader);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeBoxed(ref object value, ref Reader reader)
        {
            // ULTRA-FAST: Direct unsafe cast with compile-time type checking
            if (value is T val)
            {
                DeserializeRef(ref val, ref reader);
                return;
            }

            // Cold path for type mismatches
            ThrowInvalidCast(value?.GetType());
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)] // Cold exception path
        private static void ThrowInvalidCast(Type actualType) =>
            throw new InvalidCastException($"Cannot cast {actualType?.FullName ?? "null"} to {typeof(T).FullName}");

        // ULTRA-OPTIMIZED: Single core method with all paths optimized
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize(out T value, ref Reader reader)
        {
            // FASTEST PATH: JIT-eliminated branch for simple types
            if (IsSimpleType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // FAST PATH 2: JIT-eliminated branch for sealed types
            // If T is sealed or a value type, it CANNOT have a different runtime type
            // This completely eliminates polymorphic deserialization overhead
            if (IsSealed || SubTypeDeserializers.Count == 1)
            {
                // DIRECT DELEGATE: Generated code path - no polymorphism possible
                _deserializer(out value, ref reader);
            }
            else
            {
                DeserializePolymorphic(out value, ref reader);
            }
        }

        // ULTRA-OPTIMIZED: Single core ref method with all paths optimized
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef(ref T value, ref Reader reader)
        {
            // FASTEST PATH: JIT-eliminated branch for simple types
            if (IsSimpleType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // FAST PATH 2: JIT-eliminated branch for sealed types
            // If T is sealed or a value type, it CANNOT have a different runtime type
            // This completely eliminates polymorphic deserialization overhead
            if (IsSealed || SubTypeDeserializerRefs.Count == 1)
            {
                // DIRECT DELEGATE: Generated code path - no polymorphism possible
                _deserializerRef(ref value, ref reader);
            }
            else
            {
                DeserializeRefPolymorphic(ref value, ref reader);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DeserializePolymorphic(out T value, ref Reader reader)
        {
            // Peek type info for polymorphic types
            reader.Peak(out int typeId);

            if (typeId == TypeCollector.Null)
            {
                value = default;
                reader.Advance(4);
                return;
            }

            // Fast path: inline cache hit (most common case for batched data)
            if (typeId == _cachedTypeIdOut)
            {
                _cachedDeserializer(out value, ref reader);
                return;
            }

            // Slow path: lookup and update cache
            DeserializePolymorphicSlow(out value, ref reader, typeId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializePolymorphicSlow(out T value, ref Reader reader, int typeId)
        {
            // Handle subtype with single lookup
            if (SubTypeDeserializers.TryGetValue(typeId, out var subTypeDeserializer))
            {
                _cachedTypeIdOut = typeId;
                _cachedDeserializer = subTypeDeserializer;
                subTypeDeserializer(out value, ref reader);
                return;
            }

            throw new Exception($"Deserializer not found for type with id {typeId}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DeserializeRefPolymorphic(ref T value, ref Reader reader)
        {
            // Read type info first for polymorphic types
            reader.Peak(out int typeId);

            if (typeId == TypeCollector.Null)
            {
                value = default;
                reader.Advance(4);
                return;
            }

            // Fast path: inline cache hit (most common case for batched data)
            if (typeId == _cachedTypeIdRef)
            {
                _cachedDeserializerRef(ref value, ref reader);
                return;
            }

            // Slow path: lookup and update cache
            DeserializeRefPolymorphicSlow(ref value, ref reader, typeId);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void DeserializeRefPolymorphicSlow(ref T value, ref Reader reader, int typeId)
        {
            // Handle subtype deserialization
            if (SubTypeDeserializerRefs.TryGetValue(typeId, out var subTypeDeserializer))
            {
                _cachedTypeIdRef = typeId;
                _cachedDeserializerRef = subTypeDeserializer;
                subTypeDeserializer(ref value, ref reader);
                return;
            }

            throw new Exception($"Deserializer not found for type with id {typeId}");
        }
    }
#pragma warning restore CA1000
}