using System;
using System.IO;
using System.Linq;
using System.Text;
using Nino.Shared;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
	/// <summary>
	/// A read that Reads serialization Data
	/// </summary>
	public class Reader : IDisposable
	{
		/// <summary>
		/// Buffer that stores data
		/// </summary>
		private ExtensibleBuffer<byte> buffer;

		/// <summary>
		/// 缓存反射创建dict的参数数组
		/// </summary>
		private static volatile UncheckedStack<Type[]> _reflectionGenericTypePool = new UncheckedStack<Type[]>(3);

		/// <summary>
		/// encoding for string
		/// </summary>
		private readonly Encoding encoding;

		/// <summary>
		/// Dispose the read
		/// </summary>
		public void Dispose()
		{
			ObjectPool<ExtensibleBuffer<byte>>.Return(buffer);
			buffer = null;
		}

		/// <summary>
		/// Create a nino read
		/// </summary>
		/// <param name="data"></param>
		/// <param name="outputLength"></param>
		/// <param name="encoding"></param>
		public Reader(ExtensibleBuffer<byte> data, int outputLength, Encoding encoding)
		{
			buffer = data;
			buffer.ReadOnly = true;
			this.encoding = encoding;
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
		/// Read primitive value from binary writer, DO NOT USE THIS FOR CUSTOM EXPORTER
		/// </summary>
		/// <param name="type"></param>
		/// <exception cref="InvalidDataException"></exception>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		// ReSharper disable CognitiveComplexity
		public object ReadCommonVal(Type type)
			// ReSharper restore CognitiveComplexity
		{
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
				case TypeCode.Int32:
					return (int)DecompressAndReadNumber();
				case TypeCode.UInt32:
					return (uint)DecompressAndReadNumber();
				case TypeCode.Int64:
					return (long)DecompressAndReadNumber();
				case TypeCode.UInt64:
					return (ulong)DecompressAndReadNumber();
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
			}

			//enum, normally this wont be true, as enum has a typecode
			if (type.IsEnum)
			{
				//try decompress and read
				return Enum.ToObject(type, DecompressAndReadEnum(Enum.GetUnderlyingType(type)));
			}

			//array/ list -> recursive
			if (type.IsArray)
			{
				return ReadArray(type);
			}

			if (type.IsGenericType)
			{
				var genericDefType = type.GetGenericTypeDefinition();

				//list
				if (genericDefType == ConstMgr.ListDefType)
				{
					return ReadList(type);
				}

				//dict
				if (genericDefType == ConstMgr.DictDefType)
				{
					return ReadDictionary(type);
				}
			}

			//custom exporter
			if (Deserializer.CustomExporter.TryGetValue(type, out var exporterDelegate))
			{
				return exporterDelegate.Invoke(this);
			}
			else
			{
				//no chance to Deserialize -> see if this type can be serialized in other ways
				//try recursive
				return Deserializer.Deserialize(type, ConstMgr.Null, ConstMgr.Null, encoding, this);
			}
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
				case TypeCode.UInt32:
				case TypeCode.Int64:
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
			return buffer[_position++];
		}

		/// <summary>
		/// Read byte[]
		/// </summary>
		/// <param name="len"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte[] ReadBytes(int len)
		{
			byte[] ret = new byte[len];
			buffer.CopyTo(ref ret, _position, len);
			_position += len;
			return ret;
		}

		/// <summary>
		/// Read sbyte
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public sbyte ReadSByte()
		{
			return (sbyte)(buffer[_position++]);
		}

		/// <summary>
		/// Read char
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public char ReadChar()
		{
			return (char)ReadInt16();
		}

		/// <summary>
		/// Read short
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public short ReadInt16()
		{
			return (short)(buffer[_position++] | buffer[_position++] << 8);
		}

		/// <summary>
		/// Read ushort
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ushort ReadUInt16()
		{
			return (ushort)(ReadInt16());
		}

		/// <summary>
		/// Read int
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int ReadInt32()
		{
			return (buffer[_position++] | buffer[_position++] << 8 | buffer[_position++] << 16 |
			        buffer[_position++] << 24);
		}

		/// <summary>
		/// Read uint
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint ReadUInt32()
		{
			return (uint)(ReadInt32());
		}

		/// <summary>
		/// Read long
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long ReadInt64()
		{
			uint lo = ReadUInt32();
			uint hi = ReadUInt32();
			return (long)(hi) << 32 | lo;
		}

		/// <summary>
		/// Read ulong
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public ulong ReadUInt64()
		{
			uint lo = ReadUInt32();
			uint hi = ReadUInt32();
			return ((ulong)hi) << 32 | lo;
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
			var buf = stackalloc byte [len];
			buffer.CopyTo(buf, _position, len);
			var ret = encoding.GetString(buf, len);
			_position += len;
			return ret;
		}

		/// <summary>
		/// Read decimal
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe decimal ReadDecimal()
		{
			decimal result;
			var resultSpan = new Span<byte>(&result, ConstMgr.SizeOfDecimal);
			var buf = stackalloc byte[ConstMgr.SizeOfDecimal];
			buffer.CopyTo(buf, _position, ConstMgr.SizeOfDecimal);
			fixed (byte* resultPtr = &resultSpan.GetPinnableReference())
			{
				Buffer.MemoryCopy(buf, resultPtr, ConstMgr.SizeOfDecimal, ConstMgr.SizeOfDecimal);
			}

			_position += ConstMgr.SizeOfDecimal;
			return result;
		}

		/// <summary>
		/// Read bool
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public bool ReadBool()
		{
			return ReadByte() != 0;
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

			//byte[] -> write directly
			if (type == ConstMgr.ByteArrType)
			{
				//read item
				return ReadBytes(len);
			}

			//other type
			var elemType = type.GetElementType();
			if (elemType == null)
			{
				throw new NullReferenceException("element type is null, can not make array");
			}

			var arr = Array.CreateInstance(elemType, len);
			//read item
			for (int i = 0; i < len; i++)
			{
				var obj = ReadCommonVal(elemType);
				if (obj.GetType() != elemType)
				{
					obj = Convert.ChangeType(obj, elemType);
				}

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

			//List<byte> -> write directly
			if (type == ConstMgr.ByteListType)
			{
				return ReadBytes(len).ToList();
			}

			//other
			var elemType = type.GenericTypeArguments[0];
			Type newType = ConstMgr.ListDefType.MakeGenericType(elemType);
			var arr = Activator.CreateInstance(newType, ConstMgr.EmptyParam) as IList;
			//read item
			for (int i = 0; i < len; i++)
			{
				var obj = ReadCommonVal(elemType);
				if (obj.GetType() != elemType)
				{
					obj = Convert.ChangeType(obj, elemType);
				}

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
			Type valueType = args[1];
			Type[] temp;
			if (_reflectionGenericTypePool.Count > 0)
			{
				temp = _reflectionGenericTypePool.Pop();
				temp[0] = keyType;
				temp[1] = valueType;
			}
			else
			{
				// ReSharper disable RedundantExplicitArrayCreation
				temp = new Type[] { keyType, valueType };
				// ReSharper restore RedundantExplicitArrayCreation
			}

			Type dictType = ConstMgr.DictDefType.MakeGenericType(temp);
			_reflectionGenericTypePool.Push(temp);
			var dict = Activator.CreateInstance(dictType) as IDictionary;

			//read len
			int len = ReadLength();

			//read item
			for (int i = 0; i < len; i++)
			{
				//read key
				var key = ReadCommonVal(keyType);
				if (key.GetType() != keyType)
				{
					key = Convert.ChangeType(key, keyType);
				}

				//read value
				var val = ReadCommonVal(valueType);
				if (val.GetType() != valueType)
				{
					val = Convert.ChangeType(val, valueType);
				}

				//add
				dict?.Add(key, val);
			}

			return dict;
		}
	}
}