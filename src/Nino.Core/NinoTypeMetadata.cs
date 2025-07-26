using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public static class NinoTypeMetadata
    {
        private static readonly FastMap<IntPtr, byte> TypeFlags = new();
        private static readonly object Lock = new();
        private const byte HasSubTypeBit = 1;
        private const byte HasBaseTypeBit = 2;

        public static readonly FastMap<IntPtr, ICachedSerializer> Serializers = new();

        public static void Register<T>(SerializeDelegate<T> serializer)
        {
            lock (Lock)
            {
                if (CachedSerializer<T>.Instance != null) return;
                CachedSerializer<T>.Instance = new CachedSerializer<T>
                {
                    Serializer = serializer
                };
                Serializers.Add(typeof(T).TypeHandle.Value, CachedSerializer<T>.Instance);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasSubType(Type type)
        {
            var typeHandle = type.TypeHandle.Value;
            return TypeFlags.TryGetValue(typeHandle, out var flags) && (flags & HasSubTypeBit) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasBaseType(Type type)
        {
            var typeHandle = type.TypeHandle.Value;
            return TypeFlags.TryGetValue(typeHandle, out var flags) && (flags & HasBaseTypeBit) != 0;
        }

        public static void RecordSubTypeSerializer<TBase, TSub>(SerializeDelegate<TSub> subTypeSerializer)
        {
            lock (Lock)
            {
                var baseTypeHandle = typeof(TBase).TypeHandle.Value;
                var subTypeHandle = typeof(TSub).TypeHandle.Value;

                TypeFlags.TryGetValue(baseTypeHandle, out var baseFlags);
                TypeFlags.Add(baseTypeHandle, (byte)(baseFlags | HasSubTypeBit));

                TypeFlags.TryGetValue(subTypeHandle, out var subFlags);
                TypeFlags.Add(subTypeHandle, (byte)(subFlags | HasBaseTypeBit));

                CachedSerializer<TBase>.Instance.AddSubTypeSerializer(subTypeSerializer);
            }
        }
    }
}