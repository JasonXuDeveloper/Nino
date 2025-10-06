using System;
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

            CachedDeserializer<T>.Instance.DeserializeRef(ref value, ref reader);
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

            CachedDeserializer<T>.Instance.Deserialize(out value, ref reader);
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
                if (!NinoTypeMetadata.TypeIdToType.TryGetValue(typeId, out IntPtr typeHandle))
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}");
                }

                if (!NinoTypeMetadata.Deserializers.TryGetValue(typeHandle, out var deserializer))
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}");
                }

                return deserializer.DeserializeBoxed(ref reader);
            }

            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var typeDeserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            return typeDeserializer.DeserializeBoxed(ref reader);
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
                if (!NinoTypeMetadata.TypeIdToType.TryGetValue(typeId, out IntPtr typeHandle))
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}");
                }

                if (!NinoTypeMetadata.Deserializers.TryGetValue(typeHandle, out var deserializer))
                {
                    throw new Exception($"Deserializer not found for type with id {typeId}");
                }

                deserializer.DeserializeBoxed(ref val, ref reader);
                return;
            }

            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var typeDeserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            typeDeserializer.DeserializeBoxed(ref val, ref reader);
        }
    }

    public delegate void DeserializeDelegate<T>(out T result, ref Reader reader);

    public delegate void DeserializeDelegateRef<T>(ref T result, ref Reader reader);

    public interface ICachedDeserializer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        object DeserializeBoxed(ref Reader reader);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void DeserializeBoxed(ref object value, ref Reader reader);
    }

#pragma warning disable CA1000 // Do not declare static members on generic types
    public class CachedDeserializer<T> : ICachedDeserializer
    {
        public DeserializeDelegate<T> Deserializer;
        public DeserializeDelegateRef<T> DeserializerRef;
        internal readonly FastMap<IntPtr, DeserializeDelegate<T>> SubTypeDeserializers = new();
        internal readonly FastMap<IntPtr, DeserializeDelegateRef<T>> SubTypeDeserializerRefs = new();
        public static readonly CachedDeserializer<T> Instance = new();

        private CachedDeserializer()
        {
            Deserializer = null;
            DeserializerRef = null;
        }

        // Cache expensive type checks
        internal static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        // ReSharper disable once StaticMemberInGenericType
        private static readonly IntPtr TypeHandle = typeof(T).TypeHandle.Value;

        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool HasBaseType = NinoTypeMetadata.HasBaseType(typeof(T));

        // ULTIMATE: JIT-eliminated constants for maximum performance
        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool IsSimpleType = !IsReferenceOrContainsReferences && !HasBaseType;

        public void AddSubTypeDeserializer<TSub>(DeserializeDelegate<TSub> deserializer,
            DeserializeDelegateRef<TSub> deserializerRef)
        {
            SubTypeDeserializers.Add(typeof(TSub).TypeHandle.Value, (out T value, ref Reader reader) =>
            {
                deserializer(out TSub subValue, ref reader);
                value = subValue is T val ? val : default;
            });
            SubTypeDeserializerRefs.Add(typeof(TSub).TypeHandle.Value, (ref T value, ref Reader reader) =>
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
        public object DeserializeBoxed(ref Reader reader)
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
        public void DeserializeBoxed(ref object value, ref Reader reader)
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
        public void Deserialize(out T value, ref Reader reader)
        {
            // FASTEST PATH: JIT-eliminated branch for simple types
            if (IsSimpleType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // OPTIMIZED: Direct deserialization with polymorphism support
            if (SubTypeDeserializers.Count != 0)
            {
                DeserializePolymorphic(out value, ref reader);
            }
            else
            {
                // DIRECT DELEGATE: Generated code path - no null check needed
                Deserializer(out value, ref reader);
            }
        }

        // ULTRA-OPTIMIZED: Single core ref method with all paths optimized
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeserializeRef(ref T value, ref Reader reader)
        {
            // FASTEST PATH: JIT-eliminated branch for simple types
            if (IsSimpleType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // OPTIMIZED: Direct deserialization with polymorphism support
            if (SubTypeDeserializerRefs.Count != 0)
            {
                DeserializeRefPolymorphic(ref value, ref reader);
            }
            else
            {
                // DIRECT DELEGATE: Generated code path - no null check needed
                DeserializerRef(ref value, ref reader);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining |
                    MethodImplOptions.NoOptimization)] // Cold path: Profile-guided optimization
        private void DeserializePolymorphic(out T value, ref Reader reader)
        {
            // Peek type info for polymorphic types
            reader.Peak(out int typeId);

            if (typeId == TypeCollector.Null)
            {
                value = default;
                reader.Advance(4);
                return;
            }

            // Single lookup for type metadata
            if (!NinoTypeMetadata.TypeIdToType.TryGetValue(typeId, out IntPtr actualTypeHandle))
            {
                throw new Exception($"Deserializer not found for type with id {typeId}");
            }

            // Check same type first (most common case)
            if (actualTypeHandle == TypeHandle)
            {
                Deserializer(out value, ref reader);
                return;
            }

            // Handle subtype with single lookup
            if (SubTypeDeserializers.TryGetValue(actualTypeHandle, out var subTypeDeserializer))
            {
                subTypeDeserializer(out value, ref reader);
                return;
            }

            throw new Exception($"Deserializer not found for type with id {typeId}");
        }

        [MethodImpl(MethodImplOptions.NoInlining |
                    MethodImplOptions.NoOptimization)] // Cold path: Profile-guided optimization
        private void DeserializeRefPolymorphic(ref T value, ref Reader reader)
        {
            // Read type info first for polymorphic types
            reader.Peak(out int typeId);

            if (typeId == TypeCollector.Null)
            {
                value = default;
                reader.Advance(4);
                return;
            }

            if (!NinoTypeMetadata.TypeIdToType.TryGetValue(typeId, out IntPtr actualTypeHandle))
            {
                throw new Exception($"Deserializer not found for type with id {typeId}");
            }

            // Check if it's the same type (most common case)
            if (actualTypeHandle == TypeHandle)
            {
                DeserializerRef(ref value, ref reader);
                return;
            }

            // Handle subtype deserialization
            if (SubTypeDeserializerRefs.TryGetValue(actualTypeHandle, out var subTypeDeserializer))
            {
                subTypeDeserializer(ref value, ref reader);
                return;
            }

            throw new Exception($"Deserializer not found for type with id {typeId}");
        }
    }
#pragma warning restore CA1000
}