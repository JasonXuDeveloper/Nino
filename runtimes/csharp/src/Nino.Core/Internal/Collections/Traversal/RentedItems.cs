using System;
using System.Collections.Generic;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The first <c>count</c> items of a rented array, for a constructor or method that takes an
    /// <see cref="IEnumerable{T}"/> (ConcurrentQueue, ConcurrentBag, SortedSet, ConcurrentDictionary, List.AddRange): one
    /// object that is its own enumerator, read once, and an <see cref="ICollection{T}"/> whose <c>CopyTo</c> the
    /// runtime's copy takes. An <see cref="ArraySegment{T}"/> in its place is a box, and boxes its struct enumerator too,
    /// checking its range per item (89738283: 7% slower on x64 runners).
    /// </summary>
    internal sealed class RentedItems<T> : ICollection<T>, IEnumerator<T>
    {
        private readonly T[] _items;
        private readonly int _count;
        private int _index = -1;

        public RentedItems(T[] items, int count)
        {
            _items = items;
            _count = count;
        }

        public int Count => _count;
        public bool IsReadOnly => true;
        public T Current => _items[_index];
        object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext() => ++_index < _count;
        public IEnumerator<T> GetEnumerator() => this;
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => this;
        public void CopyTo(T[] array, int arrayIndex) => Array.Copy(_items, 0, array, arrayIndex, _count);
        public void Reset() => _index = -1;
        public void Dispose() { }
        public void Add(T item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(T item) => throw new NotSupportedException();
        public bool Remove(T item) => throw new NotSupportedException();
    }
}
