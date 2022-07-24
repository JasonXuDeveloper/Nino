using System;
using System.IO;
using System.Text;
using Nino.Shared.Mgr;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Serialization
{
	/// <summary>
	/// A read that Reads serialization Data
	/// </summary>
	// ReSharper disable CognitiveComplexity
	public class Reader
	{
		/// <summary>
		/// Buffer that stores data
		/// </summary>
		private IntPtr _buffer;

		/// <summary>
		/// encoding for string
		/// </summary>
		private Encoding _encoding;

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int _position;

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int _length;

		/// <summary>
		/// End of Reader
		/// </summary>
		public bool EndOfReader => _position >= _length;

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
		/// <param name="encoding"></param>
		public Reader(IntPtr data, int outputLength, Encoding encoding)
		{
			Init(data, outputLength, encoding);
		}

		/// <summary>
		/// Create a nino read
		/// </summary>
		/// <param name="data"></param>
		/// <param name="outputLength"></param>
		/// <param name="encoding"></param>
		public void Init(IntPtr data, int outputLength, Encoding encoding)
		{
			_buffer = data;
			_encoding = encoding;
			_position = 0;
			_length = outputLength;
		}

		/// <summary>
		/// Create a nino read
		/// </summary>
		/// <param name="data"></param>
		/// <param name="outputLength"></param>
		/// <param name="encoding"></param>
		public void Init(byte[] data, int outputLength, Encoding encoding)
		{
			_buffer = Marshal.AllocHGlobal(outputLength);
			Marshal.Copy(data, 0, _buffer, outputLength);
			_encoding = encoding;
			_position = 0;
			_length = outputLength;
		}

		/// <summary>
		/// Return buffer
		/// </summary>
		public void ReturnBuffer()
		{
			Marshal.FreeHGlobal(_buffer);
		}

		/// <summary>
		/// Get Length
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int ReadLength()
		{
			return (int)DecompressAndReadNumber();
		}

		/// <summary>
		/// Decompress number for int32, int64, uint32, uint64
		/// </summary>
		/// <returns></returns>
		/// <exception cref="InvalidOperationException"></exception>
		public ulong DecompressAndReadNumber()
		{
			var i = GetCompressType();
			switch (i)
			{
				case CompressType.Byte:
					return ReadByte();
				case CompressType.SByte:
					return (ulong)ReadSByte();
				case CompressType.Int16:
					return (ulong)ReadInt16();
				case CompressType.UInt16:
					return ReadUInt16();
				case CompressType.Int32:
					return (ulong)ReadInt32();
				case CompressType.UInt32:
					return ReadUInt32();
				case CompressType.Int64:
					return (ulong)ReadInt64();
				case CompressType.UInt64:
					return ReadUInt64();
				default:
					throw new InvalidOperationException("invalid compress type");
			}
		}


		/// <summary>
		/// Read basic type from reader
		/// </summary>
		/// <param name="type"></param>
		/// <param name="result"></param>
		// ReSharper disable CognitiveComplexity
		private object AttemptReadBasicType(Type type, out bool result)
			// ReSharper restore CognitiveComplexity
		{
			result = true;
			if (WrapperManifest.TryGetWrapper(type, out var wrapper))
			{
				return wrapper.Deserialize(this);
			}
			
			if (type.IsEnum)
			{
				return DecompressAndReadEnum(type);
			}
			
			//比如泛型，只能list和dict
			if (type.IsGenericType)
			{
				var genericDefType = type.GetGenericTypeDefinition();
				//不是list和dict就再见了
				if (genericDefType == ConstMgr.ListDefType)
				{
					return ReadList(type);
				}

				if (genericDefType == ConstMgr.DictDefType)
				{
					return ReadDictionary(type);
				}

				result = false;
				return null;
			}

			//其他类型也不行
			if (type.IsArray)
			{
#if !ILRuntime
				if (type.GetArrayRank() > 1)
				{
					throw new NotSupportedException(
						"can not deserialize multidimensional array, use jagged array instead");
				}
#endif
				return ReadArray(type);
			}
					
			result = false;
			return null;
		}

		/// <summary>
		/// Read primitive value from binary writer, DO NOT USE THIS FOR CUSTOM EXPORTER
		/// </summary>
		/// <param name="type"></param>
		/// <exception cref="InvalidDataException"></exception>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		// ReSharper disable CognitiveComplexity
		public object ReadCommonVal(Type type)
			// ReSharper restore CognitiveComplexity
		{
			var ret = AttemptReadBasicType(type, out bool result);
			if (result)
			{
#if !ILRuntime
				if (type.IsEnum)
				{
					return Enum.ToObject(type, ret);
				}
#endif
				return ret;
			}

			return Deserializer.Deserialize(type, ConstMgr.Null, ConstMgr.Null, _encoding, this, false, true, false,
				true, true);
		}

		/// <summary>
		/// Compress and write enum
		/// </summary>
		/// <param name="underlyingType"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ulong DecompressAndReadEnum(Type underlyingType)
		{
			//typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			//typeof(int), typeof(uint), typeof(long), typeof(ulong)
			switch (Type.GetTypeCode(underlyingType))
			{
				case TypeCode.Byte:
					return ReadByte();
				case TypeCode.SByte:
					return (ulong)ReadSByte();
				case TypeCode.Int16:
					return (ulong)ReadInt16();
				case TypeCode.UInt16:
					return ReadUInt16();
				//need to consider compress
				case TypeCode.Int32:
					return (ulong)(int)DecompressAndReadNumber();
				case TypeCode.UInt32:
					return (uint)DecompressAndReadNumber();
				case TypeCode.Int64:
					return (ulong)(long)DecompressAndReadNumber();
				case TypeCode.UInt64:
					return DecompressAndReadNumber();
			}

			return 0;
		}

		/// <summary>
		/// Get CompressType
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private CompressType GetCompressType()
		{
			return (CompressType)ReadByte();
		}

		/// <summary>
		/// Read a byte
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe byte ReadByte()
		{
			return *(byte*)(_buffer + _position++);
		}

		/// <summary>
		/// Read byte[]
		/// </summary>
		/// <param name="len"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte[] ReadBytes(int len)
		{
			byte[] ret = new byte[len];
			Marshal.Copy(_buffer + _position, ret, 0, len);
			_position += len;
			return ret;
		}

		/// <summary>
		/// Copy buffer to a buffer, usually buffer allocated with stackalloc
		/// </summary>
		/// <param name="ptr"></param>
		/// <param name="len"></param>
		public unsafe void ReadToBuffer(byte* ptr, int len)
		{
			Unsafe.CopyBlockUnaligned(ptr, (byte*)_buffer + _position, (uint)len);
			_position += len;
		}

		/// <summary>
		/// Read unmanaged type
		/// </summary>
		/// <param name="len"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private unsafe T Read<T>(int len) where T : unmanaged
		{
			T result;
			byte* ptr = (byte*)&result;
			ReadToBuffer(ptr, len);
			return result;
		}

		/// <summary>
		/// Read sbyte
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe sbyte ReadSByte()
		{
			return *(sbyte*)(_buffer + _position++);
		}

		/// <summary>
		/// Read char
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public char ReadChar()
		{
			return Read<char>(ConstMgr.SizeOfUShort);
		}

		/// <summary>
		/// Read DateTime
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public DateTime ReadDateTime()
		{
			return DateTime.FromOADate(ReadDouble());
		}

		/// <summary>
		/// Read short
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public short ReadInt16()
		{
			return Read<short>(ConstMgr.SizeOfShort);
		}

		/// <summary>
		/// Read ushort
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ushort ReadUInt16()
		{
			return Read<ushort>(ConstMgr.SizeOfUShort);
		}

		/// <summary>
		/// Read int
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int ReadInt32()
		{
			return Read<int>(ConstMgr.SizeOfInt);
		}

		/// <summary>
		/// Read uint
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint ReadUInt32()
		{
			return Read<uint>(ConstMgr.SizeOfUInt);
		}

		/// <summary>
		/// Read long
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long ReadInt64()
		{
			return Read<long>(ConstMgr.SizeOfLong);
		}

		/// <summary>
		/// Read ulong
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ulong ReadUInt64()
		{
			return Read<ulong>(ConstMgr.SizeOfULong);
		}

		/// <summary>
		/// Read float
		/// </summary>
		/// <returns></returns>
		[System.Security.SecuritySafeCritical]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public float ReadSingle()
		{
			return Read<float>(ConstMgr.SizeOfUInt);
		}

		/// <summary>
		/// Read float
		/// </summary>
		/// <returns></returns>
		[System.Security.SecuritySafeCritical]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public float ReadFloat()
		{
			return ReadSingle();
		}

		/// <summary>
		/// Read double
		/// </summary>
		/// <returns></returns>
		[System.Security.SecuritySafeCritical]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public double ReadDouble()
		{
			return Read<double>(ConstMgr.SizeOfULong);
		}

		/// <summary>
		/// Read string
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe string ReadString()
		{
			int len = (int)DecompressAndReadNumber();
			
			//empty string -> no gc
			if (len == 0)
			{
				return String.Empty;
			}

			//Read directly
			if (len < 1024)
			{
				byte* buf = stackalloc byte[len];
				ReadToBuffer(buf, len);
				return _encoding.GetString(buf, len);
			}

			byte* buff = (byte*)Marshal.AllocHGlobal(len);
			ReadToBuffer(buff, len);
			var ret = _encoding.GetString(buff, len);
			Marshal.FreeHGlobal((IntPtr)buff);
			return ret;
		}

		/// <summary>
		/// Read decimal
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public decimal ReadDecimal()
		{
			return Read<Decimal>(ConstMgr.SizeOfDecimal);
		}

		/// <summary>
		/// Read bool
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe bool ReadBool()
		{
			return *(bool*)(_buffer + _position++);
		}
		
		/// <summary>
		/// Read Array
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Array ReadArray(Type type)
		{
			//basic type
			if (WrapperManifest.TryGetWrapper(type, out var wrapper))
			{
				var ret = wrapper.Deserialize(this);
				return (Array)ret;
			}

			//other type
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
			while(i < len)
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
			//basic type
			if (WrapperManifest.TryGetWrapper(type, out var wrapper))
			{
				var ret = wrapper.Deserialize(this);
				return (IList)ret;
			}

			//other
			var elemType = type.GenericTypeArguments[0];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt)
			{
				elemType = wt?.CLRType.GenericArguments[0].Value.ReflectionType;
			}
#endif
			
			//read len
			int len = ReadLength();
			
			IList arr = Activator.CreateInstance(type, ConstMgr.EmptyParam) as IList;
			//read item
			int i = 0;
			while(i++ < len)
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
			//parse dict type
			var args = type.GetGenericArguments();
			Type keyType = args[0];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt)
			{
				keyType = wt?.CLRType.GenericArguments[0].Value.ReflectionType;
			}
#endif
			Type valueType = args[1];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt2)
			{
				valueType = wt2?.CLRType.GenericArguments[1].Value.ReflectionType;
			}
#endif

			var dict = Activator.CreateInstance(type) as IDictionary;

			//read len
			int len = ReadLength();

			//read item
			int i = 0;
			while(i++ < len)
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
	// ReSharper restore CognitiveComplexity
}