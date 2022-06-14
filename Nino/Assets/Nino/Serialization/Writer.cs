using System;
using System.IO;
using System.Text;
using Nino.Shared;
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
		/// Buffer that stores data
		/// </summary>
		private byte[] buffer;

		/// <summary>
		/// has been disposed or not
		/// </summary>
		private bool disposed;

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
			var ret = new byte[Length];
			Buffer.BlockCopy(buffer, 0, ret, 0, Length);
			return ret;
		}

		/// <summary>
		/// Convert writer to compressed byte
		/// </summary>
		/// <returns></returns>
		public byte[] ToCompressedBytes()
		{
			return CompressMgr.Compress(buffer, Length);
		}

		/// <summary>
		/// Dispose the writer
		/// </summary>
		public void Dispose()
		{
			BufferPool.ReturnBuffer(buffer);
			disposed = true;
		}

		/// <summary>
		/// Create a nino writer
		/// </summary>
		public Writer(Encoding encoding) : this(0, encoding)
		{

		}

		/// <summary>
		/// Create a nino writer
		/// </summary>
		/// <param name="length"></param>
		/// <param name="encoding"></param>
		private Writer(int length, Encoding encoding)
		{
			buffer = BufferPool.RequestBuffer(length);
			this.encoding = encoding;
			Length = 0;
			Position = 0;
		}

		/// <summary>
		/// Length of the buffer
		/// </summary>
		private int Length { get; set; }

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int Position { get; set; }

		/// <summary>
		/// Check the capacity
		/// </summary>
		/// <param name="addition"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void EnsureCapacity(int addition)
		{
			if (disposed)
			{
				throw new ObjectDisposedException("can not access a disposed writer");
			}

			// Check for overflow
			if (addition + Position < 0)
				throw new IOException("Stream too long");
			if (addition + Position <= buffer.Length) return;
			int newCapacity = addition + Position;
			if (newCapacity < 256)
				newCapacity = 256;
			if (newCapacity < buffer.Length * 4)
				newCapacity = buffer.Length * 4;
			//return buffer
			BufferPool.ReturnBuffer(buffer);
			//request buffer
			buffer = BufferPool.RequestBuffer(newCapacity, buffer);
		}

		/// <summary>
		/// Write byte[]
		/// </summary>
		/// <param name="data"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(byte[] data)
		{
			EnsureCapacity(data.Length);
			Buffer.BlockCopy(data, 0, buffer, Position, data.Length);
			Position += data.Length;
			Length += data.Length;
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
			EnsureCapacity(len);
			encoding.GetBytes(val, 0, val.Length, buffer, Position);
			Position += len;
			Length += len;
		}

		/// <summary>
		/// Write decimal
		/// </summary>
		/// <param name="d"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe void Write(decimal d)
		{
			var valueSpan = new ReadOnlySpan<byte>(&d, ConstMgr.SizeOfDecimal);
			EnsureCapacity(valueSpan.Length);
			valueSpan.CopyTo(buffer.AsSpan(Position));
			Position += valueSpan.Length;
			Length += valueSpan.Length;
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
			Write(BitConverter.GetBytes(ch));
		}

		#region write whole num

		/// <summary>
		/// Write byte val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(byte num)
		{
			EnsureCapacity(1);
			buffer[Position] = num;
			Position += 1;
			Length += 1;
		}

		/// <summary>
		/// Write byte val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(sbyte num)
		{
			EnsureCapacity(1);
			buffer[Position] = (byte)num;
			Position += 1;
			Length += 1;
		}

		/// <summary>
		/// Write int val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(int num)
		{
			EnsureCapacity(ConstMgr.SizeOfInt);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(int*)p = num;
			// }
			//
			// Position += SizeOfInt;
			// Length += SizeOfInt;

			buffer[Position++] = (byte)num;
			buffer[Position++] = (byte)(num >> 8);
			buffer[Position++] = (byte)(num >> 16);
			buffer[Position++] = (byte)(num >> 24);

			Length += ConstMgr.SizeOfInt;
		}

		/// <summary>
		/// Write uint val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(uint num)
		{
			EnsureCapacity(ConstMgr.SizeOfUInt);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(uint*)p = num;
			// }
			//
			// Position += SizeOfUInt;
			// Length += SizeOfUInt;

			buffer[Position++] = (byte)num;
			buffer[Position++] = (byte)(num >> 8);
			buffer[Position++] = (byte)(num >> 16);
			buffer[Position++] = (byte)(num >> 24);

			Length += ConstMgr.SizeOfUInt;
		}

		/// <summary>
		/// Write short val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(short num)
		{
			EnsureCapacity(ConstMgr.SizeOfShort);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(short*)p = num;
			// }
			//
			// Position += SizeOfShort;
			// Length += SizeOfShort;

			buffer[Position++] = (byte)num;
			buffer[Position++] = (byte)(num >> 8);

			Length += ConstMgr.SizeOfShort;
		}

		/// <summary>
		/// Write ushort val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(ushort num)
		{
			EnsureCapacity(ConstMgr.SizeOfUShort);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(ushort*)p = num;
			// }
			//
			// Position += SizeOfUShort;
			// Length += SizeOfUShort;

			buffer[Position++] = (byte)num;
			buffer[Position++] = (byte)(num >> 8);

			Length += ConstMgr.SizeOfUShort;
		}

		/// <summary>
		/// Write long val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(long num)
		{
			EnsureCapacity(ConstMgr.SizeOfLong);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(long*)p = num;
			// }
			//
			// Position += SizeOfLong;
			// Length += SizeOfLong;

			buffer[Position++] = (byte)num;
			buffer[Position++] = (byte)(num >> 8);
			buffer[Position++] = (byte)(num >> 16);
			buffer[Position++] = (byte)(num >> 24);
			buffer[Position++] = (byte)(num >> 32);
			buffer[Position++] = (byte)(num >> 40);
			buffer[Position++] = (byte)(num >> 48);
			buffer[Position++] = (byte)(num >> 56);

			Length += ConstMgr.SizeOfLong;
		}

		/// <summary>
		/// Write ulong val to binary writer
		/// </summary>
		/// <param name="num"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Write(ulong num)
		{
			EnsureCapacity(ConstMgr.SizeOfULong);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(ulong*)p = num;
			// }
			//
			// Position += SizeOfULong;
			// Length += SizeOfULong;

			buffer[Position++] = (byte)num;
			buffer[Position++] = (byte)(num >> 8);
			buffer[Position++] = (byte)(num >> 16);
			buffer[Position++] = (byte)(num >> 24);
			buffer[Position++] = (byte)(num >> 32);
			buffer[Position++] = (byte)(num >> 40);
			buffer[Position++] = (byte)(num >> 48);
			buffer[Position++] = (byte)(num >> 56);

			Length += ConstMgr.SizeOfULong;
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
				CompressAndWriteEnum(type, val);
				return;
			}

			//array/ list -> recursive
			if (type.IsArray)
			{
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

		public void Write(Array arr)
		{
			var type = arr.GetType();
			//byte[] -> write directly
			if (type == ConstMgr.ByteArrType)
			{
				var dt = (byte[])arr;
				//write len
				CompressAndWrite(dt.Length);
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

		public void Write(ICollection arr)
		{
			var type = arr.GetType();
			//List<byte> -> write directly
			if (type == ConstMgr.ByteListType)
			{
				var dt = (List<byte>)arr;
				//write len
				CompressAndWrite(dt.Count);
				//write item
				Write(dt.ToArray());
				return;
			}

			//other
			var elemType = type.GenericTypeArguments[0];
			//write len
			CompressAndWrite(arr.Count);
			//write item
			foreach (var c in arr)
			{
				WriteCommonVal(elemType, c);
			}
		}

		public void Write(IDictionary dictionary)
		{
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