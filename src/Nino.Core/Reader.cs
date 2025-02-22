using System;
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
        public Reader Slice(int length)
        {
            var slice = _data.Slice(0, length);
            _data = _data.Slice(length);
            return new Reader(slice);
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
#if NET5_0_OR_GREATER
            value = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
#else
            //to little endian
            value = (value << 24) | (value >> 24) | ((value & 0x0000FF00) << 8) | ((value & 0x00FF0000) >> 8);
#endif
            length = (int)(value & 0x7FFFFFFF);
            return true;
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T value) where T : unmanaged
        {
            value = Unsafe.ReadUnaligned<T>(ref MemoryMarshal.GetReference(_data));
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
            ref byte first = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(ret));
#else
            ret = new T[length];
            ref byte first = ref Unsafe.As<T, byte>(ref ret[0]);
#endif
            Unsafe.CopyBlockUnaligned(ref first, ref MemoryMarshal.GetReference(bytes), (uint)bytes.Length);
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
            ret = new string(MemoryMarshal.Cast<byte, char>(utf16Bytes));
#else
            ret = MemoryMarshal.Cast<byte, char>(utf16Bytes).ToString();
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