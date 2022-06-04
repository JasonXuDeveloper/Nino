using System;
using System.IO;
using System.Text;

namespace Nino.Serialization
{
	/// <summary>
	/// A writer that writes serialization Data
	/// </summary>
	internal class Writer: IDisposable
	{
		/// <summary>
		/// Buffer that stores data
		/// </summary>
		private byte[] buffer;
		
		/// <summary>
		/// encoding for string
		/// </summary>
		private Encoding encoding;

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
			buffer = null;
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
		public Writer(int length, Encoding encoding)
		{
			buffer = new byte[length];
			this.encoding = encoding;
			Length = 0;
			Position = 0;
		}

		/// <summary>
		/// Length of the buffer
		/// </summary>
		public int Length { get; private set; }

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		public int Position { get; private set; }

		/// <summary>
		/// Check the capacity
		/// </summary>
		/// <param name="addition"></param>
		private void EnsureCapacity(int addition)
		{
			var value = addition + Position;
			var capacity = buffer.Length;
			// Check for overflow
			if (value < 0)
				throw new IOException("Stream too long");
			if (value > capacity) {
				int newCapacity = value;
				if (newCapacity < 128)
					newCapacity = 128;
				// We are ok with this overflowing since the next statement will deal
				// with the cases where _capacity*2 overflows.
				if (newCapacity < capacity * 16)
					newCapacity = capacity * 16;
				var temp = new byte[newCapacity];
				if (capacity > 0)
				{
					Buffer.BlockCopy(buffer, 0, temp, 0, capacity);
				}

				buffer = temp;
			}
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
			Write(*(uint *)&value);
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
				Write( (ushort)len);
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
				buffer[Position++] = (byte) num; 
				buffer[Position++] = (byte) (num >> 8);
				buffer[Position++] = (byte) (num >> 16);
				buffer[Position++] = (byte) (num >> 24);
			}

			Length += 4 * 4;
		}

		/// <summary>
		/// Writes a boolean to this stream. A single byte is written to the stream
		/// with the value 0 representing false or the value 1 representing true.
		/// </summary>
		/// <param name="value"></param>
		public void Write(bool value) {
			Write((byte) (value ? 1 : 0));
		}

		public void Write(char ch)
		{
			Write(BitConverter.GetBytes(ch));
		}

		#region write whole num

		private const byte SizeOfUInt = sizeof(uint);
		private const byte SizeOfInt = sizeof(int);
		private const byte SizeOfUShort = sizeof(ushort);
		private const byte SizeOfShort = sizeof(short);
		private const byte SizeOfULong = sizeof(ulong);
		private const byte SizeOfLong = sizeof(long);

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
			EnsureCapacity(SizeOfInt);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(int*)p = num;
			// }
			//
			// Position += SizeOfInt;
			// Length += SizeOfInt;
			
			buffer[Position++] = (byte) num; 
			buffer[Position++] = (byte) (num >> 8);
			buffer[Position++] = (byte) (num >> 16);
			buffer[Position++] = (byte) (num >> 24);

			Length += SizeOfInt;
		}

		/// <summary>
		/// Write uint val to binary writer
		/// </summary>
		/// <param name="num"></param>
		public void Write(uint num)
		{
			EnsureCapacity(SizeOfUInt);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(uint*)p = num;
			// }
			//
			// Position += SizeOfUInt;
			// Length += SizeOfUInt;
			
			buffer[Position++] = (byte) num; 
			buffer[Position++] = (byte) (num >> 8);
			buffer[Position++] = (byte) (num >> 16);
			buffer[Position++] = (byte) (num >> 24);

			Length += SizeOfUInt;
		}

		/// <summary>
		/// Write short val to binary writer
		/// </summary>
		/// <param name="num"></param>
		public void Write(short num)
		{
			EnsureCapacity(SizeOfShort);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(short*)p = num;
			// }
			//
			// Position += SizeOfShort;
			// Length += SizeOfShort;

			buffer[Position++] = (byte) num; 
			buffer[Position++] = (byte) (num >> 8);
			
			Length += SizeOfShort;
		}

		/// <summary>
		/// Write ushort val to binary writer
		/// </summary>
		/// <param name="num"></param>
		public void Write(ushort num)
		{
			EnsureCapacity(SizeOfUShort);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(ushort*)p = num;
			// }
			//
			// Position += SizeOfUShort;
			// Length += SizeOfUShort;
			
			buffer[Position++] = (byte) num; 
			buffer[Position++] = (byte) (num >> 8);
			
			Length += SizeOfUShort;
		}

		/// <summary>
		/// Write long val to binary writer
		/// </summary>
		/// <param name="num"></param>
		public void Write(long num)
		{
			EnsureCapacity(SizeOfLong);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(long*)p = num;
			// }
			//
			// Position += SizeOfLong;
			// Length += SizeOfLong;
			
			buffer[Position++] = (byte) num; 
			buffer[Position++] = (byte) (num >> 8);
			buffer[Position++] = (byte) (num >> 16);
			buffer[Position++] = (byte) (num >> 24);
			buffer[Position++] = (byte) (num >> 32);
			buffer[Position++] = (byte) (num >> 40);
			buffer[Position++] = (byte) (num >> 48);
			buffer[Position++] = (byte) (num >> 56);

			Length += SizeOfLong;
		}

		/// <summary>
		/// Write ulong val to binary writer
		/// </summary>
		/// <param name="num"></param>
		public void Write(ulong num)
		{
			EnsureCapacity(SizeOfLong);

			// fixed (byte* p = &buffer[Position])
			// {
			// 	*(ulong*)p = num;
			// }
			//
			// Position += SizeOfULong;
			// Length += SizeOfULong;
			
			buffer[Position++] = (byte) num; 
			buffer[Position++] = (byte) (num >> 8);
			buffer[Position++] = (byte) (num >> 16);
			buffer[Position++] = (byte) (num >> 24);
			buffer[Position++] = (byte) (num >> 32);
			buffer[Position++] = (byte) (num >> 40);
			buffer[Position++] = (byte) (num >> 48);
			buffer[Position++] = (byte) (num >> 56);

			Length += SizeOfULong;
		}
		
		#endregion
	}
}