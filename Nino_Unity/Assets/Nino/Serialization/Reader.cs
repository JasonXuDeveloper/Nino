using System;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    /// <summary>
    /// A read that Reads serialization Data
    /// </summary>
    public unsafe partial class Reader
    {
        /// <summary>
        /// block size when creating buffer
        /// </summary>
        private const ushort BufferBlockSize = ushort.MaxValue;

        /// <summary>
        /// Buffer that stores data
        /// </summary>
        private ExtensibleBuffer<byte> buffer;

        /// <summary>
        /// Position of the current buffer
        /// </summary>
        private int position;

        /// <summary>
        /// Length of the current reader
        /// </summary>
        private int _length;

        /// <summary>
        /// End of Reader
        /// </summary>
        public bool EndOfReader => position >= _length;

        /// <summary>
        /// Create an empty reader, need to set up values
        /// </summary>
        public Reader()
        {
        }

        /// <summary>
        /// Create a nino read
        /// </summary>
        /// <param name="data"></param>
        /// <param name="outputLength"></param>
        public Reader(byte[] data, int outputLength)
        {
            Init(data, outputLength);
        }

        /// <summary>
        /// Create a nino read
        /// </summary>
        /// <param name="data"></param>
        /// <param name="outputLength"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Init(IntPtr data, int outputLength)
        {
            if (buffer == null)
            {
                var peak = ObjectPool<ExtensibleBuffer<byte>>.Peak();
                if (peak != null && peak.ExpandSize == BufferBlockSize)
                {
                    buffer = ObjectPool<ExtensibleBuffer<byte>>.Request();
                }
                else
                {
                    buffer = new ExtensibleBuffer<byte>(BufferBlockSize);
                }
            }

            buffer.CopyFrom((byte*)data, 0, 0, outputLength);
            position = 0;
            _length = outputLength;
        }

        /// <summary>
        /// Create a nino reader
        /// </summary>
        /// <param name="data"></param>
        /// <param name="outputLength"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Init(Span<byte> data, int outputLength)
        {
            fixed (byte* ptr = &data.GetPinnableReference())
            {
                Init((IntPtr)ptr, outputLength);
            }
        }

        /// <summary>
        /// Create a nino reader
        /// </summary>
        /// <param name="data"></param>
        /// <param name="outputLength"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Init(byte[] data, int outputLength)
        {
            fixed (byte* ptr = data)
            {
                Init((IntPtr)ptr, outputLength);
            }
        }

        /// <summary>
        /// Get Length
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadLength() => ReadInt32();

        /// <summary>
        /// Read primitive value from binary writer, DO NOT USE THIS FOR CUSTOM EXPORTER
        /// Compress and write enum
        /// </summary>
        /// <param name="type"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object ReadCommonVal(Type type) =>
            Deserializer.Deserialize(type, ConstMgr.Null, ConstMgr.Null, this, false);

        /// <summary>
        /// Read a byte
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            if (EndOfReader) return default;

            return buffer[position++];
        }

        /// <summary>
        /// Read byte[]
        /// </summary>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte[] ReadBytes(int len)
        {
            if (EndOfReader) return default;

            byte[] ret = new byte[len];
            fixed (byte* ptr = ret)
            {
                buffer.CopyTo(ptr, position, len);
                position += len;
            }

            return ret;
        }

        /// <summary>
        /// Copy buffer to a buffer, usually buffer allocated with stackalloc
        /// </summary>
        /// <param name="ptr"></param>
        /// <param name="len"></param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadToBuffer(byte* ptr, int len)
        {
            buffer.CopyTo(ptr, position, len);
            position += len;
        }

        /// <summary>
        /// Read sbyte
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sbyte ReadSByte() => Unsafe.As<byte, sbyte>(ref buffer.Data[position++]);

        /// <summary>
        /// Read char
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public char ReadChar() => Read<char>(ConstMgr.SizeOfUShort);

        /// <summary>
        /// Read DateTime
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public DateTime ReadDateTime()
        {
            if (EndOfReader) return default;

            return Read<DateTime>(ConstMgr.SizeOfLong);
        }

        /// <summary>
        /// Read short
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public short ReadInt16() => Read<short>(ConstMgr.SizeOfShort);

        /// <summary>
        /// Read ushort
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort ReadUInt16() => Read<ushort>(ConstMgr.SizeOfUShort);

        /// <summary>
        /// Read int
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadInt32() => Read<int>(ConstMgr.SizeOfInt);

        /// <summary>
        /// Read uint
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUInt32() => Read<uint>(ConstMgr.SizeOfUInt);

        /// <summary>
        /// Read long
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadInt64() => Read<long>(ConstMgr.SizeOfLong);

        /// <summary>
        /// Read ulong
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadUInt64() => Read<ulong>(ConstMgr.SizeOfULong);

        /// <summary>
        /// Read float
        /// </summary>
        /// <returns></returns>
        [System.Security.SecuritySafeCritical]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadSingle() => Read<float>(ConstMgr.SizeOfUInt);

        /// <summary>
        /// Read float
        /// </summary>
        /// <returns></returns>
        [System.Security.SecuritySafeCritical]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadFloat() => ReadSingle();

        /// <summary>
        /// Read double
        /// </summary>
        /// <returns></returns>
        [System.Security.SecuritySafeCritical]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double ReadDouble() => Read<double>(ConstMgr.SizeOfULong);

        /// <summary>
        /// Read decimal
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public decimal ReadDecimal() => Read<Decimal>(ConstMgr.SizeOfDecimal);

        /// <summary>
        /// Read bool
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool()
        {
            if (EndOfReader) return default;

            return buffer[position++] == 1;
        }

        /// <summary>
        /// Read string
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadString()
        {
            if (EndOfReader) return default;
            if (!ReadBool()) return null;

            int len = ReadInt32();
            //empty string -> no gc
            if (len == 0)
            {
                return String.Empty;
            }

            byte* ptr = buffer.Data + position;
            position += len;
            char* chars = (char*)ptr;
            return new string(chars, 0, len / ConstMgr.SizeOfUShort);
        }

        /// <summary>
        /// Compress and write enum
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadEnum(Type enumType)
        {
            if (EndOfReader) return default;

            switch (TypeModel.GetTypeCode(enumType))
            {
                case TypeCode.Byte:
                    return ReadByte();
                case TypeCode.SByte:
                    return (ulong)ReadSByte();
                case TypeCode.Int16:
                    return (ulong)ReadInt16();
                case TypeCode.UInt16:
                    return ReadUInt16();
                case TypeCode.Int32:
                    return (ulong)ReadInt32();
                case TypeCode.UInt32:
                    return ReadUInt32();
                case TypeCode.Int64:
                    return (ulong)ReadInt64();
                case TypeCode.UInt64:
                    return ReadUInt64();
            }

            return 0;
        }
        
        /// <summary>
        /// Read Array
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Array ReadArray(Type type)
        {
            if (EndOfReader) return default;

            //basic type
            if (WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                var ret = wrapper.Deserialize(this);
                return (Array)ret;
            }

            //other type
            //check null
            if (!ReadBool()) return null;
            var elemType = type.GetElementType();
            if (elemType == null)
            {
                throw new NullReferenceException("element type is null, can not make array");
            }

            //read len
            int len = ReadLength();

            Array arr = Array.CreateInstance(elemType, len);
            //read item
            int i = 0;
            while (i < len)
            {
                var obj = ReadCommonVal(elemType);
#if ILRuntime
				arr.SetValue(ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(elemType, obj), i++);
				continue;
#else
                arr.SetValue(obj, i++);
#endif
            }

            return arr;
        }

        /// <summary>
        /// Read list
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IList ReadList(Type type)
        {
            if (EndOfReader) return default;

            //basic type
            if (WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                var ret = wrapper.Deserialize(this);
                return (IList)ret;
            }

            //other
            //check null
            if (!ReadBool()) return null;
            var elemType = type.GenericTypeArguments[0];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt)
			{
				elemType = wt?.CLRType.GenericArguments[0].Value.ReflectionType;
			}

			if(!elemType.IsGenericType)
			{
				elemType = elemType.ResolveRealType();
			}
