using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public ref struct Reader
    {
        private ReadOnlySpan<byte> _data;

        public Reader(ReadOnlySpan<byte> buffer)
        {
            _data = buffer;
        }

        public bool Eof
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _data.IsEmpty;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader Slice()
        {
            int length = 0;
            if (TypeCollector.Is64Bit)
            {
                length = Unsafe.ReadUnaligned<int>(ref MemoryMarshal.GetReference(_data));
            }
            else
            {
                Span<byte> dst = MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateSpan(ref length, 1));
                _data.Slice(0, dst.Length).CopyTo(dst);
            }

            var slice = _data.Slice(4, length - 4);
            _data = _data.Slice(length);
            return new Reader(slice);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count)
        {
            _data = _data.Slice(count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Peak<T>(out T value) where T : unmanaged
        {
            if (TypeCollector.Is64Bit)
            {
                value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(_data));
            }
            else
            {
                value = default;
                Span<byte> dst = MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateSpan(ref value, 1));

                _data.Slice(0, dst.Length).CopyTo(dst);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadCollectionHeader(out int length)
        {
            if (_data[0] == TypeCollector.NullCollection)
            {
                length = 0;
                _data = _data.Slice(1);
                return false;
            }

            //if value is 0 or sign bit is not set, then it's a null collection
            Read(out uint value);
#if BIGENDIAN
            length = (int)(value & 0x7FFFFFFF);
            return true;
#else
            value = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
            length = (int)(value & 0x7FFFFFFF);
            return true;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out bool value)
        {
            value = _data[0] == 1;
            _data = _data.Slice(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out byte value)
        {
            value = _data[0];
            _data = _data.Slice(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out sbyte value)
        {
            value = (sbyte)_data[0];
            _data = _data.Slice(1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T value) where T : unmanaged
        {
            Peak(out value);
            _data = _data.Slice(Unsafe.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T? value) where T : unmanaged
        {
            Read(out bool hasValue);
            if (!hasValue)
            {
                value = null;
                return;
            }

            Read(out T val);
            value = val;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T[] ret) where T : unmanaged
        {
            if (!ReadCollectionHeader(out var length))
            {
                ret = null;
                return;
            }

            if (length == 0)
            {
                ret = Array.Empty<T>();
                return;
            }

            GetBytes(length * Unsafe.SizeOf<T>(), out var bytes);
#if NET5_0_OR_GREATER
            ret = bytes.Length <= 2048 ? new T[length] : GC.AllocateUninitializedArray<T>(length);
#else
            ret = new T[length];
#endif
            Span<byte> dst = MemoryMarshal.AsBytes(ret.AsSpan());
            bytes.CopyTo(dst);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T?[] ret) where T : unmanaged
        {
            if (!ReadCollectionHeader(out var length))
            {
                ret = null;
                return;
            }

            ret = new T?[length];
            for (int i = 0; i < length; i++)
            {
                Read(out T? item);
                ret[i] = item;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out List<T> ret) where T : unmanaged
        {
#if NET8_0_OR_GREATER
            if (!ReadCollectionHeader(out var length))
            {
                ret = null;
                return;
            }

            ret = new List<T>(length);
            CollectionsMarshal.SetCount(ret, length);
            var span = CollectionsMarshal.AsSpan(ret);
            GetBytes(length * Unsafe.SizeOf<T>(), out var bytes);
            Span<byte> dst = MemoryMarshal.AsBytes(span);
            bytes.CopyTo(dst);
#else
            Read(out T[] arr);
            if (arr == null)
            {
                ret = null;
                return;
            }

            ret = new List<T>();
            ref var lst = ref Unsafe.As<List<T>, TypeCollector.ListView<T>>(ref ret);
            lst._size = arr.Length;
            lst._items = arr;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out IList<T> ret) where T : unmanaged
        {
            Read(out List<T> list);
            ret = list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out ICollection<T> ret) where T : unmanaged
        {
            Read(out List<T> list);
            ret = list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<TKey, TValue>(out Dictionary<TKey, TValue> ret) where TKey : unmanaged where TValue : unmanaged
        {
            if (!ReadCollectionHeader(out var length))
            {
                ret = null;
                return;
            }

            ret = new Dictionary<TKey, TValue>(length);
            for (int i = 0; i < length; i++)
            {
                Read(out KeyValuePair<TKey, TValue> pair);
                ret.Add(pair.Key, pair.Value);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<TKey, TValue>(out IDictionary<TKey, TValue> ret)
            where TKey : unmanaged where TValue : unmanaged
        {
            Read(out Dictionary<TKey, TValue> dictionary);
            ret = dictionary;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out List<T?> ret) where T : unmanaged
        {
            Read(out T?[] arr);
            if (arr == null)
            {
                ret = null;
                return;
            }

            ret = new List<T?>(arr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out IList<T?> ret) where T : unmanaged
        {
            Read(out List<T?> list);
            ret = list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out ICollection<T?> ret) where T : unmanaged
        {
            Read(out List<T?> list);
            ret = list;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUtf8(out string ret)
        {
            if (!ReadCollectionHeader(out var length))
            {
                ret = null;
                return;
            }

            if (length == 0)
            {
                ret = string.Empty;
                return;
            }

            GetBytes(length, out var utf8Bytes);

#if NET5_0_OR_GREATER
            unsafe
            {
                ret = string.Create(length, (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(utf8Bytes)),
                    (dst, ptr) =>
                    {
                        var src = new Span<byte>((byte*)ptr, length);
                        if (System.Text.Unicode.Utf8.ToUtf16(src, dst, out _, out _,
                                replaceInvalidSequences: false) !=
                            System.Buffers.OperationStatus.Done)
                            throw new InvalidOperationException("Invalid utf8 string");
                    });
            }
#else
            ret = System.Text.Encoding.UTF8.GetString(utf8Bytes);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out string ret)
        {
            if (!ReadCollectionHeader(out var length))
            {
                ret = null;
                return;
            }

            if (length == 0)
            {
                ret = string.Empty;
                return;
            }

            GetBytes(length * sizeof(char), out var utf16Bytes);
#if NET5_0_OR_GREATER
            if (TypeCollector.Is64Bit)
            {
                ret = new string(MemoryMarshal.Cast<byte, char>(utf16Bytes));
            }
            else
            {
                unsafe
                {
                    ret = string.Create(length, (IntPtr)Unsafe.AsPointer(
                            ref MemoryMarshal.GetReference(utf16Bytes)),
                        (dst, ptr) =>
                        {
                            Buffer.MemoryCopy(ptr.ToPointer(),
                                Unsafe.AsPointer(ref MemoryMarshal.GetReference(
                                    MemoryMarshal.AsBytes(dst))),
                                length * sizeof(char), length * sizeof(char));
                        });
                }
            }
#else
            if (TypeCollector.Is64Bit)
            {
                ret = MemoryMarshal.Cast<byte, char>(utf16Bytes).ToString();
            }
            else if (length <= 1024)
            {
                Span<char> tmp = stackalloc char[length];
                Span<byte> dst = MemoryMarshal.AsBytes(tmp);
                utf16Bytes.CopyTo(dst);
                ret = new string(tmp);
            }
            else
            {
                char[] tmp = ArrayPool<char>.Shared.Rent(length);
                Span<char> tmpSpan = tmp.AsSpan(0, length);
                Span<byte> dst = MemoryMarshal.AsBytes(tmpSpan);
                utf16Bytes.CopyTo(dst);
                ret = new string(tmpSpan);
                ArrayPool<char>.Shared.Return(tmp);
            }
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GetBytes(int length, out ReadOnlySpan<byte> bytes)
        {
            bytes = _data.Slice(0, length);
            _data = _data.Slice(length);
        }
    }
}