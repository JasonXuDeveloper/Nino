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

    public class CachedSerializer<T> : ICachedSerializer
    {
        public SerializeDelegate<T> Serializer;
        private readonly FastMap<IntPtr, SerializeDelegate<T>> _subTypeSerializers = new();
        public static CachedSerializer<T> Instance;

        public void AddSubTypeSerializer<TSub>(SerializeDelegate<TSub> serializer)
        {
            if (typeof(TSub).IsValueType)
            {
                // cast TSub to T via boxing, T here must be interface, then add to the map
                _subTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
                    serializer((TSub)(object)val, ref writer));
            }
            else
            {
                // simply cast TSub to T directly, then add to the map
                _subTypeSerializers.Add(typeof(TSub).TypeHandle.Value, (T val, ref Writer writer) =>
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
            var typeofT = typeof(T);

            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>() && !NinoTypeMetadata.HasBaseType(typeofT))
            {
                writer.UnsafeWrite(val);
                return;
            }

            if (NinoTypeMetadata.HasSubType(typeofT))
            {
                typeofT = val.GetType();
            }

            if (typeofT.TypeHandle.Value == typeof(T).TypeHandle.Value)
            {
                if (Serializer == null)
                    throw new Exception($"Serializer not found for type {typeofT.FullName}");

                Serializer(val, ref writer);
                return;
            }

            if (!_subTypeSerializers.TryGetValue(typeofT.TypeHandle.Value, out var subTypeSerializer))
            {
                if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                {
                    writer.UnsafeWrite(val);
                    return;
                }

                throw new Exception(
                    $"Serializer not found for type {typeofT.FullName}");
            }

            subTypeSerializer(val, ref writer);
        }
    }

    public static class NinoTypeMetadata
    {
        private static readonly FastMap<IntPtr, bool> HasSubTypes = new();
        private static readonly FastMap<IntPtr, bool> HasBaseTypes = new();
        private static readonly object Lock = new();

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
            lock (Lock)
            {
                return HasSubTypes.ContainsKey(type.TypeHandle.Value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool HasBaseType(Type type)
        {
            lock (Lock)
            {
                return HasBaseTypes.ContainsKey(type.TypeHandle.Value);
            }
        }

        public static void RecordSubType<TBase, TSub>(SerializeDelegate<TSub> subTypeSerializer)
        {
            lock (Lock)
            {
                HasSubTypes.Add(typeof(TBase).TypeHandle.Value, true);
                HasBaseTypes.Add(typeof(TSub).TypeHandle.Value, true);
                CachedSerializer<TBase>.Instance.AddSubTypeSerializer(subTypeSerializer);
            }
        }
    }
}