using System;
using System.Runtime.CompilerServices;

namespace Nino.Shared.IO
{
    /// <summary>
    /// A buffer that can dynamically extend
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public sealed class ExtensibleBuffer<T> where T : unmanaged
    {
        private const int DefaultBufferSize = 128;
        private const int DefaultBufferCount = 10;
        internal readonly UncheckedList<T[]> Data;

        /// <summary>
        /// expand size for each block
        /// </summary>
        public readonly ushort ExpandSize;

        /// <summary>
        /// stores the power of 2 for the expand size, which optimizes division
        /// </summary>
        public readonly byte powerOf2;

        /// <summary>
        /// stores the block length, rather than calling Data.Count, much faster in debug (same in release with calling Data.Count)
        /// </summary>
        private int blockLength;

        /// <summary>
        /// whether or not this is readonly, if so, ensureCapacity will return
        /// </summary>
        public bool ReadOnly;

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer() : this(DefaultBufferCount, DefaultBufferSize)
        {

        }

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer(ushort expandSize) : this(DefaultBufferCount, expandSize)
        {

        }

        /// <summary>
        /// Create an extensible buffer using current T[], this avoids GC when creating a new
        /// extensible buffer and copy old value to this buffer
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static ExtensibleBuffer<T> CreateWithBlock(T[] data)
        {
            //check pool
            var peak = ObjectPool<ExtensibleBuffer<T>>.Peak();
            if (peak != null)
            {
                //same size => claim
                if (peak.ExpandSize == data.Length)
                {
                    var ret = ObjectPool<ExtensibleBuffer<T>>.Request();
                    //rewrite
                    ret.Data[0] = data;
                    return ret;
                }
            }

            //new buffer
            var buffer = new ExtensibleBuffer<T>(DefaultBufferCount, (ushort)data.Length, data);
            return buffer;
        }

        /// <summary>
        /// Init extensible buffer with a capacity
        /// </summary>
        /// <param name="capacity"></param>
        /// <param name="size"></param>
        /// <param name="initialData"></param>
        private ExtensibleBuffer(int capacity, ushort size, T[] initialData = null)
        {
            //require power of 2
            if (!PowerOf2.IsPowerOf2(size))
            {
                size = (ushort)PowerOf2.RoundUpToPowerOf2(size);
            }

            powerOf2 = PowerOf2.GetPower(size);
            ExpandSize = size;
            Data = new UncheckedList<T[]>(capacity) { initialData ?? new T[ExpandSize] };
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
                if (!ReadOnly)
                    EnsureCapacity(index);
                return Data.items[index >> powerOf2][GetCurBlockIndex(index)];
            }
            set
            {
                if (ReadOnly)
                    throw new InvalidOperationException("this extensible buffer is readonly");
                EnsureCapacity(index);
                Data.items[index >> powerOf2][GetCurBlockIndex(index)] = value;
            }
        }

        private int GetCurBlockIndex(int index)
        {
            return index & (ExpandSize - 1);
        }

        /// <summary>
        /// Ensure index exists
        /// </summary>
        /// <param name="index"></param>
        private void EnsureCapacity(int index)
        {
            while (index >> powerOf2 >= blockLength - 1)
            {
                Extend();
                blockLength++;
            }
        }

        /// <summary>
        /// Extend buffer
        /// </summary>
        private void Extend()
        {
            Data.Add(new T[ExpandSize]);
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
            if (startIndex >> powerOf2 == (startIndex + length) >> powerOf2)
            {
                EnsureCapacity(startIndex + length);
                Buffer.BlockCopy(Data.items[startIndex >> powerOf2], GetCurBlockIndex(startIndex), ret, 0, length);
            }
            //copy all blocks exceed max first
            else
            {
                //suppose startIndex = 1000, length = 50
                int index = startIndex >> powerOf2; // suppose => 1000/1024 = 0
                int dstIndex = 0;
                startIndex = GetCurBlockIndex(startIndex);
                while (startIndex + length > ExpandSize) //first iter: 1000+50 > 1024
                {
                    EnsureCapacity(index * ExpandSize);
                    //suppose: copy Data.items[0] from index 1000 to ret[0],length of 1024-1000 => 26
                    Buffer.BlockCopy(Data.items[index], startIndex, ret, dstIndex, ExpandSize - startIndex);
                    index++; //next index
                    dstIndex += ExpandSize - startIndex; //next index of writing += 1024-1000 => 24
                    length -= ExpandSize - startIndex; //next length of writing -= 1024 - 1000 => 50-24 => 26
                    startIndex = 0; //start from 0 of the src offset
                }

                EnsureCapacity(index * ExpandSize);
                //copy remained block
                //suppose: copy Data.items[1] from 0 to ret[24] with len of 26
                Buffer.BlockCopy(Data.items[index], startIndex, ret, dstIndex, length);
            }

            return ret;
        }

