using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Any <c>ICollection&lt;T&gt;</c> Nino can create (<c>SortedSet&lt;T&gt;</c>,
    /// <c>LinkedList&lt;T&gt;</c>, <c>ObservableCollection&lt;T&gt;</c>, user collections...): count (-1 = null),
    /// then the elements in enumeration order. An existing collection is cleared and refilled.
    /// </summary>
    public struct CollectionFormatter<[NinoCreated] TCollection, T, TFormatter> : INinoFormatter<TCollection>
        where TCollection : class, ICollection<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TCollection value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (BclCollections<T, TFormatter>.TryWrite(value, ref writer))
                return;

            NinoCollections.WriteEnumerable<T, TFormatter>(value, ref writer);
        }

        public void Deserialize(ref Reader reader, ref TCollection value)
        {
            // Each element takes at least its fewest bytes (NinoElements.MinWireSize), which bounds what the
            // collection allocates ahead of them (NinoElements.Heavy).
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            var collection = value ??= NinoActivator<TCollection>.Create();

            if (collection.IsReadOnly)
                NinoThrow.ReadOnlyCollection(collection.GetType());
            if (NinoRecycler.DiscardAll(collection, collection.Count))
                collection.Clear();

            if (BclCollections<T, TFormatter>.TryRead(collection, length, ref reader))
                return;

            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                collection.Add(item);
            }
        }
    }
}
