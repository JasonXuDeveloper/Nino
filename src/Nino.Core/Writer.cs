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
                Write(false);
                return;
            }

            Write(true);
            Write(value.Value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            var valueSpan = MemoryMarshal.AsBytes(value.AsSpan());
            int size = sizeof(int) + valueSpan.Length;
            var span = _bufferWriter.GetSpan(size);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.GetCollectionHeader(value.Length));
            Unsafe.CopyBlockUnaligned(ref span[4], ref valueSpan[0],
                (uint)valueSpan.Length);
            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T> value) where T : unmanaged
        {
            if (value == Span<T>.Empty)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            var valueSpan = MemoryMarshal.AsBytes(value);
            int size = sizeof(int) + valueSpan.Length;
            var span = _bufferWriter.GetSpan(size);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.GetCollectionHeader(value.Length));
            Unsafe.CopyBlockUnaligned(ref span[4], ref valueSpan[0],
                (uint)valueSpan.Length);
            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T?> value) where T : unmanaged
        {
            if (value == Span<T?>.Empty)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            Write(TypeCollector.GetCollectionHeader(value.Length));
            foreach (var item in value)
            {
                Write(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(List<T> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }
            
#if NET6_0_OR_GREATER
            Write(CollectionsMarshal.AsSpan(value));
#else
            ref var lst = ref Unsafe.As<List<T>, TypeCollector.ListView<T>>(ref value);
            Write(lst._items);
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
        public void Write<TKey, TValue>(Dictionary<TKey, TValue> value) where TKey : unmanaged where TValue : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            int eleSize = Unsafe.SizeOf<KeyValuePair<TKey, TValue>>();
            int byteLength = value.Count * eleSize;
            int size = sizeof(int) + byteLength;
            var span = _bufferWriter.GetSpan(size);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.GetCollectionHeader(value.Count));
            var current = span.Slice(4);
            foreach (var item in value)
            {
                Unsafe.WriteUnaligned(ref current[0], item);
                current = current.Slice(eleSize);
            }

            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ICollection<T> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            int eleSize = Unsafe.SizeOf<T>();
            int byteLength = value.Count * eleSize;
            int size = sizeof(int) + byteLength;
            var span = _bufferWriter.GetSpan(size);
            Unsafe.WriteUnaligned(ref span[0], TypeCollector.GetCollectionHeader(value.Count));
            var current = span.Slice(4);
            foreach (var item in value)
            {
                Unsafe.WriteUnaligned(ref current[0], item);
                current = current.Slice(eleSize);
            }

            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ICollection<T?> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            Write(TypeCollector.GetCollectionHeader(value.Count));
            foreach (var item in value)
            {
                Write(item);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUtf8(string value)
        {
            switch (value)
            {
                case null:
                    Write(TypeCollector.NullCollection);
                    return;
                case "":
                    var header = _bufferWriter.GetSpan(sizeof(int));
                    Unsafe.WriteUnaligned(ref header[0], TypeCollector.GetCollectionHeader(0));
                    _bufferWriter.Advance(sizeof(int));
                    return;
                default:
                    int spanLength = sizeof(int) + value.Length;
                    var span = _bufferWriter.GetSpan(spanLength);
                    Unsafe.WriteUnaligned(ref span[0], TypeCollector.GetCollectionHeader(value.Length));
#if NET5_0_OR_GREATER
                    if (System.Text.Unicode.Utf8.FromUtf16(value.AsSpan(), span.Slice(4), out _, out _,
                            replaceInvalidSequences: false) != OperationStatus.Done)
                        throw new InvalidOperationException("Failed to convert utf16 to utf8");
#else
                    System.Text.Encoding.UTF8.GetBytes(value, span.Slice(4));
#endif
                    _bufferWriter.Advance(spanLength);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string value)
        {
            switch (value)
            {
                case null:
                    Write(TypeCollector.NullCollection);
                    return;
                case "":
                    var header = _bufferWriter.GetSpan(sizeof(int));
                    Unsafe.WriteUnaligned(ref header[0], TypeCollector.GetCollectionHeader(0));
                    _bufferWriter.Advance(sizeof(int));
                    return;
                default:
                    var valueSpan = MemoryMarshal.AsBytes(value.AsSpan());
                    int spanLength = sizeof(int) + valueSpan.Length;
                    var span = _bufferWriter.GetSpan(spanLength);
                    Unsafe.WriteUnaligned(ref span[0], TypeCollector.GetCollectionHeader(value.Length));
                    Unsafe.CopyBlockUnaligned(ref span[4], ref MemoryMarshal.GetReference(valueSpan),
                        (uint)valueSpan.Length);
                    _bufferWriter.Advance(spanLength);
                    break;
            }
        }
    }
}