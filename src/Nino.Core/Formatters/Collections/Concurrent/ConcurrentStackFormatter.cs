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
            if (NinoRecycler<T>.MayRecycle)
            {
                while (stack.TryPop(out var old))
                    NinoRecycler<T>.Recycle(old);
            }
            else
            {
                stack.Clear();
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                stack.Push(item);
            }
        }
    }
}
