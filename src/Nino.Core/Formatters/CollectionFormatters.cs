using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Any <c>ICollection&lt;T&gt;</c> with a parameterless constructor (<c>SortedSet&lt;T&gt;</c>,
    /// <c>LinkedList&lt;T&gt;</c>, <c>ObservableCollection&lt;T&gt;</c>, user collections...): count (-1 = null),
    /// then the elements in enumeration order. An existing collection is cleared and refilled.
    /// </summary>
    public struct CollectionFormatter<TCollection, T, TFormatter> : INinoFormatter<TCollection>
        where TCollection : class, ICollection<T>, new()
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
                collection = new TCollection();
                value = collection;
            }
            else
            {
                NinoRecycler.RecycleItems(collection);
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
    public struct HashSetFormatter<TSet, T, TFormatter> : INinoFormatter<TSet>
        where TSet : HashSet<T>, new()
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

            var set = value;
            if (set == null)
            {
                set = new TSet();
                value = set;
            }
            else
            {
                NinoRecycler.RecycleItems(set);
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
                    set.Add(NinoUnsafe.Read<T>(ref src, offset));
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                set.Add(item);
            }
        }
    }

    /// <summary><c>Queue&lt;T&gt;</c>: count (-1 = null), then the elements from head to tail.</summary>
    public struct QueueFormatter<T, TFormatter> : INinoFormatter<Queue<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(Queue<T> value, ref Writer writer)
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

        public void Deserialize(ref Queue<T> value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var queue = value;
            if (queue == null)
            {
                queue = new Queue<T>(length);
                value = queue;
            }
            else
            {
                NinoRecycler.RecycleItems(queue);
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
        public void Serialize(Stack<T> value, ref Writer writer)
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

        public void Deserialize(ref Stack<T> value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var stack = value;
            if (stack == null)
            {
                stack = new Stack<T>(length);
                value = stack;
            }
            else
            {
                NinoRecycler.RecycleItems(stack);
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
    public struct ProducerConsumerFormatter<TCollection, T, TFormatter> : INinoFormatter<TCollection>
        where TCollection : class, IProducerConsumerCollection<T>, new()
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

            var collection = value;
            if (collection == null)
            {
                collection = new TCollection();
                value = collection;
            }
            else
            {
                while (collection.TryTake(out var old))
                    NinoRecycler<T>.Recycle(old);
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                collection.TryAdd(item);
            }
        }
    }
}
