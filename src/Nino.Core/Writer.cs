using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public readonly unsafe ref struct Writer
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
            var span = _bufferWriter.GetSpan(sizeof(T));
#if NET8_0_OR_GREATER
            MemoryMarshal.Write(span, in value);
#else
            MemoryMarshal.Write(span, ref value);
#endif
            _bufferWriter.Advance(sizeof(T));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? value) where T : unmanaged
        {
            if (!value.HasValue)
            {
                Write((ushort)TypeCollector.NullTypeId);
                return;
            }

            Write((ushort)TypeCollector.NullableTypeId);
            Write(value.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T> value) where T : unmanaged
        {
            if (value == null)
            {
                Write((ushort)TypeCollector.NullTypeId);
                return;
            }

            Write((ushort)TypeCollector.CollectionTypeId);
            Write(value.Length);
            MemoryMarshal.Cast<T, byte>(value).CopyTo(_bufferWriter.GetSpan(value.Length * sizeof(T)));
            _bufferWriter.Advance(value.Length * sizeof(T));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T?> value) where T : unmanaged
        {
            if (value == null)
            {
                Write((ushort)TypeCollector.NullTypeId);
                return;
            }

            Write((ushort)TypeCollector.CollectionTypeId);
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
            Write((IList<T>)value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(List<T?> value) where T : unmanaged
        {
#if NET6_0_OR_GREATER
            Write(CollectionsMarshal.AsSpan(value));
#else
            Write((IList<T>)value);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ICollection<T> value) where T : unmanaged
        {
            if (value == null)
            {
                Write((ushort)TypeCollector.NullTypeId);
                return;
            }

            Write((ushort)TypeCollector.CollectionTypeId);
            Write(value.Count);
            foreach (var item in value)
            {
                Write(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ICollection<T?> value) where T : unmanaged
        {
            if (value == null)
            {
                Write((ushort)TypeCollector.NullTypeId);
                return;
            }

            Write((ushort)TypeCollector.CollectionTypeId);
            Write(value.Count);
            foreach (var item in value)
            {
                Write(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(bool value)
        {
            _bufferWriter.GetSpan(1)[0] = *(byte*)&value;
            _bufferWriter.Advance(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string value)
        {
            if (value == null)
            {
                Write((ushort)TypeCollector.NullTypeId);
                return;
            }

            Write((ushort)TypeCollector.StringTypeId);
            Write(value.Length);
            Span<byte> span = _bufferWriter.GetSpan(value.Length * sizeof(char));
            MemoryMarshal.Cast<char, byte>(value.AsSpan()).CopyTo(span);
            _bufferWriter.Advance(value.Length * sizeof(char));
        }
    }
}