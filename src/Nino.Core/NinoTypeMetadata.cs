using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    [SuppressMessage("ReSharper", "StaticMemberInGenericType")]
    [SuppressMessage("ReSharper", "UnusedTypeParameter")]
    public static class NinoTypeMetadata
    {
        private static readonly FastMap<IntPtr, bool> HasBaseTypeMap = new();
        private static readonly object HasBaseTypeLock = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<IntPtr, SerializeDelegateBoxed> Serializers = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<IntPtr,
            (DeserializeDelegateBoxed outOverload,
            DeserializeDelegateRefBoxed refOverload)> Deserializers = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<int,
            (DeserializeDelegateBoxed outOverload,
            DeserializeDelegateRefBoxed refOverload)> TypeIdToDeserializer = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterSerializer<T>(SerializeDelegate<T> serializer, bool hasBaseType)
        {
            lock (SerializerRegistration<T>.Lock)
            {
                if (SerializerRegistration<T>.Registered)
                    return;

                var typeHandle = typeof(T).TypeHandle.Value;

                if (hasBaseType)
                {
                    lock (HasBaseTypeLock)
                        HasBaseTypeMap.Add(typeHandle, true);
                }

                CachedSerializer<T>.SetSerializer(serializer);
                Serializers.Add(typeHandle, CachedSerializer<T>.SerializeBoxed);

                SerializerRegistration<T>.Registered = true;
            }
        }

        private static class SerializerRegistration<T>
        {
            public static readonly object Lock = new();
            public static bool Registered;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterDeserializer<T>(int typeId,
            DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef,
            DeserializeDelegate<T> optimalDeserializer,
            DeserializeDelegateRef<T> optimalDeserializerRef,
            bool hasBaseType)
        {
            lock (DeserializerRegistration<T>.Lock)
            {
                if (DeserializerRegistration<T>.Registered)
                    return;

                var typeHandle = typeof(T).TypeHandle.Value;

                if (hasBaseType)
                {
                    lock (HasBaseTypeLock)
                        HasBaseTypeMap.Add(typeHandle, true);
                }

                CachedDeserializer<T>.SetDeserializer(typeId, deserializer, deserializerRef, optimalDeserializer, optimalDeserializerRef);
                (DeserializeDelegateBoxed outOverload,
                    DeserializeDelegateRefBoxed refOverload) pair = (CachedDeserializer<T>.DeserializeBoxed,
                        CachedDeserializer<T>.DeserializeBoxed);
                Deserializers.Add(typeHandle, pair);
                if (typeId != -1)
                    TypeIdToDeserializer.Add(typeId, pair);

                DeserializerRegistration<T>.Registered = true;
            }
        }

        private static class DeserializerRegistration<T>
        {
            public static readonly object Lock = new();
            public static bool Registered;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasBaseType(Type type)
        {
            var typeHandle = type.TypeHandle.Value;
            // ReSharper disable once InconsistentlySynchronizedField
            return HasBaseTypeMap.TryGetValue(typeHandle, out _);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RecordSubTypeSerializer<TBase, TSub>(SerializeDelegate<TSub> subTypeSerializer)
            where TSub : TBase
        {
            lock (SubTypeSerializerRegistration<TBase, TSub>.Lock)
            {
                if (SubTypeSerializerRegistration<TBase, TSub>.Registered)
                    return;

                CachedSerializer<TBase>.AddSubTypeSerializer(subTypeSerializer);

                SubTypeSerializerRegistration<TBase, TSub>.Registered = true;
            }
        }

        private static class SubTypeSerializerRegistration<TBase, TSub>
        {
            public static readonly object Lock = new();
            public static bool Registered;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RecordSubTypeDeserializer<TBase, TSub>(int subTypeId,
            DeserializeDelegate<TSub> subTypeDeserializer,
            DeserializeDelegateRef<TSub> subTypeDeserializerRef)
            where TSub : TBase
        {
            lock (SubTypeDeserializerRegistration<TBase, TSub>.Lock)
            {
                if (SubTypeDeserializerRegistration<TBase, TSub>.Registered)
                    return;

                CachedDeserializer<TBase>.AddSubTypeDeserializer(subTypeId, subTypeDeserializer,
                    subTypeDeserializerRef);

                SubTypeDeserializerRegistration<TBase, TSub>.Registered = true;
            }
        }

        private static class SubTypeDeserializerRegistration<TBase, TSub>
        {
            public static readonly object Lock = new();
            public static bool Registered;
        }
    }
}