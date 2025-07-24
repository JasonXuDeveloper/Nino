using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public static class NinoDeserializer
    {
        public delegate void DeserializeDelegate<T>(out T result, ref Reader reader);

        private static readonly ConcurrentDictionary<IntPtr, ICachedDeserializer> Deserializers = new();

        public static void InitAssembly(Assembly assembly)
        {
            var @namespace = assembly.GetName().Name.GetNamespace();
            var type = assembly.GetType($"{@namespace}.Deserializer");
            var method = type?.GetMethod("Init", BindingFlags.Static | BindingFlags.Public);
            method?.Invoke(null, null);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Register<T>(DeserializeDelegate<T> deserializer)
        {
            Deserializers[typeof(T).TypeHandle.Value] = new CachedDeserializer<T>(deserializer);
        }

        private interface ICachedDeserializer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            object DeserializeBoxed(ref Reader reader);
        }

        private class CachedDeserializer<T> : ICachedDeserializer
        {
            public static DeserializeDelegate<T> Deserializer;

            public CachedDeserializer(DeserializeDelegate<T> deserializer)
            {
                Deserializer = deserializer;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public object DeserializeBoxed(ref Reader reader)
            {
                if (Deserializer == null)
                    throw new Exception($"Deserializer not found for type {typeof(T).FullName}");

                if (reader.Eof)
                    return null;

                Deserializer.Invoke(out T value, ref reader);
                return value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T>(ReadOnlySpan<byte> data, out T value)
        {
            var reader = new Reader(data);
            value = DeserializeGeneric<T>(ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Deserialize<T>(ReadOnlySpan<byte> data)
        {
            var reader = new Reader(data);
            return DeserializeGeneric<T>(ref reader);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T DeserializeGeneric<T>(ref Reader reader)
        {
#if WEAK_VERSION_TOLERANCE
             if (reader.Eof)
             {
                return default;
             }
#endif

            var deserializer = CachedDeserializer<T>.Deserializer;
            if (deserializer != null)
            {
                deserializer.Invoke(out T value, ref reader);
                return value;
            }

            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                reader.UnsafeRead(out T value);
                return value;
            }

            throw new Exception($"Deserializer not found for type {typeof(T).FullName}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static object Deserialize(ReadOnlySpan<byte> data, Type type)
        {
            var reader = new Reader(data);
            return DeserializeBoxed(ref reader, type);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object DeserializeBoxed(ref Reader reader, Type type)
        {
            if (!Deserializers.TryGetValue(type.TypeHandle.Value, out var deserializer))
            {
                throw new Exception(
                    $"Deserializer not found for type {type.FullName}, if this is an unmanaged type, please use Deserialize<T>(ref Reader reader) instead.");
            }

            return deserializer.DeserializeBoxed(ref reader);
        }
    }
}