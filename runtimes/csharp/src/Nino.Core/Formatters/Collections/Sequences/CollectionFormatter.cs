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

            // A new ObservableCollection<T>: read as a list, then made around a copy of it (its constructor's), where
            // Add raises an event per element.
            if (typeof(TCollection) == typeof(System.Collections.ObjectModel.ObservableCollection<T>) && value == null)
            {
                ReadObservable(ref reader, ref value);
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
        /// Null where the data holds none. A subclass (a user's) is filled through its own <c>Add</c>, which it may
        /// override; a new <c>ObservableCollection</c> is <see cref="ReadObservable"/>.
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

        /// <summary>
        /// A new <c>ObservableCollection&lt;T&gt;</c>: the elements read as a <c>List&lt;T&gt;</c> (one block where they
        /// have one), into the list it wraps (<see cref="CollectionViews"/>), else into one it is made from (which
        /// copies it). Filled through
        /// <c>Add</c>, each element raised a change event, through a virtual call: 1,000 ints in 50,665 ns against
        /// LuminPack's 488 (LuminPack's matrix, local, #305). An existing one is cleared and refilled through
        /// <c>Add</c>, so whoever listens to it sees every change.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadObservable(ref Reader reader, ref TCollection value)
        {
            if (CollectionViews.Collection)
            {
                // Its own list read into in place (no one listens to a new one yet): no copy.
                var observable = new System.Collections.ObjectModel.ObservableCollection<T>();
                var inner = CollectionFields.List(observable);
                if (inner != null)
                {
                    default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref reader, ref inner);
                    if (inner != null)
                        value = Unsafe.As<TCollection>(observable);
                    return;
                }
            }

            List<T> list = null;
            default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref reader, ref list);
            if (list != null)
                value = Unsafe.As<TCollection>(new System.Collections.ObjectModel.ObservableCollection<T>(list));
        }

        private struct Adder : INinoAdder<TCollection, T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(TCollection target, T item) => target.Add(item);
        }
    }
}
