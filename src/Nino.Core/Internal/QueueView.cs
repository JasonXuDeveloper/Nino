using System.Diagnostics.CodeAnalysis;

namespace Nino.Core.Internal
{
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class QueueView<T>
    {
        public T[] _array; // Do not rename (binary serialization)
        public int _head; // Do not rename (binary serialization)
        public int _tail; // Do not rename (binary serialization)
        public int _size; // Do not rename (binary serialization)
    }
}
