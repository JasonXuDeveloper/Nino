using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public static class NinoTypeMetadata
    {
        private static readonly FastMap<long, bool> HasBaseTypeMap = new();
        private static readonly object Lock = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<long, ICachedSerializer> Serializers = new();
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<long, ICachedDeserializer> Deserializers = new();
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<int, IntPtr> TypeIdToType = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterSerializer<T>(SerializeDelegate<T> serializer, bool hasBaseType)
        {
            lock (Lock)
            {
                if (hasBaseType)
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value.ToInt64(), true);
                CachedSerializer<T>.Instance = new CachedSerializer<T>(serializer);
                Serializers.Add(typeof(T).TypeHandle.Value.ToInt64(), CachedSerializer<T>.Instance);
            }
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterDeserializer<T>(DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef, bool hasBaseType)
        {
            lock (Lock)
            {
                if (hasBaseType)
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value.ToInt64(), true);
                CachedDeserializer<T>.Instance = new CachedDeserializer<T>
                {
                    Deserializer = deserializer,
                    DeserializerRef = deserializerRef
                };
                Deserializers.Add(typeof(T).TypeHandle.Value.ToInt64(), CachedDeserializer<T>.Instance);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasBaseType(Type type)
        {
            var typeHandle = type.TypeHandle.Value.ToInt64();
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