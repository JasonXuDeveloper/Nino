using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nino.Core.Internal
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class HashSetView<T>
    {
        public int[] _buckets; // Do not rename (binary serialization)
        public Entry[] _entries; // Do not rename (binary serialization)
        public int _count; // Do not rename (binary serialization)
        public int _version; // Do not rename (binary serialization)
        public int _freeList; // Do not rename (binary serialization)
        public int _freeCount; // Do not rename (binary serialization)
        public IEqualityComparer<T> _comparer; // Do not rename (binary serialization)

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public struct Entry
        {
            public int hashCode; // Do not rename (binary serialization)
            public int next; // Do not rename (binary serialization)
            public T value; // Do not rename (binary serialization)
        }
    }
}
