using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The cursor form of a composite of any number of items (#355): a value tuple of three to seven items, each a
    /// string or an item the string run kernels move beside one (<see cref="Part{T,TFormatter}"/>), its run moved by
    /// the kernels (<see cref="NinoStringRuns"/>) as a pair's is, one call per run. The items are listed as a chain of
    /// <see cref="NinoItem{T,TFormatter,TNext}"/> ending in <see cref="NinoItemsEnd"/>, each with its offset in the
    /// composite; walking the chain once per run picks each item's run item by JIT-time constants and composes them into
    /// one element (<see cref="NinoItemChain{THead,TItem}"/>), so the kernel instantiated is over the items alone, never
    /// shared code over a class. A composite of two keeps its own composition (<see cref="NinoPairs{TA,TB,TFormatterA,TFormatterB}"/>,
    /// <see cref="NinoItemPair{TA,TB}"/>), which also gives it the run loop of a polymorphic second item.
    /// </summary>
    internal static class NinoItems
    {
        /// <summary>
        /// Whether an item can be a part of a composite the string run kernels move: a string in either encoding, or a
        /// value read as one or a slot's value (<see cref="NinoCursor.Item{T,TFormatter}"/>). A composite whose every
        /// item is one, and which holds references (so a string among them), takes the kernels. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Part<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind<TFormatter>.Strings || NinoCursor.Item<T, TFormatter>();

        /// <summary>
        /// Writes the composites of <paramref name="run"/> (not empty; the count is the caller's), each the chain of
        /// <paramref name="items"/>: <see cref="INinoFormatter.WriteStrings"/> of a composite of more than two items.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Writer Write<TItems>(scoped NinoRun run, TItems items, Writer writer)
            where TItems : struct, INinoItemsNext =>
            items.Write(run, default(NinoNoItem), writer);

        /// <summary>As <see cref="Write{TItems}"/>, the read in place: <see cref="INinoFormatter.ReadStrings"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read<TItems>(scoped NinoRun run, TItems items, ref byte data, int end)
            where TItems : struct, INinoItemsNext =>
            items.Read(run, default(NinoNoItem), ref data, end);
    }

    /// <summary>
    /// The rest of a chain of items (<see cref="NinoItems"/>): composes its items after <c>head</c>, the items before
    /// it already composed, and hands the element to the string run kernels at its end.
    /// </summary>
    internal interface INinoItemsNext
    {
        /// <summary>Writes the run, each element <paramref name="head"/> then this chain's items.</summary>
        Writer Write<THead>(scoped NinoRun run, THead head, Writer writer) where THead : struct, INinoRunItem;

        /// <summary>Reads the run in place, each element <paramref name="head"/> then this chain's items; returns the
        /// bytes read.</summary>
        int Read<THead>(scoped NinoRun run, THead head, ref byte data, int end) where THead : struct, INinoRunItem;
    }

    /// <summary>The end of a chain of items: the element composed, the run moved by the string run kernels.</summary>
    internal readonly struct NinoItemsEnd : INinoItemsNext
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Writer Write<THead>(scoped NinoRun run, THead head, Writer writer) where THead : struct, INinoRunItem =>
            NinoStringRuns.Write(run, head, writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read<THead>(scoped NinoRun run, THead head, ref byte data, int end)
            where THead : struct, INinoRunItem =>
            NinoStringRuns.Read(run, head, ref data, end, default(NinoStringRuns.InPlace));
    }

    /// <summary>
    /// An item of a chain (<see cref="NinoItems"/>): <typeparamref name="T"/> through
    /// <typeparamref name="TFormatter"/> at its offset in the composite, then <typeparamref name="TNext"/>. Its run
    /// item is picked by JIT-time constants, as a pair's are (<see cref="NinoStringRuns.WritePairs{TA,TB,TFormatterA,
    /// TFormatterB}"/>): a string by its formatter's type, a slot's value, or a value read as one.
    /// </summary>
    internal readonly struct NinoItem<T, TFormatter, TNext> : INinoItemsNext
        where TFormatter : struct, INinoFormatter<T>
        where TNext : struct, INinoItemsNext
    {
        // nint: added to a reference as it is (Mono widens an int offset added to a reference as unsigned).
        private readonly nint _at;
        private readonly TNext _next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItem(int at, TNext next)
        {
            _at = at;
            _next = next;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Writer Write<THead>(scoped NinoRun run, THead head, Writer writer) where THead : struct, INinoRunItem
        {
            if (typeof(TFormatter) == typeof(StringFormatter))
                return _next.Write(run, new NinoItemChain<THead, NinoUtf16Item>(head, _at), writer);
            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                return _next.Write(run, new NinoItemChain<THead, NinoUtf8Item>(head, _at), writer);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                return _next.Write(run, new NinoItemChain<THead, NinoSlotItem<T, TFormatter>>(head, _at), writer);
            return _next.Write(run, new NinoItemChain<THead, NinoValueItem<T, TFormatter>>(head, _at), writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read<THead>(scoped NinoRun run, THead head, ref byte data, int end)
            where THead : struct, INinoRunItem
        {
            if (typeof(TFormatter) == typeof(StringFormatter))
                return _next.Read(run, new NinoItemChain<THead, NinoUtf16Item>(head, _at), ref data, end);
            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                return _next.Read(run, new NinoItemChain<THead, NinoUtf8Item>(head, _at), ref data, end);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                return _next.Read(run, new NinoItemChain<THead, NinoSlotItem<T, TFormatter>>(head, _at), ref data, end);
            return _next.Read(run, new NinoItemChain<THead, NinoValueItem<T, TFormatter>>(head, _at), ref data, end);
        }
    }

    /// <summary>No item: where a chain's composition starts.</summary>
    internal readonly struct NinoNoItem : INinoRunItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element) => 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh) => 0;
    }

    /// <summary>
    /// The items <typeparamref name="THead"/> composed so far, then <typeparamref name="TItem"/> at its offset in the
    /// element: one element of any number of items, never a loop of its own (a pair is <see cref="NinoItemPair{TA,TB}"/>).
    /// </summary>
    internal readonly struct NinoItemChain<THead, TItem> : INinoRunItem
        where THead : struct, INinoRunItem
        where TItem : struct, INinoRunItem
    {
        private readonly THead _head;
        private readonly nint _at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemChain(THead head, nint at)
        {
            _head = head;
            _at = at;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            int first = _head.MaxSize(ref element);
            int second = default(TItem).MaxSize(ref Unsafe.AddByteOffset(ref element, _at));
            return first < 0 || second < 0 || (uint)first + (ulong)(uint)second > int.MaxValue ? -1 : first + second;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            int size = _head.WriteAt(ref destination, ref element);
            return size + default(TItem).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size),
                ref Unsafe.AddByteOffset(ref element, _at));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            int size = _head.ReadAt(ref source, available, ref element, fresh);
            return size + default(TItem).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size,
                ref Unsafe.AddByteOffset(ref element, _at), fresh);
        }
    }
}
