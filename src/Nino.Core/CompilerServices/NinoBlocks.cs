using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// What generated code calls on block formatters (<see cref="INinoBlockFormatter{T}"/>): the members of an
    /// explicit implementation through a constrained call (no boxing on any runtime), and the helpers of a
    /// <see cref="NinoBlockMap"/>.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoBlocks
    {
        /// <summary><see cref="INinoFixedSizeFormatter{T}.FixedSize"/> of <typeparamref name="TFormatter"/>: a JIT-time constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Size<T, TFormatter>() where TFormatter : struct, INinoBlockFormatter<T> =>
            default(TFormatter).FixedSize;

        /// <summary><see cref="INinoFixedSizeFormatter{T}.WriteFixed"/> of <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter>(in T value, ref byte destination)
            where TFormatter : struct, INinoBlockFormatter<T> =>
            default(TFormatter).WriteFixed(in value, ref destination);

        /// <summary><see cref="INinoBlockFormatter{T}.ReadBlock"/> of <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read<T, TFormatter>(ref T value, ref byte source)
            where TFormatter : struct, INinoBlockFormatter<T> =>
            default(TFormatter).ReadBlock(ref value, ref source);

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
