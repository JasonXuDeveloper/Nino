using System;
using System.IO;
using System.Text;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
	/// <summary>
	/// A writer that writes serialization Data
	/// </summary>
	public class Writer
	{
		/// <summary>
		/// block size when creating buffer
		/// </summary>
		private const ushort BufferBlockSize = 1024 * 2;

		/// <summary>
		/// Buffer that stores data
		/// </summary>
		private ExtensibleBuffer<byte> _buffer;

		/// <summary>
		/// encoding for string
		/// </summary>
		private Encoding writerEncoding;

		/// <summary>
		/// Convert writer to byte
		/// </summary>
		/// <returns></returns>
		public byte[] ToBytes()
		{
			return _buffer.ToArray(0, _position);
		}

		/// <summary>
		/// Convert writer to compressed byte
		/// </summary>
		/// <returns></returns>
		public byte[] ToCompressedBytes()
		{
			return CompressMgr.Compress(_buffer, _position);
		}
		
		/// <summary>
		/// Create a writer (needs to set up values)
		/// </summary>
		public Writer()
		{
			
		}

		/// <summary>
		/// Create a nino writer
		/// </summary>
		/// <param name="encoding"></param>
		public Writer(Encoding encoding)
		{
			Init(encoding);
		}

		/// <summary>
		/// Init writer
		/// </summary>
		/// <param name="encoding"></param>
		public void Init(Encoding encoding)
		{
			if (_buffer == null)
			{
				var peak = ObjectPool<ExtensibleBuffer<byte>>.Peak();
				if (peak != null && peak.ExpandSize == BufferBlockSize)
				{
					_buffer = ObjectPool<ExtensibleBuffer<byte>>.Request();
				}
				else
				{
					_buffer = new ExtensibleBuffer<byte>(BufferBlockSize);
				}
			}

			writerEncoding = encoding;
			_position = 0;
		}

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int _position;

		/// <summary>
		/// Write basic type to writer
		/// </summary>
		/// <param name="val"></param>
		/// <param name="type"></param>
		// ReSharper disable CognitiveComplexity
		internal bool AttemptWriteBasicType(Type type, object val)
			// ReSharper restore CognitiveComplexity
		{
			if (type == ConstMgr.ObjectType)
			{
				if (val == null) return false;
				//unbox
				type = val.GetType();
				//failed to unbox
				if (type == ConstMgr.ObjectType)
					return false;
			}

			if (WrapperManifest.TryGetWrapper(type, out var wrapper))
			{
				wrapper.Serialize(val, this);
				return true;
			}

			//因为这里不是typecode，所以enum要单独检测
			if (type.IsEnum)
			{
				//have to box enum
				CompressAndWriteEnum(type, val);
				return true;
			}

			//basic type
			//比如泛型，只能list和dict
			if (type.IsGenericType)
			{
				var genericDefType = type.GetGenericTypeDefinition();
				//不是list和dict就再见了
				if (genericDefType == ConstMgr.ListDefType)
				{
					Write((IList)val);
					return true;
				}

				if (genericDefType == ConstMgr.DictDefType)
				{
					Write((IDictionary)val);
					return true;
				}

				return false;
			}

			//其他类型也不行
			if (type.IsArray)
			{
#if !ILRuntime
				if (type.GetArrayRank() > 1)
				{
					throw new NotSupportedException(
						"can not serialize multidimensional array, use jagged array instead");
				}
#endif
				Write(val as Array);
				return true;
			}

			return false;
		}

		/// <summary>
		/// Write primitive values, DO NOT USE THIS FOR CUSTOM IMPORTER
		/// </summary>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <exception cref="InvalidDataException"></exception>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		// ReSharper disable CognitiveComplexity
		public void WriteCommonVal(Type type, object val)
			// ReSharper restore CognitiveComplexity
		{
			if (!AttemptWriteBasicType(type, val))
			{
				Serializer.Serialize(type, val, writerEncoding, this, false, true, false, true, true);
			}
		}

		/// <summary>
		/// Write byte[]
		/// </summary>
		/// <param name="data"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(byte[] data)
		{
			CompressAndWrite(data.Length);
			Write(data, data.Length);
		}

		/// <summary>
		/// Write byte[]
		/// </summary>
		/// <param name="data"></param>
		/// <param name="len"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void Write(byte[] data, int len)
		{
			_buffer.CopyFrom(data, 0, _position, len);
			_position += len;
		}

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
					_buffer[_position++] = *data++;
				}

				return;
			}
			_buffer.CopyFrom(data, 0, _position, len);
			_position += len;
		}

		/// <summary>
		/// Write unmanaged type
		/// </summary>
		/// <param name="val"></param>
		/// <param name="len"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private unsafe void Write<T>(ref T val, int len) where T : unmanaged
		{
			fixed (T* ptr = &val)
			{
				Write((byte*)ptr, ref len);
			}
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
			Write(value.ToOADate());
		}

		/// <summary>
		/// Write string
		/// </summary>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void Write(string val)
		{
			if (string.IsNullOrEmpty(val))
			{
				Write((byte)CompressType.Byte);
				Write((byte)0);
				return;
			}

			int bufferSize = writerEncoding.GetMaxByteCount(val.Length);
			if (bufferSize < 1024)
			{
				byte* buffer = stackalloc byte[bufferSize];
				fixed (char* pValue = val)
				{
					int byteCount = writerEncoding.GetBytes(pValue, val.Length, buffer, bufferSize);
					CompressAndWrite(byteCount);
					Write(buffer, ref byteCount);
				}
			}
			else
			{
				byte* buff = (byte*)Marshal.AllocHGlobal(bufferSize);
				fixed (char* pValue = val)
				{
					// ReSharper disable AssignNullToNotNullAttribute
					int byteCount = writerEncoding.GetBytes(pValue, val.Length, buff, bufferSize);
					// ReSharper restore AssignNullToNotNullAttribute
					CompressAndWrite(byteCount);
					Write(buff, ref byteCount);
				}
				Marshal.FreeHGlobal((IntPtr)buff);	
			}
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
		public unsafe void Write(bool value)
		{
			Write(*((byte*)(&value)));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void Write(char value)
		{
			Write(*((ushort*)(&value)));
		}

		#region write whole num

		/// <summary>
		/// Write byte val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(byte num)
		{
			_buffer[_position] = num;
			_position += 1;
		}

		/// <summary>
		/// Write byte val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void Write(sbyte num)
		{
			_buffer[_position] = *(byte*)&num;
			_position += 1;
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

		#region write whole number without sign

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(ulong num)
		{
			if (num <= uint.MaxValue)
			{
				CompressAndWrite((uint)num);
				return;
			}

			Write((byte)(CompressType.UInt64));
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(uint num)
		{
			if (num <= ushort.MaxValue)
			{
				CompressAndWrite((ushort)num);
				return;
			}

			Write((byte)CompressType.UInt32);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(ushort num)
		{
			//parse to byte
			if (num <= byte.MaxValue)
			{
				CompressAndWrite((byte)num);
				return;
			}

			Write((byte)CompressType.UInt16);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(byte num)
		{
			Write((byte)CompressType.Byte);
			Write(num);
		}

		#endregion

		#region write whole number with sign

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(long num)
		{
			if (num < 0)
			{
				CompressAndWriteNeg(num);
				return;
			}

			if (num <= int.MaxValue)
			{
				CompressAndWrite((int)num);
				return;
			}

			Write((byte)CompressType.Int64);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void CompressAndWriteNeg(long num)
		{
			if (num >= int.MinValue)
			{
				CompressAndWriteNeg((int)num);
				return;
			}

			Write((byte)CompressType.Int64);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(int num)
		{
			if (num < 0)
			{
				CompressAndWriteNeg(num);
				return;
			}

			if (num <= short.MaxValue)
			{
				CompressAndWrite((short)num);
				return;
			}

			Write((byte)CompressType.Int32);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void CompressAndWriteNeg(int num)
		{
			if (num >= short.MinValue)
			{
				CompressAndWriteNeg((short)num);
				return;
			}

			Write((byte)CompressType.Int32);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(short num)
		{
			if (num < 0)
			{
				CompressAndWriteNeg(num);
				return;
			}

			//parse to byte
			if (num <= sbyte.MaxValue)
			{
				CompressAndWrite((sbyte)num);
				return;
			}

			if (num <= byte.MaxValue)
			{
				CompressAndWrite((byte)num);
				return;
			}

			Write((byte)CompressType.Int16);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void CompressAndWriteNeg(short num)
		{
			if (num >= sbyte.MinValue)
			{
				CompressAndWrite((sbyte)num);
				return;
			}

			Write((byte)CompressType.Int16);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(sbyte num)
		{
			Write((byte)CompressType.SByte);
			Write(num);
		}

		#endregion

		/// <summary>
		/// Compress and write enum (no boxing)
		/// </summary>
		/// <param name="type"></param>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWriteEnum(Type type, object val)
		{
			switch (TypeModel.GetTypeCode(type))
			{
				case TypeCode.Byte:
					Write((byte)val);
					return;
				case TypeCode.SByte:
					Write((sbyte)val);
					return;
				case TypeCode.Int16:
					Write((short)val);
					return;
				case TypeCode.UInt16:
					Write((ushort)val);
					return;
				case TypeCode.Int32:
					CompressAndWrite((int)val);
					return;
				case TypeCode.UInt32:
					CompressAndWrite((uint)val);
					return;
				case TypeCode.Int64:
					CompressAndWrite((long)val);
					return;
				case TypeCode.UInt64:
					CompressAndWrite((ulong)val);
					return;
			}
		}

		/// <summary>
		/// Compress and write enum (no boxing)
		/// </summary>
		/// <param name="type"></param>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWriteEnum(Type type, ulong val)
		{
			switch (TypeModel.GetTypeCode(type))
			{
				case TypeCode.Byte:
					Write((byte)val);
					return;
				case TypeCode.SByte:
					Write((sbyte)val);
					return;
				case TypeCode.Int16:
					Write((short)val);
					return;
				case TypeCode.UInt16:
					Write((ushort)val);
					return;
				case TypeCode.Int32:
					CompressAndWrite((int)val);
					return;
				case TypeCode.UInt32:
					CompressAndWrite((uint)val);
					return;
				case TypeCode.Int64:
					CompressAndWrite((long)val);
					return;
				case TypeCode.UInt64:
					CompressAndWrite(val);
					return;
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(Array arr)
		{
			//empty
			if (arr == null || arr.Length == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			int len = arr.Length;
			CompressAndWrite(len);
			//other type
			var elemType = arr.GetValue(0)?.GetType() ?? arr.GetType().GetElementType();
			//write item
			int i = 0;
			while (i < len)
			{
				WriteCommonVal(elemType, arr.GetValue(i++));
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(IList arr)
		{
			//empty
			if (arr == null || arr.Count == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//other
			var elemType = arr[0].GetType();
			//write len
			CompressAndWrite(arr.Count);
			//write item
			foreach (var c in arr)
			{
				WriteCommonVal(elemType, c);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(IDictionary dictionary)
		{
			//empty
			if (dictionary == null || dictionary.Count == 0)
			{
				//write len
				CompressAndWrite(0);
				return;
			}

			//write len
			int len = dictionary.Count;
			CompressAndWrite(len);
			//record keys
			var keys = dictionary.Keys;
			Type keyType, valueType;
			keyType = valueType = null;
			//write items
			foreach (var c in keys)
			{
				if (keyType == null)
				{
					keyType = c.GetType();
				}
				if (valueType == null)
				{
					valueType = dictionary[c].GetType();
				}
				
				//write key
				WriteCommonVal(keyType, c);
				//write val
				WriteCommonVal(valueType, dictionary[c]);
			}
		}
	}
}