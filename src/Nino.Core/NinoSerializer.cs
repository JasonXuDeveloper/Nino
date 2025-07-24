using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    public static class NinoSerializer
    {
        public delegate void SerializeDelegate<in T>(T value, ref Writer writer);

        private static readonly ConcurrentDictionary<IntPtr, ICachedSerializer> Serializers = new();

        private static readonly ConcurrentQueue<NinoArrayBufferWriter> BufferWriters = new();

        private static readonly NinoArrayBufferWriter DefaultBufferWriter = new(1024);
        private static int _defaultUsed;

        public static void InitAssembly(Assembly assembly)
        {
            var @namespace = assembly.GetName().Name.GetNamespace();
            var type = assembly.GetType($"{@namespace}.Serializer");
            var method = type?.GetMethod("Init", BindingFlags.Static | BindingFlags.Public);
            method?.Invoke(null, null);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoArrayBufferWriter GetBufferWriter()
        {
            // Fast path
            if (Interlocked.CompareExchange(ref _defaultUsed, 1, 0) == 0)
            {
                return DefaultBufferWriter;
            }

            if (BufferWriters.Count == 0)
            {
                return new NinoArrayBufferWriter(1024);
            }

            if (BufferWriters.TryDequeue(out var bufferWriter))
            {
                return bufferWriter;
            }

            return new NinoArrayBufferWriter(1024);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReturnBufferWriter(NinoArrayBufferWriter bufferWriter)
        {
#if NET8_0_OR_GREATER
            bufferWriter.ResetWrittenCount();
#else
            bufferWriter.Clear();
#endif
            // Check if the buffer writer is the default buffer writer
            if (bufferWriter == DefaultBufferWriter)
            {
                // Ensure it is in use, otherwise throw an exception
                if (Interlocked.CompareExchange(ref _defaultUsed, 0, 1) == 0)
                {
                    throw new InvalidOperationException("The returned buffer writer is not in use.");
                }

                return;
            }

            BufferWriters.Enqueue(bufferWriter);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Register<T>(SerializeDelegate<T> serializer)
        {
            Serializers[typeof(T).TypeHandle.Value] = new CachedSerializer<T>(serializer);
        }

        private interface ICachedSerializer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void SerializeBoxed(object value, ref Writer writer);
        }

        private class CachedSerializer<T> : ICachedSerializer
        {
            public static SerializeDelegate<T> Serializer;

            public CachedSerializer(SerializeDelegate<T> serializer)
            {
                Serializer = serializer;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void SerializeBoxed(object value, ref Writer writer)
            {
                if (Serializer == null)
                    throw new Exception($"Serializer not found for type {typeof(T).FullName}");

                if (value == null)
                {
                    writer.Write(TypeCollector.Null);
                    return;
                }

                if (!(value is T val))
                    throw new Exception($"Cannot cast object to type {typeof(T).FullName}");

                Serializer.Invoke(val, ref writer);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T value)
        {
            var bufferWriter = GetBufferWriter();
            try
            {
                var writer = new Writer(bufferWriter);
                Serialize<T>(value, ref writer);
                return bufferWriter.WrittenSpan.ToArray();
            }
            finally
            {
                ReturnBufferWriter(bufferWriter);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T value, INinoBufferWriter bufferWriter)
        {
            Writer writer = new Writer(bufferWriter);
            Serialize(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Serialize<T>(T value, ref Writer writer)
        {
            var serializer = CachedSerializer<T>.Serializer;
            if (serializer != null)
            {
                serializer.Invoke(value, ref writer);
                return;
            }

            if (!RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            {
                writer.UnsafeWrite(value);
                return;
            }

            throw new Exception($"Serializer not found for type {typeof(T).FullName}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize(object value)
        {
            var bufferWriter = GetBufferWriter();
            try
            {
                var writer = new Writer(bufferWriter);
                SerializeBoxed(value, ref writer, value?.GetType());
                return bufferWriter.WrittenSpan.ToArray();
            }
            finally
            {
                ReturnBufferWriter(bufferWriter);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(object value, INinoBufferWriter bufferWriter)
        {
            Writer writer = new Writer(bufferWriter);
            SerializeBoxed(value, ref writer, value?.GetType());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SerializeBoxed(object value, ref Writer writer, Type type)
        {
            if (value == null || type == null)
            {
                writer.Write(TypeCollector.Null);
                return;
            }

            if (!Serializers.TryGetValue(type.TypeHandle.Value, out var serializer))
            {
                throw new Exception(
                    $"Serializer not found for type {type.FullName}, if this is an unmanaged type, please use Serialize<T>(T value, ref Writer writer) instead.");
            }

            serializer.SerializeBoxed(value, ref writer);
        }
    }
}