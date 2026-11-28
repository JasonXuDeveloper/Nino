using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// What a composite of items (a <c>KeyValuePair</c>, a value tuple of two to seven items, a dictionary's entry) does
    /// on a run, composed from its items at their offsets in it (-1: not known on this runtime, and the composite goes
    /// value by value), never by a rule of the composite's own (#355):
    /// <list type="bullet">
    /// <item>the cursor form: every item a string or an item the run kernels move beside one
    /// (<see cref="Part{T,TFormatter}"/>), a string among them: the composite is the one chain of items every composite
    /// is (<see cref="NinoItemAt{TMember,TItem,TNext}"/>, a generated flat type's too), each item at its offset
    /// (<see cref="NinoOffset"/>, data: an accessor generic over the composite would be code shared over a class item),
    /// moved by the run kernels (<see cref="NinoStringRuns"/>), one call per run. Each item's bottom item is picked once
    /// per run by JIT-time constants (a string by its formatter's type, a slot's value, a value read as one), outside the
    /// loop: a pair in static methods (<see cref="Write{TA,TB,TFormatterA,TFormatterB}"/>), which code shared over a
    /// class inlines whole, a composite of three items or more through a plan of its items
    /// (<see cref="NinoItemPlan{T,TFormatter,TNext}"/> ... <see cref="NinoItemPlanEnd"/>, <see cref="Write{T,TFormatter,TNext}(NinoRun, NinoItemPlan{T,TFormatter,TNext}, ref Writer)"/>),
    /// one call per node and run where code is shared over a class: the one place items are picked twice;</item>
    /// <item>the run loop of a polymorphic item (<see cref="RunLoop{TA,TB,TFormatterA,TFormatterB}"/>): a composite of
    /// two items whose second is a polymorphic base's with a run loop, the first written by that loop as a key
    /// (<see cref="NinoRun.Key"/>); the one place an item is written apart from the other.</item>
    /// </list>
    /// Recycling is composed item by item through <see cref="NinoRecycler"/> by each composite.
    /// </summary>
    internal static class NinoItems
    {
        /// <summary>
        /// Whether an item can be a part of a composite the run kernels move: a string in either encoding, or a value
        /// read as one or a slot's value (<see cref="NinoCursor.Item{T,TFormatter}"/>). A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Part<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind<TFormatter>.Strings || NinoCursor.Item<T, TFormatter>();

        /// <summary>
        /// Whether pairs of the items are moved by the run kernels: one item a string, the other a string or a
        /// <see cref="NinoCursor.Item{T,TFormatter}"/> (pairs of raw values and blocks are blocks). A JIT-time
        /// constant, each item asked once on the path a constant takes: every inlinee counts against the budget of the
        /// generated body a dictionary's write is inlined into.
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
        /// (<see cref="Pair{TA,TB,TFormatterA,TFormatterB}"/>): the chain of the two items, each picked here by JIT-time
        /// constants in static methods, which code shared over a class item inlines whole.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<TA, TB, TFormatterA, TFormatterB>(scoped in NinoRun run, int a, int b, ref Writer writer)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            if (typeof(TFormatterA) == typeof(StringFormatter))
                WriteSecond<NinoUtf16Item, TB, TFormatterB>(run, a, b, ref writer);
            else if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                WriteSecond<NinoUtf8Item, TB, TFormatterB>(run, a, b, ref writer);
            else if (NinoKind.Of<TA, TFormatterA>() == NinoElementKind.Slot)
                WriteSecond<NinoSlotItem<TA, TFormatterA, NinoMemorySize<TA>>, TB, TFormatterB>(run, a, b, ref writer);
            else if (NinoKind.Of<TA, TFormatterA>() == NinoElementKind.Block)
                WriteSecond<NinoValueItem<TA, TFormatterA>, TB, TFormatterB>(run, a, b, ref writer);
            else if (typeof(TA) == typeof(bool))
                WriteSecond<NinoBoolItem, TB, TFormatterB>(run, a, b, ref writer);
            else
                WriteSecond<NinoStructItem<TA>, TB, TFormatterB>(run, a, b, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteSecond<TItemA, TB, TFormatterB>(scoped in NinoRun run, int a, int b, ref Writer writer)
            where TItemA : struct, INinoItem
            where TFormatterB : struct, INinoFormatter<TB>
        {
            // The run rebased to the first item, which then sits at the element's start: one offset fewer for the
            // loop to keep in a register across the string kernels' calls.
            NinoRun items = run.Items(a);
            if (typeof(TFormatterB) == typeof(StringFormatter))
                NinoStringRuns.Write(items, From<TItemA, NinoUtf16Item>(b - a), ref writer);
            else if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                NinoStringRuns.Write(items, From<TItemA, NinoUtf8Item>(b - a), ref writer);
            else if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Slot)
                NinoStringRuns.Write(items, From<TItemA, NinoSlotItem<TB, TFormatterB, NinoMemorySize<TB>>>(b - a), ref writer);
            else if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Block)
                NinoStringRuns.Write(items, From<TItemA, NinoValueItem<TB, TFormatterB>>(b - a), ref writer);
            else if (typeof(TB) == typeof(bool))
                NinoStringRuns.Write(items, From<TItemA, NinoBoolItem>(b - a), ref writer);
            else
                NinoStringRuns.Write(items, From<TItemA, NinoStructItem<TB>>(b - a), ref writer);
        }

        /// <summary>
        /// As <see cref="Write{TA,TB,TFormatterA,TFormatterB}"/>, into the <paramref name="available"/> bytes at
        /// <paramref name="destination"/> alone (<see cref="NinoStringRuns.WriteFree{TItem}"/>): the bytes left (low
        /// half) and the pairs left (high half, 0 when every one fit). For a top layer out of line that takes the free
        /// space and gives back the bytes written in registers, never a writer through memory (a dictionary's entries).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong WriteFree<TA, TB, TFormatterA, TFormatterB>(scoped in NinoRun run, int a, int b,
            ref byte destination, int available)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            if (typeof(TFormatterA) == typeof(StringFormatter))
                return FreeSecond<NinoUtf16Item, TB, TFormatterB>(run, a, b, ref destination, available);
            if (typeof(TFormatterA) == typeof(Utf8StringFormatter))
                return FreeSecond<NinoUtf8Item, TB, TFormatterB>(run, a, b, ref destination, available);
            if (NinoKind.Of<TA, TFormatterA>() == NinoElementKind.Slot)
                return FreeSecond<NinoSlotItem<TA, TFormatterA, NinoMemorySize<TA>>, TB, TFormatterB>(run, a, b,
                    ref destination, available);
            if (NinoKind.Of<TA, TFormatterA>() == NinoElementKind.Block)
                return FreeSecond<NinoValueItem<TA, TFormatterA>, TB, TFormatterB>(run, a, b, ref destination,
                    available);
            if (typeof(TA) == typeof(bool))
                return FreeSecond<NinoBoolItem, TB, TFormatterB>(run, a, b, ref destination, available);
            return FreeSecond<NinoStructItem<TA>, TB, TFormatterB>(run, a, b, ref destination, available);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong FreeSecond<TItemA, TB, TFormatterB>(scoped in NinoRun run, int a, int b,
            ref byte destination, int available)
            where TItemA : struct, INinoItem
            where TFormatterB : struct, INinoFormatter<TB>
        {
            NinoRun items = run.Items(a);
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return NinoStringRuns.WriteFree(items, From<TItemA, NinoUtf16Item>(b - a), ref destination, available);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return NinoStringRuns.WriteFree(items, From<TItemA, NinoUtf8Item>(b - a), ref destination, available);
            if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Slot)
                return NinoStringRuns.WriteFree(items,
                    From<TItemA, NinoSlotItem<TB, TFormatterB, NinoMemorySize<TB>>>(b - a), ref destination, available);
            if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Block)
                return NinoStringRuns.WriteFree(items, From<TItemA, NinoValueItem<TB, TFormatterB>>(b - a),
                    ref destination, available);
            if (typeof(TB) == typeof(bool))
                return NinoStringRuns.WriteFree(items, From<TItemA, NinoBoolItem>(b - a), ref destination, available);
            return NinoStringRuns.WriteFree(items, From<TItemA, NinoStructItem<TB>>(b - a), ref destination,
                available);
        }

        /// <summary>
        /// As <see cref="Write{TA,TB,TFormatterA,TFormatterB}"/>, the read: the pairs of <paramref name="run"/> from
        /// the data at <paramref name="data"/> (<paramref name="end"/> bytes), each handed to <paramref name="store"/>
        /// (in place, or a set's or a dictionary's adds); returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read<TA, TB, TFormatterA, TFormatterB, TStore>(scoped NinoRun run, int a, int b,
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
                return ReadSecond<NinoSlotItem<TA, TFormatterA, NinoMemorySize<TA>>, TB, TFormatterB, TStore>(run, a, b, ref data, end,
                    store);
            if (NinoKind.Of<TA, TFormatterA>() == NinoElementKind.Block)
                return ReadSecond<NinoValueItem<TA, TFormatterA>, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
            if (typeof(TA) == typeof(bool))
                return ReadSecond<NinoBoolItem, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
            return ReadSecond<NinoStructItem<TA>, TB, TFormatterB, TStore>(run, a, b, ref data, end, store);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadSecond<TItemA, TB, TFormatterB, TStore>(scoped NinoRun run, int a, int b, ref byte data,
            int end, TStore store)
            where TItemA : struct, INinoItem
            where TFormatterB : struct, INinoFormatter<TB>
            where TStore : struct, INinoRunStore
        {
            if (typeof(TFormatterB) == typeof(StringFormatter))
                return NinoStringRuns.Read(run, Of<TItemA, NinoUtf16Item>(a, b), ref data, end, store);
            if (typeof(TFormatterB) == typeof(Utf8StringFormatter))
                return NinoStringRuns.Read(run, Of<TItemA, NinoUtf8Item>(a, b), ref data, end, store);
            if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Slot)
                return NinoStringRuns.Read(run, Of<TItemA, NinoSlotItem<TB, TFormatterB, NinoMemorySize<TB>>>(a, b), ref data, end, store);
            if (NinoKind.Of<TB, TFormatterB>() == NinoElementKind.Block)
                return NinoStringRuns.Read(run, Of<TItemA, NinoValueItem<TB, TFormatterB>>(a, b), ref data, end, store);
            if (typeof(TB) == typeof(bool))
                return NinoStringRuns.Read(run, Of<TItemA, NinoBoolItem>(a, b), ref data, end, store);
            return NinoStringRuns.Read(run, Of<TItemA, NinoStructItem<TB>>(a, b), ref data, end, store);
        }

        /// <summary>The chain of a pair: <typeparamref name="TItemA"/> at <paramref name="a"/>, then
        /// <typeparamref name="TItemB"/> at <paramref name="b"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoItemAt<NinoOffset, TItemA, NinoItemAt<NinoOffset, TItemB>> Of<TItemA, TItemB>(int a, int b)
            where TItemA : struct, INinoItem
            where TItemB : struct, INinoItem =>
            new(new NinoOffset(a), new(new NinoOffset(b)));

        /// <summary>The chain of a pair over a run rebased to its first item: <typeparamref name="TItemA"/> at the
        /// element's start, then <typeparamref name="TItemB"/> <paramref name="b"/> bytes from it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoItemFirst<TItemA, NinoItemAt<NinoOffset, TItemB>> From<TItemA, TItemB>(int b)
            where TItemA : struct, INinoItem
            where TItemB : struct, INinoItem =>
            new(new(new NinoOffset(b)));

        /// <summary>As <see cref="Read{TA,TB,TFormatterA,TFormatterB,TStore}"/>, each pair read in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read<TA, TB, TFormatterA, TFormatterB>(scoped NinoRun run, int a, int b, ref byte data,
            int end)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            Read<TA, TB, TFormatterA, TFormatterB, NinoStringRuns.InPlace>(run, a, b, ref data, end, default);

        /// <summary>
        /// Writes the composites of <paramref name="run"/> (not empty; the count is the caller's), each the chain its
        /// <paramref name="plan"/> picks (<see cref="NinoItemPlan{T,TFormatter,TNext}"/>, its last item first):
        /// <see cref="INinoFormatter.WriteStrings"/> of a value tuple of three items or more.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T, TFormatter, TNext>(scoped in NinoRun run, NinoItemPlan<T, TFormatter, TNext> plan,
            ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
            where TNext : struct, INinoItemPlan =>
            plan.WriteLast(run, ref writer);

        /// <summary>As <see cref="Write{T,TFormatter,TNext}(NinoRun, NinoItemPlan{T,TFormatter,TNext}, ref Writer)"/>, the read in place: <see cref="INinoFormatter.ReadStrings"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Read<T, TFormatter, TNext>(scoped NinoRun run, NinoItemPlan<T, TFormatter, TNext> plan,
            ref byte data, int end)
            where TFormatter : struct, INinoFormatter<T>
            where TNext : struct, INinoItemPlan =>
            plan.ReadLast(run, ref data, end);

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
    /// The rest of a composite's plan (<see cref="NinoItems"/>): the items before <c>tail</c> in wire order, each wrapping
    /// the chain after it, the composite's chain handed to the run kernels at the plan's end.
    /// </summary>
    internal interface INinoItemPlan
    {
        /// <summary>Writes the run, each element this plan's items then <paramref name="tail"/>.</summary>
        void Write<TTail>(scoped in NinoRun run, TTail tail, ref Writer writer) where TTail : struct, INinoItem;

        /// <summary>Reads the run in place, each element this plan's items then <paramref name="tail"/>; returns the
        /// bytes read. Not generic over a store: a second type parameter of a node's method cost an instantiating stub
        /// per node over a class item (asm.py, List&lt;(string, int, string)&gt;).</summary>
        int Read<TTail>(scoped NinoRun run, TTail tail, ref byte data, int end) where TTail : struct, INinoItem;
    }

    /// <summary>
    /// The end of a plan: the chain composed, the run moved by the run kernels. Not generic, so code shared over a class
    /// calls it directly and inlines it.
    /// </summary>
    internal readonly struct NinoItemPlanEnd : INinoItemPlan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<TTail>(scoped in NinoRun run, TTail tail, ref Writer writer) where TTail : struct, INinoItem =>
            NinoStringRuns.Write(run, tail, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read<TTail>(scoped NinoRun run, TTail tail, ref byte data, int end)
            where TTail : struct, INinoItem =>
            NinoStringRuns.Read(run, tail, ref data, end, default(NinoStringRuns.InPlace));
    }

    /// <summary>
    /// An item of a plan (<see cref="NinoItems"/>), declared from the composite's last item to its first (that one starts the chain, <see cref="WriteLast"/>):
    /// <typeparamref name="T"/> through <typeparamref name="TFormatter"/> at its offset, its bottom item picked in its
    /// own methods and put in front of the chain it is given, then <typeparamref name="TNext"/>, the items before it.
    /// </summary>
    internal readonly struct NinoItemPlan<T, TFormatter, TNext> : INinoItemPlan
        where TFormatter : struct, INinoFormatter<T>
        where TNext : struct, INinoItemPlan
    {
        private readonly int _at;
        private readonly TNext _next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemPlan(int at, TNext next)
        {
            _at = at;
            _next = next;
        }

        // The pick written out here, as Write's of a pair: in a node over a class item (code shared over it) a call to a
        // static generic method instantiated over that item takes a lookup and is never inlined, which cost a second
        // call per node and run (asm.py: List<(string, int, string)>'s first node 225 to 371 bytes, an instantiating
        // stub).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<TTail>(scoped in NinoRun run, TTail tail, ref Writer writer) where TTail : struct, INinoItem
        {
            var at = new NinoOffset(_at);
            if (typeof(TFormatter) == typeof(StringFormatter))
                _next.Write(run, new NinoItemAt<NinoOffset, NinoUtf16Item, TTail>(at, tail), ref writer);
            else if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                _next.Write(run, new NinoItemAt<NinoOffset, NinoUtf8Item, TTail>(at, tail), ref writer);
            else if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                _next.Write(run, new NinoItemAt<NinoOffset, NinoSlotItem<T, TFormatter, NinoMemorySize<T>>, TTail>(at, tail),
                    ref writer);
            else if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Block)
                _next.Write(run, new NinoItemAt<NinoOffset, NinoValueItem<T, TFormatter>, TTail>(at, tail), ref writer);
            else if (typeof(T) == typeof(bool))
                _next.Write(run, new NinoItemAt<NinoOffset, NinoBoolItem, TTail>(at, tail), ref writer);
            else
                _next.Write(run, new NinoItemAt<NinoOffset, NinoStructItem<T>, TTail>(at, tail), ref writer);
        }

        /// <summary>The composite's last item (the plan's first node): the chain's last item, which has no next.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteLast(scoped in NinoRun run, ref Writer writer)
        {
            var at = new NinoOffset(_at);
            if (typeof(TFormatter) == typeof(StringFormatter))
                _next.Write(run, new NinoItemAt<NinoOffset, NinoUtf16Item>(at), ref writer);
            else if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                _next.Write(run, new NinoItemAt<NinoOffset, NinoUtf8Item>(at), ref writer);
            else if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                _next.Write(run, new NinoItemAt<NinoOffset, NinoSlotItem<T, TFormatter, NinoMemorySize<T>>>(at), ref writer);
            else if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Block)
                _next.Write(run, new NinoItemAt<NinoOffset, NinoValueItem<T, TFormatter>>(at), ref writer);
            else if (typeof(T) == typeof(bool))
                _next.Write(run, new NinoItemAt<NinoOffset, NinoBoolItem>(at), ref writer);
            else
                _next.Write(run, new NinoItemAt<NinoOffset, NinoStructItem<T>>(at), ref writer);
        }

        /// <summary>As <see cref="WriteLast"/>, the read in place.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadLast(scoped NinoRun run, ref byte data, int end)
        {
            var at = new NinoOffset(_at);
            if (typeof(TFormatter) == typeof(StringFormatter))
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoUtf16Item>(at), ref data, end);
            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoUtf8Item>(at), ref data, end);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoSlotItem<T, TFormatter, NinoMemorySize<T>>>(at), ref data, end);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Block)
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoValueItem<T, TFormatter>>(at), ref data, end);
            if (typeof(T) == typeof(bool))
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoBoolItem>(at), ref data, end);
            return _next.Read(run, new NinoItemAt<NinoOffset, NinoStructItem<T>>(at), ref data, end);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read<TTail>(scoped NinoRun run, TTail tail, ref byte data, int end)
            where TTail : struct, INinoItem
        {
            var at = new NinoOffset(_at);
            if (typeof(TFormatter) == typeof(StringFormatter))
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoUtf16Item, TTail>(at, tail), ref data, end);
            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoUtf8Item, TTail>(at, tail), ref data, end);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Slot)
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoSlotItem<T, TFormatter, NinoMemorySize<T>>, TTail>(at, tail),
                    ref data, end);
            if (NinoKind.Of<T, TFormatter>() == NinoElementKind.Block)
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoValueItem<T, TFormatter>, TTail>(at, tail),
                ref data, end);
            if (typeof(T) == typeof(bool))
                return _next.Read(run, new NinoItemAt<NinoOffset, NinoBoolItem, TTail>(at, tail),
                ref data, end);
            return _next.Read(run, new NinoItemAt<NinoOffset, NinoStructItem<T>, TTail>(at, tail),
                ref data, end);
        }
    }
}
