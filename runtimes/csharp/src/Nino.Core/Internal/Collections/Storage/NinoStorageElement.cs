using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// Where the one item of a storage element lies (<see cref="NinoStorageFormatter{TElement,TA,TFormatterA,TFields}"/>):
    /// a struct of a collection's storage (a <c>HashSet</c>'s entry) whose wire bytes are the item's, the rest of its
    /// stride (a hash code, a link) skipped. A struct whose members the formatter calls through a constrained call:
    /// a field of the element reached through a reference, which the JIT knows the offset of on every runtime.
    /// </summary>
    internal interface INinoStorageFields<TElement, TA>
    {
        /// <summary>The item of <paramref name="element"/>.</summary>
        ref TA A(ref TElement element);
    }

    /// <summary>As <see cref="INinoStorageFields{TElement,TA}"/>, for an element of two items, written
    /// <typeparamref name="TA"/> then <typeparamref name="TB"/> (a <c>Dictionary</c>'s entry: key, then value).</summary>
    internal interface INinoStorageFields<TElement, TA, TB> : INinoStorageFields<TElement, TA>
    {
        /// <summary>The second item of <paramref name="element"/>.</summary>
        ref TB B(ref TElement element);
    }

    /// <summary>
    /// The pieces both storage formatters share: offsets measured on this runtime, and the run of an item's values
    /// inside a run of elements.
    /// </summary>
    internal static class NinoStorage
    {
        /// <summary>
        /// The byte offset of <paramref name="item"/> (inside <paramref name="element"/>), measured on this runtime's
        /// layout: for a block map, built once per type.
        /// </summary>
        public static int Offset<TElement, TItem>(ref TElement element, ref TItem item) =>
            NinoBlocks.Offset(ref element, ref item);

        /// <summary>
        /// The run of the items at <paramref name="item"/> (the first element's, not in an empty run) of the elements
        /// of <paramref name="run"/>: the same entries and stride, starting at the item, its in-use test rebased (a
        /// polymorphic value's generated loop over a run, <see cref="INinoFormatter.WriteRun"/>). The offset is the
        /// distance between the two references, so nothing is looked up in code shared over a class.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoRun Items(scoped NinoRun run, ref byte item) =>
            new(ref item, run.Count, run.Stride, NinoRun.NoKey,
                run.InUse == NinoRun.AllInUse
                    ? NinoRun.AllInUse
                    : NinoRun.Rebase(run.InUse, (int)Unsafe.ByteOffset(ref run.First, ref item)));

        /// <summary>The byte distance from <paramref name="from"/> to <paramref name="to"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Distance(ref byte from, ref byte to) => (int)Unsafe.ByteOffset(ref from, ref to);
    }

    /// <summary>
    /// A storage element of one item, the collection's element (#355; written as one, a slot where its kind is slots,
    /// <see cref="NinoElements.WriteOne{T,TFormatter}"/>): a struct of <c>Unsafe.SizeOf&lt;TElement&gt;()</c> bytes (the stride of
    /// its run) whose wire bytes are its item's, written by <typeparamref name="TFormatterA"/> as that formatter writes
    /// it alone, at the offset <typeparamref name="TFields"/> reaches; every other byte (a hash code, a link, padding)
    /// is skipped on write and left 0 by a block read (the collection's top layer sets it after: a table's links). The
    /// element of a <c>HashSet</c>'s run of entries. Composed as a value tuple is: a block where its item is raw or a
    /// block (<see cref="NinoBlocks.ItemSize{TItem,TFormatter}"/>), its <see cref="BlockMap"/> the item's at its offset
    /// here, so the block kernels (<see cref="NinoBlockPlan{T,TFormatter}"/>: groups, lanes, 64-byte permutes, the
    /// netstandard2.1 build's registered kernels) pack the items out of a run of elements and scatter them back with no
    /// code of their own; else the item's cursor form, its run loop and its formatter, through the element. The
    /// pieces a collection's top layer adds are its own: reaching the storage, measuring where the fields are (its
    /// startup check), and what it does after a fill (link). Extension points that take it unchanged: a
    /// <c>SortedList</c>'s two arrays, a <c>Queue</c>'s ring in two segments and the chunks of nodes the gather walks
    /// (<see cref="NinoGather"/>), each with an element and fields of its own. An element holding references is
    /// compiled once for every class it holds, and there the fields' accessor is a call: a set of such values takes the
    /// run of its values instead (<see cref="NinoStorage.Items"/>), the values at their offset, the entry's stride, and
    /// a dictionary of such entries an element formatter of its own that reads the entry's fields (NinoDictionary's
    /// <c>Entries</c>), composed by the same pair helpers.
    /// </summary>
    internal struct NinoStorageFormatter<TElement, TA, TFormatterA, TFields> : INinoFormatter<TElement>
        where TElement : struct
        where TFormatterA : struct, INinoFormatter<TA>
        where TFields : struct, INinoStorageFields<TElement, TA>
    {
        // The item's wire bytes, -1 where it has no fixed size or the element holds references (code shared over a
        // class reads no field of this class).
        private static readonly int Size = RuntimeHelpers.IsReferenceOrContainsReferences<TElement>()
            ? -1
            : NinoBlocks.ItemSize<TA, TFormatterA>();

        private static bool Fixed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !RuntimeHelpers.IsReferenceOrContainsReferences<TElement>() && Size > 0;
        }

        int INinoFormatter.BodyTraits => NinoBodyTraits.Nests | NinoBlocks.Dispatches<TFormatterA>();

        int INinoFormatter<TElement>.FixedSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Fixed ? Size : -1;
        }

        NinoBlockMap INinoFormatter<TElement>.BlockMap()
        {
            if (!Fixed)
                return null;
            TElement element = default;
            return new NinoBlockMap().Item<TA, TFormatterA>(
                NinoStorage.Offset(ref element, ref default(TFields).A(ref element)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<TElement>.WriteFixed(in TElement value, ref byte destination) =>
            NinoBlocks.WriteItem<TA, TFormatterA>(default(TFields).A(ref Unsafe.AsRef(in value)), ref destination);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<TElement>.ReadBlock(ref TElement value, ref byte source) =>
            NinoBlocks.ReadItem<TA, TFormatterA>(ref default(TFields).A(ref value), ref source);

        bool INinoFormatter.HasCursor => NinoCursor.Has<TA, TFormatterA>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoFormatter<TElement>.MaxSize(TElement value) =>
            NinoCursor.MaxSize<TA, TFormatterA>(in default(TFields).A(ref value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoFormatter<TElement>.WriteAt(ref byte destination, TElement value) =>
            NinoCursor.WriteAt<TA, TFormatterA>(ref destination, in default(TFields).A(ref value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoFormatter<TElement>.ReadAt(ref byte source, int available, ref TElement value) =>
            NinoCursor.ReadAt<TA, TFormatterA>(ref source, available, ref default(TFields).A(ref value));

        // A polymorphic item: its generated loop over the run of the items.
        bool INinoFormatter.HasRunLoop => !typeof(TA).IsValueType && NinoKind<TFormatterA>.Runs;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter.WriteRun(NinoRun run, ref Writer writer) => WriteRun(run, ref writer);

        /// <summary>
        /// <see cref="INinoFormatter.WriteRun"/> of the elements of <paramref name="run"/> (not empty): the item's
        /// loop over the run of the items. A static method, which code shared over a class inlines.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRun(NinoRun run, ref Writer writer) =>
            default(TFormatterA).WriteRun(NinoStorage.Items(run,
                ref Unsafe.As<TA, byte>(ref default(TFields).A(ref Unsafe.As<byte, TElement>(ref run.First)))),
                ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TElement value, ref Writer writer)
        {
            if (Fixed)
            {
                NinoBlocks.WriteItem<TA, TFormatterA>(default(TFields).A(ref value), ref writer.Reserve(Size));
                return;
            }

            // The item is the collection's element: written as one (a slot where its kind is slots, the bytes a run
            // of them writes for it).
            NinoElements.WriteOne<TA, TFormatterA>(default(TFields).A(ref value), ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref TElement value) =>
            NinoElements.ReadOne<TA, TFormatterA>(ref reader, ref default(TFields).A(ref value));
    }

    /// <summary>
    /// A storage element of two items, <typeparamref name="TA"/> then <typeparamref name="TB"/>, at the offsets
    /// <typeparamref name="TFields"/> reaches, the rest of its stride skipped: <see
    /// cref="NinoStorageFormatter{TElement,TA,TFormatterA,TFields}"/> of a pair, composed as
    /// <see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/> composes its key and value (the
    /// same bytes, cursor form, dispatch bit, and run of a polymorphic second item with its first written by that
    /// loop). The element of a <c>Dictionary</c>'s run of entries, for every key and value: a block where both items
    /// are raw or blocks, whatever this runtime moves between them (CoreCLR moves references first: a
    /// <c>Dictionary&lt;string, int&gt;</c>'s entry has its key at 0, hash code 8, link 12, value 16).
    /// </summary>
    internal struct NinoStorageFormatter<TElement, TA, TB, TFormatterA, TFormatterB, TFields> : INinoFormatter<TElement>
        where TElement : struct
        where TFormatterA : struct, INinoFormatter<TA>
        where TFormatterB : struct, INinoFormatter<TB>
        where TFields : struct, INinoStorageFields<TElement, TA, TB>
    {
        // The items' wire bytes (-1: of no fixed size, or the element holds references), and the element's: their
        // sum, or -1.
        private static readonly int SizeA = RuntimeHelpers.IsReferenceOrContainsReferences<TElement>()
            ? -1
            : NinoBlocks.ItemSize<TA, TFormatterA>();

        private static readonly int SizeB = SizeA < 0 ? -1 : NinoBlocks.ItemSize<TB, TFormatterB>();
        private static readonly int Size = SizeA < 0 || SizeB < 0 ? -1 : SizeA + SizeB;

        private static bool Fixed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !RuntimeHelpers.IsReferenceOrContainsReferences<TElement>() && Size > 0;
        }

        int INinoFormatter.BodyTraits =>
            NinoBodyTraits.Nests | NinoBlocks.Dispatches<TFormatterA>() | NinoBlocks.Dispatches<TFormatterB>();

        int INinoFormatter<TElement>.FixedSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Fixed ? Size : -1;
        }

        NinoBlockMap INinoFormatter<TElement>.BlockMap()
        {
            if (!Fixed)
                return null;
            TElement element = default;
            return new NinoBlockMap()
                .Item<TA, TFormatterA>(NinoStorage.Offset(ref element, ref default(TFields).A(ref element)))
                .Item<TB, TFormatterB>(NinoStorage.Offset(ref element, ref default(TFields).B(ref element)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<TElement>.WriteFixed(in TElement value, ref byte destination)
        {
            ref TElement element = ref Unsafe.AsRef(in value);
            NinoBlocks.WriteItem<TA, TFormatterA>(default(TFields).A(ref element), ref destination);
            NinoBlocks.WriteItem<TB, TFormatterB>(default(TFields).B(ref element), ref Unsafe.Add(ref destination, SizeA));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<TElement>.ReadBlock(ref TElement value, ref byte source)
        {
            NinoBlocks.ReadItem<TA, TFormatterA>(ref default(TFields).A(ref value), ref source);
            NinoBlocks.ReadItem<TB, TFormatterB>(ref default(TFields).B(ref value), ref Unsafe.Add(ref source, SizeA));
        }

        bool INinoFormatter.HasCursor => NinoCursor.Has<TA, TB, TFormatterA, TFormatterB>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoFormatter<TElement>.MaxSize(TElement value) =>
            NinoCursor.MaxSize<TA, TB, TFormatterA, TFormatterB>(in default(TFields).A(ref value),
                in default(TFields).B(ref value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoFormatter<TElement>.WriteAt(ref byte destination, TElement value) =>
            NinoCursor.WriteAt<TA, TB, TFormatterA, TFormatterB>(ref destination, in default(TFields).A(ref value),
                in default(TFields).B(ref value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoFormatter<TElement>.ReadAt(ref byte source, int available, ref TElement value) =>
            NinoCursor.ReadAt<TA, TB, TFormatterA, TFormatterB>(ref source, available,
                ref default(TFields).A(ref value), ref default(TFields).B(ref value));

        bool INinoFormatter.HasRunLoop => NinoRuns.PairRuns<TA, TB, TFormatterA, TFormatterB>();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter.WriteRun(NinoRun run, ref Writer writer) => WriteRun(run, ref writer);

        /// <summary>
        /// <see cref="INinoFormatter.WriteRun"/> of the elements of <paramref name="run"/> (not empty,
        /// <see cref="NinoRuns.PairRuns{TKey,TValue,TKeyFormatter,TValueFormatter}"/>): the second item's loop over
        /// the run of the second items, each after its first. A static method, which code shared over a class inlines
        /// (a dictionary's top layer calls it), with the offsets taken from the first element's references, so
        /// nothing is looked up there either.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRun(NinoRun run, ref Writer writer)
        {
            ref TElement first = ref Unsafe.As<byte, TElement>(ref run.First);
            NinoRuns.WritePairRun<TA, TB, TFormatterA, TFormatterB>(run,
                NinoStorage.Distance(ref run.First, ref Unsafe.As<TB, byte>(ref default(TFields).B(ref first))),
                NinoStorage.Distance(ref run.First, ref Unsafe.As<TA, byte>(ref default(TFields).A(ref first))),
                ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TElement value, ref Writer writer)
        {
            if (Fixed)
            {
                ref byte destination = ref writer.Reserve(Size);
                NinoBlocks.WriteItem<TA, TFormatterA>(default(TFields).A(ref value), ref destination);
                NinoBlocks.WriteItem<TB, TFormatterB>(default(TFields).B(ref value),
                    ref Unsafe.Add(ref destination, SizeA));
                return;
            }

            default(TFormatterA).Serialize(default(TFields).A(ref value), ref writer);
            default(TFormatterB).Serialize(default(TFields).B(ref value), ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref TElement value)
        {
            default(TFormatterA).Deserialize(ref reader, ref default(TFields).A(ref value));
            default(TFormatterB).Deserialize(ref reader, ref default(TFields).B(ref value));
        }
    }

    /// <summary>
    /// An item moved as its memory, every byte of it, whatever it holds: a block for a caller that makes the wire's
    /// bytes out of the memory after a write, and checks them before a read (a set's slots whose memory is their wire
    /// form, <see cref="INinoFormatter{T}.SlotsAreMemory"/>: written over themselves, their presence bytes checked
    /// first). The item of a storage element whose items are moved without their own formatter's look.
    /// </summary>
    internal struct NinoMemoryItem<T> : INinoFormatter<T>
    {
        public int FixedSize => Unsafe.SizeOf<T>();

        public NinoBlockMap BlockMap() => new NinoBlockMap().Bytes(0, 0, Unsafe.SizeOf<T>());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteFixed(in T value, ref byte destination) => NinoUnsafe.Write(ref destination, value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadBlock(ref T value, ref byte source) => value = NinoUnsafe.Read<T>(ref source);

        public void Serialize(T value, ref Writer writer) => throw new NotSupportedException();
        public void Deserialize(ref Reader reader, ref T value) => throw new NotSupportedException();
    }
}
