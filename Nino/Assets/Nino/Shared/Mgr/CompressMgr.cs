using System.IO;
using Nino.Shared.IO;
using System.IO.Compression;
using System.Collections.Generic;
using DeflateStream = Nino.Shared.IO.DeflateStream;

namespace Nino.Shared.Mgr
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
        /// Get relevant data
        /// </summary>
        /// <param name="zipStream"></param>
        /// <param name="compressedStream"></param>
        private static void GetCompressData(out DeflateStream zipStream, out MemoryStream compressedStream)
        {
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
        }

        /// <summary>
        /// Compress the given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static byte[] Compress(byte[] data, int length)
        {
            GetCompressData(out var zipStream, out var compressedStream);
            zipStream.Write(data, 0, length);
            zipStream.Finish();
            //push
            _compressStreams.Push(zipStream);
            return compressedStream.ToArray();
        }

        /// <summary>
        /// Compress the given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static byte[] Compress(ExtensibleByteBuffer data, int length)
        {
            GetCompressData(out var zipStream, out var compressedStream);
            data.WriteToStream(zipStream, length);
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