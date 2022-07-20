using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Nino.Shared.IO
{
    /// <summary>
    /// Extensible buffer ext
    /// </summary>
    public static class ExtensibleBufferExtensions
    {
        /// <summary>
        /// Write data to stream
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        public static void WriteToStream(this ExtensibleBuffer<byte> buffer, Stream stream, int length)
        {
            byte[] bytes = BufferPool.RequestBuffer(buffer.ExpandSize);
            if (length <= buffer.ExpandSize)
            {
                buffer.CopyBlockTo(ref bytes, 0);
                stream.Write(bytes, 0, length);
                BufferPool.ReturnBuffer(bytes);
                return;
            }

            int endIndex = length >> buffer.PowerOf2;
            int startIndex = -1;
            while(startIndex++ <= endIndex)
            {
                int sizeToWrite = length <= buffer.ExpandSize ? length : buffer.ExpandSize;
                buffer.CopyBlockTo(ref bytes, startIndex);
                stream.Write(bytes, 0, sizeToWrite);
                length -= sizeToWrite;
            }
            BufferPool.ReturnBuffer(bytes);
        }
        
        /// <summary>
        /// Write data to stream
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        public static unsafe void WriteToStream(this ExtensibleBuffer<byte> buffer, Nino.Shared.IO.DeflateStream stream, int length)
        {
            byte* bytes = (byte*)Marshal.AllocHGlobal(length);
            buffer.CopyTo(bytes, 0, length);
            stream.Write(bytes, 0, length);
            Marshal.FreeHGlobal((IntPtr)bytes);
        }

#if !NETSTANDARD && !NET461 && !UNITY_2017_1_OR_NEWER
        /// <summary>
        /// Write data to stream
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        public static unsafe void WriteToStream(this ExtensibleBuffer<byte> buffer, System.IO.Compression.DeflateStream stream, int length)
        {
            byte* bytes = (byte*)Marshal.AllocHGlobal(length);
            buffer.CopyTo(bytes, 0, length);
            stream.Write(new ReadOnlySpan<byte>(bytes,length));
            Marshal.FreeHGlobal((IntPtr)bytes);
        }
#endif
    }
}