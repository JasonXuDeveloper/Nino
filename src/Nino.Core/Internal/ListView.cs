using System.Diagnostics.CodeAnalysis;

namespace Nino.Core.Internal
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class ListView<T>
    {
        public T[] _items; // Do not rename (binary serialization)
        public int _size; // Do not rename (binary serialization)
    }
}
