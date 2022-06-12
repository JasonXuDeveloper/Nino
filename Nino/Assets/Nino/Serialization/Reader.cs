using System;
using System.Text;
using Nino.Shared;
using System.Collections.Generic;

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
		private readonly byte[] buffer;

		/// <summary>
		/// has been disposed or not
		/// </summary>
		private bool disposed;

		/// <summary>
		/// encoding for string
		/// </summary>
		private readonly Encoding encoding;

		/// <summary>
		/// Dispose the read
		/// </summary>
		public void Dispose()
		{
			BufferPool.ReturnBuffer(buffer);
			disposed = true;
		}
		
		/// <summary>
		/// Create a nino read
		/// </summary>
		/// <param name="data"></param>
		/// <param name="encoding"></param>
		public Reader(byte[] data, Encoding encoding)
		{
			buffer = BufferPool.RequestBuffer(data);
			this.encoding = encoding;
			Position = 0;
		}

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int Position { get; set; }

		/// <summary>
		/// Check the capacity
		/// </summary>
		/// <param name="addition"></param>
		private void EnsureLength(int addition)
		{
			if (disposed)
			{
				throw new ObjectDisposedException("can not access a disposed reader");
			}
			// Check for overflow
			if (Position + addition > buffer.Length)
			{
				throw new IndexOutOfRangeException(
					$"Can not read beyond the buffer: {Position}+{addition} : {buffer.Length}");
			}
		}

		/// <summary>
		/// Get CompressType
		/// </summary>
		/// <returns></returns>
		public CompressType GetCompressType()
		{
			return (CompressType)ReadByte();
		}

		/// <summary>
		/// Read a byte
		/// </summary>
		public byte ReadByte()
		{
			EnsureLength(1);
			return buffer[Position++];
		}

		/// <summary>
		/// Read byte[]
		/// </summary>
		/// <param name="len"></param>
		public byte[] ReadBytes(int len)
		{
			EnsureLength(len);
			byte[] ret = new byte[len];
			Buffer.BlockCopy(buffer, Position, ret, 0, len);
			Position += len;
			return ret;
		}

		/// <summary>
		/// Read sbyte
		/// </summary>
		/// <returns></returns>
		public sbyte ReadSByte()
		{
			EnsureLength(1);
			return (sbyte)(buffer[Position++]);
		}

		/// <summary>
		/// Read char
		/// </summary>
		public char ReadChar()
		{
			return (char)ReadInt16();
		}

		/// <summary>
		/// Read short
		/// </summary>
		/// <returns></returns>
		public short ReadInt16()
		{
			EnsureLength(ConstMgr.SizeOfShort);
			return (short)(buffer[Position++] | buffer[Position++] << 8);
		}

		/// <summary>
		/// Read ushort
		/// </summary>
		/// <returns></returns>
		public ushort ReadUInt16()
		{
			EnsureLength(ConstMgr.SizeOfUShort);
			return (ushort)(buffer[Position++] | buffer[Position++] << 8);
		}

		/// <summary>
		/// Read int
		/// </summary>
		/// <returns></returns>
		public int ReadInt32()
		{
			EnsureLength(ConstMgr.SizeOfInt);
			return (buffer[Position++] | buffer[Position++] << 8 | buffer[Position++] << 16 |
			             buffer[Position++] << 24);
		}

		/// <summary>
		/// Read uint
		/// </summary>
		/// <returns></returns>
		public uint ReadUInt32()
		{
			EnsureLength(ConstMgr.SizeOfUInt);
			return (uint)(buffer[Position++] | buffer[Position++] << 8 | buffer[Position++] << 16 |
			              buffer[Position++] << 24);
		}

		/// <summary>
		/// Read long
		/// </summary>
		/// <returns></returns>
		public long ReadInt64()
		{
			EnsureLength(ConstMgr.SizeOfLong);
			uint lo = (uint)(buffer[Position++] | buffer[Position++] << 8 |
			                 buffer[Position++] << 16 | buffer[Position++] << 24);
			uint hi = (uint)(buffer[Position++] | buffer[Position++] << 8 |
			                 buffer[Position++] << 16 | buffer[Position++] << 24);
			return (long)(hi) << 32 | lo;
		}

		/// <summary>
		/// Read ulong
		/// </summary>
		/// <returns></returns>
		public ulong ReadUInt64()
		{
			EnsureLength(ConstMgr.SizeOfULong);
			uint lo = (uint)(buffer[Position++] | buffer[Position++] << 8 |
			                 buffer[Position++] << 16 | buffer[Position++] << 24);
			uint hi = (uint)(buffer[Position++] | buffer[Position++] << 8 |
			                 buffer[Position++] << 16 | buffer[Position++] << 24);
			return ((ulong)hi) << 32 | lo;
		}

		/// <summary>
		/// Read float
		/// </summary>
		/// <returns></returns>
		[System.Security.SecuritySafeCritical]
		public unsafe float ReadSingle()
		{
			EnsureLength(ConstMgr.SizeOfUInt);
			uint tmpBuffer = (uint)(buffer[Position++] | buffer[Position++] << 8 | buffer[Position++] << 16 |
			                        buffer[Position++] << 24);
			return *((float*)&tmpBuffer);
		}

		/// <summary>
		/// Read float
		/// </summary>
		/// <returns></returns>
		[System.Security.SecuritySafeCritical]
		public float ReadFloat()
		{
			return ReadSingle();
		}

		/// <summary>
		/// Read double
		/// </summary>
		/// <returns></returns>
		[System.Security.SecuritySafeCritical]
		public unsafe double ReadDouble()
		{
			EnsureLength(ConstMgr.SizeOfULong);
			uint lo = (uint)(buffer[Position++] | buffer[Position++] << 8 |
			                 buffer[Position++] << 16 | buffer[Position++] << 24);
			uint hi = (uint)(buffer[Position++] | buffer[Position++] << 8 |
			                 buffer[Position++] << 16 | buffer[Position++] << 24);
			ulong tmpBuffer = ((ulong)hi) << 32 | lo;
			return *((double*)&tmpBuffer);
		}

		/// <summary>
		/// Read string
		/// </summary>
		public string ReadString()
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

			//Read directly
			return encoding.GetString(ReadBytes(len));
		}

		/// <summary>
		/// 缓存decimal的参数数组
		/// </summary>
		private static volatile Queue<int[]> _readDecimalPool = new Queue<int[]>();

		/// <summary>
		/// Read decimal
		/// </summary>
		public decimal ReadDecimal()
		{
			//4 * 32bit return of get bits
			if (_readDecimalPool.Count > 0)
			{
				var arr = _readDecimalPool.Dequeue();
				arr[0] = ReadInt32();
				arr[1] = ReadInt32();
				arr[2] = ReadInt32();
				arr[3] = ReadInt32();
				var ret = new decimal(arr);
				_readDecimalPool.Enqueue(arr);
				return ret;
			}
			else
			{
				var arr = new int[4];
				arr[0] = ReadInt32();
				arr[1] = ReadInt32();
				arr[2] = ReadInt32();
				arr[3] = ReadInt32();
				var ret = new decimal(arr);
				_readDecimalPool.Enqueue(arr);
				return ret;
			}
		}

		/// <summary>
		/// Read bool
		/// </summary>
		public bool ReadBool()
		{
			return ReadByte() != 0;
		}
	}
}