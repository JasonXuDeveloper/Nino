using System;
using System.IO;
using System.Text;
using Nino.Shared;

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
			if (newCapacity < 128)
				newCapacity = 128;
			if (newCapacity < buffer.Length * 16)
				newCapacity = buffer.Length * 16;
			var temp = new byte[newCapacity];
			if (buffer.Length > 0)
			{
				Buffer.BlockCopy(buffer, 0, temp, 0, buffer.Length);
			}

			buffer = temp;
		}

		/// <summary>
		/// Write byte[]
		/// </summary>
		/// <param name="data"></param>
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
		public unsafe void Write(double value)
		{
			Write(*(ulong*)&value);
		}

		/// <summary>
		/// Write a float
		/// </summary>
		/// <param name="value"></param>
		public unsafe void Write(float value)
		{
			Write(*(uint*)&value);
		}

		/// <summary>
		/// Write string
		/// </summary>
		/// <param name="val"></param>
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
			Write(encoding.GetBytes(val));
		}

		/// <summary>
		/// Write decimal
		/// </summary>
		/// <param name="d"></param>
		public void Write(decimal d)
		{
			EnsureCapacity(4 * 4);
			//4 * 32bit return of get bits
			foreach (var num in decimal.GetBits(d))
			{
				buffer[Position++] = (byte)num;
				buffer[Position++] = (byte)(num >> 8);
				buffer[Position++] = (byte)(num >> 16);
				buffer[Position++] = (byte)(num >> 24);
			}

			Length += 4 * 4;
		}

		/// <summary>
		/// Writes a boolean to this stream. A single byte is written to the stream
		/// with the value 0 representing false or the value 1 representing true.
		/// </summary>
		/// <param name="value"></param>
		public void Write(bool value)
		{
			Write((byte)(value ? 1 : 0));
		}

		public void Write(char ch)
		{
			Write(BitConverter.GetBytes(ch));
		}

		#region write whole num

		/// <summary>
		/// Write byte val to binary writer
		/// </summary>
		/// <param name="num"></param>
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
	}
}