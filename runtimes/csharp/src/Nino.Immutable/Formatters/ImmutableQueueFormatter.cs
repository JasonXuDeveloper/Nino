using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System;
using Nino.Core;
using Nino.Core.CompilerServices;

namespace Nino.Immutable
{
    /// <summary><c>ImmutableQueue&lt;T&gt;</c>: count (-1 = null), then the elements front to back, as a <c>Queue&lt;T&gt;</c>.</summary>
    public struct ImmutableQueueFormatter<T, TFormatter> : INinoFormatter<ImmutableQueue<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableQueue<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // It keeps no count: walked with its own (struct) enumerator a chunk at a time, the count patched after the
            // elements (values whose memory is their wire form walked straight into the writer's buffer). Through
            // IEnumerable<T> each step was two interface calls (1,000 ints written in 1.6 times the time), and walked
            // into a rented array first, 1.2 times (local quick runs).
            NinoGathered.Write<T, TFormatter, Items>(new Items(value.GetEnumerator()), -1, ref writer);
        }

        /// <summary>Its elements front to back, through its enumerator (a struct of no interface).</summary>
        private struct Items : INinoGatherSource<T>
        {
            private ImmutableQueue<T>.Enumerator _items;

            public Items(ImmutableQueue<T>.Enumerator items) => _items = items;

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

        public void Deserialize(ref Reader reader, ref ImmutableQueue<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableQueue.CreateRange(array);
        }
    }
}
