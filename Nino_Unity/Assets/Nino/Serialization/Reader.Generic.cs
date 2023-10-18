using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Serialization
{
    /// <summary>
    /// Generic methods for reader (ILRuntime hotupdate requests wont access here)
    /// </summary>
    public unsafe partial class Reader
    {
        /// <summary>
        /// Read primitive value from binary writer, DO NOT USE THIS FOR CUSTOM EXPORTER
        /// Compress and write enum
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadCommonVal<T>() =>
            Deserializer.Deserialize<T>(buffer.AsSpan(position, _length - position), this, false);

        /// <summary>
        /// Compress and write enum
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadEnum<T>()
        {
            T val = default;
            ReadEnum(ref val);
            return val;
        }

        /// <summary>
        /// Compress and write enum
        /// </summary>
        /// <param name="val"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadEnum<T>(ref T val)
        {
            if (EndOfReader) return;

            switch (TypeModel.GetTypeCode(typeof(T)))
            {
                case TypeCode.Byte:
                    Unsafe.As<T, byte>(ref val) = ReadByte();
                    return;
                case TypeCode.SByte:
                    Unsafe.As<T, sbyte>(ref val) = ReadSByte();
                    return;
                case TypeCode.Int16:
                    Unsafe.As<T, short>(ref val) = ReadInt16();
                    return;
                case TypeCode.UInt16:
                    Unsafe.As<T, ushort>(ref val) = ReadUInt16();
                    return;
                case TypeCode.Int32:
                    Unsafe.As<T, int>(ref val) = ReadInt32();
                    return;
                case TypeCode.UInt32:
                    Unsafe.As<T, uint>(ref val) = ReadUInt32();
                    return;
                case TypeCode.Int64:
                    Unsafe.As<T, long>(ref val) = ReadInt64();
                    return;
                case TypeCode.UInt64:
                    Unsafe.As<T, ulong>(ref val) = ReadUInt64();
                    return;
            }
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>(int len) where T : unmanaged
        {
            if (EndOfReader)
            {
                return default;
            }

            var ret = MemoryMarshal.Read<T>(buffer.AsSpan(position, sizeof(T)));
            position += len;
            return ret;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        /// <param name="val"></param>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // ReSharper disable UnusedMember.Local
        internal void ReadAsUnmanaged<T>(ref T val, int len)
        // ReSharper restore UnusedMember.Local
        {
            if (EndOfReader)
            {
                return;
            }

            Span<byte> span = buffer.AsSpan(position, len);
            var first = span[0];
            val = Unsafe.ReadUnaligned<T>(ref first);
            position += len;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        /// <param name="val"></param>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // ReSharper disable UnusedMember.Local
        public void Read<T>(ref T val, int len) where T : unmanaged
        // ReSharper restore UnusedMember.Local
        {
            if (EndOfReader)
            {
                return;
            }

            val = MemoryMarshal.Read<T>(buffer.AsSpan(position, sizeof(T)));
            position += len;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T1, T2>(ref T1 val1, int len1, ref T2 val2, int len2) where T1 : unmanaged where T2 : unmanaged
        {
            if (EndOfReader)
            {
                return;
            }

            val1 = MemoryMarshal.Read<T1>(buffer.AsSpan(position, sizeof(T1)));
            position += len1;
            val2 = MemoryMarshal.Read<T2>(buffer.AsSpan(position, sizeof(T2)));
            position += len2;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T1, T2, T3>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
        {
            if (EndOfReader)
            {
                return;
            }

            val1 = MemoryMarshal.Read<T1>(buffer.AsSpan(position, sizeof(T1)));
            position += len1;
            val2 = MemoryMarshal.Read<T2>(buffer.AsSpan(position, sizeof(T2)));
            position += len2;
            val3 = MemoryMarshal.Read<T3>(buffer.AsSpan(position, sizeof(T3)));
            position += len3;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T1, T2, T3, T4>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3,
            ref T4 val4, int len4) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        {
            if (EndOfReader)
            {
                return;
            }

            val1 = MemoryMarshal.Read<T1>(buffer.AsSpan(position, sizeof(T1)));
            position += len1;
            val2 = MemoryMarshal.Read<T2>(buffer.AsSpan(position, sizeof(T2)));
            position += len2;
            val3 = MemoryMarshal.Read<T3>(buffer.AsSpan(position, sizeof(T3)));
            position += len3;
            val4 = MemoryMarshal.Read<T4>(buffer.AsSpan(position, sizeof(T4)));
            position += len4;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T1, T2, T3, T4, T5>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3,
            ref T4 val4, int len4, ref T5 val5, int len5) where T1 : unmanaged
            where T2 : unmanaged
            where T3 : unmanaged
            where T4 : unmanaged
            where T5 : unmanaged
        {
            if (EndOfReader)
            {
                return;
            }

            val1 = MemoryMarshal.Read<T1>(buffer.AsSpan(position, sizeof(T1)));
            position += len1;
            val2 = MemoryMarshal.Read<T2>(buffer.AsSpan(position, sizeof(T2)));
            position += len2;
            val3 = MemoryMarshal.Read<T3>(buffer.AsSpan(position, sizeof(T3)));
            position += len3;
            val4 = MemoryMarshal.Read<T4>(buffer.AsSpan(position, sizeof(T4)));
            position += len4;
            val5 = MemoryMarshal.Read<T5>(buffer.AsSpan(position, sizeof(T5)));
            position += len5;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T1, T2, T3, T4, T5, T6>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3,
            ref T4 val4, int len4, ref T5 val5, int len5, ref T6 val6, int len6) where T1 : unmanaged
            where T2 : unmanaged
            where T3 : unmanaged
            where T4 : unmanaged
            where T5 : unmanaged
            where T6 : unmanaged
        {
            if (EndOfReader)
            {
                return;
            }

            val1 = MemoryMarshal.Read<T1>(buffer.AsSpan(position, sizeof(T1)));
            position += len1;
            val2 = MemoryMarshal.Read<T2>(buffer.AsSpan(position, sizeof(T2)));
            position += len2;
            val3 = MemoryMarshal.Read<T3>(buffer.AsSpan(position, sizeof(T3)));
            position += len3;
            val4 = MemoryMarshal.Read<T4>(buffer.AsSpan(position, sizeof(T4)));
            position += len4;
            val5 = MemoryMarshal.Read<T5>(buffer.AsSpan(position, sizeof(T5)));
            position += len5;
            val6 = MemoryMarshal.Read<T6>(buffer.AsSpan(position, sizeof(T6)));
            position += len6;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T1, T2, T3, T4, T5, T6, T7>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3,
            int len3, ref T4 val4, int len4, ref T5 val5, int len5, ref T6 val6, int len6, ref T7 val7, int len7)
            where T1 : unmanaged
            where T2 : unmanaged
            where T3 : unmanaged
            where T4 : unmanaged
            where T5 : unmanaged
            where T6 : unmanaged
            where T7 : unmanaged
        {
            if (EndOfReader)
            {
                return;
            }

            val1 = MemoryMarshal.Read<T1>(buffer.AsSpan(position, sizeof(T1)));
            position += len1;
            val2 = MemoryMarshal.Read<T2>(buffer.AsSpan(position, sizeof(T2)));
            position += len2;
            val3 = MemoryMarshal.Read<T3>(buffer.AsSpan(position, sizeof(T3)));
            position += len3;
            val4 = MemoryMarshal.Read<T4>(buffer.AsSpan(position, sizeof(T4)));
            position += len4;
            val5 = MemoryMarshal.Read<T5>(buffer.AsSpan(position, sizeof(T5)));
            position += len5;
            val6 = MemoryMarshal.Read<T6>(buffer.AsSpan(position, sizeof(T6)));
            position += len6;
            val7 = MemoryMarshal.Read<T7>(buffer.AsSpan(position, sizeof(T7)));
            position += len7;
        }

        /// <summary>
        /// Read unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T1, T2, T3, T4, T5, T6, T7, T8>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3,
            int len3, ref T4 val4, int len4, ref T5 val5, int len5, ref T6 val6, int len6, ref T7 val7, int len7,
            ref T8 val8, int len8) where T1 : unmanaged
            where T2 : unmanaged
            where T3 : unmanaged
            where T4 : unmanaged
            where T5 : unmanaged
            where T6 : unmanaged
            where T7 : unmanaged
            where T8 : unmanaged
        {
            if (EndOfReader)
            {
                return;
            }

            val1 = MemoryMarshal.Read<T1>(buffer.AsSpan(position, sizeof(T1)));
            position += len1;
            val2 = MemoryMarshal.Read<T2>(buffer.AsSpan(position, sizeof(T2)));
            position += len2;
            val3 = MemoryMarshal.Read<T3>(buffer.AsSpan(position, sizeof(T3)));
            position += len3;
            val4 = MemoryMarshal.Read<T4>(buffer.AsSpan(position, sizeof(T4)));
            position += len4;
            val5 = MemoryMarshal.Read<T5>(buffer.AsSpan(position, sizeof(T5)));
            position += len5;
            val6 = MemoryMarshal.Read<T6>(buffer.AsSpan(position, sizeof(T6)));
            position += len6;
            val7 = MemoryMarshal.Read<T7>(buffer.AsSpan(position, sizeof(T7)));
            position += len7;
            val8 = MemoryMarshal.Read<T8>(buffer.AsSpan(position, sizeof(T8)));
            position += len8;
        }

        /// <summary>
        /// Read nullable
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        public T? ReadNullable<T>() where T : struct
        {
            if (!ReadBool())
            {
                return null;
            }

            return ReadCommonVal<T>();
        }


        /// <summary>
        /// Read Array
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] ReadArray<T>()
        {
            if (EndOfReader) return default;
            //check null
            if (!ReadBool()) return null;

            //read len
            int len = ReadLength();

            T[] arr = new T[len];
            //read item
            int i = 0;
            while (i < len)
            {
                arr[i++] = ReadCommonVal<T>();
            }

            return arr;
        }

        /// <summary>
        /// Read list
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public List<T> ReadList<T>()
        {
            if (EndOfReader) return default;
            //check null
            if (!ReadBool()) return null;
            //read len
            int len = ReadLength();
            List<T> lst = new List<T>();
            //read item
            while (len-- > 0)
            {
                lst.Add(ReadCommonVal<T>());
            }

            return lst;
        }

        /// <summary>
        /// Read hashset
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public HashSet<T> ReadHashSet<T>()
        {
            if (EndOfReader) return default;
            //check null
            if (!ReadBool()) return null;
            //read len
            int len = ReadLength();
            HashSet<T> lst = new HashSet<T>();
            //read item
            while (len-- > 0)
            {
                lst.Add(ReadCommonVal<T>());
            }

            return lst;
        }

        /// <summary>
        /// Read Queue
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Queue<T> ReadQueue<T>()
        {
            if (EndOfReader) return default;
            //check null
            if (!ReadBool()) return null;
            //read len
            int len = ReadLength();
            Queue<T> lst = new Queue<T>();
            //read item
            while (len-- > 0)
            {
                lst.Enqueue(ReadCommonVal<T>());
            }

            return lst;
        }

        /// <summary>
        /// Read Stack
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Stack<T> ReadStack<T>()
        {
            if (EndOfReader) return default;
            //reverse stack
            var arr = ReadArray<T>();
            if (arr == null)
            {
                return null;
            }

            Array.Reverse(arr);
            var lst = new Stack<T>(arr);

            return lst;
        }

        /// <summary>
        /// Read Dictionary
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Dictionary<TKey, TValue> ReadDictionary<TKey, TValue>()
        {
            if (EndOfReader) return default;
            if (!ReadBool()) return null;
            //read len
            int len = ReadLength();
            Dictionary<TKey, TValue> dic = new Dictionary<TKey, TValue>();
            //read item
            while (len-- > 0)
            {
                var key = ReadCommonVal<TKey>();
                var val = ReadCommonVal<TValue>();
                dic.Add(key, val);
            }

            return dic;
        }
    }
}