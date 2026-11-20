using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// A <c>HashSet</c>'s entry as the element of its run (#355), the storage element of one item: a struct of
    /// <c>Unsafe.SizeOf&lt;SetEntry&lt;T&gt;&gt;()</c> bytes (the stride of its run) whose wire bytes are its value's,
    /// written by <typeparamref name="TFormatter"/> as that formatter writes it alone (a slot where its kind is slots,
    /// <see cref="NinoElements.WriteOne{T,TFormatter}"/>); every other byte (the hash code, the link, padding) is
    /// skipped on write and left 0 by a block read (the set's top layer links the entries after). Composed as a value
    /// tuple is: a block where its value is raw or a block (<see cref="NinoBlocks.ItemSize{TItem,TFormatter}"/>), its
    /// <see cref="BlockMap"/> the value's at its offset in the entry, so the block kernels
    /// (<see cref="NinoBlockPlan{T,TFormatter}"/>: groups, lanes, 64-byte permutes, the netstandard2.1 build's registered
    /// kernels) pack the values out of a run of entries and scatter them back with no code of their own; else its run
    /// loop and its formatter, through the entry. The entry's fields are read as fields, never through a struct type
    /// parameter: in code shared over a class (an entry holding a reference), a method of such a struct is an
    /// indirect call the JIT never inlines (two per pair in a dictionary's write, 1.9 times as long, #426). A set of
    /// values with references takes the run of its values instead (<see cref="NinoRun.Items"/>).
    /// </summary>
    internal struct NinoSetEntryFormatter<T, TFormatter> : INinoFormatter<CollectionFields.SetEntry<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        // The value's wire bytes, -1 where it has no fixed size or holds references (code shared over a class reads no
        // field of this class).
        private static readonly int Size = RuntimeHelpers.IsReferenceOrContainsReferences<T>()
            ? -1
            : NinoBlocks.ItemSize<T, TFormatter>();

        private static bool Fixed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !RuntimeHelpers.IsReferenceOrContainsReferences<T>() && Size > 0;
        }

        int INinoFormatter.BodyTraits => NinoBodyTraits.Nests | NinoBlocks.Dispatches<TFormatter>();

        int INinoFormatter<CollectionFields.SetEntry<T>>.FixedSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Fixed ? Size : -1;
        }

        NinoBlockMap INinoFormatter<CollectionFields.SetEntry<T>>.BlockMap() =>
            Fixed ? new NinoBlockMap().Item<T, TFormatter>(CollectionFields.SetEntry<T>.ValueOffset) : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<CollectionFields.SetEntry<T>>.WriteFixed(in CollectionFields.SetEntry<T> value,
            ref byte destination) => NinoBlocks.WriteItem<T, TFormatter>(value.Value, ref destination);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<CollectionFields.SetEntry<T>>.ReadBlock(ref CollectionFields.SetEntry<T> value,
            ref byte source) => NinoBlocks.ReadItem<T, TFormatter>(ref value.Value, ref source);

        // A polymorphic value: its generated loop over the run of the values.
        bool INinoFormatter.HasRunLoop => !typeof(T).IsValueType && NinoKind<TFormatter>.Runs;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter.WriteRun(NinoRun run, ref Writer writer) =>
            default(TFormatter).WriteRun(run.Items(CollectionFields.SetEntry<T>.ValueOffset), ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(CollectionFields.SetEntry<T> value, ref Writer writer)
        {
            if (Fixed)
            {
                NinoBlocks.WriteItem<T, TFormatter>(value.Value, ref writer.Reserve(Size));
                return;
            }

            // The value is the collection's element: written as one (a slot where its kind is slots, the bytes a run
            // of them writes for it).
            NinoElements.WriteOne<T, TFormatter>(value.Value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref CollectionFields.SetEntry<T> value) =>
            NinoElements.ReadOne<T, TFormatter>(ref reader, ref value.Value);
    }

    /// <summary>
    /// A <c>Dictionary</c>'s entry as the element of its run (#355), the storage element of two items, the key then
    /// the value, wherever this runtime put them (CoreCLR moves references first: a <c>Dictionary&lt;string,
    /// int&gt;</c>'s entry has its key at 0, hash code 8, link 12, value 16), the rest of its stride skipped: composed
    /// as <see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/> composes a pair (the same bytes,
    /// block, dispatch bit, and run of a polymorphic value with its key written by that loop,
    /// <see cref="NinoItems"/>), for every key and value: a block where both are raw or
    /// blocks, which the block kernels move skipping the hash code and the link; else the two formatters, the entry's
    /// fields read as fields (see <see cref="NinoSetEntryFormatter{T,TFormatter}"/>), also in code shared over a class.
    /// </summary>
    internal struct NinoMapEntryFormatter<TKey, TValue, TKeyFormatter, TValueFormatter>
        : INinoFormatter<CollectionFields.MapEntry<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueFormatter : struct, INinoFormatter<TValue>
    {
        // The items' wire bytes (-1: of no fixed size, or the entry holds references), and the entry's: their sum, or
        // -1.
        private static readonly int KeySize =
            RuntimeHelpers.IsReferenceOrContainsReferences<CollectionFields.MapEntry<TKey, TValue>>()
                ? -1
                : NinoBlocks.ItemSize<TKey, TKeyFormatter>();

        private static readonly int ValueSize = KeySize < 0 ? -1 : NinoBlocks.ItemSize<TValue, TValueFormatter>();
        private static readonly int Size = KeySize < 0 || ValueSize < 0 ? -1 : KeySize + ValueSize;

        private static bool Fixed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !RuntimeHelpers.IsReferenceOrContainsReferences<CollectionFields.MapEntry<TKey, TValue>>() &&
                   Size > 0;
        }

        int INinoFormatter.BodyTraits =>
            NinoBodyTraits.Nests | NinoBlocks.Dispatches<TKeyFormatter>() | NinoBlocks.Dispatches<TValueFormatter>();

        int INinoFormatter<CollectionFields.MapEntry<TKey, TValue>>.FixedSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Fixed ? Size : -1;
        }

        NinoBlockMap INinoFormatter<CollectionFields.MapEntry<TKey, TValue>>.BlockMap() => Fixed
            ? new NinoBlockMap()
                .Item<TKey, TKeyFormatter>(CollectionFields.MapEntry<TKey, TValue>.KeyOffset)
                .Item<TValue, TValueFormatter>(CollectionFields.MapEntry<TKey, TValue>.ValueOffset)
            : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<CollectionFields.MapEntry<TKey, TValue>>.WriteFixed(
            in CollectionFields.MapEntry<TKey, TValue> value, ref byte destination)
        {
            NinoBlocks.WriteItem<TKey, TKeyFormatter>(value.Key, ref destination);
            NinoBlocks.WriteItem<TValue, TValueFormatter>(value.Value, ref Unsafe.Add(ref destination, KeySize));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter<CollectionFields.MapEntry<TKey, TValue>>.ReadBlock(
            ref CollectionFields.MapEntry<TKey, TValue> value, ref byte source)
        {
            NinoBlocks.ReadItem<TKey, TKeyFormatter>(ref value.Key, ref source);
            NinoBlocks.ReadItem<TValue, TValueFormatter>(ref value.Value, ref Unsafe.Add(ref source, KeySize));
        }

        bool INinoFormatter.HasRunLoop => NinoItems.RunLoop<TKey, TValue, TKeyFormatter, TValueFormatter>(
            CollectionFields.MapEntry<TKey, TValue>.KeyOffset, CollectionFields.MapEntry<TKey, TValue>.ValueOffset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoFormatter.WriteRun(NinoRun run, ref Writer writer) => WriteRun(run, ref writer);

        /// <summary>
        /// <see cref="INinoFormatter.WriteRun"/> of the entries of <paramref name="run"/> (not empty,
        /// <see cref="NinoItems.RunLoop{TA,TB,TFormatterA,TFormatterB}"/>): the value's loop over the run of
        /// the values, each after its key. A static method, which code shared over a class inlines (a dictionary's top
        /// layer calls it), its offsets the entry's fields', constants there too.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRun(NinoRun run, ref Writer writer) =>
            NinoItems.WriteRun<TKey, TValue, TKeyFormatter, TValueFormatter>(run,
                CollectionFields.MapEntry<TKey, TValue>.KeyOffset, CollectionFields.MapEntry<TKey, TValue>.ValueOffset,
                ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(CollectionFields.MapEntry<TKey, TValue> value, ref Writer writer)
        {
            if (Fixed)
            {
                ref byte destination = ref writer.Reserve(Size);
                NinoBlocks.WriteItem<TKey, TKeyFormatter>(value.Key, ref destination);
                NinoBlocks.WriteItem<TValue, TValueFormatter>(value.Value, ref Unsafe.Add(ref destination, KeySize));
                return;
            }

            default(TKeyFormatter).Serialize(value.Key, ref writer);
            default(TValueFormatter).Serialize(value.Value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref CollectionFields.MapEntry<TKey, TValue> value)
        {
            default(TKeyFormatter).Deserialize(ref reader, ref value.Key);
            default(TValueFormatter).Deserialize(ref reader, ref value.Value);
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
