using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    public unsafe ref struct Reader
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
            if (typeId == TypeCollector.NullTypeId)
            {
                value = null;
                return;
            }

            if (typeId != TypeCollector.NullableTypeId)
            {
                throw new InvalidOperationException($"Invalid type id {typeId}");
            }

            Read(out T ret);
            value = ret;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T[] ret) where T : unmanaged
        {
            ret = null;
            Read(out ushort typeId);
            if (typeId == TypeCollector.NullTypeId)
            {
                return;
            }

            if (typeId != TypeCollector.CollectionTypeId)
            {
                throw new InvalidOperationException($"Invalid type id {typeId}");
            }

            Read(out int length);
            ret = new T[length];
            var span = ret.AsSpan();
            _bufferReader.GetBytes(length * sizeof(T), out var bytes);
            MemoryMarshal.Cast<byte, T>(bytes).CopyTo(span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T?[] ret) where T : unmanaged
        {
            ret = null;
            Read(out ushort typeId);
            if (typeId == TypeCollector.NullTypeId)
            {
                return;
            }

            if (typeId != TypeCollector.CollectionTypeId)
            {
                throw new InvalidOperationException($"Invalid type id {typeId}");
            }

            Read(out int length);
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
            ret = null;
            Read(out T[] arr);
            if (arr == null)
            {
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
            ret = null;
#if NET5_0_OR_GREATER
            Read(out KeyValuePair<TKey, TValue>[] arr);
            if (arr == null)
            {
                return;
            }

            ret = new Dictionary<TKey, TValue>(arr);
#else
            Read(out ushort typeId);
            if (typeId == TypeCollector.NullTypeId)
            {
                return;
            }

            if (typeId != TypeCollector.CollectionTypeId)
            {
                throw new InvalidOperationException($"Invalid type id {typeId}");
            }

            Read(out int length);
            ret = new Dictionary<TKey, TValue>(length);
            for (int i = 0; i < length; i++)
            {
                Read(out TKey key);
                Read(out TValue value);
                ret[key] = value;
            }
#endif
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<TKey, TValue>(out IDictionary<TKey, TValue> ret) where TKey : unmanaged where TValue : unmanaged
        {
            Read(out Dictionary<TKey, TValue> dictionary);
            ret = dictionary;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out List<T?> ret) where T : unmanaged
        {
            ret = null;
            Read(out T?[] arr);
            if (arr == null)
            {
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
            ret = null;
            Read(out ushort typeId);
            if (typeId == TypeCollector.NullTypeId)
            {
                return;
            }

            if (typeId != TypeCollector.StringTypeId)
            {
                throw new InvalidOperationException($"Invalid type id {typeId}");
            }

            Read(out int length);
            _bufferReader.GetBytes(length * sizeof(char), out var bytes);
            ret = MemoryMarshal.Cast<byte, char>(bytes).ToString();
        }
    }
}