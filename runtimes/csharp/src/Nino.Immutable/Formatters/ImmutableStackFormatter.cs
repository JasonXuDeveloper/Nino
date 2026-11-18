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

            // One walk of the nodes, top to bottom, into a rented array that grows as it fills, then reversed in place:
            // counting them first walked the linked nodes twice, and the walk, not the copy, is what costs (#305:
            // ImmutableStack<int> of 1,000 written at 0.48x LuminPack's speed on EPYC 7763, 0.81x on Neoverse-N2).
            var array = ArrayPool<T>.Shared.Rent(16);
            int count = 0;
            foreach (var item in value)
            {
                if (count == array.Length)
                    array = Grow(array);
                array[count++] = item;
            }

            array.AsSpan(0, count).Reverse();
            ImmutableCollections.WriteRented<T, TFormatter>(array, count, ref writer);
        }

        /// <summary>A rented array twice the size of <paramref name="full"/>, holding its elements; returns it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T[] Grow(T[] full)
        {
            var larger = ArrayPool<T>.Shared.Rent(full.Length * 2);
            full.AsSpan().CopyTo(larger);
            ImmutableCollections.Return(full);
            return larger;
        }

        public void Deserialize(ref Reader reader, ref ImmutableStack<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableStack.CreateRange(array);
        }
    }
}
