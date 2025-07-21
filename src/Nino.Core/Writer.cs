using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public ref struct Writer
    {
        private readonly IBufferWriter<byte> _bufferWriter;

        public int WrittenCount;

        public Writer(IBufferWriter<byte> bufferWriter)
        {
            _bufferWriter = bufferWriter;
            WrittenCount = 0;
        }

        /// <summary>
        /// Returns the position before advancing
        /// </summary>
        /// <param name="count"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Advance(int count)
        {
            var pos = WrittenCount;
            _bufferWriter.GetSpan(count);
            _bufferWriter.Advance(count);
            WrittenCount += count;

            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutLength(int oldPos)
        {
            var diff = WrittenCount - oldPos;
            ref byte oldPosByte = ref Unsafe.Subtract(ref MemoryMarshal.GetReference(_bufferWriter.GetSpan()), diff);
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                    ref diff
#else
                    diff
#endif
                ), 1));
            src.CopyTo(MemoryMarshal.CreateSpan(ref oldPosByte, 4));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutBack<T>(T value, int oldPos)
            where T : unmanaged
        {
            var diff = WrittenCount - oldPos;
            ref byte oldPosByte = ref Unsafe.Subtract(ref MemoryMarshal.GetReference(_bufferWriter.GetSpan()), diff);
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                    ref value
#else
                    value
#endif
                ), 1));
            src.CopyTo(MemoryMarshal.CreateSpan(ref oldPosByte, Unsafe.SizeOf<T>()));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(byte value)
        {
            ref byte ret = ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(1));
            ret = value;
            _bufferWriter.Advance(1);
            WrittenCount += 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(sbyte value)
        {
            ref byte ret = ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(1));
            ret = (byte)value;
            _bufferWriter.Advance(1);
            WrittenCount += 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(bool value)
        {
            ref byte ret = ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(1));
            ret = value ? (byte)1 : (byte)0;
            _bufferWriter.Advance(1);
            WrittenCount += 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeWrite<T>(T value)
        {
            int size = Unsafe.SizeOf<T>();
            if (TypeCollector.Is64Bit)
            {
                Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(_bufferWriter.GetSpan(size)), value);
            }
            else
            {
                ReadOnlySpan<T> valSpan = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                    ref value
#else
                        value
#endif
                ), 1);
                unsafe
                {
                    Span<T> srcSpan = MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(valSpan), 1);
                    ReadOnlySpan<byte> src = new ReadOnlySpan<byte>(
                        Unsafe.AsPointer(ref srcSpan.GetPinnableReference()),
                        Unsafe.SizeOf<T>());
                    src.CopyTo(_bufferWriter.GetSpan(size));
                }
            }

            _bufferWriter.Advance(size);
            WrittenCount += size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T value) where T : unmanaged
        {
            UnsafeWrite(value);
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
            var span = _bufferWriter.GetSpan(size);
            ref byte header = ref MemoryMarshal.GetReference(span);
            T val = value.Value;
            header = 1;
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                    ref val
#else
                    val
#endif
                ), 1));
            src.CopyTo(span.Slice(1));
            _bufferWriter.Advance(size);
            WrittenCount += size;
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
            var span = _bufferWriter.GetSpan(size);
            var header = TypeCollector.GetCollectionHeader(value.Length);
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                    ref header
#else
                    header
#endif
                ), 1));
            src.CopyTo(span);
            valueSpan.CopyTo(span.Slice(4));

            _bufferWriter.Advance(size);
            WrittenCount += size;
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
        public void Write<TKey, TValue>(IDictionary<TKey, TValue> value) where TKey : unmanaged where TValue : unmanaged
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
            var header = TypeCollector.GetCollectionHeader(value.Count);
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                    ref header
#else
                    header
#endif
                ), 1));
            src.CopyTo(span);
            span = span.Slice(4);

            foreach (var item in value)
            {
#if NET8_0_OR_GREATER
                KeyValuePair<TKey, TValue> temp = item;
#endif
                ReadOnlySpan<byte> src2 = MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                        ref temp
#else
                        item
#endif
                    ), 1));
                src2.CopyTo(span);
                span = span.Slice(eleSize);
            }

            _bufferWriter.Advance(size);
            WrittenCount += size;
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
            var header = TypeCollector.GetCollectionHeader(value.Count);
            ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                    ref header
#else
                    header
#endif
                ), 1));
            src.CopyTo(span);
            span = span.Slice(4);
            foreach (var item in value)
            {
#if NET8_0_OR_GREATER
                T temp = item;
#endif
                ReadOnlySpan<byte> src2 = MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                        ref temp
#else
                        item
#endif
                    ), 1));
                src2.CopyTo(span);
                span = span.Slice(eleSize);
            }

            _bufferWriter.Advance(size);
            WrittenCount += size;
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
                    var header = TypeCollector.GetCollectionHeader(value.Length);
                    ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                            ref header
#else
                            header
#endif
                        ), 1));
                    src.CopyTo(span);
                    span = span.Slice(4);
#if NET5_0_OR_GREATER
                    if (System.Text.Unicode.Utf8.FromUtf16(value.AsSpan(), span, out _, out _,
                            replaceInvalidSequences: false) != OperationStatus.Done)
                        throw new InvalidOperationException("Failed to convert utf16 to utf8");
#else
                    System.Text.Encoding.UTF8.GetBytes(value, span);
#endif
                    _bufferWriter.Advance(spanLength);
                    WrittenCount += spanLength;
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
                    var header = TypeCollector.GetCollectionHeader(value.Length);
                    ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(
                        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(
#if NET8_0_OR_GREATER
                            ref header
#else
                            header
#endif
                        ), 1));
                    src.CopyTo(span);
                    valueSpan.CopyTo(span.Slice(4));
                    _bufferWriter.Advance(spanLength);
                    WrittenCount += spanLength;
                    break;
            }
        }
    }
}