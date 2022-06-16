using System.IO;

namespace Nino.Shared.IO
{
    /// <summary>
    /// A byte buffer that can dynamically extend, and allow to write data to stream
    /// </summary>
    public class ExtensibleByteBuffer: ExtensibleBuffer<byte>
    {
        public ExtensibleByteBuffer(): base()
        {
            //pass to base to be able to call by obj pool
        }

        /// <summary>
        /// Extend
        /// </summary>
        protected override void Extend()
        {
            //check to prevent gc
            if (BufferPool.PreviewNextCacheBufferLength() >= MaxBufferSize)
            {
                Data.Add(BufferPool.RequestBuffer(MaxBufferSize));
            }
            else
            {
                Data.Add(new byte[MaxBufferSize]);
            }
        }

        /// <summary>
        /// Write data to stream
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        public void WriteToStream(Stream stream, int length)
        {
            if (length <= MaxBufferSize)
            {
                stream.Write(Data[0], 0, length);
                return;
            }

            int endIndex = length / MaxBufferSize;
            int startIndex = 0;
            for (; startIndex < endIndex; startIndex++)
            {
                int sizeToWrite = length <= MaxBufferSize ? length : MaxBufferSize;
                stream.Write(Data[startIndex], 0, sizeToWrite);
                length -= sizeToWrite;
            }
            //write last block
            stream.Write(Data[startIndex], 0, length);
        }
    }
}