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
    /// <c>ConcurrentStack&lt;T&gt;</c>: the producer/consumer wire format, with concrete calls when refilling a stack.
    /// </summary>
    public struct ConcurrentStackFormatter<T, TFormatter> : INinoFormatter<ConcurrentStack<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ConcurrentStack<T> value, ref Writer writer) =>
            default(ProducerConsumerFormatter<ConcurrentStack<T>, T, TFormatter>).Serialize(value, ref writer);

        public void Deserialize(ref Reader reader, ref ConcurrentStack<T> value)
        {
            if (value != null && value.GetType() != typeof(ConcurrentStack<T>))
            {
                default(ProducerConsumerFormatter<ConcurrentStack<T>, T, TFormatter>).Deserialize(ref reader, ref value);
                return;
            }

            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            if (value == null)
                value = BclCollections<T, TFormatter>.TryCreate<ConcurrentStack<T>>(length, ref reader);
            else
                Refill(value, length, ref reader);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Refill(ConcurrentStack<T> stack, int length, ref Reader reader)
        {
            if (NinoRecycler.MayRecycle<T, TFormatter>())
            {
                while (stack.TryPop(out var old))
                    NinoRecycler.Recycle<T, TFormatter>(old);
            }
            else
            {
                stack.Clear();
            }

            // A chunk at a time where the run's read loop moves several, then pushed in order.
            reader = NinoCollections.AddAll<ConcurrentStack<T>, T, TFormatter, Adder>(stack, length, reader);
        }

        private struct Adder : INinoAdder<ConcurrentStack<T>, T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(ConcurrentStack<T> target, T item) => target.Push(item);
        }
    }
}
