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
                    // Safe paths for 32-bit platforms - avoid potentially problematic unaligned 8-byte writes
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
                        default:
                            unsafe
                            {
                                T temp = value;
                                Span<T> srcSpan = MemoryMarshal.CreateSpan(ref temp, 1);
                                ReadOnlySpan<byte> src =
                                    new ReadOnlySpan<byte>(Unsafe.AsPointer(ref srcSpan.GetPinnableReference()), size);
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
}