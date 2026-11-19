using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The cursor form of a value's write and read (#347): written at a reference into free space after its room is
    /// checked (<see cref="MaxSize{T,TFormatter}"/>, <see cref="WriteAt{T,TFormatter}"/>), read from a reference and the
    /// bytes left (<see cref="ReadAt{T,TFormatter}"/>), each returning the bytes it took. A loop over many values
    /// (<see cref="NinoRuns.WriteCursors{T,TFormatter}(NinoRun, Writer)"/>,
    /// <see cref="NinoRuns.ReadCursors{T,TFormatter,TStore}"/>) then holds three values, its data, position and end, in
    /// registers, where through the writer or the reader every value would load and store their fields. One decision
    /// per value, folded by the JIT: raw values and blocks read as one (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/>)
    /// through their kind, with the checks a read of them makes; formatters with <see cref="INinoFormatter.HasCursor"/>
    /// (strings in the default encoding and UTF-8, and composites whose items have it: a pair, a dictionary's entry,
    /// composed by the pair helpers here) through their members, a constrained call. Other values have no cursor form:
    /// their loops go through the writer and the reader, value by value.
    /// </summary>
    internal static class NinoCursor
    {
        /// <summary>Whether <typeparamref name="T"/>'s values have the cursor form: a JIT-time constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Has<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoRuns.ScattersItems<T, TFormatter>() || Own<T, TFormatter>();

        /// <summary>
        /// Whether <typeparamref name="TFormatter"/> has the cursor form itself (<see cref="INinoFormatter.HasCursor"/>,
        /// <see cref="NinoKind{TFormatter}.Cursor"/>): a JIT-time constant everywhere. The member is internal, so of
        /// the formatters of classes only Nino.Core's two string formatters have it, and those are told by their types:
        /// in code shared over a class, a field of <see cref="NinoKind{TFormatter}"/> is read at run time, even of a
        /// formatter that is not shared, and every collection of strings then compiled the cursor loop, the per-value
        /// loop and its local writer's loop side by side (asm.py: <c>ArrayFormatter</c> of strings 914 to 1,439 bytes,
        /// a generic class's 849 to 1,470). Value types (pairs) read the field only where their kind is per value: a
        /// raw, slot or block collection returns before any read of it, so the field was not initialized when Tier1
        /// compiled the method and stayed a helper call there, with the cursor loop compiled in (asm.py:
        /// <c>HashSet&lt;NinoNullable&lt;int&gt;&gt;</c> 811 to 1,174 bytes).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Own<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            typeof(T).IsValueType
                ? NinoKind.Of<T, TFormatter>() == NinoElementKind.PerValue && NinoKind<TFormatter>.Cursor
                : typeof(TFormatter) == typeof(StringFormatter) || typeof(TFormatter) == typeof(Utf8StringFormatter);

        /// <summary>The most bytes <see cref="WriteAt{T,TFormatter}"/> writes for <paramref name="value"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MaxSize<T, TFormatter>(in T value) where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoRuns.ScattersItems<T, TFormatter>())
                return NinoKind.Stride<T, TFormatter>();
            return default(TFormatter).MaxSize(value);
        }

        /// <summary>
        /// Writes <paramref name="value"/> at <paramref name="destination"/> (room for
        /// <see cref="MaxSize{T,TFormatter}"/> bytes) as its formatter's <c>Serialize</c> writes it. Returns the bytes
        /// written.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WriteAt<T, TFormatter>(ref byte destination, in T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoRuns.ScattersItems<T, TFormatter>())
            {
                NinoBlocks.WriteItem<T, TFormatter>(in value, ref destination);
                return NinoKind.Stride<T, TFormatter>();
            }

            return default(TFormatter).WriteAt(ref destination, value);
        }

        /// <summary>
        /// Reads into <paramref name="value"/> the value at <paramref name="source"/> (<paramref name="available"/>
        /// bytes), as its formatter's <c>Deserialize</c> reads it, with its checks: too few bytes and values no write
        /// makes are invalid data. Returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadAt<T, TFormatter>(ref byte source, int available, ref T value)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (NinoRuns.ScattersItems<T, TFormatter>())
            {
                int size = NinoKind.Stride<T, TFormatter>();
                if ((uint)available < (uint)size)
                    NinoThrow.EndOfData();
                NinoBlocks.ReadItem<T, TFormatter>(ref value, ref source);
                return size;
            }

            return default(TFormatter).ReadAt(ref source, available, ref value);
        }

        /// <summary>
        /// The cursor form of a pair of items, <typeparamref name="TA"/> then <typeparamref name="TB"/>: whether it has
        /// one (both items have it). What a composite of two items (<see cref="KeyValuePairFormatter{TKey,TValue,
        /// TKeyFormatter,TValueFormatter}"/>, a dictionary's entry) answers for <see cref="INinoFormatter.HasCursor"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Has<TA, TB, TFormatterA, TFormatterB>()
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            Has<TA, TFormatterA>() && Has<TB, TFormatterB>();

        /// <summary>
        /// Whether pairs of the items take the cursor form in a collection (<see cref="Has{TA,TB,TFormatterA,
        /// TFormatterB}"/>, one item a formatter's own: a string; pairs of raw values and blocks are blocks). A JIT-time
        /// constant, where the pair's own formatter's <see cref="INinoFormatter.HasCursor"/> is read through a field.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pairs<TA, TB, TFormatterA, TFormatterB>()
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            (Own<TA, TFormatterA>() || Own<TB, TFormatterB>()) && Has<TA, TB, TFormatterA, TFormatterB>();

        /// <summary>The most bytes <see cref="WriteAt{TA,TB,TFormatterA,TFormatterB}"/> writes (negative: more than an
        /// <c>int</c> holds, which the writer rejects).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int MaxSize<TA, TB, TFormatterA, TFormatterB>(in TA a, in TB b)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            int first = MaxSize<TA, TFormatterA>(in a);
            int second = MaxSize<TB, TFormatterB>(in b);
            return (uint)first + (ulong)(uint)second > int.MaxValue ? -1 : first + second;
        }

        /// <summary>Writes <paramref name="a"/>, then <paramref name="b"/>, in their cursor form; returns the bytes
        /// written.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int WriteAt<TA, TB, TFormatterA, TFormatterB>(ref byte destination, in TA a, in TB b)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            int size = WriteAt<TA, TFormatterA>(ref destination, in a);
            return size + WriteAt<TB, TFormatterB>(ref Unsafe.Add(ref destination, (nint)(uint)size), in b);
        }

        /// <summary>Reads <paramref name="a"/>, then <paramref name="b"/>, in their cursor form, each in place; returns
        /// the bytes read.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadAt<TA, TB, TFormatterA, TFormatterB>(ref byte source, int available, ref TA a, ref TB b)
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB>
        {
            int size = ReadAt<TA, TFormatterA>(ref source, available, ref a);
            return size + ReadAt<TB, TFormatterB>(ref Unsafe.Add(ref source, (nint)(uint)size), available - size,
                ref b);
        }

        /// <summary>
        /// What <see cref="NinoRuns.ReadCursors{T,TFormatter,TStore}"/> does with each value it read: nothing, where it
        /// read it in place (<see cref="InPlace{T}"/>: an array's, a list's, a new set's or a new dictionary's entries,
        /// filled then linked), or what a collection that takes its values one at a time does with it (a dictionary's
        /// adds, which reads each pair into one pair of locals, the run of the loop, and adds its key and value:
        /// the dictionary's own layer).
        /// </summary>
        internal interface IStore<T>
        {
            /// <summary>Takes the value read at <paramref name="value"/> (the loop reads the next into the next
            /// entry, or here again).</summary>
            void Stored(ref T value);
        }

        /// <summary>The values read in place: nothing more.</summary>
        internal struct InPlace<T> : IStore<T>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Stored(ref T value)
            {
            }
        }
    }
}
