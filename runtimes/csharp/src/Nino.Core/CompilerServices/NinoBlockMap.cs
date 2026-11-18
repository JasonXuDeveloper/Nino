using System;
using System.Collections.Generic;
using System.ComponentModel;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// Where the bytes of a block (<see cref="INinoFormatter{T}.BlockMap"/>) lie in a value's memory, built item by item
    /// in wire order (<see cref="Item{TItem, TFormatter}"/>), each at the item's offset in the value's memory
    /// (<see cref="NinoBlocks.Offset{T, TField}"/>, or <see cref="NinoBlocks.Find{T}"/>) and at the bytes the items
    /// before it take on the wire. A value tuple, a pair and a generated struct each build theirs from their items'
    /// formatters, so a composite is a block wherever its items are, whatever they are. An item whose bytes are not its
    /// memory as it lies (padding, a layout other than the wire's), a block whose own map says nothing, or an offset not
    /// found fails the whole map, and values are then moved one at a time (still as blocks: <see cref="Size"/> stays).
    /// Built once per type, off the hot paths.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class NinoBlockMap
    {
        // (memory offset, block offset, size) triples.
        private readonly List<int> _runs = new();

        // The block offsets of the bools, whose bytes a read checks (0 or 1).
        private List<int> _bools;

        // Where the next item starts in the block: the bytes of the items so far, or -1 once one has no fixed size.
        private int _wire;

        private bool _failed;

        /// <summary>
        /// The next item, of <typeparamref name="TItem"/> at <paramref name="memory"/> in the value, written by
        /// <typeparamref name="TFormatter"/>: raw (<see cref="RawFormatter.Is{T, TFormatter}"/>, the layout check) as
        /// its bytes, a block (<see cref="INinoFormatter{T}.BlockMap"/>) with its own map merged in, or of no fixed
        /// size, which leaves the value no block (<see cref="Size"/> -1). Its wire size is
        /// <see cref="NinoBlocks.ItemSize{TItem, TFormatter}"/>.
        /// </summary>
        public NinoBlockMap Item<TItem, TFormatter>(int memory) where TFormatter : struct, INinoFormatter<TItem>
        {
            int size = NinoBlocks.ItemSize<TItem, TFormatter>();
            if (_wire < 0 || size < 0)
            {
                _wire = -1;
                return Fail();
            }

            int wire = _wire;
            _wire += size;
            if (_failed || memory < 0)
                return Fail();
            if (RawFormatter.Is<TItem, TFormatter>())
                return Raw<TItem>(memory, wire);
            // A block whose own map says nothing (DateTimeOffset's) leaves the composite's bytes unmapped too.
            if (NinoBlockPlan<TItem, TFormatter>.Description is not { _failed: false } inner ||
                !NinoBlockPlan<TItem, TFormatter>.Mapped)
                return Fail();
            for (int i = 0; i < inner._runs.Count; i += 3)
                Add(memory + inner._runs[i], wire + inner._runs[i + 1], inner._runs[i + 2]);
            if (inner._bools != null)
                foreach (int at in inner._bools)
                    (_bools ??= new List<int>()).Add(wire + at);
            Checked |= inner.Checked;
            return this;
        }

        /// <summary>
        /// The bytes of the items so far on the wire (a composite's <see cref="INinoFormatter{T}.FixedSize"/> once all
        /// are in), or -1 when one has no fixed size.
        /// </summary>
        public int Size => _wire;

        /// <summary>
        /// <paramref name="size"/> bytes at <paramref name="memory"/> in the value and <paramref name="wire"/> in the
        /// block, moved as they lie: for a caller that makes them the wire's bytes itself, over the whole run (slots,
        /// cleared by their kernels after the move).
        /// </summary>
        internal NinoBlockMap Bytes(int memory, int wire, int size)
        {
            if (_failed || memory < 0)
                return Fail();
            Add(memory, wire, size);
            return this;
        }

        // A raw item: its bytes as they lie, unless they hold padding or this runtime lays it out otherwise.
        private NinoBlockMap Raw<TItem>(int memory, int wire)
        {
            if (!NinoLayout<TItem>.IsCanonical || NinoLayout<TItem>.HasPadding)
                return Fail();
            Add(memory, wire, NinoLayout<TItem>.Size);
            if (typeof(TItem) == typeof(bool))
                (_bools ??= new List<int>()).Add(wire);
            Checked |= NinoLayout<TItem>.Checks != null;
            return this;
        }

        /// <summary>
        /// Whether an item has values to check in <see cref="NinoSettings.Untrusted"/> mode
        /// (<see cref="NinoLayout{T}.Checks"/>): values are then read one at a time there, each checked by its read.
        /// </summary>
        internal bool Checked { get; private set; }

        /// <summary>
        /// The (memory offset, block offset, size) triples, or null when the map failed or its items do not fill a
        /// block of <paramref name="size"/> bytes exactly once inside a value of <paramref name="memorySize"/> bytes.
        /// </summary>
        internal int[] Runs(int size, int memorySize)
        {
            if (_failed)
                return null;
            var wire = new bool[size];
            var memory = new bool[memorySize];
            for (int i = 0; i < _runs.Count; i += 3)
            for (int b = 0; b < _runs[i + 2]; b++)
            {
                int m = _runs[i] + b, w = _runs[i + 1] + b;
                if (m >= memorySize || w >= size || memory[m] || wire[w])
                    return null;
                memory[m] = wire[w] = true;
            }

            return Array.IndexOf(wire, false) < 0 ? _runs.ToArray() : null;
        }

        /// <summary>The block offsets of the <c>bool</c> items, or null for none.</summary>
        internal int[] Bools => _bools?.ToArray();

        private void Add(int memory, int wire, int size)
        {
            _runs.Add(memory);
            _runs.Add(wire);
            _runs.Add(size);
        }

        private NinoBlockMap Fail()
        {
            _failed = true;
            return this;
        }
    }
}
