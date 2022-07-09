using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        
        /// <summary>
        /// Data that stores everything
        /// </summary>
        private readonly UncheckedList<IntPtr> extensibleData;

        /// <summary>
        /// Size of T
        /// </summary>
        private readonly byte sizeOfT;
        
        /// <summary>
        /// expand size for each block
        /// </summary>
        public readonly ushort ExpandSize;

        /// <summary>
        /// stores the power of 2 for the expand size, which optimizes division
        /// </summary>
        public readonly byte PowerOf2;

        /// <summary>
        /// stores the block length, rather than calling Data.Count, much faster in debug (same in release with calling Data.Count)
        /// </summary>
        private int blockLength;

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer() : this(DefaultBufferCount, DefaultBufferSize)
        {

        }

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer(int bufferCount) : this(bufferCount, DefaultBufferSize)
        {

        }

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer(ushort expandSize) : this(DefaultBufferCount, expandSize)
        {

        }

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer(int bufferCount, ushort expandSize) : this(bufferCount, expandSize, null)
        {

        }

        /// <summary>
        /// Create an extensible buffer using current T[], this avoids GC when creating a new
        /// extensible buffer and copy old value to this buffer
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        public static unsafe ExtensibleBuffer<T> CreateWithBlock(T[] data)
        {
            //check pool
            var peak = ObjectPool<ExtensibleBuffer<T>>.Peak();
            if (peak != null)
            {
                //same size => claim
                if (peak.ExpandSize == data.Length)
                {
                    var ret = ObjectPool<ExtensibleBuffer<T>>.Request();
                    fixed (T* ptr = data)
                    {
                        //rewrite
                        ret.extensibleData[0] = (IntPtr)ptr;
                    }
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
        private unsafe ExtensibleBuffer(int capacity, ushort size, T[] initialData)
        {
            //require power of 2
            if (!Shared.PowerOf2.IsPowerOf2(size))
            {
                size = (ushort)Shared.PowerOf2.RoundUpToPowerOf2(size);
            }

            PowerOf2 = Shared.PowerOf2.GetPower(size);
            sizeOfT = (byte)sizeof(T);
            ExpandSize = size;
            extensibleData = new UncheckedList<IntPtr>(capacity);
            if (initialData != null)
            {
                fixed(T* ptr = initialData)
                {
                    extensibleData.Add((IntPtr)ptr);
                }
            }
            else
            {
                extensibleData.Add(Marshal.AllocHGlobal(sizeOfT * ExpandSize));
            }
            blockLength = 1;
        }

        /// <summary>
        /// Get element at index
        /// </summary>
        /// <param name="index"></param>
        public unsafe T this[int index]
        {
            get => *((T*)extensibleData.items[index >> PowerOf2] + GetCurBlockIndex(index));
            set
            {
                EnsureCapacity(index);
                *((T*)extensibleData.items[index >> PowerOf2] + GetCurBlockIndex(index)) = value;
            }
        }

        /// <summary>
        /// Get Current block index
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
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
            while (index >> PowerOf2 >= blockLength - 1)
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
            extensibleData.Add(Marshal.AllocHGlobal(sizeOfT * ExpandSize));
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
            CopyTo(ref ret, startIndex, length);
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
        public unsafe void CopyFrom(T[] src, int srcIndex, int dstIndex, int length)
        {
            fixed (T* ptr = src)
            {
                CopyFrom(ptr, srcIndex, dstIndex, length);
            }
        }

        /// <summary>
        /// Copy data to extensible buffer
        /// why unaligned? https://stackoverflow.com/a/72418388
        /// </summary>
        /// <param name="src"></param>
        /// <param name="srcIndex"></param>
        /// <param name="dstIndex"></param>
        /// <param name="length"></param>
        /// <exception cref="InvalidOperationException"></exception>
        public unsafe void CopyFrom(T* src, int srcIndex, int dstIndex, int length)
        {
            //same block
            if (dstIndex >> PowerOf2 == (dstIndex + length) >> PowerOf2)
            {
                EnsureCapacity(dstIndex >> PowerOf2);
                var ptr = (T*)extensibleData.items[dstIndex >> PowerOf2];
                Unsafe.CopyBlockUnaligned(ptr + GetCurBlockIndex(dstIndex), src + srcIndex, (uint)length);
            }
            //separate blocks
            else
            {
                //suppose from index 1000 copy 50 bytes => copy 24 bytes to _Data.items[0], then copy 26 bytes to _data[1], srcIndex = 0
                int index = dstIndex >> PowerOf2; //index = 1000/1024 => 0
                dstIndex = GetCurBlockIndex(dstIndex);
                T* ptr;
                //copy exceed blocks
                while (
                    dstIndex + length >
                    ExpandSize) //first iteration => suppose 1000 + 50 > 1024, second iter: 0+26 < max, break
                {
                    EnsureCapacity(index * ExpandSize);
                    //first iteration => suppose copy src[0] to _Data.items[0][1000], will copy 1024-1000=> 24 bytes
                    ptr = (T*)extensibleData.items[index];
                    Unsafe.CopyBlockUnaligned(ptr + dstIndex, src + srcIndex, (uint)(ExpandSize - dstIndex));

                    index++; //next index of extensible buffer
                    srcIndex += ExpandSize - dstIndex; //first iteration => 0 += 1024-1000 => srcIndex = 24
                    length -= ExpandSize - dstIndex; //first iteration => 50 -= 1024 - 1000 => length = 26
                    dstIndex = 0; //empty the dstIndex for the next iteration
                }

                EnsureCapacity(index * ExpandSize);
                //copy remained block
                //suppose srcIndex = 24, index = 1, dstIndex=0, length = 26
                //will copy from src[24] to _Data.items[1][0], length of 26
                ptr = (T*)extensibleData.items[index];
                Unsafe.CopyBlockUnaligned(ptr + dstIndex, src + srcIndex, (uint)length);
            }
        }

        /// <summary>
        /// Copy data from buffer to dst from dst[0]
        /// </summary>
        /// <param name="dst"></param>
        /// <param name="srcIndex"></param>
        /// <param name="length"></param>
        /// <exception cref="OverflowException"></exception>
        public unsafe void CopyTo(ref T[] dst, int srcIndex, int length)
        {
            fixed (T* ptr = dst)
            {
                CopyTo(ptr, srcIndex, length);
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
            if (srcIndex >> PowerOf2 == (srcIndex + length) >> PowerOf2)
            {
                EnsureCapacity(srcIndex >> PowerOf2);
                var ptr = (T*)extensibleData.items[srcIndex >> PowerOf2];
                Unsafe.CopyBlockUnaligned(dst, ptr + GetCurBlockIndex(srcIndex), (uint)length);
            }
            //separate blocks
            else
            {
                int index = srcIndex >> PowerOf2;
                int dstIndex = 0;
                srcIndex = GetCurBlockIndex(srcIndex);
                T* ptr;
                while (
                    srcIndex + length >
                    ExpandSize)
                {
                    EnsureCapacity(index * ExpandSize);
                    ptr = (T*)extensibleData.items[index];
                    Unsafe.CopyBlockUnaligned(dst + dstIndex, ptr + srcIndex, (uint)(ExpandSize - srcIndex));

                    index++;
                    dstIndex += ExpandSize - srcIndex;
                    length -= ExpandSize - srcIndex;
                    srcIndex = 0;
                }

                EnsureCapacity(index * ExpandSize);
                ptr = (T*)extensibleData.items[index];
                Unsafe.CopyBlockUnaligned(dst + dstIndex, ptr + srcIndex, (uint)length);
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
            Unsafe.CopyBlockUnaligned((T*)this.extensibleData.items[blockIndex], data, ExpandSize);
        }

        /// <summary>
        /// Copy entire block to an array
        /// </summary>
        /// <param name="data"></param>
        /// <param name="block"></param>
        public unsafe void CopyBlockTo(ref T[] data, int block)
        {
            fixed (T* dst = data)
            {
                var ptr = (T*)this.extensibleData.items[block];
                Unsafe.CopyBlockUnaligned(dst, ptr, ExpandSize);
            }
        }
        
        /// <summary>
        /// Free allocated memories
        /// </summary>
        ~ExtensibleBuffer()
        {
            foreach (var ptr in extensibleData)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }
}