#endif

            //read len
            int len = ReadLength();

            IList arr = Activator.CreateInstance(type, ConstMgr.EmptyParam) as IList;
            //read item
            int i = 0;
            while (i++ < len)
            {
                var obj = ReadCommonVal(elemType);
#if ILRuntime
				arr?.Add(ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(elemType, obj));
				continue;
#else
                arr?.Add(obj);
#endif
            }

            return arr;
        }

        /// <summary>
        /// Read Dictionary
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public IDictionary ReadDictionary(Type type)
        {
            if (EndOfReader) return default;
            if (!ReadBool()) return null;

            //parse dict type
            var args = type.GetGenericArguments();
            Type keyType = args[0];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt)
			{
				keyType = wt?.CLRType.GenericArguments[0].Value.ReflectionType;
			}

			if(!keyType.IsGenericType)
			{
				keyType = keyType.ResolveRealType();
			}
#endif
            Type valueType = args[1];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt2)
			{
				valueType = wt2?.CLRType.GenericArguments[1].Value.ReflectionType;
			}
			
			if(!valueType.IsGenericType)
			{
				valueType = valueType.ResolveRealType();
			}
#endif

            var dict = Activator.CreateInstance(type) as IDictionary;

            //read len
            int len = ReadLength();

            //read item
            int i = 0;
            while (i++ < len)
            {
                //read key
                var key = ReadCommonVal(keyType);
                //read value
                var val = ReadCommonVal(valueType);

                //add
#if ILRuntime
				dict?.Add(ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(keyType, key),
							ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(valueType, val));
				continue;
#else
                dict?.Add(key, val);
#endif
            }

            return dict;
        }
    }
}