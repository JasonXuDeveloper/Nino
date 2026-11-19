using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The cursor form of a value's read and what takes it (#347): strings in the default encoding and UTF-8, raw
    /// values and blocks read as one (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/>), and pairs of them with a
    /// string among them, read from a reference and the bytes left rather than through the reader, so a loop over many
    /// values holds its data, position and end in registers. A run of them is moved by the string run kernels
    /// (<see cref="NinoStringRuns"/>, #355), its items told apart here by JIT-time constants: a string by its
    /// formatter's type, never a formatter member, which in code shared over a class would be an indirect call.
    /// </summary>
    internal static class NinoCursor
    {
        /// <summary>
        /// Whether <typeparamref name="TFormatter"/>'s values are moved by the string run kernels: strings, or a
        /// composite whose items are (<see cref="INinoFormatter.HasCursor"/>, <see cref="NinoKind{TFormatter}.Cursor"/>:
        /// a pair, <see cref="KeyValuePairFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>). A JIT-time
        /// constant everywhere. The member is internal, so of the formatters of classes only Nino.Core's two string
        /// formatters have it, and those are told by their types:
        /// in code shared over a class, a field of <see cref="NinoKind{TFormatter}"/> is read at run time, even of a
        /// formatter that is not shared, and every collection of strings then compiled the cursor loop, the per-value
        /// loop and its local writer's loop side by side (asm.py: <c>ArrayFormatter</c> of strings 914 to 1,439 bytes,
        /// a generic class's 849 to 1,470). Value types (pairs) read the field only where their kind is per value: a
        /// raw, slot or block collection returns before any read of it, so the field was not initialized when Tier1
        /// compiled the method and stayed a helper call there, with the cursor loop compiled in (asm.py:
        /// <c>HashSet&lt;NinoNullable&lt;int&gt;&gt;</c> 811 to 1,174 bytes). A value without references holds no
        /// string, so it reads nothing: a set's or a dictionary's entries ask this of the entry.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Own<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            typeof(T).IsValueType
                ? RuntimeHelpers.IsReferenceOrContainsReferences<T>() &&
                  NinoKind.Of<T, TFormatter>() == NinoElementKind.PerValue && NinoKind<TFormatter>.Cursor
                : typeof(TFormatter) == typeof(StringFormatter) || typeof(TFormatter) == typeof(Utf8StringFormatter);

        /// <summary>
        /// Reads into <paramref name="value"/> (a string, or a value read as one) the value at
        /// <paramref name="source"/> (<paramref name="available"/> bytes), as its formatter's <c>Deserialize</c> reads
        /// it, with its checks: too few bytes and values no write makes are invalid data. Returns the bytes read. An
        /// item of a pair merged into an existing dictionary (NinoDictionary.Refill), read apart.
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

            // A string, told by its formatter's type (a constant in code shared over a class too).
            ref string slot = ref Unsafe.As<T, string>(ref value);
            string current = slot, read;
            int bytes;
            if (typeof(TFormatter) == typeof(Utf8StringFormatter))
            {
                SizedString utf8 = Reader.ReadUtf8At(ref source, available, current);
                read = utf8.Value;
                bytes = utf8.Size;
            }
            else
                read = NinoStrings.Read(ref source, available, current, out bytes);

            // An unchanged string (a read into an existing collection) is not stored again: no write barrier.
            if (!ReferenceEquals(read, current))
                slot = read;
            return bytes;
        }

        /// <summary>
        /// Whether pairs of the items are moved by the string run kernels
        /// (<see cref="NinoStringRuns.WritePairs{TA,TB,TFormatterA,TFormatterB}"/>): one item a string, the other a
        /// string or a value read as one (pairs of raw values and blocks are blocks). A JIT-time constant, where the
        /// pair's own formatter's <see cref="INinoFormatter.HasCursor"/> is read through a field.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Pairs<TA, TB, TFormatterA, TFormatterB>()
            where TFormatterA : struct, INinoFormatter<TA>
            where TFormatterB : struct, INinoFormatter<TB> =>
            // Each item asked once on the path a constant takes: every inlinee counts against the budget of the
            // generated body a dictionary's write is inlined into.
            NinoKind<TFormatterA>.Strings
                ? NinoKind<TFormatterB>.Strings || NinoRuns.ScattersItems<TB, TFormatterB>()
                : NinoKind<TFormatterB>.Strings && NinoRuns.ScattersItems<TA, TFormatterA>();
    }
}
