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
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T value) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            var span = _bufferWriter.GetSpan(size);
            Unsafe.WriteUnaligned(ref span[0], value);
            _bufferWriter.Advance(size);
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
            if (value == null)
            {
                Write(TypeCollector.NullTypeId);
                return;
            }

            var valueSpan = MemoryMarshal.AsBytes(value.AsSpan());
            int size = sizeof(ushort) + sizeof(int) + valueSpan.Length;
            var span = _bufferWriter.GetSpan(size);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.CollectionTypeId);
            Unsafe.WriteUnaligned(ref span[2], value.Length);
            Unsafe.CopyBlockUnaligned(ref span[6], ref valueSpan[0],
                (uint)valueSpan.Length);
            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T> value) where T : unmanaged
        {
            if (value == Span<T>.Empty)
            {
                Write(TypeCollector.NullTypeId);
                return;
            }

            var valueSpan = MemoryMarshal.AsBytes(value);
            int size = sizeof(ushort) + sizeof(int) + valueSpan.Length;
            var span = _bufferWriter.GetSpan(size);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.CollectionTypeId);
            Unsafe.WriteUnaligned(ref span[2], value.Length);
            Unsafe.CopyBlockUnaligned(ref span[6], ref valueSpan[0],
                (uint)valueSpan.Length);
            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T?> value) where T : unmanaged
        {
            if (value == Span<T?>.Empty)
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

            int eleSize = Unsafe.SizeOf<T>();
            int byteLength = value.Count * eleSize;
            var span = _bufferWriter.GetSpan(sizeof(ushort) + sizeof(int) + byteLength);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.CollectionTypeId);
            Unsafe.WriteUnaligned(ref span[2], value.Count);
            var current = span.Slice(6);
            foreach (var item in value)
            {
                Unsafe.WriteUnaligned(ref current[0], item);
                current = current.Slice(eleSize);
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
                    var header = _bufferWriter.GetSpan(sizeof(ushort) + sizeof(int));
                    Unsafe.WriteUnaligned(ref header[0], TypeCollector.StringTypeId);
                    Unsafe.WriteUnaligned(ref header[2], 0);
                    _bufferWriter.Advance(sizeof(ushort) + sizeof(int));
                    return;
                default:
                    var valueSpan = MemoryMarshal.AsBytes(value.AsSpan());
                    int spanLength = sizeof(ushort) + sizeof(int) + valueSpan.Length;
                    var span = _bufferWriter.GetSpan(spanLength);
                    Unsafe.WriteUnaligned(ref span[0], TypeCollector.StringTypeId);
                    Unsafe.WriteUnaligned(ref span[2], value.Length);
                    Unsafe.CopyBlockUnaligned(ref span[6], ref MemoryMarshal.GetReference(valueSpan),
                        (uint)valueSpan.Length);
                    _bufferWriter.Advance(spanLength);
                    break;
            }
        }
    }
}