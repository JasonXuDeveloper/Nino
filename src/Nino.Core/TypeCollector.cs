using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#pragma warning disable CS0649

namespace Nino.Core
{
    public static class TypeCollector
    {
        public const int Null = 0;
        public const int Ref = ~0;
        public const uint HasCircularMeta = 0xABCDDBCA;
        public const byte NullCollection = 0;
        public const uint EmptyCollectionHeader = 128;
        public static readonly bool Is64Bit = IntPtr.Size == 8;
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetCollectionHeader(int size)
        {
            // set sign bit to 1 - indicates that this is a collection and not null
            uint ret = (uint)size | 0x80000000;
#if BIGENDIAN
            return ret;
#else
            //to big endian
            return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(ret);
#endif
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public class ListView<T>
        {
            public T[] _items; // Do not rename (binary serialization)
            public int _size; // Do not rename (binary serialization)
        }
    }
}