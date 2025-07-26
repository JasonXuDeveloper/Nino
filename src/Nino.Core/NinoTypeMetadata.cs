using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public static class NinoTypeMetadata
    {
        private static readonly FastMap<IntPtr, bool> HasBaseTypeMap = new();
        private static readonly object Lock = new();

        public static readonly FastMap<IntPtr, ICachedSerializer> Serializers = new();
        public static readonly FastMap<IntPtr, ICachedDeserializer> Deserializers = new();
        public static readonly FastMap<int, IntPtr> TypeIdToType = new();

        public static void RegisterSerializer<T>(SerializeDelegate<T> serializer, bool hasBaseType)
        {
            lock (Lock)
            {
                if (hasBaseType)
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value, true);
                if (CachedSerializer<T>.Instance != null) return;
                CachedSerializer<T>.Instance = new CachedSerializer<T>
                {
                    Serializer = serializer
                };
                Serializers.Add(typeof(T).TypeHandle.Value, CachedSerializer<T>.Instance);
            }
        }

        public static void RegisterDeserializer<T>(DeserializeDelegate<T> deserializer, bool hasBaseType)
        {
            lock (Lock)
            {
                if (hasBaseType)
                    HasBaseTypeMap.Add(typeof(T).TypeHandle.Value, true);
                if (CachedDeserializer<T>.Instance != null) return;
                CachedDeserializer<T>.Instance = new CachedDeserializer<T>
                {
                    Deserializer = deserializer
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

        public static void RegisterType<T>(int id)
        {
            lock (Lock)
            {
                TypeIdToType.Add(id, typeof(T).TypeHandle.Value);
            }
        }

        public static void RecordSubTypeSerializer<TBase, TSub>(SerializeDelegate<TSub> subTypeSerializer)
        {
            lock (Lock)
            {
                CachedSerializer<TBase>.Instance.AddSubTypeSerializer(subTypeSerializer);
            }
        }

        public static void RecordSubTypeDeserializer<TBase, TSub>(DeserializeDelegate<TSub> subTypeDeserializer)
        {
            lock (Lock)
            {
                CachedDeserializer<TBase>.Instance.AddSubTypeDeserializer(subTypeDeserializer);
            }
        }
    }
}