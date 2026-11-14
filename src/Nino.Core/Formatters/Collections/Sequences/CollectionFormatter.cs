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
            // Collection<T> itself: through the list it wraps (ReadThroughList).
            if (typeof(TCollection) == typeof(System.Collections.ObjectModel.Collection<T>) &&
                (value == null || CollectionViews.Collection &&
                    CollectionFields.List(Unsafe.As<System.Collections.ObjectModel.Collection<T>>(value)) != null))
            {
                ReadThroughList(ref reader, ref value);
                return;
            }

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

            // Slots (raw T?): read as one run, as an array of them is, then added.
            if (NinoElements.SlotStride<T, TFormatter>() > 0)
            {
                NinoCollections.AddSlots<TCollection, T, TFormatter, Adder>(collection, length, ref reader);
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                collection.Add(item);
            }
        }

        /// <summary>
        /// A <c>Collection&lt;T&gt;</c> (the type itself, which adds nothing to its list on the way in) read through
        /// the <c>List&lt;T&gt;</c> it wraps, as a list is: the same bytes, the elements as one block where they have
        /// one, where <c>Add</c> inserts each through a virtual call (1,000 <c>int</c> into a new one: 468 ns
        /// against 3,934, local). A new one is made around the list read; an existing one keeps its list, refilled.
        /// Null where the data holds none. A subclass (<c>ObservableCollection</c>, a user's) is filled through its
        /// own <c>Add</c>, which it may override.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadThroughList(ref Reader reader, ref TCollection value)
        {
            var list = value == null
                ? null
                : CollectionFields.List(Unsafe.As<System.Collections.ObjectModel.Collection<T>>(value));
            default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref reader, ref list);
            if (list == null)
                value = null;
            else if (value == null)
                value = Unsafe.As<TCollection>(new System.Collections.ObjectModel.Collection<T>(list));
        }

        private struct Adder : INinoAdder<TCollection, T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(TCollection target, T item) => target.Add(item);
        }
    }
}
