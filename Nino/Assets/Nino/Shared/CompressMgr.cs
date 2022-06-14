using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using DeflateStream = Nino.Shared.DeflateStream;

namespace Nino.Shared
{
    public static class CompressMgr
    {
        private static volatile Stack<DeflateStream> _compressStreams = new Stack<DeflateStream>();
        private static volatile Stack<DeflateStream> _decompressStreams = new Stack<DeflateStream>();

        /// <summary>
        /// Compress the given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] Compress(byte[] data)
        {
            return Compress(data, data.Length);
        }
        
        /// <summary>
        /// Compress the given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static byte[] Compress(byte[] data, int length)
        {
            DeflateStream zipStream;
            MemoryStream compressedStream;
            //try get stream
            if (_compressStreams.Count > 0)
            {
                zipStream = _compressStreams.Pop();
                zipStream.Reset();
                compressedStream = (MemoryStream)zipStream.BaseStream;
            }
            else
            {
                //create
                compressedStream = new MemoryStream();
                zipStream = new DeflateStream(compressedStream, CompressionMode.Compress, true);
            }

            zipStream.Write(data, 0, length);
            zipStream.Finish();
            //push
            _compressStreams.Push(zipStream);
            return compressedStream.ToArray();
        }

        /// <summary>
        /// Decompress thr given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] Decompress(byte[] data)
        {
            DeflateStream zipStream;
            FlexibleReadStream dataStream;
            //try get stream
            if (_decompressStreams.Count > 0)
            {
                zipStream = _decompressStreams.Pop();
                zipStream.Reset();
                dataStream = (FlexibleReadStream)zipStream.BaseStream;
                dataStream.ChangeBuffer(data);
            }
            else
            {
                //create
                dataStream = new FlexibleReadStream(data);
                zipStream = new DeflateStream(dataStream, CompressionMode.Decompress, true);
            }

            var ret = zipStream.GetDecompressedBytes();
            //push
            _decompressStreams.Push(zipStream);
            return ret;
        }
    }
}