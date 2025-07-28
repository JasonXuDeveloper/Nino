using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public static class NinoDeserializer
    {
        private static readonly object Lock = new();

        /// <summary>
        /// Registers a custom deserializer for a type.
        /// This method allows you to provide a custom deserialization logic for a specific type.
        /// The deserializer will be used when deserializing instances of that type.
        /// </summary>
        /// <param name="deserializer"></param>
        /// <param name="deserializerRef"></param>
        /// <typeparam name="T"></typeparam>
        public static void AddCustomDeserializer<T>(DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef)
        {
            lock (Lock)
            {
                CustomDeserializer<T>.Instance ??= new CustomDeserializer<T>(deserializer, deserializerRef);
            }
        }

        /// <summary>
        /// Removes a custom deserializer for a type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public static void RemoveCustomDeserializer<T>()
        {
            lock (Lock)
            {
                CustomDeserializer<T>.Instance = null;
            }
        }

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
            // Empty reader check
            if (reader.Eof)
            {
                value = default;
                return;
            }

            // Fast path for simple types - check first to avoid lookups
            if (!CachedDeserializer<T>.IsReferenceOrContainsReferences && !CachedDeserializer<T>.HasBaseType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // Check custom deserializer only if needed
            var customDeserializer = CustomDeserializer<T>.Instance;
            if (customDeserializer != null)
            {
                customDeserializer.DeserializerRef(ref value, ref reader);
                return;
            }

            // Inline the most common path to avoid additional method call
            var cachedDeserializer = CachedDeserializer<T>.Instance;
            if (cachedDeserializer.SubTypeDeserializerRefs.Count == 0)
            {
                // Direct delegate call for non-polymorphic types
                cachedDeserializer.DeserializerRef(ref value, ref reader);
                return;
            }

            // Handle complex cases through the instance method
            cachedDeserializer.DeserializeRef(ref value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(out T value, ref Reader reader)
        {
            // Empty reader check
            if (reader.Eof)
            {
                value = default;
                return;
            }

            // Fast path for simple types
            if (!CachedDeserializer<T>.IsReferenceOrContainsReferences && !CachedDeserializer<T>.HasBaseType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // Optimize the common case: check for custom deserializers first
            var customDeserializer = CustomDeserializer<T>.Instance;
            if (customDeserializer != null)
            {
                customDeserializer.Deserializer(out value, ref reader);
                return;
            }

            // Inline the most common path to avoid additional method call
            var cachedDeserializer = CachedDeserializer<T>.Instance;
            if (cachedDeserializer.SubTypeDeserializers.Count == 0)
            {
                // Direct delegate call for non-polymorphic types
                cachedDeserializer.Deserializer(out value, ref reader);
                return;
            }

            // Handle complex cases through the instance method
            cachedDeserializer.Deserialize(out value, ref reader);
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
            // Check if type is null
            if (type == null)
                throw new ArgumentNullException(nameof(type));

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
            // Check if type is null
            if (type == null)
                throw new ArgumentNullException(nameof(type));

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


    internal class CustomDeserializer<T> : ICachedDeserializer
    {
        public readonly DeserializeDelegate<T> Deserializer;
        public readonly DeserializeDelegateRef<T> DeserializerRef;

        public static CustomDeserializer<T> Instance;

        public CustomDeserializer(DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef)
        {
            Deserializer = deserializer;
            DeserializerRef = deserializerRef;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object DeserializeBoxed(ref Reader reader)
        {
            Deserializer(out T value, ref reader);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeserializeBoxed(ref object value, ref Reader reader)
        {
            if (!(value is T val))
            {
                throw new Exception($"Cannot cast {value.GetType().FullName} to {typeof(T).FullName}");
            }

            DeserializerRef(ref val, ref reader);
        }
    }

#pragma warning disable CA1000 // Do not declare static members on generic types
    public class CachedDeserializer<T> : ICachedDeserializer
    {
        public DeserializeDelegate<T> Deserializer;
        public DeserializeDelegateRef<T> DeserializerRef;
        internal readonly FastMap<IntPtr, DeserializeDelegate<T>> SubTypeDeserializers = new();
        internal readonly FastMap<IntPtr, DeserializeDelegateRef<T>> SubTypeDeserializerRefs = new();
        public static CachedDeserializer<T> Instance = new();

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
            // Empty reader check
            if (reader.Eof)
            {
                value = default;
                return;
            }

            // Fast path for simple types - check first
            if (!IsReferenceOrContainsReferences && !HasBaseType)
            {
                reader.UnsafeRead(out value);
                return;
            }

            // Check custom deserializer only if needed
            var customDeserializer = CustomDeserializer<T>.Instance;
            if (customDeserializer != null)
            {
                customDeserializer.Deserializer(out value, ref reader);
                return;
            }

            // Optimized polymorphism handling
            if (SubTypeDeserializers.Count > 0)
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

            // Direct deserialization path
            Deserializer(out value, ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void DeserializeRef(ref T value, ref Reader reader)
        {
            // Empty reader check
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

            // Fast path for non-polymorphic types (most common case for classes)
            if (SubTypeDeserializerRefs.Count == 0)
            {
                // Check custom deserializer only if we don't have polymorphism
                var customDeserializer = CustomDeserializer<T>.Instance;
                if (customDeserializer != null)
                {
                    customDeserializer.DeserializerRef(ref value, ref reader);
                    return;
                }
                
                DeserializerRef(ref value, ref reader);
                return;
            }

            // Handle polymorphic case (less common)
            DeserializeRefPolymorphic(ref value, ref reader);
        }

        [MethodImpl(MethodImplOptions.NoInlining)] // Keep cold path out of hot path
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