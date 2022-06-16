using System;
using System.Collections.Generic;

namespace Nino.Shared.IO
{
    /// <summary>
    /// A buffer that can dynamically extend
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class ExtensibleBuffer<T> where T : unmanaged
    {
        internal const int MaxBufferSize = 1024 * 8;

        internal readonly List<T[]> Data;

        /// <summary>
        /// internal store block length, rather than calling Data.Count
        /// </summary>
        private int blockLength;
        
        /// <summary>
        /// Init buffer
        /// </summary>
        protected ExtensibleBuffer() : this(10)
        {

        }

        /// <summary>
        /// Init extensible buffer with a capacity
        /// </summary>
        /// <param name="capacity"></param>
        protected ExtensibleBuffer(int capacity)
        {
            Data = new List<T[]>(capacity) { new T[MaxBufferSize] };
            blockLength = 1;
        }

        /// <summary>
        /// Get element at index
        /// </summary>
        /// <param name="index"></param>
        public T this[int index]
        {
            get
            {
                EnsureCapacity(index);
                return Data[index / MaxBufferSize][GetCurBlockIndex(index)];
            }
            set
            {
                EnsureCapacity(index);
                Data[index / MaxBufferSize][GetCurBlockIndex(index)] = value;
            }
        }

        private int GetCurBlockIndex(int index)
        {
            return index - (index / MaxBufferSize) * MaxBufferSize;
        }

        /// <summary>
        /// Ensure index exists
        /// </summary>
        /// <param name="index"></param>
        private void EnsureCapacity(int index)
        {
            while (index / MaxBufferSize >= blockLength - 1)
            {
                Extend();
                blockLength++;
            }
        }

        /// <summary>
        /// Extend buffer
        /// </summary>
        protected virtual void Extend()
        {
            Data.Add(new T[MaxBufferSize]);
        }

        /// <summary>
        /// Convert buffer data to an Array (will create a new array and copy values)
        /// </summary>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public T[] ToArray(int startIndex, int length)
        {
            T[] ret = new T[length];
            //in a block
            if (startIndex / MaxBufferSize == (startIndex + length) / MaxBufferSize)
            {
                EnsureCapacity(startIndex + length);
                Buffer.BlockCopy(Data[startIndex / MaxBufferSize], GetCurBlockIndex(startIndex), ret, 0, length);
            }
            //copy all blocks exceed max first
            else
            {
                //suppose startIndex = 1000, length = 50
                int index = startIndex / MaxBufferSize; // suppose => 1000/1024 = 0
                int dstIndex = 0;
                startIndex = GetCurBlockIndex(startIndex);
                while (startIndex + length > MaxBufferSize) //first iter: 1000+50 > 1024
                {
                    EnsureCapacity(index * MaxBufferSize);
                    //suppose: copy data[0] from index 1000 to ret[0],length of 1024-1000 => 26
                    Buffer.BlockCopy(Data[index], startIndex, ret, dstIndex, MaxBufferSize - startIndex);
                    index++; //next index
                    dstIndex += MaxBufferSize - startIndex; //next index of writing += 1024-1000 => 24
                    length -= MaxBufferSize - startIndex; //next length of writing -= 1024 - 1000 => 50-24 => 26
                    startIndex = 0; //start from 0 of the src offset
                }

                EnsureCapacity(index * MaxBufferSize);
                //copy remained block
                //suppose: copy data[1] from 0 to ret[24] with len of 26
                Buffer.BlockCopy(Data[index], startIndex, ret, dstIndex, length);
            }

            return ret;
        }

        /// <summary>
        /// Copy data to extensible buffer
        /// </summary>
        /// <param name="src"></param>
        /// <param name="srcIndex"></param>
        /// <param name="dstIndex"></param>
        /// <param name="length"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public void CopyFrom(T[] src, int srcIndex, int dstIndex, int length)
        {
            //valid length
            if (src.Length < length) throw new InvalidOperationException("src is not long enough");
            //same block
            if (dstIndex / MaxBufferSize == (dstIndex + length) / MaxBufferSize)
            {
                EnsureCapacity(dstIndex / MaxBufferSize);
                Buffer.BlockCopy(src, srcIndex, Data[dstIndex / MaxBufferSize], GetCurBlockIndex(dstIndex), length);
            }

            //separate blocks
            {
                //suppose from index 1000 copy 50 bytes => copy 24 bytes to _data[0], then copy 26 bytes to _data[1], srcIndex = 0
                int index = dstIndex / MaxBufferSize; //index = 1000/1024 => 0
                dstIndex = GetCurBlockIndex(dstIndex);
                //copy exceed blocks
                while (
                    dstIndex + length >
                    MaxBufferSize) //first iteration => suppose 1000 + 50 > 1024, second iter: 0+26 < max, break
                {
                    EnsureCapacity(index * MaxBufferSize);
                    //first iteration => suppose copy src[0] to _data[0][1000], will copy 1024-1000=> 24 bytes
                    Buffer.BlockCopy(src, srcIndex, Data[index], dstIndex,
                        MaxBufferSize - dstIndex);
                    index++; //next index of extensible buffer
                    srcIndex += MaxBufferSize - dstIndex; //first iteration => 0 += 1024-1000 => srcIndex = 24
                    length -= MaxBufferSize - dstIndex; //first iteration => 50 -= 1024 - 1000 => length = 26
                    dstIndex = 0; //empty the dstIndex for the next iteration
                }

                EnsureCapacity(index * MaxBufferSize);
                //copy remained block
                //suppose srcIndex = 24, index = 1, dstIndex=0, length = 26
                //will copy from src[24] to _data[1][0], length of 26
                Buffer.BlockCopy(src, srcIndex, Data[index], dstIndex, length);
            }
        }
    }
}