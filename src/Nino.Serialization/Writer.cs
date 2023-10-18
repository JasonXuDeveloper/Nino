using System;
using System.IO;
using Nino.Shared.Mgr;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    /// <summary>
    /// A writer that writes serialization Data
    /// </summary>
    public ref partial struct Writer
    {
        /// <summary>
        /// Buffer that stores data
        /// </summary>
        private Span<byte> buffer;

        /// <summary>
        /// Position of the current buffer
        /// </summary>
        public int Position;

        /// <summary>
        /// Create a nino writer
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="position"></param>
        public Writer(Span<byte> buffer, int position)
        {
            this.buffer = buffer;
            Position = position;
        }

        /// <summary>
        /// Write primitive values, DO NOT USE THIS FOR CUSTOM IMPORTER
        /// </summary>
        /// <param name="type"></param>
        /// <param name="val"></param>
        /// <exception cref="InvalidDataException"></exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteCommonVal(Type type, object val) =>
            Position = Serializer.Serialize(type, val, null, buffer, Position);

        /// <summary>
        /// Write byte[]
        /// </summary>
        /// <param name="data"></param>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void Write(byte* data, ref int len)
        {
            if (len <= 8)
            {
                while (len-- > 0)
                {
                    buffer[Position++] = *data++;
                }

                return;
            }

            Unsafe.CopyBlockUnaligned(ref buffer[Position], ref *data, (uint)len);
            Position += len;
        }
        
        /// <summary>
        /// Write a double
        /// </summary>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(double value)
        {
            Write(ref value, ConstMgr.SizeOfULong);
        }

        /// <summary>
        /// Write a float
        /// </summary>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(float value)
        {
            Write(ref value, ConstMgr.SizeOfUInt);
        }

        /// <summary>
        /// Write a DateTime
        /// </summary>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(DateTime value)
        {
            Write(ref value, ConstMgr.SizeOfLong);
        }

        /// <summary>
        /// Write decimal
        /// </summary>
        /// <param name="d"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(decimal d)
        {
            Write(ref d, ConstMgr.SizeOfDecimal);
        }

        /// <summary>
        /// Writes a boolean to this stream. A single byte is written to the stream
        /// with the value 0 representing false or the value 1 representing true.
        /// </summary>
        /// <param name="value"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(bool value)
        {
            Unsafe.WriteUnaligned(ref buffer[Position++], value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(char value)
        {
            Write(ref value, ConstMgr.SizeOfUShort);
        }

        /// <summary>
        /// Write string
        /// </summary>
        /// <param name="val"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void Write(string val)
        {
            if (val is null)
            {
                Write(false);
                return;
            }

            Write(true);

            if (val == string.Empty)
            {
                Write(0);
                return;
            }

            var strSpan = val.AsSpan(); // 2*len, utf16 str
            int len = strSpan.Length * ConstMgr.SizeOfUShort;
            fixed (char* first = &strSpan.GetPinnableReference())
            {
                Write(len);
                Write((byte*)first, ref len);
            }
        }

        #region write whole num

        /// <summary>
        /// Write byte val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(byte num)
        {
            buffer[Position++] = num;
        }

        /// <summary>
        /// Write byte val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(sbyte num)
        {
            Unsafe.WriteUnaligned(ref buffer[Position++], num);
        }

        /// <summary>
        /// Write int val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(int num)
        {
            Write(ref num, ConstMgr.SizeOfInt);
        }

        /// <summary>
        /// Write uint val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(uint num)
        {
            Write(ref num, ConstMgr.SizeOfUInt);
        }

        /// <summary>
        /// Write short val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(short num)
        {
            Write(ref num, ConstMgr.SizeOfShort);
        }

        /// <summary>
        /// Write ushort val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ushort num)
        {
            Write(ref num, ConstMgr.SizeOfUShort);
        }

        /// <summary>
        /// Write long val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(long num)
        {
            Write(ref num, ConstMgr.SizeOfLong);
        }

        /// <summary>
        /// Write ulong val to binary writer
        /// </summary>
        /// <param name="num"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(ulong num)
        {
            Write(ref num, ConstMgr.SizeOfULong);
        }

        #endregion

        /// <summary>
        /// Write array
        /// </summary>
        /// <param name="arr"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(Array arr)
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
            Write(len);
            //write item
            int i = 0;
            while (i < len)
            {
                var obj = arr.GetValue(i++);
                var eType = obj.GetType();
                WriteCommonVal(eType, obj);
            }
        }

        /// <summary>
        /// Write list
        /// </summary>
        /// <param name="arr"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(IList arr)
        {
            //null
            if (arr == null)
            {
                Write(false);
                return;
            }

            Write(true);
            var len = arr.Count;
            //write len
            Write(len);
            //empty
            if (len == 0)
            {
                return;
            }

            //write item
            foreach (var c in arr)
            {
                var eType = c.GetType();
                WriteCommonVal(eType, c);
            }
        }

        /// <summary>
        /// Write dictionary
        /// </summary>
        /// <param name="dictionary"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(IDictionary dictionary)
        {
            //null
            if (dictionary == null)
            {
                Write(false);
                return;
            }

            Write(true);
            //write len
            int len = dictionary.Count;
            Write(len);
            //empty
            if (dictionary.Count == 0)
            {
                return;
            }

            //record keys
            var keys = dictionary.Keys;
            //write items
            foreach (var c in keys)
            {
                //write key
                var eType = c.GetType();
                WriteCommonVal(eType, c);
                //write val
                var val = dictionary[c];
                eType = val.GetType();
                WriteCommonVal(eType, val);
            }
        }
    }
}