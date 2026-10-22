using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        public void Serialize(TCollection value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (BclCollections<T, TFormatter>.TryWrite(value, ref writer))
                return;

            NinoCollections.WriteEnumerable<T, TFormatter>(value, ref writer);
        }

        public void Deserialize(ref TCollection value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, 1, out int length))
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
                default(TFormatter).Deserialize(ref item, ref reader);
                collection.Add(item);
            }
        }
    }

    /// <summary><c>HashSet&lt;T&gt;</c> (or a type derived from it): count (-1 = null), then the elements.</summary>
    public struct HashSetFormatter<[NinoCreated] TSet, T, TFormatter> : INinoFormatter<TSet>
        where TSet : HashSet<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TSet value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (RawFormatter.Is<T, TFormatter>())
            {
                int size = Unsafe.SizeOf<T>();
                int count = value.Count;
                ref byte elements = ref writer.ReserveCounted(count, size);
                // The set's own entries when none is free: a loop over an array, without the enumerator's version
                // check and reloads of the set's fields on each element. The enumerator out of line, so that its
                // state does not take registers or inlining budget here.
                var entries = CollectionViews.HashSet ? CollectionFields.Entries(value, count) : null;
                if (entries != null && count != 0 && count <= entries.Length)
                {
                    // Cursors rather than an int index, which every load and store would scale and sign-extend.
                    ref var entry = ref entries[0];
                    ref byte at = ref elements;
                    int left = count;
                    do
                    {
                        NinoUnsafe.Write(ref at, entry.Value);
                        entry = ref Unsafe.Add(ref entry, 1);
                        at = ref Unsafe.Add(ref at, size);
                    } while (--left != 0);
                }
                else if (count != 0)
                    WriteEnumerated(value, ref elements, count);

                NinoLayout<T>.ClearPadding(ref elements, count);
                return;
            }

            // An empty set skips the call (as NinoElements.Read).
            if (value.Count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            // Elements with references through the writer parameter, which their formatters' calls take anyway: a copy
            // in and out of WriteEach for each set cost more (NinoElements.WritesOnLocal).
            if (NinoElements.WritesOnLocal<T>())
                writer = WriteEach(value, writer);
            else
                WriteEach(value, ref writer);
        }

        /// <summary>The <paramref name="count"/> raw elements of a set with free entries, in enumeration order.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteEnumerated(TSet value, ref byte next, int count)
        {
            int size = Unsafe.SizeOf<T>();
            int written = 0;
            foreach (var item in value)
            {
                if (++written > count)
                    NinoThrow.InvalidData("collection was modified during serialization");
                NinoUnsafe.Write(ref next, item);
                next = ref Unsafe.Add(ref next, size);
            }
        }

        /// <summary>
        /// The count and the elements one by one: one loop, out of Serialize (a second loop there made the raw one's
        /// code slower: HashSet&lt;int&gt; of 1,000 serialized 40% slower on x64 and Arm64). The writer by value and
        /// returned: a local here, its cursor in registers (HashSet&lt;int?&gt; of 1,000: 5 to 10% faster), where one
        /// passed by reference would be kept in memory, the caller's too.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEach(TSet value, Writer writer)
        {
            WriteEach(value, ref writer);
            return writer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteEach(TSet value, ref Writer writer)
        {
            writer.WriteLength(value.Count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
        }

        public void Deserialize(ref TSet value, ref Reader reader)
        {
            bool raw = RawFormatter.Is<T, TFormatter>();
            if (!reader.TryReadLengthOrDiscard(ref value, raw ? Unsafe.SizeOf<T>() : 1, out int length))
                return;

            // InterfaceSetFormatter reads into a plain HashSet<T>, which no generated code registers a creator for.
            var set = value ??= typeof(TSet) == typeof(HashSet<T>)
                ? (TSet)new HashSet<T>()
                : NinoActivator<TSet>.Create();
            if (NinoRecycler.DiscardAll(set, set.Count))
                set.Clear();

#if NET8_0_OR_GREATER || NETSTANDARD2_1
            if (length != 0)
                set.EnsureCapacity(length);
#endif
            if (raw)
            {
                int size = Unsafe.SizeOf<T>();
                ref byte src = ref reader.ReadElements(length, size);
                for (int i = 0; i < length; i++)
                {
                    if (!set.Add(NinoUnsafe.Read<T>(ref src)))
                        NinoThrow.DuplicateKey(set.GetType());
                    src = ref Unsafe.Add(ref src, size);
                }

                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                if (!set.Add(item))
                    NinoThrow.DuplicateKey(set.GetType());
            }
        }
    }

    /// <summary><c>Queue&lt;T&gt;</c>: count (-1 = null), then the elements from head to tail.</summary>
    public struct QueueFormatter<T, TFormatter> : INinoFormatter<Queue<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(Queue<T> value, ref Writer writer) =>
            default(QueueFormatter<Queue<T>, T, TFormatter>).Serialize(value, ref writer);

        public void Deserialize(ref Queue<T> value, ref Reader reader) =>
            default(QueueFormatter<Queue<T>, T, TFormatter>).Deserialize(ref value, ref reader);
    }

    /// <summary>
    /// A type derived from <c>Queue&lt;T&gt;</c> (and <c>Queue&lt;T&gt;</c> itself): count (-1 = null), then the
    /// elements from head to tail. An existing queue is cleared and refilled.
    /// </summary>
    public struct QueueFormatter<[NinoCreated] TQueue, T, TFormatter> : INinoFormatter<TQueue>
        where TQueue : Queue<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TQueue value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            if (CollectionViews.Queue)
            {
                // Head to tail: the backing ring as at most two spans (a block each for raw elements).
                var ring = CollectionFields.Array<T>(value);
                int count = CollectionFields.Size<T>(value);
                int head = CollectionFields.Head<T>(value);
                int first = Math.Min(count, ring.Length - head);
                writer.WriteLength(count);
                NinoElements.Write<T, TFormatter>(new ReadOnlySpan<T>(ring, head, first), ref writer);
                NinoElements.Write<T, TFormatter>(new ReadOnlySpan<T>(ring, 0, count - first), ref writer);
                return;
            }

            writer.WriteLength(value.Count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
        }

        public void Deserialize(ref TQueue value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, 1, out int length))
                return;

            var queue = value ??= typeof(TQueue) == typeof(Queue<T>)
                ? (TQueue)new Queue<T>(length)
                : NinoActivator<TQueue>.Create();
            if (NinoRecycler.DiscardAll(queue, queue.Count))
                queue.Clear();

            if (CollectionViews.Queue)
            {
                // Read in place from the start of the backing array (a block for raw elements), then the ring set
                // around them: no capacity check and version bump per element.
                var ring = NinoCollectionViews.ReadInto<T, TFormatter>(ref CollectionFields.Array<T>(queue), length,
                    ref reader);
                CollectionFields.Head<T>(queue) = 0;
                CollectionFields.Tail<T>(queue) = length == ring.Length ? 0 : length;
                CollectionFields.Size<T>(queue) = length;
                CollectionFields.Version<T>(queue)++;
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                queue.Enqueue(item);
            }
        }
    }

    /// <summary><c>Stack&lt;T&gt;</c>: count (-1 = null), then the elements from bottom to top.</summary>
    public struct StackFormatter<T, TFormatter> : INinoFormatter<Stack<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(Stack<T> value, ref Writer writer) =>
            default(StackFormatter<Stack<T>, T, TFormatter>).Serialize(value, ref writer);

        public void Deserialize(ref Stack<T> value, ref Reader reader) =>
            default(StackFormatter<Stack<T>, T, TFormatter>).Deserialize(ref value, ref reader);
    }

    /// <summary>
    /// A type derived from <c>Stack&lt;T&gt;</c> (and <c>Stack&lt;T&gt;</c> itself): count (-1 = null), then the
    /// elements from bottom to top, so reading is a sequence of pushes. An existing stack is cleared and refilled.
    /// </summary>
    public struct StackFormatter<[NinoCreated] TStack, T, TFormatter> : INinoFormatter<TStack>
        where TStack : Stack<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TStack value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            int count = value.Count;
            writer.WriteLength(count);
            if (count == 0)
                return;

            if (CollectionViews.Stack)
            {
                // The backing array is bottom to top already (a block for raw elements).
                NinoElements.Write<T, TFormatter>(new ReadOnlySpan<T>(CollectionFields.Array<T>(value), 0, count),
                    ref writer);
                return;
            }

            // Enumeration is top to bottom; write bottom to top so reading is a sequence of pushes: the snapshot
            // reversed, then written as a span (a block for elements of a fixed size).
            var buffer = ArrayPool<T>.Shared.Rent(count);
            try
            {
                value.CopyTo(buffer, 0);
                var items = new Span<T>(buffer, 0, count);
                items.Reverse();
                NinoElements.Write<T, TFormatter>(items, ref writer);
            }
            finally
            {
                NinoCollections.ReturnRented(buffer);
            }
        }

        public void Deserialize(ref TStack value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, 1, out int length))
                return;

            var stack = value ??= typeof(TStack) == typeof(Stack<T>)
                ? (TStack)new Stack<T>(length)
                : NinoActivator<TStack>.Create();
            if (NinoRecycler.DiscardAll(stack, stack.Count))
                stack.Clear();

            if (CollectionViews.Stack)
            {
                // Bottom to top is the backing array's order: read in place, then set the count.
                NinoCollectionViews.ReadInto<T, TFormatter>(ref CollectionFields.Array<T>(stack), length, ref reader);
                CollectionFields.Size<T>(stack) = length;
                CollectionFields.Version<T>(stack)++;
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                stack.Push(item);
            }
        }
    }

    internal static class NinoCollectionViews
    {
        /// <summary>
        /// Reads <paramref name="length"/> elements into the start of the backing array of an empty Stack or Queue
        /// (<see cref="CollectionFields"/>), replacing it with a new one when it is too
        /// small, and returns the array. Each element is read as a new value, as Push and Enqueue of a default
        /// one would: slots of references are already default in an empty collection, unmanaged ones may hold
        /// what was removed and are cleared first, unless the read overwrites them
        /// (<see cref="NinoElements.ReadOverwrites{T,TFormatter}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] ReadInto<T, TFormatter>(ref T[] array, int length, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            var items = array;
            if (items.Length < length)
                array = items = new T[length];
            var span = new Span<T>(items, 0, length);
            if (!NinoElements.ReadOverwrites<T, TFormatter>() && !NinoUnsafe.IsReferenceOrContainsReferences<T>())
                span.Clear();
            NinoElements.Read<T, TFormatter>(span, ref reader);
            return items;
        }
    }

    /// <summary>
    /// <c>ConcurrentQueue&lt;T&gt;</c>, <c>ConcurrentStack&lt;T&gt;</c>, <c>ConcurrentBag&lt;T&gt;</c> and other
    /// <c>IProducerConsumerCollection&lt;T&gt;</c>s: count (-1 = null), then the elements in the order they have to
    /// be added back (stacks bottom to top).
    /// </summary>
    public struct ProducerConsumerFormatter<[NinoCreated] TCollection, T, TFormatter> : INinoFormatter<TCollection>
        where TCollection : class, IProducerConsumerCollection<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TCollection value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            var items = value.ToArray(); // snapshot, safe against concurrent modification
            // ToArray() of a stack or bag is in pop order, and adding reverses it: written reversed.
            if (value is ConcurrentStack<T> || value is ConcurrentBag<T>)
                Array.Reverse(items);
            NinoCollections.WriteSpan<T, TFormatter>(items, ref writer);
        }

        public void Deserialize(ref TCollection value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, 1, out int length))
                return;

            if (value == null)
            {
                value = BclCollections<T, TFormatter>.TryCreate<TCollection>(length, ref reader);
                if (value != null)
                    return;
            }

            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            var collection = value ??= NinoActivator<TCollection>.Create();
            NinoRecycler.DrainAndDiscard(collection);

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                collection.TryAdd(item);
            }
        }
    }
}
