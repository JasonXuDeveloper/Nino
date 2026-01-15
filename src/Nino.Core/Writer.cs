using System;
// ReSharper disable once RedundantUsingDirective
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    public ref struct Writer
    {
        private readonly INinoBufferWriter _bufferWriter;

        public Writer(INinoBufferWriter bufferWriter)
        {
            _bufferWriter = bufferWriter;
        }

        /// <summary>
        /// Returns the position before advancing
        /// </summary>
        /// <param name="count"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Advance(int count)
        {
            var pos = _bufferWriter.WrittenCount;
            _bufferWriter.GetSpan(count);
            _bufferWriter.Advance(count);

            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutLength(int oldPos)
        {
            var diff = _bufferWriter.WrittenCount - oldPos;
            if (TypeCollector.Is64Bit)
            {
                Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(_bufferWriter.WrittenSpan.Slice(oldPos)), diff);
            }
            else
            {
                ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref diff, 1));
                ref byte dst = ref MemoryMarshal.GetReference(_bufferWriter.WrittenSpan.Slice(oldPos));
                src.CopyTo(MemoryMarshal.CreateSpan(ref dst, 4));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PutBack<T>(T value, int oldPos)
            where T : unmanaged
        {
            if (TypeCollector.Is64Bit)
            {
                Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(_bufferWriter.WrittenSpan.Slice(oldPos)), value);
            }
            else
            {
                ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
                ref byte dst = ref MemoryMarshal.GetReference(_bufferWriter.WrittenSpan.Slice(oldPos));
                src.CopyTo(MemoryMarshal.CreateSpan(ref dst, Unsafe.SizeOf<T>()));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(byte value)
        {
            _bufferWriter.GetSpan(1)[0] = value;
            _bufferWriter.Advance(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(sbyte value)
        {
            _bufferWriter.GetSpan(1)[0] = (byte)value;
            _bufferWriter.Advance(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(bool value)
        {
            _bufferWriter.GetSpan(1)[0] = value ? (byte)1 : (byte)0;
            _bufferWriter.Advance(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeWrite<T>(T value)
        {
            int size = Unsafe.SizeOf<T>();
            var span = _bufferWriter.GetSpan(size);

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
                        // Use safe memory copy for 8+ byte values on 32-bit to avoid alignment issues
                        Unsafe.CopyBlockUnaligned(ref MemoryMarshal.GetReference(span), ref Unsafe.As<T, byte>(ref value), (uint)size);

                        break;
                }
            }

            _bufferWriter.Advance(size);
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
            T val = value.Value;
            span[0] = 1;
            if (TypeCollector.Is64Bit)
            {
                Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span.Slice(1)), val);
            }
            else
            {
                ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref val, 1));
                src.CopyTo(span.Slice(1));
            }

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
            var span = _bufferWriter.GetSpan(size);
            var header = TypeCollector.GetCollectionHeader(value.Length);
            if (TypeCollector.Is64Bit)
            {
                Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), header);
            }
            else
            {
                ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1));
                src.CopyTo(span);
            }

            valueSpan.CopyTo(span.Slice(4));
            _bufferWriter.Advance(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteSpanWithoutHeader<T>(Span<T> value) where T : unmanaged
        {
            if (value.IsEmpty)
            {
                return;
            }

            var valueSpan = MemoryMarshal.AsBytes(value);
            var span = _bufferWriter.GetSpan(valueSpan.Length);
            valueSpan.CopyTo(span);
            _bufferWriter.Advance(valueSpan.Length);
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
            ref var lst = ref Unsafe.As<List<T>, ListView<T>>(ref value);
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
            ref var lst = ref Unsafe.As<List<T?>, ListView<T?>>(ref value);
            Write(lst._items.AsSpan(0, lst._size));
#endif
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
                case var _ when !TypeCollector.Is64Bit:
                {
                    int length = value.Length;
                    int spanLength = sizeof(int) + length;
                    var span = _bufferWriter.GetSpan(spanLength);
                    var header = TypeCollector.GetCollectionHeader(length);
                    ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1));
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
                    return;
                }
                default:
                {
                    int length = value.Length;
                    int spanLength = sizeof(int) + length;
                    var span = _bufferWriter.GetSpan(spanLength);
                    var header = TypeCollector.GetCollectionHeader(length);
                    Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), header);
                    span = span.Slice(4);
#if NET5_0_OR_GREATER
                    if (System.Text.Unicode.Utf8.FromUtf16(value.AsSpan(), span, out _, out _,
                            replaceInvalidSequences: false) != OperationStatus.Done)
                        throw new InvalidOperationException("Failed to convert utf16 to utf8");
#else
                    System.Text.Encoding.UTF8.GetBytes(value, span);
#endif
                    _bufferWriter.Advance(spanLength);
                    return;
                }
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
                case var _ when !TypeCollector.Is64Bit:
                {
                    int length = value.Length;
                    var valueSpan = MemoryMarshal.AsBytes(value.AsSpan());
                    int spanLength = sizeof(int) + valueSpan.Length;
                    var span = _bufferWriter.GetSpan(spanLength);
                    var header = TypeCollector.GetCollectionHeader(length);
                    ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1));
                    src.CopyTo(span);
                    valueSpan.CopyTo(span.Slice(4));
                    _bufferWriter.Advance(spanLength);
                    return;
                }
                default:
                {
                    int length = value.Length;
                    var valueSpan = MemoryMarshal.AsBytes(value.AsSpan());
                    int spanLength = sizeof(int) + valueSpan.Length;
                    var span = _bufferWriter.GetSpan(spanLength);
                    var header = TypeCollector.GetCollectionHeader(length);
                    Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(span), header);
                    valueSpan.CopyTo(span.Slice(4));
                    _bufferWriter.Advance(spanLength);
                    return;
                }
            }
        }
    }
}
