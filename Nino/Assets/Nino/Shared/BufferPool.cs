using System;
using System.Collections.Generic;

namespace Nino.Shared
{
    public static class BufferPool
    {
        /// <summary>
        /// A shared buffer queue
        /// </summary>
        private static volatile Queue<byte[]> _buffers = new Queue<byte[]>();

        /// <summary>
        /// Request a buffer
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        public static byte[] RequestBuffer(int size = 0)
        {
            byte[] ret;
            if (_buffers.Count > 0)
            {
                ret = _buffers.Dequeue();
                if (ret.Length < size)
                {
                    byte[] buffer = new byte[size];
                    ReturnBuffer(ret);
                    return buffer;
                }
            }
            else
            {
                ret = new byte[size];
            }

            return ret;
        }
        
        /// <summary>
        /// Request a buffer from a source
        /// </summary>
        /// <param name="original"></param>
        /// <returns></returns>
        public static byte[] RequestBuffer(byte[] original)
        {
            byte[] ret = RequestBuffer(original.Length);
            Buffer.BlockCopy(original,0,ret,0,original.Length);
            return ret;
        }
        
        /// <summary>
        /// Request a buffer from a source
        /// </summary>
        /// <param name="len"></param>
        /// <param name="original"></param>
        /// <returns></returns>
        public static byte[] RequestBuffer(int len, byte[] original)
        {
            byte[] ret = RequestBuffer(len);
            Buffer.BlockCopy(original,0,ret,0,original.Length);
            return ret;
        }

        /// <summary>
        /// Return buffer to the pool
        /// </summary>
        /// <param name="buffer"></param>
        public static void ReturnBuffer(byte[] buffer)
        {
            _buffers.Enqueue(buffer);
        }
    }
}