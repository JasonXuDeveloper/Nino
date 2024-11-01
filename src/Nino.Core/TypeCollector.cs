using System.Diagnostics.CodeAnalysis;

#pragma warning disable CS0649

namespace Nino.Core
{
    public static class TypeCollector
    {
        public const ushort NullTypeId = 0;
        public const ushort StringTypeId = 1;
        public const ushort CollectionTypeId = 2;
        public const ushort NullableTypeId = 3;

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        internal class ListView<T>
        {
            internal T[] _items; // Do not rename (binary serialization)
            internal int _size; // Do not rename (binary serialization)
        }
    }
}