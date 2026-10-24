using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// Where the bytes of a block (<see cref="INinoBlockFormatter{T}"/>) lie in a value's memory, built item by item:
    /// <see cref="Raw{TItem}"/> for a value copied as its bytes, <see cref="Block{TItem, TFormatter}"/> for one written
    /// as a block of its own (a value tuple, a pair, a generated struct of fixed size). Each takes the item's offset in
    /// the value's memory (<see cref="NinoBlocks.Offset{T, TField}"/>, or <see cref="NinoBlocks.Find{T}"/>) and in
    /// the block. An item whose bytes are not its memory as it lies (padding, a layout other than the wire's) or not
    /// found fails the whole map, and values are then moved one at a time. Built once per type, off the hot paths.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class NinoBlockMap
    {
        // (memory offset, block offset, size) triples.
        private readonly List<int> _runs = new List<int>();

        // The block offsets of the bools, whose bytes a read checks (0 or 1).
        private List<int> _bools;

        private bool _failed;

        /// <summary>An item of <typeparamref name="TItem"/> at <paramref name="memory"/> in the value and <paramref name="wire"/> in the block, copied as its bytes.</summary>
        public NinoBlockMap Raw<TItem>(int memory, int wire)
        {
            if (_failed || memory < 0 || NinoUnsafe.IsReferenceOrContainsReferences<TItem>() ||
                !NinoLayout<TItem>.IsCanonical || NinoLayout<TItem>.HasPadding)
                return Fail();
            Add(memory, wire, NinoLayout<TItem>.Size);
            if (typeof(TItem) == typeof(bool))
                (_bools ??= new List<int>()).Add(wire);
            Checked |= NinoLayout<TItem>.Checks != null;
            return this;
        }

        /// <summary>
        /// An item of <typeparamref name="TItem"/> at <paramref name="memory"/> in the value and <paramref name="wire"/>
        /// in the block, written by <typeparamref name="TFormatter"/> as a block (<see cref="INinoBlockFormatter{T}"/>)
        /// whose own map is merged in.
        /// </summary>
        public NinoBlockMap Block<TItem, TFormatter>(int memory, int wire)
            where TFormatter : struct, INinoFormatter<TItem>
        {
            if (_failed || memory < 0 || NinoUnsafe.IsReferenceOrContainsReferences<TItem>() ||
                !(default(TFormatter) is INinoBlockFormatter<TItem> block) || block.BlockSize <= 0 ||
                !(block.BlockMap() is { _failed: false } inner))
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

    /// <summary>
    /// What generated code calls on block formatters (<see cref="INinoBlockFormatter{T}"/>): the members of an
    /// explicit implementation through a constrained call (no boxing on any runtime), and the helpers of a
    /// <see cref="NinoBlockMap"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoBlocks
    {
        /// <summary><see cref="INinoBlockFormatter{T}.BlockSize"/> of <typeparamref name="TFormatter"/>: a JIT-time constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Size<T, TFormatter>() where TFormatter : struct, INinoBlockFormatter<T> =>
            default(TFormatter).BlockSize;

        /// <summary><see cref="INinoBlockFormatter{T}.WriteBlock"/> of <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(ref byte destination, in T value)
            where TFormatter : struct, INinoBlockFormatter<T> =>
            default(TFormatter).WriteBlock(ref destination, in value);

        /// <summary><see cref="INinoBlockFormatter{T}.ReadBlock"/> of <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(ref byte source, ref T value)
            where TFormatter : struct, INinoBlockFormatter<T> =>
            default(TFormatter).ReadBlock(ref source, ref value);

        /// <summary>The byte offset of <paramref name="field"/> inside <paramref name="value"/>.</summary>
        public static int Offset<T, TField>(ref T value, ref TField field) =>
            (int)Unsafe.ByteOffset(ref Unsafe.As<T, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));

        /// <summary>
        /// Where the bytes of an item that <paramref name="fill"/> stores into a value land in its memory: the offset of
        /// the first byte of the run of <paramref name="size"/> marked bytes, or -1. For values whose fields generated
        /// code cannot name (<c>KeyValuePair</c>'s, an auto-property's).
        /// </summary>
        public static int Find<T>(int size, Func<T> fill) where T : struct
        {
            T value = fill();
            ref byte bytes = ref Unsafe.As<T, byte>(ref value);
            for (int start = 0; start + size <= Unsafe.SizeOf<T>(); start++)
            {
                int i = 0;
                while (i < size && Unsafe.Add(ref bytes, start + i) == 0xA5)
                    i++;
                if (i == size)
                    return start;
            }

            return -1;
        }

        /// <summary>An item marked for <see cref="Find{T}"/>: every byte 0xA5 (raw items only).</summary>
        public static TItem Marked<TItem>()
        {
            TItem item = default!;
            Unsafe.InitBlockUnaligned(ref Unsafe.As<TItem, byte>(ref item), 0xA5, (uint)Unsafe.SizeOf<TItem>());
            return item;
        }
    }
}
