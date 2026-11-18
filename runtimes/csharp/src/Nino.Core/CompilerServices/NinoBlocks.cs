using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// The items of composites (value tuples, pairs, generated structs) whose values are blocks
    /// (<see cref="INinoFormatter{T}.BlockMap"/>) when their items are: each item's size, write and read, raw or a
    /// block, the block's members reached through a constrained call (no boxing on any runtime, explicit
    /// implementations included), and the offsets a <see cref="NinoBlockMap"/> is built from.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoBlocks
    {
        /// <summary>
        /// The wire bytes of an item of a composite (a value tuple's, a pair's, a generated struct's member typed by a
        /// type parameter): its layout's for a raw item (<see cref="RawFormatter.Is{T, TFormatter}"/>), its block's for
        /// a block (<see cref="INinoFormatter{T}.BlockMap"/>), else -1 (slots, values that vary in size, values with
        /// references), which leaves the composite with no fixed size. A JIT-time constant; read once into a
        /// <c>static readonly</c> field by the composites.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ItemSize<TItem, TFormatter>() where TFormatter : struct, INinoFormatter<TItem> =>
            RawFormatter.Is<TItem, TFormatter>()
                ? NinoLayout<TItem>.WireSize
                : NinoBlockCollections.FixedSize<TItem, TFormatter>();

        /// <summary>
        /// Writes an item of a composite (<see cref="ItemSize{TItem, TFormatter}"/> not -1) into its bytes at
        /// <paramref name="destination"/>: a raw one through its layout (converted where this runtime lays it out
        /// otherwise), a block through <see cref="INinoFormatter{T}.WriteFixed"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteItem<TItem, TFormatter>(in TItem value, ref byte destination)
            where TFormatter : struct, INinoFormatter<TItem>
        {
            if (RawFormatter.Is<TItem, TFormatter>())
                NinoLayout<TItem>.Write(ref destination, in value);
            else
                default(TFormatter).WriteFixed(in value, ref destination);
        }

        /// <summary>
        /// Reads an item of a composite (<see cref="ItemSize{TItem, TFormatter}"/> not -1) from its bytes at
        /// <paramref name="source"/>, with the checks a read of it makes otherwise: a raw one through its layout, a
        /// block through <see cref="INinoFormatter{T}.ReadBlock"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadItem<TItem, TFormatter>(ref TItem value, ref byte source)
            where TFormatter : struct, INinoFormatter<TItem>
        {
            if (RawFormatter.Is<TItem, TFormatter>())
                value = NinoLayout<TItem>.Read(ref source);
            else
                default(TFormatter).ReadBlock(ref value, ref source);
        }

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
