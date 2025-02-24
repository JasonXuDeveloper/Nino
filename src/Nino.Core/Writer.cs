using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public readonly ref struct Writer
    {
        private readonly INinoBufferWriter _bufferWriter;

        public int WrittenCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bufferWriter.WrittenCount;
        }

        public Writer(INinoBufferWriter bufferWriter)
        {
            _bufferWriter = bufferWriter;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count)
        {
            _bufferWriter.GetSpan(count);
            _bufferWriter.Advance(count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBack<T>(T value, int offset) where T : unmanaged
        {
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(_bufferWriter.WrittenSpan.Slice(offset)), value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T value) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(size)), value);
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

            int size = Unsafe.SizeOf<T>() + 1;
            ref byte header = ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(size));
            header = 1;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref header, 1), value.Value);
            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            Write(value.AsSpan());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T?[] value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

            Write(value.AsSpan());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T> value) where T : unmanaged
        {
            if (value.IsEmpty)
            {
                Write(TypeCollector.EmptyCollectionHeader);
                return;
            }

            var valueSpan = MemoryMarshal.AsBytes(value);
            int size = sizeof(int) + valueSpan.Length;
            ref byte header = ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(size));
            Unsafe.WriteUnaligned(ref header, TypeCollector.GetCollectionHeader(value.Length));
            Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref header, 4), ref MemoryMarshal.GetReference(valueSpan),
                (uint)valueSpan.Length);
            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Span<T?> value) where T : unmanaged
        {
            if (value.IsEmpty)
            {
                Write(TypeCollector.EmptyCollectionHeader);
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
            Write(lst._items.AsSpan(0, lst._size));
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(List<T?> value) where T : unmanaged
        {
            if (value == null)
            {
                Write(TypeCollector.NullCollection);
                return;
            }

#if NET6_0_OR_GREATER
            Write(CollectionsMarshal.AsSpan(value));
#else
            ref var lst = ref Unsafe.As<List<T?>, TypeCollector.ListView<T?>>(ref value);
            Write(lst._items.AsSpan(0, lst._size));
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
            ref byte header = ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(size));
            Unsafe.WriteUnaligned(ref header, TypeCollector.GetCollectionHeader(value.Count));
            int offset = 4;
            foreach (var item in value)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref header, offset), item);
                offset += eleSize;
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
            ref byte header = ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(size));
            Unsafe.WriteUnaligned(ref header, TypeCollector.GetCollectionHeader(value.Count));
            int offset = 4;
            foreach (var item in value)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref header, offset), item);
                offset += eleSize;
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
                    Write(TypeCollector.EmptyCollectionHeader);
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
                    Write(TypeCollector.EmptyCollectionHeader);
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