using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public static class NinoTypeMetadata
    {
        private static readonly FastMap<IntPtr, bool> HasBaseTypeMap = new();
        private static readonly object Lock = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<IntPtr, ICachedSerializer> Serializers = new();
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<IntPtr, ICachedDeserializer> Deserializers = new();
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<int, IntPtr> TypeIdToType = new();

        /// <summary>
        /// Registers a custom serializer for a type.
        /// This method allows you to provide a custom serialization logic for a specific type.
        /// The serializer will be used when serializing instances of that type.
        /// </summary>
        /// <param name="serializer"></param>
        /// <typeparam name="T"></typeparam>
        public static void RegisterCustomSerializer<T>(SerializeDelegate<T> serializer)
        {
            lock (Lock)
            {
                if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value, true);
                }

                CachedSerializer<T>.Instance = new CachedSerializer<T>
                {
                    Serializer = serializer
                };
                Serializers.Add(typeof(T).TypeHandle.Value, CachedSerializer<T>.Instance);
            }
        }

        /// <summary>
        /// Registers a custom deserializer for a type.
        /// This method allows you to provide a custom deserialization logic for a specific type.
        /// The deserializer will be used when deserializing instances of that type.
        /// </summary>
        /// <param name="deserializer"></param>
        /// <param name="deserializerRef"></param>
        /// <typeparam name="T"></typeparam>
        public static void RegisterCustomDeserializer<T>(DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef)
        {
            lock (Lock)
            {
                if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value, true);
                }

                CachedDeserializer<T>.Instance = new CachedDeserializer<T>
                {
                    Deserializer = deserializer,
                    DeserializerRef = deserializerRef
                };
                Deserializers.Add(typeof(T).TypeHandle.Value, CachedDeserializer<T>.Instance);
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterSerializer<T>(SerializeDelegate<T> serializer, bool hasBaseType)
        {
            lock (Lock)
            {
                if (hasBaseType)
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value, true);
                CachedSerializer<T>.Instance = new CachedSerializer<T>
                {
                    Serializer = serializer
                };
                Serializers.Add(typeof(T).TypeHandle.Value, CachedSerializer<T>.Instance);
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterDeserializer<T>(DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef, bool hasBaseType)
        {
            lock (Lock)
            {
                if (hasBaseType)
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value, true);
                CachedDeserializer<T>.Instance = new CachedDeserializer<T>
                {
                    Deserializer = deserializer,
                    DeserializerRef = deserializerRef
                };
                Deserializers.Add(typeof(T).TypeHandle.Value, CachedDeserializer<T>.Instance);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasBaseType(Type type)
        {
            var typeHandle = type.TypeHandle.Value;
            return HasBaseTypeMap.TryGetValue(typeHandle, out _);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterType<T>(int id)
        {
            lock (Lock)
            {
                TypeIdToType.Add(id, typeof(T).TypeHandle.Value);
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RecordSubTypeSerializer<TBase, TSub>(SerializeDelegate<TSub> subTypeSerializer)
        {
            lock (Lock)
            {
                CachedSerializer<TBase>.Instance.AddSubTypeSerializer(subTypeSerializer);
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RecordSubTypeDeserializer<TBase, TSub>(DeserializeDelegate<TSub> subTypeDeserializer,
            DeserializeDelegateRef<TSub> subTypeDeserializerRef)
        {
            lock (Lock)
            {
                CachedDeserializer<TBase>.Instance.AddSubTypeDeserializer(subTypeDeserializer, subTypeDeserializerRef);
            }
        }
    }
}