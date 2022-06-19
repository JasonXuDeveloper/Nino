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
            if (length <= buffer.ExpandSize)
            {
                stream.Write(buffer.Data[0], 0, length);
                return;
            }

            int endIndex = length >> buffer.powerOf2;
            int startIndex = 0;
            for (; startIndex < endIndex; startIndex++)
            {
                int sizeToWrite = length <= buffer.ExpandSize ? length : buffer.ExpandSize;
                stream.Write(buffer.Data[startIndex], 0, sizeToWrite);
                length -= sizeToWrite;
            }
            //write last block
            stream.Write(buffer.Data[startIndex], 0, length);
        }
    }
}