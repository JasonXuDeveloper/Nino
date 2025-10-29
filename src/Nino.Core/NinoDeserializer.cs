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

                if (deserializer.outOverload == null)
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}. " +
                                        $"The type may not have been registered. Please ensure the appropriate Init() methods are called.");
                }

                return deserializer.outOverload(ref reader);
            }

            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var typeDeserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            if (typeDeserializer.outOverload == null)
            {
                var ns = NinoHelper.GetGeneratedNamespace(type);
                throw new Exception(NinoHelper.GetRegistrationErrorMessage(type.FullName, ns));
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

                if (deserializer.refOverload == null)
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}. " +
                                        $"The type may not have been registered. Please ensure the appropriate Init() methods are called.");
                }

                deserializer.refOverload(ref val, ref reader);
                return;
            }

            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var typeDeserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            if (typeDeserializer.refOverload == null)
            {
                var ns = NinoHelper.GetGeneratedNamespace(type);
                throw new Exception(NinoHelper.GetRegistrationErrorMessage(type.FullName, ns));
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
        private static int _typeId = -1;
        private static DeserializeDelegate<T> _deserializer;
        private static DeserializeDelegateRef<T> _deserializerRef;
        private static DeserializeDelegate<T> _optimalDeserializer;
        private static DeserializeDelegateRef<T> _optimalDeserializerRef;
        private static readonly FastMap<int, DeserializeDelegate<T>> SubTypeDeserializers = new();
        private static readonly FastMap<int, DeserializeDelegateRef<T>> SubTypeDeserializerRefs = new();
        private static int _singleSubTypeId = int.MinValue;
        private static DeserializeDelegate<T> _singleSubTypeDeserializer;
        private static DeserializeDelegateRef<T> _singleSubTypeDeserializerRef;

        // Inline cache for polymorphic deserialization (separate caches for out/ref)
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
            DeserializeDelegateRef<T> deserializerRef, DeserializeDelegate<T> optimalDeserializer,
            DeserializeDelegateRef<T> optimalDeserializerRef)
        {
            _typeId = typeId;
            _deserializer = deserializer;
            _deserializerRef = deserializerRef;
            _optimalDeserializer = optimalDeserializer;
            _optimalDeserializerRef = optimalDeserializerRef;
            SubTypeDeserializers.Add(typeId, _deserializer);
            SubTypeDeserializerRefs.Add(typeId, _deserializerRef);
            _singleSubTypeId = int.MinValue;
            _singleSubTypeDeserializer = null;
            _singleSubTypeDeserializerRef = null;
        }

        public static void AddSubTypeDeserializer<TSub>(int subTypeId,
            DeserializeDelegate<TSub> deserializer,
            DeserializeDelegateRef<TSub> deserializerRef) where TSub : T
        {
            // Use static generic helper classes to create inlineable wrappers
            SubTypeDeserializerWrapper<TSub>.OutDeserializer = deserializer;
            SubTypeDeserializerWrapper<TSub>.RefDeserializer = deserializerRef;
            SubTypeDeserializers.Add(subTypeId, SubTypeDeserializerWrapper<TSub>.DeserializeOutWrapper);
            SubTypeDeserializerRefs.Add(subTypeId, SubTypeDeserializerWrapper<TSub>.DeserializeRefWrapper);

            if (SubTypeDeserializers.Count == 2)
            {
                _singleSubTypeId = subTypeId;
                _singleSubTypeDeserializer = SubTypeDeserializerWrapper<TSub>.DeserializeOutWrapper;
                _singleSubTypeDeserializerRef = SubTypeDeserializerWrapper<TSub>.DeserializeRefWrapper;
            }
            else
            {
                _singleSubTypeId = int.MinValue;
                _singleSubTypeDeserializer = null;
                _singleSubTypeDeserializerRef = null;
            }
        }

        // Static wrapper class per TSub - allows better inlining than lambda
        private static class SubTypeDeserializerWrapper<TSub> where TSub : T
        {
            public static DeserializeDelegate<TSub> OutDeserializer;
            public static DeserializeDelegateRef<TSub> RefDeserializer;
            private static readonly bool IsValueType = typeof(TSub).IsValueType;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void DeserializeOutWrapper(out T value, ref Reader reader)
            {
                OutDeserializer(out TSub subValue, ref reader);
                value = subValue;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static void DeserializeRefWrapper(ref T value, ref Reader reader)
            {
                if (IsValueType)
                {
                    TSub temp = default;
                    RefDeserializer(ref temp, ref reader);
                    value = temp;
                }
                else
                {
                    RefDeserializer(ref Unsafe.As<T, TSub>(ref value), ref reader);
                }
            }
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

            // FAST PATH 1: Optimal serializer for polymorphic usage (with subtypes)
            // This is a pre-generated serializer that handles polymorphism internally
            if (_optimalDeserializer != null)
            {
                _optimalDeserializer(out value, ref reader);
                return;
            }

            DeserializePolymorphic(out value, ref reader);
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

            // FAST PATH 1: Optimal serializer for polymorphic usage (with subtypes)
            // This is a pre-generated serializer that handles polymorphism internally
            if (_optimalDeserializerRef != null)
            {
                _optimalDeserializerRef(ref value, ref reader);
                return;
            }

            DeserializeRefPolymorphic(ref value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializePolymorphic(out T value, ref Reader reader)
        {
            // FAST PATH 2: JIT-eliminated branch for sealed types
            // If T is sealed or a value type, it CANNOT have a different runtime type
            // This completely eliminates polymorphic deserialization overhead
            if (IsSealed || SubTypeDeserializers.Count == 1)
            {
                // DIRECT DELEGATE: Generated code path - no polymorphism possible
                if (_deserializer == null)
                {
                    var ns = NinoHelper.GetGeneratedNamespace(typeof(T));
                    throw new Exception(NinoHelper.GetRegistrationErrorMessage(typeof(T).FullName, ns));
                }

                _deserializer(out value, ref reader);
                return;
            }

            // Peek type info for polymorphic types
            reader.Peak(out int typeId);

            if (typeId == TypeCollector.Null)
            {
                value = default;
                reader.Advance(4);
                return;
            }

            // FAST PATH: Exact type match
            if (typeId == _typeId)
            {
                if (_deserializer == null)
                {
                    var ns = NinoHelper.GetGeneratedNamespace(typeof(T));
                    throw new Exception(NinoHelper.GetRegistrationErrorMessage(typeof(T).FullName, ns));
                }

                _deserializer(out value, ref reader);
                return;
            }

            // FAST PATH: Exact type match for single subtype
            if (typeId == _singleSubTypeId && _singleSubTypeDeserializer is not null)
            {
                _singleSubTypeDeserializer(out value, ref reader);
                return;
            }

            // FAST PATH: Cache hit (optimized for monomorphic arrays)
            if (typeId == reader.CachedTypeId && reader.CachedDeserializer is DeserializeDelegate<T> cachedDeserializer)
            {
                cachedDeserializer(out value, ref reader);
                return;
            }

            // SLOW PATH: Full lookup in subtype map and update cache
            if (SubTypeDeserializers.TryGetValue(typeId, out var subTypeDeserializer))
            {
                reader.CachedTypeId = typeId;
                reader.CachedDeserializer = subTypeDeserializer;
                subTypeDeserializer(out value, ref reader);
                return;
            }

            throw new Exception($"Deserializer not found for type with id {typeId}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRefPolymorphic(ref T value, ref Reader reader)
        {
            // FAST PATH 2: JIT-eliminated branch for sealed types
            // If T is sealed or a value type, it CANNOT have a different runtime type
            // This completely eliminates polymorphic deserialization overhead
            if (IsSealed || SubTypeDeserializerRefs.Count == 1)
            {
                // DIRECT DELEGATE: Generated code path - no polymorphism possible
                if (_deserializerRef == null)
                {
                    var ns = NinoHelper.GetGeneratedNamespace(typeof(T));
                    throw new Exception(NinoHelper.GetRegistrationErrorMessage(typeof(T).FullName, ns));
                }

                _deserializerRef(ref value, ref reader);
                return;
            }

            // Read type info first for polymorphic types
            reader.Peak(out int typeId);

            if (typeId == TypeCollector.Null)
            {
                value = default;
                reader.Advance(4);
                return;
            }

            // FAST PATH: Exact type match
            if (typeId == _typeId)
            {
                if (_deserializerRef == null)
                {
                    var ns = NinoHelper.GetGeneratedNamespace(typeof(T));
                    throw new Exception(NinoHelper.GetRegistrationErrorMessage(typeof(T).FullName, ns));
                }

                _deserializerRef(ref value, ref reader);
                return;
            }

            // FAST PATH: Exact type match for single subtype
            if (typeId == _singleSubTypeId && _singleSubTypeDeserializerRef is not null)
            {
                _singleSubTypeDeserializerRef(ref value, ref reader);
                return;
            }

            // FAST PATH: Cache hit (optimized for monomorphic arrays)
            if (typeId == reader.CachedTypeIdRef &&
                reader.CachedDeserializerRef is DeserializeDelegateRef<T> cachedDeserializerRef)
            {
                cachedDeserializerRef(ref value, ref reader);
                return;
            }

            // SLOW PATH: Full lookup in subtype map and update cache
            if (SubTypeDeserializerRefs.TryGetValue(typeId, out var subTypeDeserializer))
            {
                reader.CachedTypeIdRef = typeId;
                reader.CachedDeserializerRef = subTypeDeserializer;
                subTypeDeserializer(ref value, ref reader);
                return;
            }

            throw new Exception($"Deserializer not found for type with id {typeId}");
        }
    }
#pragma warning restore CA1000
}