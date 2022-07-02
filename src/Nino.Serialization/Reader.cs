using System;
using System.IO;
using System.Linq;
using System.Text;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
	/// <summary>
	/// A read that Reads serialization Data
	/// </summary>
	// ReSharper disable CognitiveComplexity
	public class Reader : IDisposable
	{
		/// <summary>
		/// Buffer that stores data
		/// </summary>
		private ExtensibleBuffer<byte> _buffer;

		/// <summary>
		/// encoding for string
		/// </summary>
		private readonly Encoding _encoding;

		/// <summary>
		/// Dispose the read
		/// </summary>
		public void Dispose()
		{
			ObjectPool<ExtensibleBuffer<byte>>.Return(_buffer);
			_buffer = null;
		}

		/// <summary>
		/// Create a nino read
		/// </summary>
		/// <param name="data"></param>
		/// <param name="outputLength"></param>
		/// <param name="encoding"></param>
		public Reader(ExtensibleBuffer<byte> data, int outputLength, Encoding encoding)
		{
			_buffer = data;
			_buffer.ReadOnly = true;
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
		public Reader(byte[] data, int outputLength, Encoding encoding)
		{
			_buffer = ObjectPool<ExtensibleBuffer<byte>>.Request();
			_buffer.CopyFrom(data, 0, 0, outputLength);
			_buffer.ReadOnly = true;
			this._encoding = encoding;
			_position = 0;
			_length = outputLength;
		}

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int _position;

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private readonly int _length;

		/// <summary>
		/// End of Reader
		/// </summary>
		public bool EndOfReader => _position == _length;

		/// <summary>
		/// Get Length
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int ReadLength()
		{
			switch (GetCompressType())
			{
				case CompressType.Byte:
					return ReadByte();
				case CompressType.SByte:
					return ReadSByte();
				case CompressType.Int16:
					return ReadInt16();
				case CompressType.UInt16:
					return ReadUInt16();
				case CompressType.Int32:
					return ReadInt32();
			}

			return 0;
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
		internal object AttemptReadBasicType(Type type, out bool result)
			// ReSharper restore CognitiveComplexity
		{
			result = true;
			switch (TypeModel.GetTypeCode(type))
			{
				case TypeCode.Byte:
					return ReadByte();
				case TypeCode.SByte:
					return ReadSByte();
				case TypeCode.Int16:
					return ReadInt16();
				case TypeCode.UInt16:
					return ReadUInt16();
				case TypeCode.Int32:
					return (int)DecompressAndReadNumber();
				case TypeCode.UInt32:
					return (uint)DecompressAndReadNumber();
				case TypeCode.Int64:
					return (long)DecompressAndReadNumber();
				case TypeCode.UInt64:
					return DecompressAndReadNumber();
				case TypeCode.String:
					return ReadString();
				case TypeCode.Boolean:
					return ReadBool();
				case TypeCode.Double:
					return ReadDouble();
				case TypeCode.Single:
					return ReadSingle();
				case TypeCode.Decimal:
					return ReadDecimal();
				case TypeCode.Char:
					return ReadChar();
				case TypeCode.DateTime:
					return ReadDateTime();
				default:
					//basic type
					//看看有没有注册委托，没的话有概率不行
					if (!Deserializer.CustomExporter.ContainsKey(type))
					{
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

						if (type.IsEnum)
						{
							//try decompress and read
#if ILRuntime
							switch (Type.GetTypeCode(type))
							{
								case TypeCode.Byte:
									return ReadByte();
								case TypeCode.SByte:
									return ReadSByte();
								case TypeCode.Int16:
									return ReadInt16();
								case TypeCode.UInt16:
									return ReadUInt16();
								//need to consider compress
								case TypeCode.Int32:
									return (int)DecompressAndReadNumber();
								case TypeCode.UInt32:
									return (uint)DecompressAndReadNumber();
								case TypeCode.Int64:
									return (long)DecompressAndReadNumber();
								case TypeCode.UInt64:
									return DecompressAndReadNumber();
							}
#endif
							return Enum.ToObject(type, DecompressAndReadEnum(Enum.GetUnderlyingType(type)));
						}
					}
					else
					{
						if (Deserializer.CustomExporter.TryGetValue(type, out var exporterDelegate))
						{
							return exporterDelegate.Invoke(this);
						}
					}

					result = false;
					return null;
			}
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
				return ret;
			}

			return Deserializer.Deserialize(type, ConstMgr.Null, ConstMgr.Null, _encoding, this, false);
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
		public byte ReadByte()
		{
			return _buffer[_position++];
		}

		/// <summary>
		/// Read byte[]
		/// </summary>
		/// <param name="len"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte[] ReadBytes(int len)
		{
			byte[] ret = new byte[len];
			_buffer.CopyTo(ref ret, _position, len);
			_position += len;
			return ret;
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
			int i = 0;
			while (i < len)
			{
				*(ptr + i++) = ReadByte();
			}

			return result;
		}

		/// <summary>
		/// Read sbyte
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public sbyte ReadSByte()
		{
			return (sbyte)(_buffer[_position++]);
		}

		/// <summary>
		/// Read char
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe char ReadChar()
		{
			ushort tmpBuffer = ReadUInt16();
			return *((char*)&tmpBuffer);
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
		public unsafe float ReadSingle()
		{
			uint tmpBuffer = ReadUInt32();
			return *((float*)&tmpBuffer);
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
		public unsafe double ReadDouble()
		{
			ulong tmpBuffer = ReadUInt64();
			return *((double*)&tmpBuffer);
		}

		/// <summary>
		/// Read string
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe string ReadString()
		{
			var type = GetCompressType();
			int len;
			switch (type)
			{
				case CompressType.ByteString:
					len = ReadByte();
					break;
				case CompressType.UInt16String:
					len = ReadUInt16();
					break;
				default:
					throw new InvalidOperationException($"invalid compress type for string: {type}");
			}

			//empty string -> no gc
			if (len == 0)
			{
				return String.Empty;
			}

			//Read directly
			var buf = stackalloc byte[len];
			_buffer.CopyTo(buf, _position, len);
			var ret = _encoding.GetString(buf, len);
			_position += len;
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
			var ret = ReadByte();
			return *((bool*)&ret);
		}

		/// <summary>
		/// Try get basic type arr
		/// </summary>
		/// <param name="type"></param>
		/// <param name="len"></param>
		/// <param name="result"></param>
		/// <returns></returns>
		private Array TryGetBasicTypeArray(Type type, int len, out bool result)
		{
			result = true;
			switch (TypeModel.GetTypeCode(type))
			{
				case TypeCode.Byte:
					return ReadBytes(len);
				case TypeCode.SByte:
					var sByteArr = new sbyte[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						sByteArr[i] = ReadSByte();
					}

					return sByteArr;
				case TypeCode.Int16:
					var shortArr = new short[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						shortArr[i] = ReadInt16();
					}

					return shortArr;
				case TypeCode.UInt16:
					var ushortArr = new ushort[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						ushortArr[i] = ReadUInt16();
					}

					return ushortArr;
				case TypeCode.Int32:
					var intArr = new int[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						intArr[i] = (int)DecompressAndReadNumber();
					}

					return intArr;
				case TypeCode.UInt32:
					var uintArr = new uint[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						uintArr[i] = (uint)DecompressAndReadNumber();
					}

					return uintArr;
				case TypeCode.Int64:
					var longArr = new long[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						longArr[i] = (long)DecompressAndReadNumber();
					}

					return longArr;
				case TypeCode.UInt64:
					var ulongArr = new ulong[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						ulongArr[i] = DecompressAndReadNumber();
					}

					return ulongArr;
				case TypeCode.String:
					var strArr = new string[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						strArr[i] = ReadString();
					}

					return strArr;
				case TypeCode.Boolean:
					var boolArr = new bool[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						boolArr[i] = ReadBool();
					}

					return boolArr;
				case TypeCode.Double:
					var doubleArr = new double[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						doubleArr[i] = ReadDouble();
					}

					return doubleArr;
				case TypeCode.Single:
					var floatArr = new float[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						floatArr[i] = ReadSingle();
					}

					return floatArr;
				case TypeCode.Decimal:
					var decimalArr = new decimal[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						decimalArr[i] = ReadDecimal();
					}

					return decimalArr;
				case TypeCode.Char:
					var charArr = new char[len];
					//read item
					for (int i = 0; i < len; i++)
					{
						charArr[i] = ReadChar();
					}

					return charArr;
			}

			result = false;
			return null;
		}

		/// <summary>
		/// Try get basic type list
		/// </summary>
		/// <param name="type"></param>
		/// <param name="len"></param>
		/// <param name="result"></param>
		/// <returns></returns>
		private IList TryGetBasicTypeList(Type type, int len, out bool result)
		{
			result = true;
			switch (TypeModel.GetTypeCode(type))
			{
				case TypeCode.Byte:
					return ReadBytes(len).ToList();
				case TypeCode.SByte:
					var sByteArr = new System.Collections.Generic.List<sbyte>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						sByteArr.Add(ReadSByte());
					}

					return sByteArr;
				case TypeCode.Int16:
					var shortArr = new System.Collections.Generic.List<short>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						shortArr.Add(ReadInt16());
					}

					return shortArr;
				case TypeCode.UInt16:
					var ushortArr = new System.Collections.Generic.List<ushort>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						ushortArr.Add(ReadUInt16());
					}

					return ushortArr;
				case TypeCode.Int32:
					var intArr = new System.Collections.Generic.List<int>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						intArr.Add((int)DecompressAndReadNumber());
					}

					return intArr;
				case TypeCode.UInt32:
					var uintArr = new System.Collections.Generic.List<uint>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						uintArr.Add((uint)DecompressAndReadNumber());
					}

					return uintArr;
				case TypeCode.Int64:
					var longArr = new System.Collections.Generic.List<long>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						longArr.Add((long)DecompressAndReadNumber());
					}

					return longArr;
				case TypeCode.UInt64:
					var ulongArr = new System.Collections.Generic.List<ulong>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						ulongArr.Add(DecompressAndReadNumber());
					}

					return ulongArr;
				case TypeCode.String:
					var strArr = new System.Collections.Generic.List<string>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						strArr.Add(ReadString());
					}

					return strArr;
				case TypeCode.Boolean:
					var boolArr = new System.Collections.Generic.List<bool>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						boolArr.Add(ReadBool());
					}

					return boolArr;
				case TypeCode.Double:
					var doubleArr = new System.Collections.Generic.List<double>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						doubleArr.Add(ReadDouble());
					}

					return doubleArr;
				case TypeCode.Single:
					var floatArr = new System.Collections.Generic.List<float>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						floatArr.Add(ReadSingle());
					}

					return floatArr;
				case TypeCode.Decimal:
					var decimalArr = new System.Collections.Generic.List<decimal>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						decimalArr.Add(ReadDecimal());
					}

					return decimalArr;
				case TypeCode.Char:
					var charArr = new System.Collections.Generic.List<char>(len);
					//read item
					for (int i = 0; i < len; i++)
					{
						charArr.Add(ReadChar());
					}

					return charArr;
			}

			result = false;
			return null;
		}

		/// <summary>
		/// Read Array
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public Array ReadArray(Type type)
		{
			//read len
			int len = ReadLength();

			//other type
			var elemType = type.GetElementType();
			if (elemType == null)
			{
				throw new NullReferenceException("element type is null, can not make array");
			}

			var arr = TryGetBasicTypeArray(elemType, len, out bool result);
			if (result)
			{
				return arr;
			}

			arr = Array.CreateInstance(elemType, len);
			//read item
			for (int i = 0; i < len; i++)
			{
				var obj = ReadCommonVal(elemType);
#if ILRuntime
				arr.SetValue(ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(elemType, obj), i);
				continue;
#endif
				arr.SetValue(obj, i);
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
			//read len
			int len = ReadLength();

			//other
			var elemType = type.GenericTypeArguments[0];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt)
			{
				elemType = wt?.CLRType.GenericArguments[0].Value.ReflectionType;
			}
#endif

			var arr = TryGetBasicTypeList(elemType, len, out bool result);
			if (result)
			{
				return arr;
			}

			arr = Activator.CreateInstance(type, ConstMgr.EmptyParam) as IList;
			//read item
			for (int i = 0; i < len; i++)
			{
				var obj = ReadCommonVal(elemType);
#if ILRuntime
				arr?.Add(ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(elemType, obj));
				continue;
#endif
				arr?.Add(obj);
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
			for (int i = 0; i < len; i++)
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
#endif
				dict?.Add(key, val);
			}

			return dict;
		}
	}
	// ReSharper restore CognitiveComplexity
}