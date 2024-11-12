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
            return (uint)size | 0x80000000;
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        internal class ListView<T>
        {
            internal T[] _items; // Do not rename (binary serialization)
            internal int _size; // Do not rename (binary serialization)
        }
    }
}