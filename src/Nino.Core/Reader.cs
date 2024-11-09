using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public ref struct Reader
    {
        private Span<byte> _data;

        public Reader(Span<byte> buffer)
        {
            _data = buffer;
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
            Read(out ushort typeId);
            switch (typeId)
            {
                case TypeCollector.NullTypeId:
                    value = null;
                    return;
                case TypeCollector.NullableTypeId:
                    Read(out T ret);
                    value = ret;
                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T[] ret) where T : unmanaged
        {
            Read(out ushort typeId);
            switch (typeId)
            {
                case TypeCollector.NullTypeId:
                    ret = null;
                    return;
                case TypeCollector.CollectionTypeId:
                    Read(out int length);
                    GetBytes(length * Unsafe.SizeOf<T>(), out var bytes);
#if NET5_0_OR_GREATER
                    ret = bytes.Length > 2048 ? GC.AllocateUninitializedArray<T>(length) : new T[length];
                    Span<T> span = ret;
                    Span<byte> byteSpan = MemoryMarshal.Cast<T, byte>(span);
                    Unsafe.CopyBlockUnaligned(ref byteSpan[0], ref bytes[0], (uint)bytes.Length);
#else
                    ret = MemoryMarshal.Cast<byte, T>(bytes).ToArray();
#endif
                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T?[] ret) where T : unmanaged
        {
            ret = null;
            Read(out ushort typeId);
            switch (typeId)
            {
                case TypeCollector.NullTypeId:
                    return;
                case TypeCollector.CollectionTypeId:
                    Read(out int length);
                    ret = new T?[length];
                    for (int i = 0; i < length; i++)
                    {
                        Read(out T? item);
                        ret[i] = item;
                    }

                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out List<T> ret) where T : unmanaged
        {
            ret = null;
            Read(out ushort typeId);
            switch (typeId)
            {
                case TypeCollector.NullTypeId:
                    return;
                case TypeCollector.CollectionTypeId:
                    Read(out int length);
                    GetBytes(length * Unsafe.SizeOf<T>(), out var bytes);
                    ret = new List<T>(length);
#if NET5_0_OR_GREATER
                    ref var lst = ref Unsafe.As<List<T>, TypeCollector.ListView<T>>(ref ret);
                    lst._size = length;
                    Span<byte> byteSpan = MemoryMarshal.Cast<T, byte>(lst._items);
                    Unsafe.CopyBlockUnaligned(ref byteSpan[0], ref bytes[0], (uint)bytes.Length);
#else
                    ReadOnlySpan<T> span = MemoryMarshal.Cast<byte, T>(bytes);
                    for (int i = 0; i < length; i++)
                    {
                        ret.Add(span[i]);
                    }
#endif

                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
            }
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
            Read(out ushort typeId);
            switch (typeId)
            {
                case TypeCollector.NullTypeId:
                    ret = null;
                    return;
                case TypeCollector.CollectionTypeId:
                    Read(out int length);
                    ret = new Dictionary<TKey, TValue>(length);
                    for (int i = 0; i < length; i++)
                    {
                        Read(out KeyValuePair<TKey, TValue> pair);
                        ret.Add(pair.Key, pair.Value);
                    }

                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
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
        public void Read(out string ret)
        {
            Read(out ushort typeId);
            switch (typeId)
            {
                case TypeCollector.NullTypeId:
                    ret = null;
                    return;
                case TypeCollector.StringTypeId:
                    Read(out int length);
                    GetBytes(length * sizeof(char), out var bytes);
#if NET5_0_OR_GREATER
                    ret = new string(MemoryMarshal.Cast<byte, char>(bytes));
#else
                    ret = MemoryMarshal.Cast<byte, char>(bytes).ToString();
#endif
                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void GetBytes(int length, out Span<byte> bytes)
        {
            bytes = _data.Slice(0, length);
            _data = _data.Slice(length);
        }
    }
}