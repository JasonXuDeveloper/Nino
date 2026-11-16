using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableStack&lt;T&gt;</c>: count (-1 = null), then the elements bottom to top, as a <c>Stack&lt;T&gt;</c>, so
    /// pushing them in order rebuilds it.
    /// </summary>
    public struct ImmutableStackFormatter<T, TFormatter> : INinoFormatter<ImmutableStack<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableStack<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Enumerated top to bottom: counted, then copied into a T[] from its end (raw elements then go as one
            // block), without a list to grow and reverse.
            int count = 0;
            for (var rest = value; !rest.IsEmpty; rest = rest.Pop())
                count++;
            var array = ArrayPool<T>.Shared.Rent(count);
            ref T slot = ref Unsafe.Add(ref MemoryMarshal.GetReference(array.AsSpan()), count);
            foreach (var item in value)
            {
                slot = ref Unsafe.Subtract(ref slot, 1);
                slot = item;
            }
            ImmutableCollections.WriteRented<T, TFormatter>(array, count, ref writer);
        }

        public void Deserialize(ref Reader reader, ref ImmutableStack<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableStack.CreateRange(array);
        }
    }
}
