using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#pragma warning disable CS0649

namespace Nino.Core
{
    public static class TypeCollector
    {
        public const int Null = 0;
        public const byte NullCollection = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint GetCollectionHeader(int size)
        {
            // set sign bit to 1 - indicates that this is a collection and not null
            uint ret = (uint)size | 0x80000000;
#if BIGENDIAN
            return ret;
#else
            //to big endian
#if NET5_0_OR_GREATER
            return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(ret);
#else
            return (ret << 24) | (ret >> 24) | ((ret & 0x0000FF00) << 8) | ((ret & 0x00FF0000) >> 8);
#endif
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