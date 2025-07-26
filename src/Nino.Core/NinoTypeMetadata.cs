using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public delegate void SerializeDelegate<in TVal>(TVal value, ref Writer writer);

    public interface ICachedSerializer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void SerializeBoxed(object value, ref Writer writer);
    }

#pragma warning disable CA1000 // Do not declare static members on generic types
    public class CachedSerializer<T> : ICachedSerializer
    {
        public SerializeDelegate<T> Serializer;
        internal readonly FastMap<IntPtr, SerializeDelegate<T>> SubTypeSerializers = new();
        public static CachedSerializer<T> Instance;

        // Cache expensive type checks
        internal static readonly bool IsReferenceOrContainsReferences =
            RuntimeHelpers.IsReferenceOrContainsReferences<T>();

        private static readonly Type TypeOfT = typeof(T);

        // ReSharper disable once StaticMemberInGenericType
        private static readonly IntPtr TypeHandle = TypeOfT.TypeHandle.Value;

        // ReSharper disable once StaticMemberInGenericType
        internal static readonly bool HasBaseTypeFlag = NinoTypeMetadata.HasBaseType(TypeOfT);

        public void AddSubTypeSerializer<TSub>(SerializeDelegate<TSub> serializer)
        {
            if (typeof(TSub).IsValueType)
            {
                // cast TSub to T via boxing, T here must be interface, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
                    serializer((TSub)(object)val, ref writer));
            }
            else
            {
                // simply cast TSub to T directly, then add to the map
                SubTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
                    serializer(Unsafe.As<T, TSub>(ref val), ref writer));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SerializeBoxed(object value, ref Writer writer)
        {
            Serialize((T)value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T val, ref Writer writer)
        {
            // Fast path for simple types - use cached flags
            if (!IsReferenceOrContainsReferences && !HasBaseTypeFlag)
            {
                writer.UnsafeWrite(val);
                return;
            }

            // Only check for polymorphism if we have subtypes registered
            if (SubTypeSerializers.Count > 0)
            {
                IntPtr actualTypeHandle = val.GetType().TypeHandle.Value;
                
                // Check if it's the same type (most common case)
                if (actualTypeHandle == TypeHandle)
                {
                    Serializer(val, ref writer);
                    return;
                }
                
                // Handle subtype serialization
                if (!SubTypeSerializers.TryGetValue(actualTypeHandle, out var subTypeSerializer))
                {
                    throw new Exception(
                        $"Serializer not found for type {val.GetType().FullName}");
                }

                subTypeSerializer(val, ref writer);
                return;
            }
            
            // No polymorphism - direct serialization
            Serializer(val, ref writer);
        }
    }
#pragma warning restore CA1000

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

        public static void RecordSubType<TBase, TSub>(SerializeDelegate<TSub> subTypeSerializer)
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