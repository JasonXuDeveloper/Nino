using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// What a composite of items (a <c>KeyValuePair</c>, a value tuple of two to seven items, a dictionary's entry,
    /// <see cref="NinoMapEntryFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>) does on a run, composed once from
    /// its items at their offsets in it (-1: not known on this runtime, and the composite goes value by value), never by
    /// a rule of the composite's own (#355):
    /// <list type="bullet">
    /// <item>the cursor form: every item a string or an item the string run kernels move beside one
    /// (<see cref="Part{T,TFormatter}"/>), a string among them, the run moved by the kernels
    /// (<see cref="NinoStringRuns"/>), one call per run. Each item's run item is picked once per run by JIT-time
    /// constants (a string by its formatter's type, a slot's value, a value read as one) and the element composed from
    /// them (<see cref="NinoItemAt{TItem}"/>, then <see cref="NinoItemChain{THead,TItem}"/> per further item), so the
    /// kernel is instantiated over the items alone, never over the composite: a run over code shared over a class pays
    /// nothing per element. A composite of three items or more walks a chain of its items
    /// (<see cref="NinoItem{T,TFormatter,TNext}"/> ... <see cref="NinoItemsEnd"/>, <see cref="Write{TItems}"/>), each
    /// node picking its item in its own method, one call per node and run where code is shared over a class item; a
    /// pair picks both in static methods (<see cref="WritePair{TA,TB,TFormatterA,TFormatterB}"/>), which code shared
    /// over a class inlines whole, where a chain's first node would be a call (a method of a struct generic over a
    /// class item is reached through a lookup and never inlined): the one place items are picked twice;</item>
    /// <item>the run loop of a polymorphic item (<see cref="RunLoop{TA,TB,TFormatterA,TFormatterB}"/>): a composite of
    /// two items whose second is a polymorphic base's with a run loop, the first written by that loop as a key
    /// (<see cref="NinoRun.Key"/>, the one key a run carries, whose fields every loop reads: two would cost every
    /// loop over a run); the one place an item is written apart from the other.</item>
    /// </list>
    /// Recycling is composed item by item through <see cref="NinoRecycler"/> by each composite (its
    /// <see cref="INinoFormatter{T}.RecycleItems"/>), a static call per item, as code shared over a class needs.
    /// </summary>
    internal static class NinoItems
    {
        /// <summary>
        /// Whether an item can be a part of a composite the string run kernels move: a string in either encoding, or a
        /// value read as one or a slot's value (<see cref="NinoCursor.Item{T,TFormatter}"/>). A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Part<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind<TFormatter>.Strings || NinoCursor.Item<T, TFormatter>();

        /// <summary>
        /// Whether pairs of the items are moved by the string run kernels: one item a string, the other a string or a
        /// <see cref="NinoCursor.Item{T,TFormatter}"/> (<see cref="Part{T,TFormatter}"/> of both, a string among them;
        /// pairs of raw values and blocks are blocks). A JIT-time constant, where the pair's own formatter's
        /// <see cref="INinoFormatter.HasCursor"/> is read through a field; each item asked once on the path a constant
        /// takes: every inlinee counts against the budget of the generated body a dictionary's write is inlined into.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pair<TA, TB, TFormatterA, TFormatterB>()
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            NinoKind<TFormatterA>.Strings
                ? NinoKind<TFormatterB>.Strings || NinoCursor.Item<TB, TFormatterB>()
                : NinoKind<TFormatterB>.Strings && NinoCursor.Item<TA, TFormatterA>();

        /// <summary><see cref="Pair{TA,TB,TFormatterA,TFormatterB}"/> of items whose offsets are known: a pair's
        /// <see cref="INinoFormatter.HasCursor"/>.</summary>
        public static bool Cursor<TA, TB, TFormatterA, TFormatterB>(int a, int b)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            Pair<TA, TB, TFormatterA, TFormatterB>() && a >= 0 && b >= 0;

        /// <summary>
        /// Writes the pairs of <paramref name="run"/> (not empty; the count is the caller's), <typeparamref name="TA"/>
        /// at <paramref name="a"/> bytes into each element and <typeparamref name="TB"/> at <paramref name="b"/>
        /// (<see cref="Pair{TA,TB,TFormatterA,TFormatterB}"/>): the element a chain of two items,
        /// <see cref="NinoItemChain{THead,TItem}"/> of <see cref="NinoItemAt{TItem}"/>, each item's run item picked
        /// here by JIT-time constants in static methods, which code shared over a class item inlines whole.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Writer WritePair<TA, TB, TFormatterA, TFormatterB>(scoped NinoRun run, int a, int b,
            Writer writer)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            if (typeof(TFormatterA) == typeof(StringFormatter))
                return WriteSecond<NinoUtf16Item, TB, TFormatterB>(run, a, b, writer);
            if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                return WriteSecond<NinoUtf8Item, TB, TFormatterB>(run, a, b, writer);
            if (NinoKind.Of<TA, TFormatterA>() == NinoElementKind.Slot)
                return WriteSecond<NinoSlotItem<TA, TFormatterA>, TB, TFormatterB>(run, a, b, writer);
            return WriteSecond<NinoValueItem<TA, TFormatterA>, TB, TFormatterB>(run, a, b, writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Writer WriteSecond<TItemA, TB, TFormatterB>(scoped NinoRun run, int a, int b, Writer writer)
            where TItemA : struct, INinoRunItem
            where TFormatterB : struct, INinoFormatter<TB>
        {
            var first = new NinoItemAt<TItemA>(a);
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return NinoStringRuns.Write(run, new NinoItemChain<NinoItemAt<TItemA>, NinoUtf16Item>(first, b), writer);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return NinoStringRuns.Write(run, new NinoItemChain<NinoItemAt<TItemA>, NinoUtf8Item>(first, b), writer);
            if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Slot)
                return NinoStringRuns.Write(run,
                    new NinoItemChain<NinoItemAt<TItemA>, NinoSlotItem<TB, TFormatterB>>(first, b), writer);
            return NinoStringRuns.Write(run,
                new NinoItemChain<NinoItemAt<TItemA>, NinoValueItem<TB, TFormatterB>>(first, b), writer);
        }

        /// <summary>
        /// As <see cref="WritePair{TA,TB,TFormatterA,TFormatterB}"/>, the read: the pairs of <paramref name="run"/>
        /// from the data at <paramref name="data"/> (<paramref name="end"/> bytes), each handed to
        /// <paramref name="store"/> (in place, or a set's or a dictionary's adds); returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadPair<TA, TB, TFormatterA, TFormatterB, TStore>(scoped NinoRun run, int a, int b,
            ref byte data, int end, TStore store)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
            where TStore : struct, INinoRunStore
        {
            if (typeof(TFormatterA) == typeof(StringFormatter))
                return ReadSecond<NinoUtf16Item, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
            if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                return ReadSecond<NinoUtf8Item, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
            if (NinoKind.Of<TA, TFormatterA>() == NinoElementKind.Slot)
                return ReadSecond<NinoSlotItem<TA, TFormatterA>, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
            return ReadSecond<NinoValueItem<TA, TFormatterA>, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadSecond<TItemA, TB, TFormatterB, TStore>(scoped NinoRun run, int a, int b, ref byte data,
            int end, TStore store)
            where TItemA : struct, INinoRunItem
            where TFormatterB : struct, INinoFormatter<TB>
            where TStore : struct, INinoRunStore
        {
            var first = new NinoItemAt<TItemA>(a);
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return NinoStringRuns.Read(run, new NinoItemChain<NinoItemAt<TItemA>, NinoUtf16Item>(first, b),
                    ref data, end, store);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return NinoStringRuns.Read(run, new NinoItemChain<NinoItemAt<TItemA>, NinoUtf8Item>(first, b),
                    ref data, end, store);
            if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Slot)
                return NinoStringRuns.Read(run,
                    new NinoItemChain<NinoItemAt<TItemA>, NinoSlotItem<TB, TFormatterB>>(first, b), ref data, end,
                    store);
            return NinoStringRuns.Read(run,
                new NinoItemChain<NinoItemAt<TItemA>, NinoValueItem<TB, TFormatterB>>(first, b), ref data, end,
                    store);
        }

        /// <summary>As <see cref="ReadPair{TA,TB,TFormatterA,TFormatterB,TStore}"/>, each pair read in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadPair<TA, TB, TFormatterA, TFormatterB>(scoped NinoRun run, int a, int b, ref byte data,
            int end)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            ReadPair<TA, TB, TFormatterA, TFormatterB, NinoStringRuns.InPlace>(run, a, b, ref data, end, default);

        /// <summary>
        /// Writes the composites of <paramref name="run"/> (not empty; the count is the caller's), each the chain of
        /// <paramref name="items"/> (<see cref="NinoItem{T,TFormatter,TNext}"/>): <see cref="INinoFormatter.WriteStrings"/>
        /// of a value tuple of three items or more.
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

        /// <summary>
        /// Whether a run of composites of two items is written in one loop (<see cref="INinoFormatter.HasRunLoop"/>):
        /// the second item is a polymorphic base's with a run loop, and the first is written by that loop
        /// (<see cref="NinoRuns.KeyOf{TKey,TKeyFormatter}"/>). A JIT-time constant where the offsets are.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RunLoop<TA, TB, TFormatterA, TFormatterB>(int a, int b)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            !typeof(TB).IsValueType && NinoKind<TFormatterB>.Runs &&
            NinoRuns.KeyOf<TA, TFormatterA>() != NinoRun.NoKey && a >= 0 && b >= 0;

        /// <summary>
        /// Writes a run of composites of two items (<see cref="RunLoop{TA,TB,TFormatterA,TFormatterB}"/>; the count is
        /// the caller's): the second item's generated loop over the run of the second items (<see cref="NinoRun.Items"/>),
        /// each written after its first.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRun<TA, TB, TFormatterA, TFormatterB>(NinoRun run, int a, int b, ref Writer writer)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            default(TFormatterB).WriteRun(run.Items(b, NinoRuns.KeyOf<TA, TFormatterA>() + ((a - b) << 8)), ref writer);
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
        /// bytes read. Not generic over a store: a second type parameter of a node's method cost an instantiating stub
        /// per node over a class item (asm.py, List&lt;(string, int, string)&gt;).</summary>
        int Read<THead>(scoped NinoRun run, THead head, ref byte data, int end) where THead : struct, INinoRunItem;
    }

    /// <summary>
    /// The end of a chain of items: the element composed, the run moved by the string run kernels. Not generic, so
    /// code shared over a class calls it directly and inlines it.
    /// </summary>
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
    /// <typeparamref name="TFormatter"/> at its offset in the composite, then <typeparamref name="TNext"/>, its run item
    /// picked in its own methods (<see cref="NinoItems"/>).
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

        // The pick written out here, as WritePair's: in a node over a class item (code shared over it) a call to a
        // static generic method instantiated over that item takes a lookup and is never inlined, which cost a second
        // call per node and run (asm.py: List<(string, int, string)>'s first node 225 to 371 bytes, an instantiating
        // stub).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Writer Write<THead>(scoped NinoRun run, THead head, Writer writer) where THead : struct, INinoRunItem
        {
            bool first = typeof(THead) == typeof(NinoNoItem);
            if (typeof(TFormatter) == typeof(StringFormatter))
            {
                if (first)
                    return _next.Write(run, new NinoItemAt<NinoUtf16Item>(_at), writer);
                return _next.Write(run, new NinoItemChain<THead, NinoUtf16Item>(head, _at), writer);
            }

            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
            {
                if (first)
                    return _next.Write(run, new NinoItemAt<NinoUtf8Item>(_at), writer);
                return _next.Write(run, new NinoItemChain<THead, NinoUtf8Item>(head, _at), writer);
            }

            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
            {
                if (first)
                    return _next.Write(run, new NinoItemAt<NinoSlotItem<T, TFormatter>>(_at), writer);
                return _next.Write(run, new NinoItemChain<THead, NinoSlotItem<T, TFormatter>>(head, _at), writer);
            }

            if (first)
                return _next.Write(run, new NinoItemAt<NinoValueItem<T, TFormatter>>(_at), writer);
            return _next.Write(run, new NinoItemChain<THead, NinoValueItem<T, TFormatter>>(head, _at), writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read<THead>(scoped NinoRun run, THead head, ref byte data, int end)
            where THead : struct, INinoRunItem
        {
            bool first = typeof(THead) == typeof(NinoNoItem);
            if (typeof(TFormatter) == typeof(StringFormatter))
            {
                if (first)
                    return _next.Read(run, new NinoItemAt<NinoUtf16Item>(_at), ref data, end);
                return _next.Read(run, new NinoItemChain<THead, NinoUtf16Item>(head, _at), ref data, end);
            }

            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
            {
                if (first)
                    return _next.Read(run, new NinoItemAt<NinoUtf8Item>(_at), ref data, end);
                return _next.Read(run, new NinoItemChain<THead, NinoUtf8Item>(head, _at), ref data, end);
            }

            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
            {
                if (first)
                    return _next.Read(run, new NinoItemAt<NinoSlotItem<T, TFormatter>>(_at), ref data, end);
                return _next.Read(run, new NinoItemChain<THead, NinoSlotItem<T, TFormatter>>(head, _at), ref data, end);
            }

            if (first)
                return _next.Read(run, new NinoItemAt<NinoValueItem<T, TFormatter>>(_at), ref data, end);
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

    /// <summary>The first item of an element, <typeparamref name="TItem"/> at its offset in it.</summary>
    internal readonly struct NinoItemAt<TItem> : INinoRunItem where TItem : struct, INinoRunItem
    {
        private readonly nint _at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemAt(nint at) => _at = at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => default(TItem).MaxSize(ref Unsafe.AddByteOffset(ref element, _at));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element) =>
            default(TItem).WriteAt(ref destination, ref Unsafe.AddByteOffset(ref element, _at));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh) =>
            default(TItem).ReadAt(ref source, available, ref Unsafe.AddByteOffset(ref element, _at), fresh);
    }

    /// <summary>
    /// The items <typeparamref name="THead"/> composed so far, then <typeparamref name="TItem"/> at its offset in the
    /// element: one element of any number of items (a pair's is a <see cref="NinoItemAt{TItem}"/> then its second
    /// item), never a loop of its own.
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
            return (uint)first + (ulong)(uint)second > int.MaxValue ? -1 : first + second;
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
