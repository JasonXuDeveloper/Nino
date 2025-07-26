using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nino.Core
{
    public static class NinoSerializer
    {
        private static readonly ConcurrentQueue<NinoArrayBufferWriter> BufferWriters = new();

        private static readonly NinoArrayBufferWriter DefaultBufferWriter = new(1024);
        private static int _defaultUsed;

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
        public static byte[] Serialize<T>(T value)
        {
            // Fast path for simple unmanaged types - use cached HasBaseType
            if (!CachedSerializer<T>.IsReferenceOrContainsReferences && !CachedSerializer<T>.HasBaseTypeFlag)
            {
                var size = Unsafe.SizeOf<T>();
                var result = new byte[size];
                var span = result.AsSpan();
                
                if (TypeCollector.Is64Bit)
                {
                    Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value);
                }
                else
                {
                    // Optimized paths for common sizes on 32-bit
                    switch (size)
                    {
                        case 1:
                            span[0] = Unsafe.As<T, byte>(ref value);
                            break;
                        case 2:
                            Unsafe.WriteUnaligned(ref span[0], Unsafe.As<T, ushort>(ref value));
                            break;
                        case 4:
                            Unsafe.WriteUnaligned(ref span[0], Unsafe.As<T, uint>(ref value));
                            break;
                        case 8:
                            Unsafe.WriteUnaligned(ref span[0], Unsafe.As<T, ulong>(ref value));
                            break;
                        default:
                            unsafe
                            {
                                T temp = value;
                                Span<T> srcSpan = MemoryMarshal.CreateSpan(ref temp, 1);
                                ReadOnlySpan<byte> src = new ReadOnlySpan<byte>(Unsafe.AsPointer(ref srcSpan.GetPinnableReference()), size);
                                src.CopyTo(span);
                            }
                            break;
                    }
                }
                
                return result;
            }
            
            var bufferWriter = GetBufferWriter();
            try
            {
                var writer = new Writer(bufferWriter);
                Serialize(value, ref writer);
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
            var writer = new Writer(bufferWriter);
            Serialize(value, ref writer);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T>(T value, ref Writer writer)
        {
            // Fast path for simple unmanaged types - use cached HasBaseType
            if (!CachedSerializer<T>.IsReferenceOrContainsReferences && !CachedSerializer<T>.HasBaseTypeFlag)
            {
                writer.UnsafeWrite(value);
                return;
            }
            
            if (value is null)
            {
                writer.Write(TypeCollector.Null);
                return;
            }
            
            // Direct access to cached serializer - inline for performance
            var serializer = CachedSerializer<T>.Instance;
            
            // Fast path for most common case: no polymorphism
            if (serializer.SubTypeSerializers.Count == 0)
            {
                serializer.Serializer(value, ref writer);
                return;
            }
            
            // Handle polymorphism
            serializer.Serialize(value, ref writer);
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

            if (!NinoTypeMetadata.Serializers.TryGetValue(type.TypeHandle.Value, out var serializer))
            {
                throw new Exception(
                    $"Serializer not found for type {type.FullName}, if this is an unmanaged type, please use Serialize<T>(T value, ref Writer writer) instead.");
            }

            serializer.SerializeBoxed(value, ref writer);
        }
    }
}