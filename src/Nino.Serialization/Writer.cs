using System;
using System.IO;
using System.Text;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
	/// <summary>
	/// A writer that writes serialization Data
	/// </summary>
	public class Writer : IDisposable
	{
		/// <summary>
		/// block size when creating buffer
		/// </summary>
		private const int BufferBlockSize = 1024 * 2;
		
		/// <summary>
		/// Buffer that stores data
		/// </summary>
		private ExtensibleBuffer<byte> buffer;

		/// <summary>
		/// encoding for string
		/// </summary>
		private readonly Encoding encoding;

		/// <summary>
		/// Convert writer to byte
		/// </summary>
		/// <returns></returns>
		public byte[] ToBytes()
		{
			return buffer.ToArray(0, length);
		}

		/// <summary>
		/// Convert writer to compressed byte
		/// </summary>
		/// <returns></returns>
		public byte[] ToCompressedBytes()
		{
			return CompressMgr.Compress(buffer, length);
		}

		/// <summary>
		/// Dispose the writer
		/// </summary>
		public void Dispose()
		{
			ObjectPool<ExtensibleBuffer<byte>>.Return(buffer);
			buffer = null;
		}

		/// <summary>
		/// Create a nino writer
		/// </summary>
		/// <param name="encoding"></param>
		public Writer(Encoding encoding)
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
			buffer.ReadOnly = false;
			this.encoding = encoding;
			length = 0;
			position = 0;
		}

		/// <summary>
		/// Length of the buffer
		/// </summary>
		private int length;

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int position;

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
			buffer.CopyFrom(data, 0, position, len);
			position += len;
			length += len;
		}

		/// <summary>
		/// Write a double
		/// </summary>
		/// <param name="value"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void Write(double value)
		{
			Write(*(ulong*)&value);
		}

		/// <summary>
		/// Write a float
		/// </summary>
		/// <param name="value"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void Write(float value)
		{
			Write(*(uint*)&value);
		}

		/// <summary>
		/// Write string
		/// </summary>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(string val)
		{
            if (string.IsNullOrEmpty(val))
			{
				Write((byte)CompressType.ByteString);
				Write((byte)0);
				return;
			}
			var len = encoding.GetByteCount(val);
			if (len <= byte.MaxValue)
			{
				Write((byte)CompressType.ByteString);
				Write((byte)len);
			}
			else if (len <= ushort.MaxValue)
			{
				Write((byte)CompressType.UInt16String);
				Write((ushort)len);
			}
			else
			{
				throw new InvalidDataException($"string is too long, len:{len}, max is: {ushort.MaxValue}");
			}

			//write directly
			var b = BufferPool.RequestBuffer(len);
			if (len == encoding.GetBytes(val, 0, val.Length, b, 0))
			{
				Write(b, len);
			}
			else
			{
				throw new InvalidDataException("invalid string to write, can not determine length");
			}
			BufferPool.ReturnBuffer(b);
		}

		/// <summary>
		/// Write decimal
		/// </summary>
		/// <param name="d"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void Write(decimal d)
		{
			var valueSpan = new ReadOnlySpan<byte>(&d, ConstMgr.SizeOfDecimal);
			//16 bytes can consider write manually
			buffer[position++] = valueSpan[0];
			buffer[position++] = valueSpan[1];
			buffer[position++] = valueSpan[2];
			buffer[position++] = valueSpan[3];
			buffer[position++] = valueSpan[4];
			buffer[position++] = valueSpan[5];
			buffer[position++] = valueSpan[6];
			buffer[position++] = valueSpan[7];
			buffer[position++] = valueSpan[8];
			buffer[position++] = valueSpan[9];
			buffer[position++] = valueSpan[10];
			buffer[position++] = valueSpan[11];
			buffer[position++] = valueSpan[12];
			buffer[position++] = valueSpan[13];
			buffer[position++] = valueSpan[14];
			buffer[position++] = valueSpan[15];
			length += ConstMgr.SizeOfDecimal;
		}

		/// <summary>
		/// Writes a boolean to this stream. A single byte is written to the stream
		/// with the value 0 representing false or the value 1 representing true.
		/// </summary>
		/// <param name="value"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(bool value)
		{
			Write((byte)(value ? 1 : 0));
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(char ch)
		{
			var bytes = BitConverter.GetBytes(ch);
			//faster
			for (int i = 0; i < bytes.Length; i++)
			{
				Write(bytes[i]);
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
			buffer[position] = num;
			position += 1;
			length += 1;
		}

		/// <summary>
		/// Write byte val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(sbyte num)
		{
			buffer[position] = (byte)num;
			position += 1;
			length += 1;
		}

		/// <summary>
		/// Write int val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(int num)
		{

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(int*)p = num;
			// }
			//
			// Position += SizeOfInt;
			// Length += SizeOfInt;

			buffer[position++] = (byte)num;
			buffer[position++] = (byte)(num >> 8);
			buffer[position++] = (byte)(num >> 16);
			buffer[position++] = (byte)(num >> 24);

			length += ConstMgr.SizeOfInt;
		}

		/// <summary>
		/// Write uint val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(uint num)
		{

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(uint*)p = num;
			// }
			//
			// Position += SizeOfUInt;
			// Length += SizeOfUInt;

			buffer[position++] = (byte)num;
			buffer[position++] = (byte)(num >> 8);
			buffer[position++] = (byte)(num >> 16);
			buffer[position++] = (byte)(num >> 24);

			length += ConstMgr.SizeOfUInt;
		}

		/// <summary>
		/// Write short val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(short num)
		{

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(short*)p = num;
			// }
			//
			// Position += SizeOfShort;
			// Length += SizeOfShort;

			buffer[position++] = (byte)num;
			buffer[position++] = (byte)(num >> 8);

			length += ConstMgr.SizeOfShort;
		}

		/// <summary>
		/// Write ushort val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(ushort num)
		{

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(ushort*)p = num;
			// }
			//
			// Position += SizeOfUShort;
			// Length += SizeOfUShort;

			buffer[position++] = (byte)num;
			buffer[position++] = (byte)(num >> 8);

			length += ConstMgr.SizeOfUShort;
		}

		/// <summary>
		/// Write long val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(long num)
		{

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(long*)p = num;
			// }
			//
			// Position += SizeOfLong;
			// Length += SizeOfLong;

			buffer[position++] = (byte)num;
			buffer[position++] = (byte)(num >> 8);
			buffer[position++] = (byte)(num >> 16);
			buffer[position++] = (byte)(num >> 24);
			buffer[position++] = (byte)(num >> 32);
			buffer[position++] = (byte)(num >> 40);
			buffer[position++] = (byte)(num >> 48);
			buffer[position++] = (byte)(num >> 56);

			length += ConstMgr.SizeOfLong;
		}

		/// <summary>
		/// Write ulong val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(ulong num)
		{

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(ulong*)p = num;
			// }
			//
			// Position += SizeOfULong;
			// Length += SizeOfULong;

			buffer[position++] = (byte)num;
			buffer[position++] = (byte)(num >> 8);
			buffer[position++] = (byte)(num >> 16);
			buffer[position++] = (byte)(num >> 24);
			buffer[position++] = (byte)(num >> 32);
			buffer[position++] = (byte)(num >> 40);
			buffer[position++] = (byte)(num >> 48);
			buffer[position++] = (byte)(num >> 56);

			length += ConstMgr.SizeOfULong;
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
			if (num <= int.MaxValue)
			{
				CompressAndWrite((int)num);
				return;
			}

			Write((byte)CompressType.Int64);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(int num)
		{
			if (num <= short.MaxValue)
			{
				CompressAndWrite((short)num);
				return;
			}

			Write((byte)CompressType.Int32);
			Write(num);
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWrite(short num)
		{
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
		public void CompressAndWrite(sbyte num)
		{
			Write((byte)CompressType.SByte);
			Write(num);
		}

		#endregion

		/// <summary>
		/// Write primitive values, DO NOT USE THIS FOR CUSTOM IMPORTER
		/// </summary>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <exception cref="InvalidDataException"></exception>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void WriteCommonVal(Type type, object val)
		{
			//write basic values
			switch (val)
			{
				//without sign
				case ulong ul:
					CompressAndWrite(ul);
					return;
				case uint ui:
					CompressAndWrite(ui);
					return;
				case ushort us: //unnecessary to compress
					Write(us);
					return;
				case byte b: //unnecessary to compress
					Write(b);
					return;
				// with sign
				case long l:
					CompressAndWrite(l);
					return;
				case int i:
					CompressAndWrite(i);
					return;
				case short s: //unnecessary to compress
					Write(s);
					return;
				case sbyte sb: //unnecessary to compress
					Write(sb);
					return;
				case bool b:
					Write(b);
					return;
				case double db:
					Write(db);
					return;
				case decimal dc:
					Write(dc);
					return;
				case float fl:
					Write(fl);
					return;
				case char c:
					Write(c);
					return;
				case string s:
					Write(s);
					return;
			}

			//enum
			if (type.IsEnum)
			{
				//try compress and write
				// ReSharper disable PossibleInvalidCastException
				CompressAndWriteEnum(type, val);
				// ReSharper restore PossibleInvalidCastException
				return;
			}

			//array/ list -> recursive
			if (type.IsArray)
			{
#if !ILRuntime
				if (type.GetArrayRank() > 1)
				{
					throw new NotSupportedException("can not serialize multidimensional array, use jagged array instead");
				}
#endif
				Write((Array)val);
				return;
			}

			if (type.IsGenericType)
			{
				var genericDefType = type.GetGenericTypeDefinition();

				//list
				if (genericDefType == ConstMgr.ListDefType)
				{
					Write((ICollection)val);
					return;
				}

				//dict
				if (genericDefType == ConstMgr.DictDefType)
				{
					Write((IDictionary)val);
					return;
				}
			}

			//custom importer
			if (Serializer.CustomImporter.TryGetValue(type, out var importerDelegate))
			{
				importerDelegate.Invoke(val, this);
			}
			else
			{
				Serializer.Serialize(type, val, encoding, this);
			}
		}

		/// <summary>
		/// Compress and write enum
		/// </summary>
		/// <param name="type"></param>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void CompressAndWriteEnum(Type type, object val)
		{
			type = Enum.GetUnderlyingType(type);
			//typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			//typeof(int), typeof(uint), typeof(long), typeof(ulong)
			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Byte:
					WriteCommonVal(type, (byte)val);
					return;
				case TypeCode.SByte:
					WriteCommonVal(type, (sbyte)val);
					return;
				case TypeCode.Int16:
					WriteCommonVal(type, (short)val);
					return;
				case TypeCode.UInt16:
					WriteCommonVal(type, (ushort)val);
					return;
				case TypeCode.Int32:
					WriteCommonVal(type, (int)val);
					return;
				case TypeCode.UInt32:
					WriteCommonVal(type, (uint)val);
					return;
				case TypeCode.Int64:
					WriteCommonVal(type, (long)val);
					return;
				case TypeCode.UInt64:
					WriteCommonVal(type, (ulong)val);
					return;
			}
		}

		/// <summary>
		/// Compress and write enum
		/// </summary>
		/// <param name="type"></param>
		/// <param name="val"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void CompressAndWriteEnum(Type type, ulong val)
		{
			switch (Type.GetTypeCode(type))
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
			if (arr == null)
			{
				//write len
				CompressAndWrite(0);
				return;
			}
			var type = arr.GetType();
			//byte[] -> write directly
			if (type == ConstMgr.ByteArrType)
			{
				var dt = (byte[])arr;
				//write item
				Write(dt);
				return;
			}

			//other type
			var elemType = type.GetElementType();
			//write len
			CompressAndWrite(arr.Length);
			//write item
			foreach (var c in arr)
			{
				WriteCommonVal(elemType, c);
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(ICollection arr)
		{
			//empty
			if(arr == null)
            {
				//write len
				CompressAndWrite(0);
				return;
			}
			var type = arr.GetType();
			//List<byte> -> write directly
			if (type == ConstMgr.ByteListType)
			{
				var dt = (List<byte>)arr;
				//write item
				Write(dt.ToArray());
				return;
			}

			//other
			var elemType = type.GenericTypeArguments[0];
#if ILRuntime
			if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt)
			{
				elemType = wt?.CLRType.GenericArguments[0].Value.ReflectionType;
			}
#endif

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
			if (dictionary == null)
			{
				//write len
				CompressAndWrite(0);
				return;
			}
			var type = dictionary.GetType();
			var args = type.GetGenericArguments();
			Type keyType = args[0];
			Type valueType = args[1];
			//write len
			CompressAndWrite(dictionary.Count);
			//record keys
			var keys = dictionary.Keys;
			//write items
			foreach (var c in keys)
			{
				//write key
				WriteCommonVal(keyType, c);
				//write val
				WriteCommonVal(valueType, dictionary[c]);
			}
		}
	}
}