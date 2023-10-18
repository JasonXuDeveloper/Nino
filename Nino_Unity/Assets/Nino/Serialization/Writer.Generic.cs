using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Nino.Shared.Mgr;

namespace Nino.Serialization
{
    public ref partial struct Writer
    {
        /// <summary>
        /// Write primitive values, DO NOT USE THIS FOR CUSTOM IMPORTER
        /// </summary>
        /// <param name="val"></param>
        /// <exception cref="InvalidDataException"></exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteCommonVal<T>(T val) => Position = Serializer.Serialize(typeof(T), val, null, buffer, Position);

        /// <summary>
        /// Write primitive values, DO NOT USE THIS FOR CUSTOM IMPORTER
        /// </summary>
        /// <param name="type"></param>
        /// <param name="val"></param>
        /// <exception cref="InvalidDataException"></exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteCommonVal<T>(Type type, T val) =>
            Position = Serializer.Serialize(type, val, null, buffer, Position);

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        /// <param name="val"></param>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteAsUnmanaged<T>(ref T val, int len)
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val);
            Position += len;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        /// <param name="val"></param>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ref T val, int len) where T : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val);
            Position += len;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T1, T2>(ref T1 val1, int len1, ref T2 val2, int len2)
            where T1 : unmanaged where T2 : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val1);
            Position += len1;
            Unsafe.WriteUnaligned(ref buffer[Position], val2);
            Position += len2;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T1, T2, T3>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3)
            where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val1);
            Position += len1;
            Unsafe.WriteUnaligned(ref buffer[Position], val2);
            Position += len2;
            Unsafe.WriteUnaligned(ref buffer[Position], val3);
            Position += len3;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T1, T2, T3, T4>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3,
            ref T4 val4, int len4) where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged where T4 : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val1);
            Position += len1;
            Unsafe.WriteUnaligned(ref buffer[Position], val2);
            Position += len2;
            Unsafe.WriteUnaligned(ref buffer[Position], val3);
            Position += len3;
            Unsafe.WriteUnaligned(ref buffer[Position], val4);
            Position += len4;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T1, T2, T3, T4, T5>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3,
            ref T4 val4, int len4, ref T5 val5, int len5) where T1 : unmanaged
            where T2 : unmanaged
            where T3 : unmanaged
            where T4 : unmanaged
            where T5 : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val1);
            Position += len1;
            Unsafe.WriteUnaligned(ref buffer[Position], val2);
            Position += len2;
            Unsafe.WriteUnaligned(ref buffer[Position], val3);
            Position += len3;
            Unsafe.WriteUnaligned(ref buffer[Position], val4);
            Position += len4;
            Unsafe.WriteUnaligned(ref buffer[Position], val5);
            Position += len5;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T1, T2, T3, T4, T5, T6>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3, int len3,
            ref T4 val4, int len4, ref T5 val5, int len5, ref T6 val6, int len6) where T1 : unmanaged
            where T2 : unmanaged
            where T3 : unmanaged
            where T4 : unmanaged
            where T5 : unmanaged
            where T6 : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val1);
            Position += len1;
            Unsafe.WriteUnaligned(ref buffer[Position], val2);
            Position += len2;
            Unsafe.WriteUnaligned(ref buffer[Position], val3);
            Position += len3;
            Unsafe.WriteUnaligned(ref buffer[Position], val4);
            Position += len4;
            Unsafe.WriteUnaligned(ref buffer[Position], val5);
            Position += len5;
            Unsafe.WriteUnaligned(ref buffer[Position], val6);
            Position += len6;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T1, T2, T3, T4, T5, T6, T7>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3,
            int len3, ref T4 val4, int len4, ref T5 val5, int len5, ref T6 val6, int len6, ref T7 val7, int len7)
            where T1 : unmanaged
            where T2 : unmanaged
            where T3 : unmanaged
            where T4 : unmanaged
            where T5 : unmanaged
            where T6 : unmanaged
            where T7 : unmanaged
        {
            Unsafe.WriteUnaligned(ref buffer[Position], val1);
            Position += len1;
            Unsafe.WriteUnaligned(ref buffer[Position], val2);
            Position += len2;
            Unsafe.WriteUnaligned(ref buffer[Position], val3);
            Position += len3;
            Unsafe.WriteUnaligned(ref buffer[Position], val4);
            Position += len4;
            Unsafe.WriteUnaligned(ref buffer[Position], val5);
            Position += len5;
            Unsafe.WriteUnaligned(ref buffer[Position], val6);
            Position += len6;
            Unsafe.WriteUnaligned(ref buffer[Position], val7);
            Position += len7;
        }

        /// <summary>
        /// Write unmanaged type
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T1, T2, T3, T4, T5, T6, T7, T8>(ref T1 val1, int len1, ref T2 val2, int len2, ref T3 val3,
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
            Unsafe.WriteUnaligned(ref buffer[Position], val1);
            Position += len1;
            Unsafe.WriteUnaligned(ref buffer[Position], val2);
            Position += len2;
            Unsafe.WriteUnaligned(ref buffer[Position], val3);
            Position += len3;
            Unsafe.WriteUnaligned(ref buffer[Position], val4);
            Position += len4;
            Unsafe.WriteUnaligned(ref buffer[Position], val5);
            Position += len5;
            Unsafe.WriteUnaligned(ref buffer[Position], val6);
            Position += len6;
            Unsafe.WriteUnaligned(ref buffer[Position], val7);
            Position += len7;
            Unsafe.WriteUnaligned(ref buffer[Position], val8);
            Position += len8;
        }

        /// <summary>
        /// Write byte[]
        /// </summary>
        /// <param name="data"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Write<T>(Span<T> data) where T : unmanaged
        {
            if (data == null)
            {
                Write(false);
                return;
            }

            Write(true);
            var len = data.Length;
            Write(len);
            len *= sizeof(T);
            MemoryMarshal.AsBytes(data).CopyTo(buffer.Slice(Position, len));
            Position += len;
        }

        /// <summary>
        /// write enum
        /// </summary>
        /// <param name="val"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteEnum<T>(T val)
        {
            //boxed
            if (typeof(T) == ConstMgr.ObjectType)
            {
                WriteEnum((object)val);
                return;
            }

            ref byte p = ref MemoryMarshal.GetReference(buffer);
            switch (TypeModel.GetTypeCode(typeof(T)))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                    byte len = (byte)Unsafe.SizeOf<T>();
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), val);
                    Position += len;
                    return;
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), val);
                    Position += 8;
                    return;
            }
        }

        /// <summary>
        /// write enum
        /// </summary>
        /// <param name="val"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void WriteEnum(object val)
        {
            var type = val.GetType();
            ref byte p = ref MemoryMarshal.GetReference(buffer);
            switch (TypeModel.GetTypeCode(type))
            {
                case TypeCode.Byte:
                    buffer[Position++] = Unsafe.Unbox<byte>(val);
                    return;
                case TypeCode.SByte:
                    buffer[Position++] = *(byte*)Unsafe.Unbox<sbyte>(val);
                    return;
                case TypeCode.Int16:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), Unsafe.Unbox<short>(val));
                    Position += 2;
                    return;
                case TypeCode.UInt16:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), Unsafe.Unbox<ushort>(val));
                    Position += 2;
                    return;
                case TypeCode.Int32:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), Unsafe.Unbox<int>(val));
                    Position += 4;
                    return;
                case TypeCode.UInt32:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), Unsafe.Unbox<uint>(val));
                    Position += 4;
                    return;
                case TypeCode.Int64:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), Unsafe.Unbox<long>(val));
                    Position += 8;
                    return;
                case TypeCode.UInt64:
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref p, Position), Unsafe.Unbox<ulong>(val));
                    Position += 8;
                    return;
            }
        }

        /// <summary>
        /// Write array
        /// </summary>
        /// <param name="arr"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] arr)
        {
            //null
            if (arr == null)
            {
                Write(false);
                return;
            }

            Write(true);
            //write len
            int len = arr.Length;

            //empty
            if (len == 0)
            {
                //write len
                Write(0);
                return;
            }

            Write(len);
            //write item
            int i = 0;
            while (i < len)
            {
                var obj = arr[i++];
                var eType = obj.GetType();
                WriteCommonVal(eType, obj);
            }
        }

        /// <summary>
        /// Write nullable
        /// </summary>
        /// <param name="val"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? val) where T : struct
        {
            //null
            if (val == null)
            {
                Write(false);
                return;
            }

            Write(true);
            WriteCommonVal(val.Value);
        }

        /// <summary>
        /// Write List
        /// </summary>
        /// <param name="lst"></param>
        /// <typeparam name="T"></typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(List<T> lst)
        {
            //null
            if (lst == null)
            {
                Write(false);
                return;
            }

            Write(true);
            int len = lst.Count;
            //write len
            Write(lst.Count);
            //empty
            if (len == 0)
            {
                return;
            }

            //write item
            foreach (var c in lst)
            {
                WriteCommonVal(c);
            }
        }

        /// <summary>
        /// Write List
        /// </summary>
        /// <param name="lst"></param>
        /// <typeparam name="T"></typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(HashSet<T> lst)
        {
            //null
            if (lst == null)
            {
                Write(false);
                return;
            }

            Write(true);
            int len = lst.Count;
            //write len
            Write(len);
            //empty
            if (len == 0)
            {
                //write len
                return;
            }

            //write item
            foreach (var c in lst)
            {
                WriteCommonVal(c);
            }
        }

        /// <summary>
        /// Write List
        /// </summary>
        /// <param name="lst"></param>
        /// <typeparam name="T"></typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Queue<T> lst)
        {
            //null
            if (lst == null)
            {
                Write(false);
                return;
            }

            Write(true);
            int len = lst.Count;
            //write len
            Write(len);
            //empty
            if (len == 0)
            {
                //write len
                return;
            }

            //write item
            foreach (var c in lst)
            {
                WriteCommonVal(c);
            }
        }

        /// <summary>
        /// Write List
        /// </summary>
        /// <param name="lst"></param>
        /// <typeparam name="T"></typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(Stack<T> lst)
        {
            //null
            if (lst == null)
            {
                Write(false);
                return;
            }

            Write(true);
            int len = lst.Count;
            //write len
            Write(len);
            //empty
            if (len == 0)
            {
                //write len
                return;
            }

            //write item
            foreach (var c in lst)
            {
                WriteCommonVal(c);
            }
        }

        /// <summary>
        /// Write dictionary
        /// </summary>
        /// <param name="dictionary"></param>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        {
            //null
            if (dictionary == null)
            {
                Write(false);
                return;
            }

            Write(true);
            int len = dictionary.Count;
            //write len
            Write(len);
            //empty
            if (len == 0)
            {
                //write len
                return;
            }

            //record keys
            var keys = dictionary.Keys;
            //write items
            foreach (var c in keys)
            {
                //write key
                WriteCommonVal(c);
                //write val
                var val = dictionary[c];
                WriteCommonVal(val);
            }
        }
    }
}