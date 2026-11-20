using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;
using Nino.Core;
using Nino.Core.CompilerServices;

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

            // One walk of the nodes, top to bottom, into the gather's chunks, written in reverse: counting them first
            // walked the linked nodes twice, and the walk, not the copy, is what costs (#305: ImmutableStack<int> of
            // 1,000 written at 0.48x LuminPack's speed on EPYC 7763, 0.81x on Neoverse-N2).
            NinoGathered.WriteReversed<T, TFormatter, Items>(new Items(value.GetEnumerator()), ref writer);
        }

        /// <summary>Its elements top to bottom, through its enumerator (a struct of no interface).</summary>
        private struct Items : INinoGatherSource<T>
        {
            private ImmutableStack<T>.Enumerator _items;

            public Items(ImmutableStack<T>.Enumerator items) => _items = items;

            public int Fill(Span<T> chunk)
            {
                ref T at = ref MemoryMarshal.GetReference(chunk);
                int filled = 0;
                while (filled < chunk.Length && _items.MoveNext())
                    Unsafe.Add(ref at, filled++) = _items.Current;
                return filled;
            }

            public void Finish()
            {
            }
        }

        public void Deserialize(ref Reader reader, ref ImmutableStack<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableStack.CreateRange(array);
        }
    }
}
