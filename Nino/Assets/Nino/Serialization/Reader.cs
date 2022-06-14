using System;
using System.Text;
using Nino.Shared;
using System.Collections.Generic;
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
			Length = data.Length; // in case buffer pool gives a longer buffer
		}

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int Position { get; set; }

		/// <summary>
		/// Position of the current buffer
		/// </summary>
		private int Length { get; set; }

		/// <summary>
		/// Check the capacity
		/// </summary>
		/// <param name="addition"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private void EnsureLength(int addition)
		{
			if (disposed)
			{
				throw new ObjectDisposedException("can not access a disposed reader");
			}
			// Check for overflow
			if (Position + addition > Length)
			{
				throw new IndexOutOfRangeException(
					$"Can not read beyond the buffer: {Position}+{addition} : {Length}");
			}
		}

		/// <summary>
		/// Get CompressType
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public CompressType GetCompressType()
		{
			return (CompressType)ReadByte();
		}

		/// <summary>
		/// Read a byte
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public byte ReadByte()
		{
			EnsureLength(1);
			return buffer[Position++];
		}

		/// <summary>
		/// Read byte[]
		/// </summary>
		/// <param name="len"></param>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
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
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public sbyte ReadSByte()
		{
			EnsureLength(1);
			return (sbyte)(buffer[Position++]);
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
			EnsureLength(ConstMgr.SizeOfShort);
			return (short)(buffer[Position++] | buffer[Position++] << 8);
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
			EnsureLength(ConstMgr.SizeOfInt);
			return (buffer[Position++] | buffer[Position++] << 8 | buffer[Position++] << 16 |
			             buffer[Position++] << 24);
		}

		/// <summary>
		/// Read uint
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public uint ReadUInt32()
		{
			EnsureLength(ConstMgr.SizeOfUInt);
			return (uint)(ReadInt32());
		}

		/// <summary>
		/// Read long
		/// </summary>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public long ReadInt64()
		{
			EnsureLength(ConstMgr.SizeOfLong);
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
			var ret = encoding.GetString(buffer, Position, len);
			Position += len;
			return ret;
		}

		/// <summary>
		/// Read decimal
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public unsafe decimal ReadDecimal()
		{
			EnsureLength(ConstMgr.SizeOfDecimal);
			decimal result;
			var resultSpan = new Span<byte>(&result, ConstMgr.SizeOfDecimal);
			buffer.AsSpan(Position, ConstMgr.SizeOfDecimal).CopyTo(resultSpan);
			Position += ConstMgr.SizeOfDecimal;
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
	}
}