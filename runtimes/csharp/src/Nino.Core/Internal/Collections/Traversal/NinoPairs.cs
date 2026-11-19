using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// How a composite of two items composes what its items do on a run (#355): a <c>KeyValuePair</c>, a value tuple of
    /// two, a dictionary's entry (<see cref="NinoMapEntryFormatter{TKey,TValue,TKeyFormatter,TValueFormatter}"/>), each
    /// item <typeparamref name="TA"/> then <typeparamref name="TB"/> at its offset in the composite (-1: not known on
    /// this runtime, and the composite goes value by value). Written once for every such composite, which forwards its
    /// capability members here with its own offsets, so <see cref="NinoRuns"/>' one write and read move a run of any of
    /// them the same way, never by a rule of the composite's own:
    /// <list type="bullet">
    /// <item>the cursor form: a string among the items and the other a string or a value read as one, the run moved by
    /// the string run kernels (<see cref="NinoStringRuns"/>, one call per run);</item>
    /// <item>the run loop: a polymorphic second item whose base has a generated loop, the first written by that loop
    /// (<see cref="NinoRuns.KeyOf{TKey,TKeyFormatter}"/>): the one place an item is written apart from the other;</item>
    /// <item>recycling: a composite dropped by a collection recycles its items (<see cref="NinoRecycler"/>).</item>
    /// </list>
    /// </summary>
    internal static class NinoPairs<TA, TB, TFormatterA, TFormatterB>
        where TFormatterA : struct, INinoFormatter<TA>
        where TFormatterB : struct, INinoFormatter<TB>
    {
        /// <summary>Whether a run of the composites is moved by the string run kernels (<see cref="INinoFormatter.HasCursor"/>).</summary>
        public static bool Cursor(int a, int b) => NinoCursor.Pairs<TA, TB, TFormatterA, TFormatterB>() && a >= 0 && b >= 0;

        /// <summary><see cref="INinoFormatter.WriteStrings"/> of composites whose items are at <paramref name="a"/> and
        /// <paramref name="b"/>.</summary>
        public static Writer WriteStrings(scoped NinoRun run, int a, int b, Writer writer) =>
            NinoStringRuns.WritePairs<TA, TB, TFormatterA, TFormatterB>(run, a, b, writer);

        /// <summary><see cref="INinoFormatter.ReadStrings"/> of composites whose items are at <paramref name="a"/> and
        /// <paramref name="b"/>, in place.</summary>
        public static int ReadStrings(scoped NinoRun run, int a, int b, ref byte data, int end) =>
            NinoStringRuns.ReadPairs<TA, TB, TFormatterA, TFormatterB, NinoStringRuns.InPlace>(run, a, b, ref data, end,
                default);

        /// <summary>
        /// Whether a run of the composites is written in one loop (<see cref="INinoFormatter.HasRunLoop"/>): the
        /// second item is a polymorphic base's with a run loop, and the first is written by that loop. A JIT-time
        /// constant where the offsets are.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool RunLoop(int a, int b) =>
            !typeof(TB).IsValueType && NinoKind<TFormatterB>.Runs &&
            NinoRuns.KeyOf<TA, TFormatterA>() != NinoRun.NoKey && a >= 0 && b >= 0;

        /// <summary>
        /// Writes a run of the composites (<see cref="RunLoop"/>; the count is the caller's): the second item's
        /// generated loop over the run of the second items (<see cref="NinoRun.Items"/>), each written after its first.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRun(NinoRun run, int a, int b, ref Writer writer) =>
            default(TFormatterB).WriteRun(run.Items(b, NinoRuns.KeyOf<TA, TFormatterA>() + ((a - b) << 8)), ref writer);

        /// <summary>
        /// Whether a dropped composite has an item to recycle (<see cref="INinoFormatter{T}.ItemsMayRecycle"/>): asked
        /// as each collection asks its elements, so a pool made later is seen.
        /// </summary>
        public static bool MayRecycle
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NinoRecycler.MayRecycle<TA, TFormatterA>() || NinoRecycler.MayRecycle<TB, TFormatterB>();
        }

        /// <summary>Recycles the items of a dropped composite, each once (<see cref="INinoFormatter{T}.RecycleItems"/>).</summary>
        public static void Recycle(TA a, TB b)
        {
            NinoRecycler.Recycle<TA, TFormatterA>(a);
            NinoRecycler.Recycle<TB, TFormatterB>(b);
        }
    }
}
