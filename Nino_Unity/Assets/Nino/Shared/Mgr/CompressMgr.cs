using System.IO;
using Nino.Shared.IO;
using System.IO.Compression;
using DeflateStream = Nino.Shared.IO.DeflateStream;
// ReSharper disable UnusedMember.Local

namespace Nino.Shared.Mgr
{
    public static class CompressMgr
    {
        /// <summary>
        /// static ctor
        /// </summary>
        static CompressMgr()
        {
#if UNITY_2017_1_OR_NEWER
            GetCompressInformation(out _, out _);
            var empty = System.Array.Empty<byte>();
            GetDecompressInformation(out _, ref empty);
#endif
        }
        
        /// <summary>
        /// compress stream pool (deflateStream compress + flexibleStream)
        /// </summary>
        private static readonly UncheckedStack<DeflateStream> CompressStreams = new UncheckedStack<DeflateStream>();
        
        /// <summary>
        /// decompress stream pool (deflateStream decompress + flexibleStream)
        /// </summary>
        private static readonly UncheckedStack<DeflateStream> DecompressStreams = new UncheckedStack<DeflateStream>();
        
        /// <summary>
        /// lock compressed streams
        /// </summary>
        private static readonly object CompressedLock = new object();
        

        /// <summary>
        /// lock decompressed streams
        /// </summary>
        private static readonly object DecompressedLock = new object();

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
#if !UNITY_2017_1_OR_NEWER
            return CompressOnNative(data, length);
#endif
            GetCompressInformation(out var zipStream, out var compressedStream);
            zipStream.Write(data, 0, length);
            return GetCompressBytes(zipStream, compressedStream);
        }

        /// <summary>
        /// Compress the given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public static byte[] Compress(ExtensibleBuffer<byte> data, int length)
        {
#if !UNITY_2017_1_OR_NEWER
            return CompressOnNative(data, length);
#endif
            GetCompressInformation(out var zipStream, out var compressedStream);
            data.WriteToStream(zipStream, length);
            return GetCompressBytes(zipStream, compressedStream);
        }

        /// <summary>
        /// Get compressed data
        /// </summary>
        /// <param name="zipStream"></param>
        /// <param name="compressedStream"></param>
        /// <returns></returns>
        private static byte[] GetCompressBytes(DeflateStream zipStream, FlexibleStream compressedStream)
        {
            lock (CompressedLock)
            {
                zipStream.Finish();
                //push
                CompressStreams.Push(zipStream);
                return compressedStream.ToArray();
            }
        }

        /// <summary>
        /// Get relevant data
        /// </summary>
        /// <param name="zipStream"></param>
        /// <param name="compressedStream"></param>
        private static void GetCompressInformation(out DeflateStream zipStream, out FlexibleStream compressedStream)
        {
            lock (CompressedLock)
            {
                //try get stream
                if (CompressStreams.Count > 0)
                {
                    zipStream = CompressStreams.Pop();
                    zipStream.Reset();
                    compressedStream = zipStream.BaseStream;
                }
                else
                {
                    //create
                    compressedStream = new FlexibleStream();
                    zipStream = new DeflateStream(compressedStream, CompressionMode.Compress, true);
                }
            }
        }

        /// <summary>
        /// Decompress thr given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="outputLength"></param>
        /// <returns></returns>
        public static ExtensibleBuffer<byte> Decompress(byte[] data, out int outputLength)
        {
#if !UNITY_2017_1_OR_NEWER
            ExtensibleBuffer<byte> buffer = ObjectPool<ExtensibleBuffer<byte>>.Request();
            var dt = DecompressOnNative(data);
            buffer.CopyFrom(dt, 0, 0, dt.Length);
            outputLength = dt.Length;
            return buffer;
#endif
            lock (DecompressedLock)
            {
                GetDecompressInformation(out var zipStream, ref data);
                var ret = zipStream.GetDecompressedBytes(out outputLength);
                //push
                DecompressStreams.Push(zipStream);
                return ret;
            }
        }

        /// <summary>
        /// Decompress thr given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static byte[] Decompress(byte[] data)
        {
#if !UNITY_2017_1_OR_NEWER
            return DecompressOnNative(data);
#endif
            lock (DecompressedLock)
            {
                GetDecompressInformation(out var zipStream, ref data);
                var ret = zipStream.GetDecompressedBytes(out var len);
                //push
                DecompressStreams.Push(zipStream);
                return ret.ToArray(0,len);
            }
        }


        /// <summary>
        /// Get relevant data
        /// </summary>
        /// <param name="zipStream"></param>
        /// <param name="data"></param>
        private static void GetDecompressInformation(out DeflateStream zipStream, ref byte[] data)
        {
            lock (DecompressedLock)
            {
                //try get stream
                if (DecompressStreams.Count > 0)
                {
                    zipStream = DecompressStreams.Pop();
                    zipStream.Reset();
                    var dataStream = zipStream.BaseStream;
                    dataStream.ChangeBuffer(data);
                }
                else
                {
                    //create
                    var dataStream = new FlexibleStream(data);
                    zipStream = new DeflateStream(dataStream, CompressionMode.Decompress, true);
                }
            }
        }

        #region NON_UNITY

        /// <summary>
        /// Compress the given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private static byte[] CompressOnNative(byte[] data, int length)
        {
            using (var compressedStream = new MemoryStream())
            using (var zipStream = new System.IO.Compression.DeflateStream(compressedStream, CompressionMode.Compress))
            {
                zipStream.Write(data, 0, length);
                zipStream.Close();
                return compressedStream.ToArray();
            }
        }

        /// <summary>
        /// Compress the given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        private static byte[] CompressOnNative(ExtensibleBuffer<byte> data, int length)
        {
            using (var compressedStream = new MemoryStream())
            using (var zipStream = new System.IO.Compression.DeflateStream(compressedStream, CompressionMode.Compress))
            {
                data.WriteToStream(zipStream, length);
                zipStream.Close();
                return compressedStream.ToArray();
            }
        }

        /// <summary>
        /// Decompress thr given bytes
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private static byte[] DecompressOnNative(byte[] data)
        {
            using (var compressedStream = new MemoryStream(data))
            using (var zipStream = new System.IO.Compression.DeflateStream(compressedStream, CompressionMode.Decompress))
            using (var resultStream = new MemoryStream())
            {
                zipStream.CopyTo(resultStream);
                return resultStream.ToArray();
            }
        }

        #endregion
    }
}