        /// <summary>
        /// CAUTION: THIS METHOD WILL PRODUCE GC EACH TIME CALLING IT (APPROX. length BYTES)
        /// convert an extensible to buffer from start index with provided length
        /// </summary>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public Span<T> AsSpan(int startIndex, int length)
        {
            return ToArray(startIndex, length);
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
            if (dstIndex >> powerOf2 == (dstIndex + length) >> powerOf2)
            {
                EnsureCapacity(dstIndex >> powerOf2);
                Buffer.BlockCopy(src, srcIndex, Data.items[dstIndex >> powerOf2], GetCurBlockIndex(dstIndex), length);
            }
            //separate blocks
            else
            {
                //suppose from index 1000 copy 50 bytes => copy 24 bytes to _Data.items[0], then copy 26 bytes to _data[1], srcIndex = 0
                int index = dstIndex >> powerOf2; //index = 1000/1024 => 0
                dstIndex = GetCurBlockIndex(dstIndex);
                //copy exceed blocks
                while (
                    dstIndex + length >
                    ExpandSize) //first iteration => suppose 1000 + 50 > 1024, second iter: 0+26 < max, break
                {
                    EnsureCapacity(index * ExpandSize);
                    //first iteration => suppose copy src[0] to _Data.items[0][1000], will copy 1024-1000=> 24 bytes
                    Buffer.BlockCopy(src, srcIndex, Data.items[index], dstIndex,
                        ExpandSize - dstIndex);
                    index++; //next index of extensible buffer
                    srcIndex += ExpandSize - dstIndex; //first iteration => 0 += 1024-1000 => srcIndex = 24
                    length -= ExpandSize - dstIndex; //first iteration => 50 -= 1024 - 1000 => length = 26
                    dstIndex = 0; //empty the dstIndex for the next iteration
                }

                EnsureCapacity(index * ExpandSize);
                //copy remained block
                //suppose srcIndex = 24, index = 1, dstIndex=0, length = 26
                //will copy from src[24] to _Data.items[1][0], length of 26
                Buffer.BlockCopy(src, srcIndex, Data.items[index], dstIndex, length);
            }
        }

        /// <summary>
        /// Copy data to extensible buffer
        /// </summary>
        /// <param name="src"></param>
        /// <param name="srcIndex"></param>
        /// <param name="dstIndex"></param>
        /// <param name="length"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public unsafe void CopyFrom(T* src, int srcIndex, int dstIndex, int length)
        {
            //same block
            if (dstIndex >> powerOf2 == (dstIndex + length) >> powerOf2)
            {
                EnsureCapacity(dstIndex >> powerOf2);
                fixed (T* ptr = Data.items[dstIndex >> powerOf2])
                {
                    Unsafe.CopyBlockUnaligned(ptr + GetCurBlockIndex(dstIndex), src + srcIndex, (uint)length);
                }
            }
            //separate blocks
            else
            {
                //suppose from index 1000 copy 50 bytes => copy 24 bytes to _Data.items[0], then copy 26 bytes to _data[1], srcIndex = 0
                int index = dstIndex >> powerOf2; //index = 1000/1024 => 0
                dstIndex = GetCurBlockIndex(dstIndex);
                //copy exceed blocks
                while (
                    dstIndex + length >
                    ExpandSize) //first iteration => suppose 1000 + 50 > 1024, second iter: 0+26 < max, break
                {
                    EnsureCapacity(index * ExpandSize);
                    //first iteration => suppose copy src[0] to _Data.items[0][1000], will copy 1024-1000=> 24 bytes
                    fixed (T* ptr = Data.items[index])
                    {
                        Unsafe.CopyBlockUnaligned(ptr + dstIndex, src + srcIndex, (uint)(ExpandSize - dstIndex));
                    }

                    index++; //next index of extensible buffer
                    srcIndex += ExpandSize - dstIndex; //first iteration => 0 += 1024-1000 => srcIndex = 24
                    length -= ExpandSize - dstIndex; //first iteration => 50 -= 1024 - 1000 => length = 26
                    dstIndex = 0; //empty the dstIndex for the next iteration
                }

                EnsureCapacity(index * ExpandSize);
                //copy remained block
                //suppose srcIndex = 24, index = 1, dstIndex=0, length = 26
                //will copy from src[24] to _Data.items[1][0], length of 26
                fixed (T* ptr = Data.items[index])
                {
                    Unsafe.CopyBlockUnaligned(ptr + dstIndex, src + srcIndex, (uint)length);
                }
            }
        }

