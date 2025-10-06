using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    [SuppressMessage("ReSharper", "StaticMemberInGenericType")]
    [SuppressMessage("ReSharper", "UnusedTypeParameter")]
    public static class NinoTypeMetadata
    {
        // Lock-free registration using Interlocked.CompareExchange
        // 0 = not registered, 1 = in progress, 2 = registered
        private const int NotRegistered = 0;
        private const int InProgress = 1;
        private const int Registered = 2;

        private static readonly FastMap<IntPtr, bool> HasBaseTypeMap = new();
        private static readonly object HasBaseTypeLock = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<IntPtr, ICachedSerializer> Serializers = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<IntPtr, ICachedDeserializer> Deserializers = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static readonly FastMap<int, IntPtr> TypeIdToType = new();

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterSerializer<T>(SerializeDelegate<T> serializer, bool hasBaseType)
        {
            // Lock-free fast path - if already registered, return immediately
            if (SerializerRegistration<T>.State == Registered)
                return;

            // Try to claim registration with CAS
            if (Interlocked.CompareExchange(ref SerializerRegistration<T>.State, InProgress, NotRegistered) !=
                NotRegistered)
            {
                // Someone else is registering or already registered
                // Spin-wait until registration completes
                SpinWait spinner = new SpinWait();
                while (SerializerRegistration<T>.State != Registered)
                    spinner.SpinOnce();
                return;
            }

            // We won the race - perform registration
            var typeHandle = typeof(T).TypeHandle.Value;

            if (hasBaseType)
            {
                lock (HasBaseTypeLock)
                    HasBaseTypeMap.Add(typeHandle, true);
            }

            CachedSerializer<T>.Instance.Serializer = serializer;

            // Memory barrier to ensure all writes complete before marking as registered
            Thread.MemoryBarrier();
            Serializers.Add(typeHandle, CachedSerializer<T>.Instance);

            // Mark as fully registered - release semantic
            Volatile.Write(ref SerializerRegistration<T>.State, Registered);
        }

        private static class SerializerRegistration<T>
        {
            public static int State;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterDeserializer<T>(DeserializeDelegate<T> deserializer,
            DeserializeDelegateRef<T> deserializerRef, bool hasBaseType)
        {
            // Lock-free fast path
            if (DeserializerRegistration<T>.State == Registered)
                return;

            // Try to claim registration with CAS
            if (Interlocked.CompareExchange(ref DeserializerRegistration<T>.State, InProgress, NotRegistered) !=
                NotRegistered)
            {
                // Spin-wait until registration completes
                SpinWait spinner = new SpinWait();
                while (DeserializerRegistration<T>.State != Registered)
                    spinner.SpinOnce();
                return;
            }

            // We won the race - perform registration
            var typeHandle = typeof(T).TypeHandle.Value;

            if (hasBaseType)
            {
                lock (HasBaseTypeLock)
                    HasBaseTypeMap.Add(typeHandle, true);
            }

            CachedDeserializer<T>.Instance.Deserializer = deserializer;
            CachedDeserializer<T>.Instance.DeserializerRef = deserializerRef;

            Thread.MemoryBarrier();
            Deserializers.Add(typeHandle, CachedDeserializer<T>.Instance);

            Volatile.Write(ref DeserializerRegistration<T>.State, Registered);
        }

        private static class DeserializerRegistration<T>
        {
            public static int State;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasBaseType(Type type)
        {
            var typeHandle = type.TypeHandle.Value;
            lock (HasBaseTypeLock)
                return HasBaseTypeMap.TryGetValue(typeHandle, out _);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterType<T>(int id)
        {
            // Lock-free fast path
            if (TypeIdRegistration<T>.State == Registered)
                return;

            // Try to claim registration with CAS
            if (Interlocked.CompareExchange(ref TypeIdRegistration<T>.State, InProgress, NotRegistered) !=
                NotRegistered)
            {
                // Spin-wait until registration completes
                SpinWait spinner = new SpinWait();
                while (TypeIdRegistration<T>.State != Registered)
                    spinner.SpinOnce();
                return;
            }

            // We won the race - perform registration
            Thread.MemoryBarrier();
            TypeIdToType.Add(id, typeof(T).TypeHandle.Value);

            Volatile.Write(ref TypeIdRegistration<T>.State, Registered);
        }

        private static class TypeIdRegistration<T>
        {
            public static int State;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RecordSubTypeSerializer<TBase, TSub>(SerializeDelegate<TSub> subTypeSerializer)
        {
            // Lock-free fast path
            if (SubTypeSerializerRegistration<TBase, TSub>.State == Registered)
                return;

            // Try to claim registration with CAS
            if (Interlocked.CompareExchange(ref SubTypeSerializerRegistration<TBase, TSub>.State, InProgress,
                    NotRegistered) != NotRegistered)
            {
                // Spin-wait until registration completes
                SpinWait spinner = new SpinWait();
                while (SubTypeSerializerRegistration<TBase, TSub>.State != Registered)
                    spinner.SpinOnce();
                return;
            }

            // We won the race - perform registration
            CachedSerializer<TBase>.Instance.AddSubTypeSerializer(subTypeSerializer);

            Volatile.Write(ref SubTypeSerializerRegistration<TBase, TSub>.State, Registered);
        }

        private static class SubTypeSerializerRegistration<TBase, TSub>
        {
            public static int State;
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RecordSubTypeDeserializer<TBase, TSub>(DeserializeDelegate<TSub> subTypeDeserializer,
            DeserializeDelegateRef<TSub> subTypeDeserializerRef)
        {
            // Lock-free fast path
            if (SubTypeDeserializerRegistration<TBase, TSub>.State == Registered)
                return;

            // Try to claim registration with CAS
            if (Interlocked.CompareExchange(ref SubTypeDeserializerRegistration<TBase, TSub>.State, InProgress,
                    NotRegistered) != NotRegistered)
            {
                // Spin-wait until registration completes
                SpinWait spinner = new SpinWait();
                while (SubTypeDeserializerRegistration<TBase, TSub>.State != Registered)
                    spinner.SpinOnce();
                return;
            }

            // We won the race - perform registration
            CachedDeserializer<TBase>.Instance.AddSubTypeDeserializer(subTypeDeserializer, subTypeDeserializerRef);

            Volatile.Write(ref SubTypeDeserializerRegistration<TBase, TSub>.State, Registered);
        }

        private static class SubTypeDeserializerRegistration<TBase, TSub>
        {
            public static int State;
        }
    }
}