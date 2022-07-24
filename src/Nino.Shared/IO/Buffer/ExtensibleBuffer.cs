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
        
        /// <summary>
        /// Data that stores everything
        /// </summary>
        public IntPtr Data { get; private set; }

        /// <summary>
        /// Size of T
        /// </summary>
        private readonly byte sizeOfT;
        
        /// <summary>
        /// expand size for each block
        /// </summary>
        public readonly int ExpandSize;

        /// <summary>
        /// Total length of the buffer
        /// </summary>
        public int TotalLength;

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer() : this(DefaultBufferSize)
        {

        }

        /// <summary>
        /// Init buffer
        /// </summary>
        public ExtensibleBuffer(int expandSize) : this(expandSize, null)
        {

        }
        
        /// <summary>
        /// Init extensible buffer with a capacity
        /// </summary>
        /// <param name="size"></param>
        /// <param name="initialData"></param>
        private unsafe ExtensibleBuffer(int size, T[] initialData)
        {
            sizeOfT = (byte)sizeof(T);
            ExpandSize = size;
            Data = Marshal.AllocHGlobal(sizeOfT * ExpandSize);
            if (initialData != null)
            {
                fixed(T* ptr = initialData)
                {
                    CopyFrom(ptr, 0, 0, initialData.Length);
                }
            }

            TotalLength = ExpandSize;
        }

        /// <summary>
        /// Get element at index
        /// </summary>
        /// <param name="index"></param>
        public unsafe T this[int index]
        {
            get => *((T*)Data + index);
            set
            {
                EnsureCapacity(index);
                *((T*)Data + index) = value;
            }
        }

        /// <summary>
        /// Ensure index exists
        /// </summary>
        /// <param name="index"></param>
        private void EnsureCapacity(int index)
        {
            if (index < TotalLength) return;
            while (index >= TotalLength)
            {
                TotalLength += ExpandSize;
            }
            Extend();
        }

        /// <summary>
        /// Extend buffer
        /// </summary>
        private void Extend()
        {
            Data = Marshal.ReAllocHGlobal(Data, new IntPtr(TotalLength * sizeOfT));
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
        /// convert an extensible to buffer from start index with provided length
        /// </summary>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns></returns>
        public unsafe Span<T> AsSpan(int startIndex, int length)
        {
            return new Span<T>((T*)Data + startIndex, length);
        }

        /// <summary>
        /// Convert to span
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        public static implicit operator Span<T>(ExtensibleBuffer<T> buffer) => buffer.AsSpan(0, buffer.TotalLength);

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
            //size check
            EnsureCapacity(dstIndex + length);
            //copy
            Unsafe.CopyBlockUnaligned((T*)Data + dstIndex, src + srcIndex, (uint)length);
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
            //size check
            EnsureCapacity(srcIndex + length);
            //copy
            Unsafe.CopyBlockUnaligned(dst, (T*)Data + srcIndex, (uint)length);
        }
        
        /// <summary>
        /// Free allocated memories
        /// </summary>
        ~ExtensibleBuffer()
        {
            Marshal.FreeHGlobal(Data);
        }
    }
}