        /// <summary>
        /// Copy data from buffer to dst from dst[0]
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="srcIndex"></param>
        /// <param name="length"></param>
        /// <exception cref="OverflowException"></exception>
        public void CopyTo(ref T[] dst, int srcIndex, int length)
        {
            if (dst.Length < length)
            {
                throw new OverflowException("dst is not long enough");
            }

            //same block
            if (srcIndex >> powerOf2 == (srcIndex + length) >> powerOf2)
            {
                EnsureCapacity(srcIndex >> powerOf2);
                Buffer.BlockCopy(Data.items[srcIndex >> powerOf2], GetCurBlockIndex(srcIndex), dst, 0, length);
            }
            //separate blocks
            else
            {
                int index = srcIndex >> powerOf2;
                int dstIndex = 0;
                srcIndex = GetCurBlockIndex(srcIndex);
                while (
                    srcIndex + length >
                    ExpandSize)
                {
                    EnsureCapacity(index * ExpandSize);
                    Buffer.BlockCopy(Data.items[index], srcIndex, dst, dstIndex,
                        ExpandSize - srcIndex);
                    index++;
                    dstIndex += ExpandSize - srcIndex;
                    length -= ExpandSize - srcIndex;
                    srcIndex = 0;
                }

                EnsureCapacity(index * ExpandSize);
                Buffer.BlockCopy(Data.items[index], srcIndex, dst, dstIndex, length);
            }
        }

        /// <summary>
        /// Copy data from buffer to dst from dst[0]
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="srcIndex"></param>
        /// <param name="length"></param>
        /// <exception cref="OverflowException"></exception>
        public unsafe void CopyTo(T* dst, int srcIndex, int length)
        {
            //same block
            if (srcIndex >> powerOf2 == (srcIndex + length) >> powerOf2)
            {
                EnsureCapacity(srcIndex >> powerOf2);
                fixed (T* ptr = Data.items[srcIndex >> powerOf2])
                {
                    Unsafe.CopyBlockUnaligned(dst, ptr + GetCurBlockIndex(srcIndex), (uint)length);
                }
            }
            //separate blocks
            else
            {
                int index = srcIndex >> powerOf2;
                int dstIndex = 0;
                srcIndex = GetCurBlockIndex(srcIndex);
                while (
                    srcIndex + length >
                    ExpandSize)
                {
                    EnsureCapacity(index * ExpandSize);
                    fixed (T* ptr = Data.items[index])
                    {
                        Unsafe.CopyBlockUnaligned(dst + dstIndex, ptr + srcIndex, (uint)(ExpandSize - srcIndex));

                    }

                    index++;
                    dstIndex += ExpandSize - srcIndex;
                    length -= ExpandSize - srcIndex;
                    srcIndex = 0;
                }

                EnsureCapacity(index * ExpandSize);
                fixed (T* ptr = Data.items[index])
                {
                    Unsafe.CopyBlockUnaligned(dst + dstIndex, ptr + srcIndex, (uint)length);
                }
            }
        }

        /// <summary>
        /// override an entire block
        /// </summary>
        /// <param name="blockIndex"></param>
        /// <param name="data"></param>
        public unsafe void OverrideBlock(int blockIndex, T* data)
        {
            EnsureCapacity(blockIndex * ExpandSize);
            fixed (T* ptr = Data.items[blockIndex])
            {
                Unsafe.CopyBlockUnaligned(ptr, data, ExpandSize);
            }
        }
    }
}