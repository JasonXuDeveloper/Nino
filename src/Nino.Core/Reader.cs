using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public ref struct Reader
    {
        private SpanBufferReader _bufferReader;

        public Reader(ReadOnlySpan<byte> buffer)
        {
            _bufferReader = new SpanBufferReader(buffer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T value) where T : unmanaged
        {
            _bufferReader.Get(out value);
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
                    _bufferReader.GetBytes(length * Unsafe.SizeOf<T>(), out var bytes);
#if NET5_0_OR_GREATER
                    ret = GC.AllocateUninitializedArray<T>(length);
                    Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref ret[0]), ref MemoryMarshal.GetReference(bytes),
                        (uint)bytes.Length);
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
            Read(out T[] arr);
            if (arr == null)
            {
                ret = null;
                return;
            }

            ret = new List<T>(arr);
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
#if NET5_0_OR_GREATER
            Read(out KeyValuePair<TKey, TValue>[] arr);
            if (arr == null)
            {
                ret = null;
                return;
            }

            ret = new Dictionary<TKey, TValue>(arr);
#else
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
                        Read(out TKey key);
                        Read(out TValue value);
                        ret.Add(key, value);
                    }

                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
            }
#endif
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
        public void Read(out bool value)
        {
            _bufferReader.Get(out value);
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
                    _bufferReader.GetBytes(length * sizeof(char), out var bytes);
                    ret = MemoryMarshal.Cast<byte, char>(bytes).ToString();
                    return;
                default:
                    throw new InvalidOperationException($"Invalid type id {typeId}");
            }
        }
    }
}