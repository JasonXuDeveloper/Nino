using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public readonly ref struct Writer
    {
        private readonly IBufferWriter<byte> _bufferWriter;

        public Writer(IBufferWriter<byte> bufferWriter)
        {
            _bufferWriter = bufferWriter;
#if NET6_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(bufferWriter);
#else
            if (bufferWriter == null)
            {
                throw new ArgumentNullException(nameof(bufferWriter));
            }
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T value) where T : unmanaged
        {
            var span = _bufferWriter.GetSpan(Unsafe.SizeOf<T>());
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), value);
            _bufferWriter.Advance(Unsafe.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? value) where T : unmanaged
        {
            if (!value.HasValue)
            {
                Write(TypeCollector.NullTypeId);
                return;
            }

            Write(TypeCollector.NullableTypeId);
            Write(value.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] value) where T : unmanaged
        {
            Write((Span<T>)value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullTypeId);
                return;
            }

            int byteLength = value.Length * Unsafe.SizeOf<T>();
            var span = _bufferWriter.GetSpan(sizeof(ushort) + sizeof(int) + byteLength);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.CollectionTypeId);
            Unsafe.WriteUnaligned(ref span[2], value.Length);
            Unsafe.CopyBlockUnaligned(ref span[6], ref Unsafe.As<T, byte>(ref value[0]),
                (uint)byteLength);
            _bufferWriter.Advance(sizeof(ushort) + sizeof(int) + byteLength);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T?> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullTypeId);
                return;
            }

            Write(TypeCollector.CollectionTypeId);
            Write(value.Length);
            foreach (var item in value)
            {
                Write(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(List<T> value) where T : unmanaged
        {
#if NET6_0_OR_GREATER
            Write(CollectionsMarshal.AsSpan(value));
#else
            Write((ICollection<T>)value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(List<T?> value) where T : unmanaged
        {
#if NET6_0_OR_GREATER
            Write(CollectionsMarshal.AsSpan(value));
#else
            Write((ICollection<T>)value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ICollection<T> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullTypeId);
                return;
            }

            int byteLength = value.Count * Unsafe.SizeOf<T>();
            var span = _bufferWriter.GetSpan(sizeof(ushort) + sizeof(int) + byteLength);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.CollectionTypeId);
            Unsafe.WriteUnaligned(ref span[2], value.Count);
            var current = span.Slice(6);
            foreach (var item in value)
            {
                Unsafe.WriteUnaligned(ref current[0], item);
                current = current.Slice(Unsafe.SizeOf<T>());
            }

            _bufferWriter.Advance(sizeof(ushort) + sizeof(int) + byteLength);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ICollection<T?> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullTypeId);
                return;
            }

            Write(TypeCollector.CollectionTypeId);
            Write(value.Count);
            foreach (var item in value)
            {
                Write(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string value)
        {
            switch (value)
            {
                case null:
                    Write(TypeCollector.NullTypeId);
                    return;
                case "":
                    Write(TypeCollector.StringTypeId);
                    Write(0);
                    return;
                default:
                    int byteLength = value.Length * Unsafe.SizeOf<char>();
                    int spanLength = sizeof(ushort) + sizeof(int) + byteLength;
                    var span = _bufferWriter.GetSpan(spanLength);
                    Unsafe.WriteUnaligned(ref span[0], TypeCollector.StringTypeId);
                    Unsafe.WriteUnaligned(ref span[2], value.Length);
                    ref var valueRef = ref MemoryMarshal.GetReference(value.AsSpan());
                    ref byte valueByte = ref Unsafe.As<char, byte>(ref valueRef);
                    Unsafe.CopyBlockUnaligned(ref span[6], ref valueByte, (uint)byteLength);
                    _bufferWriter.Advance(spanLength);
                    break;
            }
        }
    }
}