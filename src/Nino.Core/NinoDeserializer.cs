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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeRef<T>(ref T value, ref Reader reader)
        {
            // Fast path for simple unmanaged types
            if (!CachedDeserializer<T>.IsReferenceOrContainsReferences && !CachedDeserializer<T>.HasBaseType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            if (reader.Eof)
            {
                value = default;
                return;
            }

            // Direct access to cached deserializer - inline for performance
            var deserializer = CachedDeserializer<T>.Instance;

            // Fast path for most common case: no polymorphism
            if (deserializer.SubTypeDeserializerRefs.Count == 0)
            {
                deserializer.DeserializerRef(ref value, ref reader);
                return;
            }

            // Handle polymorphism
            deserializer.DeserializeRef(ref value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(out T value, ref Reader reader)
        {
            // Fast path for simple unmanaged types
            if (!CachedDeserializer<T>.IsReferenceOrContainsReferences && !CachedDeserializer<T>.HasBaseType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            if (reader.Eof)
            {
                value = default;
                return;
            }

            // Direct access to cached deserializer - inline for performance
            var deserializer = CachedDeserializer<T>.Instance;

            // Fast path for most common case: no polymorphism
            if (deserializer.SubTypeDeserializers.Count == 0)
            {
                deserializer.Deserializer(out value, ref reader);
                return;
            }

            // Handle polymorphism
            deserializer.Deserialize(out value, ref reader);
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
        private static object DeserializeBoxed(ref Reader reader, Type type)
        {
            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var deserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            return deserializer.DeserializeBoxed(ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void DeserializeRefBoxed(ref object val, ref Reader reader, Type type)
        {
            if (!NinoTypeMetadata.Deserializers.TryGetValue(type.TypeHandle.Value, out var deserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            deserializer.DeserializeBoxed(ref val, ref reader);
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
        public static CachedDeserializer<T> Instance;

        // Cache expensive type checks
        internal static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        // ReSharper disable once StaticMemberInGenericType
        private static readonly IntPtr TypeHandle = typeof(T).TypeHandle.Value;

        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool HasBaseType = NinoTypeMetadata.HasBaseType(typeof(T));

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object DeserializeBoxed(ref Reader reader)
        {
            Deserialize(out T value, ref reader);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeserializeBoxed(ref object value, ref Reader reader)
        {
            if (!(value is T val))
            {
                throw new Exception($"Cannot cast {value.GetType().FullName} to {typeof(T).FullName}");
            }

            DeserializeRef(ref val, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(out T value, ref Reader reader)
        {
            // Empty reader
            if (reader.Eof)
            {
                value = default;
                return;
            }

            // Fast path for simple types
            if (!IsReferenceOrContainsReferences && !HasBaseType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // Only check for polymorphism if we have subtypes registered
            if (SubTypeDeserializers.Count > 0)
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
                    throw new Exception(
                        $"Deserializer not found for type with id {typeId}");
                }

                // Check if it's the same type (most common case)
                if (actualTypeHandle == TypeHandle)
                {
                    Deserializer(out value, ref reader);
                    return;
                }

                // Handle subtype deserialization
                if (!SubTypeDeserializers.TryGetValue(actualTypeHandle, out var subTypeDeserializer))
                {
                    throw new Exception(
                        $"Deserializer not found for type with id {typeId}");
                }

                subTypeDeserializer(out value, ref reader);
                return;
            }

            // No polymorphism - direct deserialization
            Deserializer(out value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeserializeRef(ref T value, ref Reader reader)
        {
            // Empty reader
            if (reader.Eof)
            {
                value = default;
                return;
            }

            // Fast path for simple types
            if (!IsReferenceOrContainsReferences && !HasBaseType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // Only check for polymorphism if we have subtypes registered
            if (SubTypeDeserializers.Count > 0)
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
                    throw new Exception(
                        $"Deserializer not found for type with id {typeId}");
                }

                // Check if it's the same type (most common case)
                if (actualTypeHandle == TypeHandle)
                {
                    DeserializerRef(ref value, ref reader);
                    return;
                }

                // Handle subtype deserialization
                if (!SubTypeDeserializerRefs.TryGetValue(actualTypeHandle, out var subTypeDeserializer))
                {
                    throw new Exception(
                        $"Deserializer not found for type with id {typeId}");
                }

                subTypeDeserializer(ref value, ref reader);
                return;
            }

            // No polymorphism - direct deserialization
            DeserializerRef(ref value, ref reader);
        }
    }
#pragma warning restore CA1000
}