using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

            writer = Write(value, writer);
        }

        // Out of line, the writer by value and returned, as Nino.Core's own trees (BclCollections.WriteSorted): inlined,
        // the walk's setup (its source built and passed by value) took registers and stack from every caller (asm.py,
        // a top-level ImmutableSortedSet<int> write: 43 stack accesses, 33 out of line, the writer's copy in and out).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer Write(ImmutableQueue<T> value, Writer writer)
        {
            // It keeps no count: its nodes walked a chunk at a time (StackNodes: its front stack, then its back one as
            // the queue keeps it reversed), the count patched after the elements (values whose memory is their wire
            // form walked straight into the writer's buffer). Its enumerator calls Pop and Peek per element, neither
            // inlined (1,000 ints written in 1.55 times the time); it stays for a back not reversed yet, which it
            // reverses and the queue keeps, and where the mirror does not match. Through IEnumerable<T> each step was
            // two interface calls (1.6 times the time), and walked into a rented array first, 1.2 times (local quick
            // runs).
            if (StackNode<T>.TryItems(value, out var nodes))
                NinoGathered.Write<T, TFormatter, StackItems<T>>(nodes, -1, ref writer);
            else
                NinoGathered.Write<T, TFormatter, Items>(new Items(value.GetEnumerator()), -1, ref writer);

            return writer;
        }

        /// <summary>Its elements front to back, through its enumerator (a struct of no interface).</summary>
        private struct Items : INinoGatherSource<T>
        {
            private ImmutableQueue<T>.Enumerator _items;

            public Items(ImmutableQueue<T>.Enumerator items) => _items = items;

            public int Fill(Span<T> chunk)
            {
                // The enumerator in a local for the loop, written back once (NinoGathered.Enumerated).
                ref T at = ref MemoryMarshal.GetReference(chunk);
                var items = _items;
                int filled = 0;
                while (filled < chunk.Length && items.MoveNext())
                    Unsafe.Add(ref at, filled++) = items.Current;
                _items = items;
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
