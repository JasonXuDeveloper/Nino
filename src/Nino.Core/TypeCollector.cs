using System;
using System.Collections.Generic;
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

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public class DictionaryView<TKey, TValue>
        {
            public int[] _buckets; // Do not rename (binary serialization)
            public Entry[] _entries; // Do not rename (binary serialization)
            public int _count; // Do not rename (binary serialization)
            public int _version; // Do not rename (binary serialization)
            public int _freeList; // Do not rename (binary serialization)
            public int _freeCount; // Do not rename (binary serialization)
            public IEqualityComparer<TKey> _comparer; // Do not rename (binary serialization)
            public Dictionary<TKey, TValue>.KeyCollection _keys; // Do not rename (binary serialization)
            public Dictionary<TKey, TValue>.ValueCollection _values; // Do not rename (binary serialization)
            public object _syncRoot; // Do not rename (binary serialization)

            [SuppressMessage("ReSharper", "InconsistentNaming")]
            public struct Entry
            {
                public int hashCode; // Do not rename (binary serialization)
                public int next; // Do not rename (binary serialization)
                public TKey key; // Do not rename (binary serialization)
                public TValue value; // Do not rename (binary serialization)
            }
        }
    }
}
