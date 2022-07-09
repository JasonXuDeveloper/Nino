using System.IO;

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
    }
}