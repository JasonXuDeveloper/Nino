using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// How a collection moves the elements of a value type without references: one path per kind, the same in every
    /// collection, decided once by <see cref="NinoKind{T,TFormatter}"/> in the order the members are listed (#355).
    /// Elements with references are always <see cref="PerValue"/>; what else they do is the formatter's
    /// (<see cref="NinoKind{TFormatter}"/>: strings, polymorphic loops, prefetch).
    /// </summary>
    internal enum NinoElementKind : byte
    {
        /// <summary>One value at a time through the formatter's <c>Serialize</c> and <c>Deserialize</c>, a constrained
        /// call per element.</summary>
        PerValue,

        /// <summary>
        /// Values written as their memory: one block, copied by the SIMD kernels. Only where this runtime's layout of
        /// the type is its wire layout, by Nino's one layout check (<see cref="RawFormatter{T,TFormatter}"/>: an
        /// <see cref="INinoRawFormatter"/> whose <c>NinoLayout</c> is canonical here); being unmanaged is not enough,
        /// and a type laid out otherwise on this runtime is a block (its BlockMap) or per value.
        /// <see cref="NinoNullable{T}"/> is one.
        /// </summary>
        Raw,

        /// <summary>Slots of a fixed size (<see cref="INinoFormatter{T}.SlotStride"/>: a raw <c>T?</c>): one
        /// block, written and read by the slot kernels.</summary>
        Slot,

        /// <summary>
        /// Block formatters (<see cref="INinoFormatter{T}.BlockMap"/>: <see cref="DateTimeOffset"/>, value tuples,
        /// pairs and generated structs whose items are raw or blocks, composed by
        /// <see cref="NinoBlockMap.Item{TItem,TFormatter}"/>): one block, each value moved by the block kernels (one
        /// copy where the block is the value's memory here, <see cref="NinoBlockPlan{T,TFormatter}.IsMemory"/>).
        /// </summary>
        Block,
    }

    /// <summary>The element kind of <see cref="NinoKind{T,TFormatter}"/>, read where code may be shared.</summary>
    internal static class NinoKind
    {
        /// <summary>
        /// The kind of <typeparamref name="T"/>'s elements under <typeparamref name="TFormatter"/>: a JIT-time
        /// constant, <see cref="NinoElementKind.PerValue"/> without a lookup for values with references (whose code
        /// may be shared over a class).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoElementKind Of<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoUnsafe.IsReferenceOrContainsReferences<T>()
                ? NinoElementKind.PerValue
                : NinoKind<T, TFormatter>.Kind;

        /// <summary>
        /// The wire bytes of each element whose kind moves them as one block (raw, slot, block), else -1. A JIT-time
        /// constant; -1 without a lookup for values with references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Stride<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoUnsafe.IsReferenceOrContainsReferences<T>() ? -1 : NinoKind<T, TFormatter>.Stride;
    }

    /// <summary>
    /// What <typeparamref name="TFormatter"/> says of every value, read once: its body traits, whether it writes
    /// strings, its cursor form, its run loop, its prefetch and its top-level writes. A formatter without a capability
    /// answers its query through the interface's default, boxed, so the queries are asked here once and the hot paths
    /// read these fields and call a member only where the formatter has it (a constrained call). Generic over the
    /// formatter alone, so a field folds where the code reading it is shared over a class element (whose formatter is
    /// still its exact struct), where a field of a class generic over the element would be a lookup per collection.
    /// Code asks it before any path that returns earlier, so the class is initialized when Tier1 compiles it.
    /// </summary>
    internal static class NinoKind<TFormatter> where TFormatter : struct, INinoFormatter
    {
        /// <summary><see cref="INinoFormatter.BodyTraits"/> (<see cref="NinoBodyTraits"/> flags).</summary>
        public static readonly int Traits = default(TFormatter).BodyTraits;

        /// <summary>
        /// Whether the formatter writes strings in the default encoding (<see cref="StringFormatter"/>): the keys a
        /// polymorphic value's generated loop writes itself (<see cref="NinoRun.WriteKey"/>). A <c>typeof</c>, a
        /// constant on every runtime (NativeAOT and IL2CPP too). Collections move strings through the string run
        /// kernels (<see cref="Strings"/>).
        /// </summary>
        public static bool IsString
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => typeof(TFormatter) == typeof(StringFormatter);
        }

        /// <summary>
        /// Whether the formatter writes strings, in the default encoding or UTF-8
        /// (<see cref="Utf8StringFormatter"/>): a run of them is moved by the string run kernels
        /// (<see cref="NinoStringRuns"/>), one call not generic over the element. A <c>typeof</c>, as
        /// <see cref="IsString"/>: a constant in code shared over a class too, where a field of this class is not.
        /// </summary>
        public static bool Strings
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => typeof(TFormatter) == typeof(StringFormatter) || typeof(TFormatter) == typeof(Utf8StringFormatter);
        }

        /// <summary>
        /// <see cref="INinoFormatter.HasCursor"/>: pairs whose runs the string run kernels move
        /// (<see cref="NinoCursor.Own{T,TFormatter}"/>).
        /// </summary>
        public static readonly bool Cursor = default(TFormatter).HasCursor;

        /// <summary><see cref="INinoFormatter.HasRunLoop"/>: the values of a run (<see cref="NinoRun"/>) in one
        /// generated loop.</summary>
        public static readonly bool Runs = default(TFormatter).HasRunLoop;

        /// <summary><see cref="INinoFormatter.HasSizedWrite"/>.</summary>
        public static readonly bool SizedWrite = default(TFormatter).HasSizedWrite;

        /// <summary><see cref="INinoFormatter.HasExternalWrite"/>.</summary>
        public static readonly bool ExternalWrite = default(TFormatter).HasExternalWrite;

        private static readonly int PrefetchBytes = default(TFormatter).PrefetchBytes;

        /// <summary><see cref="INinoFormatter.HasMemberPrefetch"/> of a formatter that prefetches: only then does
        /// <see cref="NinoPrefetch.Ahead{TFormatter}"/> call <see cref="INinoFormatter.PrefetchMembers"/>.</summary>
        public static readonly bool PrefetchMembers = PrefetchBytes > 0 && default(TFormatter).HasMemberPrefetch;

        /// <summary><see cref="NinoPrefetch.Threshold"/> of the formatter's
        /// <see cref="INinoFormatter.PrefetchBytes"/>.</summary>
        public static readonly int PrefetchThreshold = NinoPrefetch.Threshold(PrefetchBytes);

        /// <summary><see cref="NinoPrefetch.ReverseReadIsLocal"/> of the formatter's objects.</summary>
        public static readonly bool ReverseReadIsLocal =
            NinoPrefetch.ReverseReadIsLocal(PrefetchBytes, PrefetchMembers);
    }

    /// <summary>
    /// The kind of <typeparamref name="T"/>'s elements under <typeparamref name="TFormatter"/> and the traits that
    /// follow from it, read once. Generic over the element too, because the element decides them: raw by
    /// <typeparamref name="T"/>'s layout on this runtime (<see cref="RawFormatter{T,TFormatter}"/>), blocks by its
    /// memory (<see cref="NinoBlockPlan{T,TFormatter}"/>), slots and blocks through members of
    /// <see cref="INinoFormatter{T}"/>. Those kinds exist only for values without references, whose code is never
    /// shared, so these fields are constants where they are read: read them behind
    /// <c>IsReferenceOrContainsReferences</c> (<see cref="NinoKind.Of{T,TFormatter}"/>, the <c>NinoElements</c>
    /// accessors) or a size test that excludes references. Three are read for any element, as the classes they came
    /// from were: <see cref="FlatClass"/> (a class decides it), <see cref="HeavyHashed"/> and, for elements over 16
    /// bytes, <see cref="Heavy"/>.
    /// </summary>
    internal static class NinoKind<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
    {
        /// <summary>The kind, in the order of <see cref="NinoElementKind"/>.</summary>
        public static readonly NinoElementKind Kind = Decide();

        /// <summary><see cref="NinoKind.Stride{T,TFormatter}"/>.</summary>
        public static readonly int Stride = Kind switch
        {
            NinoElementKind.Raw => Unsafe.SizeOf<T>(),
            NinoElementKind.Slot => default(TFormatter).SlotStride,
            NinoElementKind.Block => NinoBlockPlan<T, TFormatter>.Size,
            _ => -1,
        };

        /// <summary><see cref="NinoElements.MinWireSize{T,TFormatter}"/>: the fewest bytes an element takes.</summary>
        public static readonly int MinWire = Math.Max(Stride, 1);

        /// <summary>
        /// <see cref="NinoElements.CopiedWhole{T,TFormatter}"/>. Block reads write every byte of a value, padding as 0
        /// (NinoBlockCollections.ReadEach); values read a field at a time (ReadSize -1, Untrusted mode) leave their
        /// padding as it was, and blocks without a map (DateTimeOffset, a tuple holding one) are read value by value,
        /// which takes a zeroed array (see <see cref="NinoElements.CopiedWhole{T,TFormatter}"/>). Slots that are memory
        /// copy every byte and clear the run when they reject it (NinoNullables.ReadPrechecked); other slots are read
        /// one by one after the presence bytes are checked, so data they reject would leave the new array as it was
        /// allocated.
        /// </summary>
        public static readonly bool CopiedWhole = Kind switch
        {
            NinoElementKind.Raw => NinoLayout<T>.CopiesAsIs,
            NinoElementKind.Slot => default(TFormatter).SlotsAreMemory,
            NinoElementKind.Block => NinoBlockPlan<T, TFormatter>.ReadSize > 0 && NinoBlockPlan<T, TFormatter>.Mapped,
            _ => false,
        };

        /// <summary><see cref="NinoElements.ReadOverwrites{T,TFormatter}"/>: every kind read as one block.</summary>
        public static readonly bool ReadOverwrites = Stride > 0;

        /// <summary>
        /// <see cref="NinoElements.Heavy(int, int)"/> for the elements of an array or a list (<see cref="MinWire"/>):
        /// the formatters test <c>Unsafe.SizeOf&lt;T&gt;() &gt; Amplification &amp;&amp; NinoKind&lt;T,
        /// TFormatter&gt;.Heavy</c>, an intrinsic and a field the JIT folds while it imports the method (no load of the
        /// field in shared code for references, whose slots are 8 bytes), so the branch is not compiled where it is
        /// false. A method returning the same (an inlined call) left it compiled: the array slot it passes by reference
        /// was kept in memory, and what else the read inlines changed.
        /// </summary>
        public static readonly bool Heavy = NinoElements.Heavy(Unsafe.SizeOf<T>(), MinWire);

        /// <summary>
        /// <see cref="NinoElements.Heavy(int, int)"/> for a hash set's entries (<see cref="NinoElements.HashSlot"/>
        /// bytes beyond the element, <see cref="NinoElements.MinHashedSize{T,TFormatter}"/>), as <see cref="Heavy"/>.
        /// </summary>
        public static readonly bool HeavyHashed = NinoElements.Heavy(Unsafe.SizeOf<T>() + NinoElements.HashSlot,
            Kind is NinoElementKind.Raw or NinoElementKind.Slot ? Stride : 1);

        /// <summary>
        /// A sealed class whose body opens no class body (<see cref="NinoElements.WritesOnLocal{T,TFormatter}"/>): how
        /// the per-value loop holds its writer, which the class decides (sealed), so keyed here.
        /// </summary>
        public static readonly bool FlatClass = typeof(T).IsSealed && !typeof(T).IsValueType &&
                                                (NinoKind<TFormatter>.Traits & NinoBodyTraits.Nests) == 0;

        // Each test only where the ones before it failed: a raw type never builds a block plan. The raw test is the
        // layout check RawFormatter holds (the raw marker and NinoLayout.Canonical), read as it is.
        private static NinoElementKind Decide()
        {
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                return NinoElementKind.PerValue;
            if (RawFormatter<T, TFormatter>.Is)
                return NinoElementKind.Raw;
            if (default(TFormatter).SlotStride > 0)
                return NinoElementKind.Slot;
            return NinoBlockPlan<T, TFormatter>.Size > 0 ? NinoElementKind.Block : NinoElementKind.PerValue;
        }
    }
}
