using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            NinoCollections.WriteEnumerable<T, TFormatter>(value, ref writer);
        }

        public void Deserialize(ref TCollection value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var collection = value;
            if (collection == null)
            {
                // InterfaceSetFormatter reads into a plain HashSet<T>, which no generated code registers a creator for.
                collection = typeof(TCollection) == typeof(HashSet<T>)
                    ? (TCollection)(object)new HashSet<T>()
                    : NinoFactory<TCollection>.Create();
                value = collection;
            }

            if (collection.IsReadOnly)
                NinoThrow.ReadOnlyCollection(collection.GetType());
            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            if (collection.Count != 0)
            {
                NinoRecycler.DiscardAll(collection);
                collection.Clear();
            }

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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            if (RawFormatter<TFormatter>.Is)
            {
                int size = Unsafe.SizeOf<T>();
                int count = value.Count;
                ref byte dst = ref writer.Reserve(checked(NinoWire.LengthSize + count * size));
                NinoUnsafe.Write(ref dst, count);
                int offset = NinoWire.LengthSize, written = 0;
                foreach (var item in value)
                {
                    if (++written > count)
                        NinoThrow.InvalidData("collection was modified during serialization");
                    NinoUnsafe.Write(ref dst, offset, item);
                    offset += size;
                }

                NinoLayout<T>.ClearPadding(ref Unsafe.Add(ref dst, NinoWire.LengthSize), count);

                return;
            }

            writer.WriteLength(value.Count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
        }

        public void Deserialize(ref TSet value, ref Reader reader)
        {
            bool raw = RawFormatter<TFormatter>.Is;
            int length = reader.ReadLength(raw ? Unsafe.SizeOf<T>() : 1);
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var set = value ??= NinoFactory<TSet>.Create();
            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            if (set.Count != 0)
            {
                NinoRecycler.DiscardAll(set);
                set.Clear();
            }

#if NET8_0_OR_GREATER || NETSTANDARD2_1
            set.EnsureCapacity(length);
#endif
            if (raw)
            {
                int size = Unsafe.SizeOf<T>();
                ref byte src = ref reader.ReadBlock(length * size);
                for (int i = 0, offset = 0; i < length; i++, offset += size)
                {
                    if (!set.Add(NinoUnsafe.Read<T>(ref src, offset)))
                        NinoThrow.DuplicateKey(set.GetType());
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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
        }

        public void Deserialize(ref TQueue value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var queue = value ??= typeof(TQueue) == typeof(Queue<T>)
                ? (TQueue)new Queue<T>(length)
                : NinoFactory<TQueue>.Create();
            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            if (queue.Count != 0)
            {
                NinoRecycler.DiscardAll(queue);
                queue.Clear();
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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            int count = value.Count;
            writer.WriteLength(count);
            if (count == 0)
                return;

            // Enumeration is top to bottom; write bottom to top so reading is a sequence of pushes.
            var buffer = ArrayPool<T>.Shared.Rent(count);
            try
            {
                value.CopyTo(buffer, 0);
                for (int i = count - 1; i >= 0; i--)
                    default(TFormatter).Serialize(buffer[i], ref writer);
            }
            finally
            {
                ArrayPool<T>.Shared.Return(buffer, NinoUnsafe.IsReferenceOrContainsReferences<T>());
            }
        }

        public void Deserialize(ref TStack value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var stack = value ??= typeof(TStack) == typeof(Stack<T>)
                ? (TStack)new Stack<T>(length)
                : NinoFactory<TStack>.Create();
            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            if (stack.Count != 0)
            {
                NinoRecycler.DiscardAll(stack);
                stack.Clear();
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                stack.Push(item);
            }
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
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            var items = value.ToArray(); // snapshot, safe against concurrent modification
            writer.WriteLength(items.Length);
            if (value is ConcurrentStack<T> || value is ConcurrentBag<T>)
            {
                // ToArray() is in pop order; adding reverses it.
                for (int i = items.Length - 1; i >= 0; i--)
                    default(TFormatter).Serialize(items[i], ref writer);
            }
            else
            {
                NinoElements.Write<T, TFormatter>(items, ref writer);
            }
        }

        public void Deserialize(ref TCollection value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            // Existing contents, also of a created instance (a factory may hand out a pooled one that is not empty).
            var collection = value ??= NinoFactory<TCollection>.Create();
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
