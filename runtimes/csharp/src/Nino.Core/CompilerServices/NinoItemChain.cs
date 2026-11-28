using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    // The one item chain of #355: every composite whose members are strings, raw values, bools, slots (a raw T?) and
    // values read as one (a pair, a value tuple, a dictionary's entry, a generated flat type) is a chain of items at its
    // members, the last one without a next, and every run of such composites (or of strings alone) is moved by the same run
    // kernels (NinoStringRuns, NinoFlatRuns) over INinoItem. One base for every item: `ref byte item`, the composite's
    // memory (a struct in place) or a reference to it (a class). The generator emits only facts: a member accessor per
    // member (INinoMember, typed field access, a constant offset on every backend) and the chain as nested type
    // arguments. Nothing is read per element from a static readonly field (#436).

    /// <summary>
    /// An item of a run's element or of a composite, at the memory it is given (generated code and Nino.Core): its bytes
    /// at most, written at a reference and read from the data. One interface for a string, a value, a chain of items at
    /// a composite's members (<see cref="NinoItemAt{TMember,TItem,TNext}"/>) and a run's element.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoItem
    {
        /// <summary>
        /// The most bytes <see cref="WriteAt"/> writes for <paramref name="item"/>, as a long: the items of a chain add
        /// theirs without a check each (the constants fold into one), and the caller checks the total once (more than
        /// <see cref="int.MaxValue"/>: too large; negative: an element its run leaves).
        /// </summary>
        long MaxSize(ref byte item);

        /// <summary>Writes <paramref name="item"/> at <paramref name="destination"/> (room for <see cref="MaxSize"/>);
        /// returns the bytes written.</summary>
        int WriteAt(ref byte destination, ref byte item);

        /// <summary>
        /// Reads the bytes at <paramref name="source"/> (<paramref name="available"/> bytes) into
        /// <paramref name="item"/>; returns the bytes read (negative: an element its run leaves, nothing read). Each
        /// item checks its bytes before it reads or allocates for them: too few, or a length out of range, is invalid
        /// data (<see cref="NinoDataException"/>). A string equal to the one held is kept unless
        /// <paramref name="fresh"/> (a constant where it can be: a new object, or one slot every element is read into).
        /// </summary>
        int ReadAt(ref byte source, int available, ref byte item, bool fresh);

        /// <summary>
        /// Whether <see cref="ReadAt"/> may leave the element (a negative size: a subtype, another key, a region the
        /// items do not fill): a constant, so a node tests the size before reading on only after an item that may
        /// (a string's size is no constant the JIT can prove positive: a test after each cost a flat struct's runs
        /// 1.08 times their read time).
        /// </summary>
        bool Leaves { get; }
    }

    /// <summary>
    /// A size an item takes as a type argument (generated code and Nino.Core): a constant, never a field read per
    /// element (#436). A generated accessor's is a literal of the generator: a block's bytes, a raw <c>T?</c>'s longest
    /// single value; Nino.Core's are JIT-time constants (<c>Unsafe.SizeOf</c>).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoSize
    {
        /// <summary>The bytes (a constant): of a block that starts at a member, or the most a slot's value
        /// (<see cref="NinoSlotItem{T,TFormatter,TSize}"/>) writes; unused by other items.</summary>
        int Size { get; }
    }

    /// <summary>
    /// Where a member of a composite lies (generated code and Nino.Core): its memory in the composite's, and its
    /// <see cref="INinoSize.Size"/> (a block's, a slot's).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoMember : INinoSize
    {
        /// <summary>The member's memory in <paramref name="item"/>: the composite in place for a struct, a reference to
        /// the object (never null) for a class.</summary>
        ref byte Ref(ref byte item);
    }

    /// <summary>What a generated <see cref="INinoMember"/> reaches its field through.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoMemberAccess
    {
        /// <summary>
        /// <paramref name="item"/> as a <typeparamref name="T"/>, unchecked: the struct in place, or the reference to
        /// the object for a class, so <c>As&lt;T&gt;(ref item).Field</c> is the field either way.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T As<T>(ref byte item) => ref Unsafe.As<byte, T>(ref item);

        /// <summary>The memory of <paramref name="field"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte Bytes<TField>(ref TField field) => ref Unsafe.As<TField, byte>(ref field);
    }

    /// <summary>
    /// A member at an offset known only to the process (a <c>KeyValuePair</c>'s, a value tuple's, a dictionary entry's),
    /// carried as data: an accessor generic over a composite that holds a class would be code shared over it, and in
    /// shared code a struct's method instantiated over a class is an indirect call per element. The offset is a
    /// JIT-time constant where the composite's formatter computes it, held in a register through a run.
    /// </summary>
    internal readonly struct NinoOffset : INinoMember
    {
        // An int, widened with its sign where it is added (Mono widens an int offset added to a reference as unsigned):
        // a pair's chain (two offsets and the empty end) stays 12 bytes, passed to the run kernels in registers, where
        // two nints and the end's padding made 24 bytes passed through the stack (asm.py: a Dictionary<string, string>'s
        // WriteEntries 38 to 45 stack accesses, ReadStrings 14 to 23).
        private readonly int _at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoOffset(int at) => _at = at;

        public int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte Ref(ref byte item) => ref Unsafe.AddByteOffset(ref item, (nint)_at);
    }


    /// <summary>
    /// <typeparamref name="TItem"/> at <typeparamref name="TMember"/>'s memory in the composite: the last item of a chain.
    /// A chain ends at its last item rather than at an empty end node, whose byte (and padding) made a pair's chain
    /// 12 bytes built in memory, where two offsets are 8 bytes in one register (asm.py: a Dictionary&lt;string,
    /// string&gt;'s WriteEntries 38 to 44 stack accesses).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoItemAt<TMember, TItem> : INinoItem
        where TMember : struct, INinoMember
        where TItem : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TItem).Leaves;
        }

        private readonly TMember _member;

        /// <summary>The item at <paramref name="member"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemAt(TMember member) => _member = member;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(TItem).MaxSize(ref _member.Ref(ref item));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item) =>
            default(TItem).WriteAt(ref destination, ref _member.Ref(ref item));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh) =>
            default(TItem).ReadAt(ref source, available, ref _member.Ref(ref item), fresh);
    }

    /// <summary>
    /// <typeparamref name="TItem"/> at <typeparamref name="TMember"/>'s memory in the composite, then the items
    /// <typeparamref name="TNext"/>: the one composition node (the last item is <see cref="NinoItemAt{TMember,TItem}"/>). A generated accessor is an empty struct (nothing of it is
    /// read); <see cref="NinoOffset"/> holds its offset.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoItemAt<TMember, TItem, TNext> : INinoItem
        where TMember : struct, INinoMember
        where TItem : struct, INinoItem
        where TNext : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TItem).Leaves || default(TNext).Leaves;
        }

        private readonly TMember _member;
        private readonly TNext _next;

        /// <summary>The node at <paramref name="member"/>, then <paramref name="next"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemAt(TMember member, TNext next)
        {
            _member = member;
            _next = next;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) =>
            default(TItem).MaxSize(ref _member.Ref(ref item)) + _next.MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            int size = default(TItem).WriteAt(ref destination, ref _member.Ref(ref item));
            return size + _next.WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int size = default(TItem).ReadAt(ref source, available, ref _member.Ref(ref item), fresh);
            // An item that left the element (NinoFlatRuns.Left: a subtype, another key) ends the read here: the next
            // items would read at its negative size. Only after an item that may (Leaves, a constant).
            if (default(TItem).Leaves && size < 0)
                return size;
            return size + _next.ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item,
                fresh);
        }
    }

    /// <summary>
    /// <typeparamref name="TItem"/> (a nested flat body, the generated <c>Nested</c>) read out of line: its read a
    /// method of its own, its own root for the JIT, while its size and write stay inline. Where the generator gives a
    /// nested body this item (FlatRules' locals budget, #456): a chain read whole in one method ran out of the JIT's
    /// locals (the run's root, NinoFlatRuns.Read, past its limit) and left the rest of each element as calls (Comment,
    /// two ShallowUsers each nesting BadgeCount: 12 "too many locals" failures at Tier1).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoReadCall<TItem> : INinoItem
        where TItem : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TItem).Leaves;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(TItem).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item) => default(TItem).WriteAt(ref destination, ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh) =>
            default(TItem).ReadAt(ref source, available, ref item, fresh);
    }

    /// <summary>
    /// A chain's first item at the element's start, then <typeparamref name="TNext"/>: the chain of a composite whose
    /// run was rebased to its first item (<see cref="NinoRun.Items"/>; a pair's write, <see cref="Internal.NinoItems"/>),
    /// so the kernels hold one offset fewer in a register. Its only field is the next node's: an empty member as a
    /// field made the chain a byte and an int built in memory and loaded whole, which the CPU cannot forward (a
    /// store-forwarding stall per call: the write of SimpleClass's Dictionary&lt;int, string&gt;).
    /// </summary>
    internal readonly struct NinoItemFirst<TItem, TNext> : INinoItem
        where TItem : struct, INinoItem
        where TNext : struct, INinoItem
    {
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TItem).Leaves || default(TNext).Leaves;
        }

        private readonly TNext _next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoItemFirst(TNext next) => _next = next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(TItem).MaxSize(ref item) + _next.MaxSize(ref item);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            int size = default(TItem).WriteAt(ref destination, ref item);
            return size + _next.WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int size = default(TItem).ReadAt(ref source, available, ref item, fresh);
            if (default(TItem).Leaves && size < 0)
                return size;
            return size + _next.ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item,
                fresh);
        }
    }

    /// <summary>
    /// Parts of a generated chain over the same composite, run in order (#452): a generated chain is a tree of groups
    /// of at most four parts over its items (<see cref="NinoItemAt{TMember,TItem}"/>,
    /// <see cref="NinoBlockAt{TMember,TCheck}"/>, <see cref="NinoPropertyAt{TProperty,TValue,TItem}"/>), so the levels
    /// of inlining it takes grow with the logarithm of its items, not with their count (the JIT inlines at most 20
    /// deep: a right-nested chain of a class nesting two classes went past it). A part that may leave the element
    /// (<see cref="INinoItem.Leaves"/>) ends the read there, as <see cref="NinoItemAt{TMember,TItem,TNext}"/> does.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoGroup<T1, T2> : INinoItem
        where T1 : struct, INinoItem
        where T2 : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(T1).Leaves || default(T2).Leaves;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(T1).MaxSize(ref item) + default(T2).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            int size = default(T1).WriteAt(ref destination, ref item);
            return size + default(T2).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int size = default(T1).ReadAt(ref source, available, ref item, fresh);
            if (default(T1).Leaves && size < 0)
                return size;
            return size + default(T2).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item,
                fresh);
        }
    }

    /// <summary>As <see cref="NinoGroup{T1,T2}"/>, 3 parts.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoGroup<T1, T2, T3> : INinoItem
        where T1 : struct, INinoItem
        where T2 : struct, INinoItem
        where T3 : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(T1).Leaves || default(T2).Leaves || default(T3).Leaves;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) =>
            default(T1).MaxSize(ref item) + default(T2).MaxSize(ref item) +
            default(T3).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            int size = default(T1).WriteAt(ref destination, ref item);
            size += default(T2).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
            return size + default(T3).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int size = default(T1).ReadAt(ref source, available, ref item, fresh);
            if (default(T1).Leaves && size < 0)
                return size;
            size += default(T2).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item, fresh);
            if (default(T2).Leaves && size < 0)
                return size;
            return size + default(T3).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item,
                fresh);
        }
    }

    /// <summary>As <see cref="NinoGroup{T1,T2}"/>, 4 parts.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoGroup<T1, T2, T3, T4> : INinoItem
        where T1 : struct, INinoItem
        where T2 : struct, INinoItem
        where T3 : struct, INinoItem
        where T4 : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(T1).Leaves || default(T2).Leaves || default(T3).Leaves || default(T4).Leaves;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) =>
            default(T1).MaxSize(ref item) + default(T2).MaxSize(ref item) +
            default(T3).MaxSize(ref item) + default(T4).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            int size = default(T1).WriteAt(ref destination, ref item);
            size += default(T2).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
            size += default(T3).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
            return size + default(T4).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref item);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int size = default(T1).ReadAt(ref source, available, ref item, fresh);
            if (default(T1).Leaves && size < 0)
                return size;
            size += default(T2).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item, fresh);
            if (default(T2).Leaves && size < 0)
                return size;
            size += default(T3).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item, fresh);
            if (default(T3).Leaves && size < 0)
                return size;
            return size + default(T4).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref item,
                fresh);
        }
    }

    /// <summary>
    /// What a block (<see cref="NinoBlockAt{TMember,TCheck}"/>) checks in its bytes before a read copies them
    /// (generated code, #444): the bools in it, each 0 or 1, at constant offsets. Nothing is copied before the check, so a
    /// rejected read leaves the element as it was.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoBlockCheck
    {
        /// <summary>Throws <see cref="NinoDataException"/> where the block's bytes at <paramref name="source"/> hold a
        /// value Nino never writes.</summary>
        void Check(ref byte source);
    }

    /// <summary>The check of a block that holds no bool: nothing.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoUnchecked : INinoBlockCheck
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Check(ref byte source)
        {
        }
    }

    /// <summary>The pieces of a generated <see cref="INinoBlockCheck"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoBlockCheck
    {
        /// <summary>The byte at <paramref name="offset"/> of the block at <paramref name="source"/> (a bool's).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Flag(ref byte source, int offset) => Unsafe.Add(ref source, offset);

        /// <summary>Throws unless every bool or-ed into <paramref name="flags"/> is 0 or 1: one test for all of a
        /// block's bools.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Bools(int flags)
        {
            if ((flags & ~1) != 0)
                NinoThrow.InvalidBool(flags);
        }
    }

    /// <summary>
    /// Consecutive members from <typeparamref name="TMember"/> on whose memory is their bytes on the wire (primitives,
    /// bools, raw structs without padding: #444), laid out in the composite as on the wire: one copy of
    /// <see cref="INinoSize.Size"/> bytes, the chain's raw item, read after <typeparamref name="TCheck"/> checked its
    /// bytes (the bools in it). Only where a startup check proved the layout, and for reads only where Untrusted mode
    /// checks nothing in it. A part of a generated chain (<see cref="NinoGroup{T1,T2}"/>).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoBlockAt<TMember, TCheck> : INinoItem
        where TMember : struct, INinoMember
        where TCheck : struct, INinoBlockCheck
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        private readonly TMember _member;

        /// <summary>The block at <paramref name="member"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoBlockAt(TMember member) => _member = member;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => _member.Size;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            NinoUnsafe.Copy(ref destination, ref _member.Ref(ref item), (uint)_member.Size);
            return _member.Size;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int size = _member.Size;
            if ((uint)available < (uint)size)
                NinoThrow.EndOfData();
            default(TCheck).Check(ref source);
            NinoUnsafe.Copy(ref _member.Ref(ref item), ref source, (uint)size);
            return size;
        }
    }

    /// <summary>
    /// A property of a composite (generated code): its value read and assigned through its accessors, where no field can
    /// be reached by reference (an auto-property's backing field has no name C# can write).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoProperty<TValue> : INinoSize
    {
        /// <summary>The property's value in <paramref name="item"/> (as <see cref="INinoMember.Ref"/>'s item).</summary>
        TValue Load(ref byte item);

        /// <summary>Assigns <paramref name="value"/> to the property in <paramref name="item"/>.</summary>
        void Store(ref byte item, TValue value);
    }

    /// <summary>
    /// The value of a property whose type is a class (a string, a nested flat class) as
    /// <see cref="INinoProperty{TValue}"/>'s value: the reference alone, as the bottom item sees it at
    /// <c>ref byte item</c>. A type argument that is a class makes every type and method it reaches code shared over
    /// classes (<c>__Canon</c>): the whole chain, and the run kernels over it, with each item a call through a runtime
    /// lookup (a version-tolerant class with one string property wrote its runs in 4.2 times the generated body's time);
    /// this struct is no class.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NinoReference
    {
        // The only field: the bottom item reads and writes it at the struct's start.
        private object _value;

        /// <summary>A copy of <paramref name="value"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoReference(object value) => _value = value;

        /// <summary>The value as the <typeparamref name="T"/> it is (the bottom item read one), without a cast.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T As<T>() where T : class => Unsafe.As<T>(_value);
    }

    /// <summary>
    /// <typeparamref name="TItem"/> over the value of the property <typeparamref name="TProperty"/>: a part of a generated
    /// chain (<see cref="NinoGroup{T1,T2}"/>) for a member reached through its accessors, the bottom item over a local
    /// copy (a read assigns it back; a string equal to the one held is kept).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoPropertyAt<TProperty, TValue, TItem> : INinoItem
        where TProperty : struct, INinoProperty<TValue>
        where TItem : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TItem).Leaves;
        }

        private readonly TProperty _property;

        /// <summary>The item at <paramref name="property"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoPropertyAt(TProperty property) => _property = property;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item)
        {
            TValue value = _property.Load(ref item);
            return default(TItem).MaxSize(ref Unsafe.As<TValue, byte>(ref value));
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            TValue value = _property.Load(ref item);
            return default(TItem).WriteAt(ref destination, ref Unsafe.As<TValue, byte>(ref value));
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            // The value held, so a string equal to it is kept (fresh: none); assigned after the read, so a rejected one
            // assigns nothing.
            TValue value = fresh ? default : _property.Load(ref item);
            int size = default(TItem).ReadAt(ref source, available, ref Unsafe.As<TValue, byte>(ref value), fresh);
            // Nothing stored for an item that read nothing (a version-tolerant region ended before it,
            // NinoTolerantItem) or left the element: the property keeps its value, as the member-by-member read does.
            if (size > 0)
                _property.Store(ref item, value);
            return size;
        }
    }

    /// <summary>A keyed member's key (generated code): a constant, the generated accessor's literal (#436).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoKey
    {
        /// <summary>The member's key (a constant): the hash of its name, as the layout publishes it.</summary>
        int Key { get; }
    }

    /// <summary>
    /// A body in a length-prefixed region (a version-tolerant or keyed body, #355): an <c>int32</c> length, then
    /// <typeparamref name="TBody"/>'s bytes, the length written after them. A read gives <typeparamref name="TBody"/>
    /// the region's bytes only and skips those after its last item (members a newer version appended); a region longer
    /// than the bytes left, or one holding a section of deferred bodies (the sign bit of its length, which no flat body
    /// writes), leaves the element to its formatter, which reads or rejects it.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoRegionItem<TBody> : INinoItem where TBody : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => true;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => NinoWire.LengthSize + default(TBody).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            int size = default(TBody).WriteAt(ref Unsafe.Add(ref destination, NinoWire.LengthSize), ref item);
            NinoUnsafe.Write(ref destination, size);
            return NinoWire.LengthSize + size;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            if (available < NinoWire.LengthSize)
                NinoThrow.EndOfData();
            int length = NinoUnsafe.Read<int>(ref source);
            if ((uint)length > (uint)(available - NinoWire.LengthSize))
                return NinoFlatRuns.Left;
            int read = default(TBody).ReadAt(ref Unsafe.Add(ref source, NinoWire.LengthSize), length, ref item,
                fresh);
            return read < 0 ? read : NinoWire.LengthSize + length;
        }
    }

    /// <summary>
    /// The entries of a keyed body (#355), in a <see cref="NinoRegionItem{TBody}"/>: a read that leaves bytes of the
    /// region unread (entries of members it does not know, a member repeated, entries in another order) leaves the
    /// element to its formatter, which reads entries in any order.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoEntriesItem<TBody> : INinoItem where TBody : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => true;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(TBody).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item) => default(TBody).WriteAt(ref destination, ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int read = default(TBody).ReadAt(ref source, available, ref item, fresh);
            return read == available ? read : NinoFlatRuns.Left;
        }
    }

    /// <summary>
    /// A keyed member (#355): its key (<typeparamref name="TKey"/>'s constant), the length of its value, then
    /// <typeparamref name="TItem"/>, the length written after the value. A read takes the entry only where its key is
    /// the member's and its value fills it exactly; any other entry leaves the element to its formatter (a former name's
    /// key, entries in another order, a value that does not fill its entry, which the formatter rejects).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoKeyItem<TKey, TItem> : INinoItem
        where TKey : struct, INinoKey
        where TItem : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => true;
        }

        private const int Header = NinoWire.KeySize + NinoWire.LengthSize;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => Header + default(TItem).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            NinoUnsafe.Write(ref destination, default(TKey).Key);
            int size = default(TItem).WriteAt(ref Unsafe.Add(ref destination, Header), ref item);
            NinoUnsafe.Write(ref Unsafe.Add(ref destination, NinoWire.KeySize), size);
            return Header + size;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            if (available < Header || NinoUnsafe.Read<int>(ref source) != default(TKey).Key)
                return NinoFlatRuns.Left;
            int length = NinoUnsafe.Read<int>(ref Unsafe.Add(ref source, NinoWire.KeySize));
            if ((uint)length > (uint)(available - Header))
                return NinoFlatRuns.Left;
            int read = default(TItem).ReadAt(ref Unsafe.Add(ref source, Header), length, ref item, fresh);
            return read == length ? Header + length : NinoFlatRuns.Left;
        }
    }

    /// <summary>
    /// A member of a version-tolerant body (#355), in a <see cref="NinoRegionItem{TBody}"/>: a read where the region
    /// has ended reads nothing, and the member keeps its value (written by an older version, which lacked it).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoTolerantItem<TItem> : INinoItem where TItem : struct, INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => default(TItem).Leaves;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(TItem).MaxSize(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item) => default(TItem).WriteAt(ref destination, ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh) =>
            available == 0 ? 0 : default(TItem).ReadAt(ref source, available, ref item, fresh);
    }

    /// <summary>
    /// A primitive or enum <typeparamref name="T"/> (every bit pattern a value), stored and loaded as the generated
    /// bodies do.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoRawItem<T> : INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => Unsafe.SizeOf<T>();

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            NinoUnsafe.Write(ref destination, Unsafe.As<byte, T>(ref item));
            return Unsafe.SizeOf<T>();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            if ((uint)available < (uint)Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            Unsafe.As<byte, T>(ref item) = NinoUnsafe.Read<T>(ref source);
            return Unsafe.SizeOf<T>();
        }
    }

    /// <summary>
    /// A raw struct <typeparamref name="T"/> (a <c>DateTime</c>, a <c>decimal</c>, a raw user struct): written with its
    /// padding cleared and read with Untrusted mode's checks, as the generated bodies do.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoStructItem<T> : INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => Unsafe.SizeOf<T>();

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            NinoUnsafe.WriteValueIn(ref destination, 0, in Unsafe.As<byte, T>(ref item));
            return Unsafe.SizeOf<T>();
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            if ((uint)available < (uint)Unsafe.SizeOf<T>())
                NinoThrow.EndOfData();
            Unsafe.As<byte, T>(ref item) = NinoUnsafe.ReadChecked<T>(ref source, 0);
            return Unsafe.SizeOf<T>();
        }
    }

    /// <summary>A bool: one byte, checked on every read (0 or 1), as the generated bodies check it.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoBoolItem : INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => 1;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            destination = item;
            return 1;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            if (available < 1)
                NinoThrow.EndOfData();
            byte flag = source;
            if (flag > 1)
                NinoThrow.InvalidBool(flag);
            item = flag;
            return 1;
        }
    }

    /// <summary>A string in the default encoding (<see cref="StringFormatter"/>, <see cref="NinoStrings"/>).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoUtf16Item : INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref string At(ref byte item) => ref Unsafe.As<byte, string>(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => NinoStrings.MaxSize(At(ref item));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item) => NinoStrings.Write(ref destination, At(ref item));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            // An unchanged string (a read into an existing collection or object) is not stored again: no write barrier;
            // the comparison out of line (Reader.ReadStringOver, ReadString(current)'s way): inline it kept the loop's
            // values on the stack (a flat class's run read 64 to 69 stack accesses, a body in a polymorphic dispatch 14
            // to 61). Nothing held, or a fresh read (null too: one slot still holds the string read before): the string
            // stored as read, no comparison live across its allocation.
            string current = fresh ? null : At(ref item);
            if (current == null)
            {
                At(ref item) = NinoStrings.Read(ref source, available, null, out int size);
                return size;
            }

            SizedString read = Reader.ReadStringOver(ref source, available, current);
            if (!ReferenceEquals(read.Value, current))
                At(ref item) = read.Value;
            return read.Size;
        }
    }

    /// <summary>A UTF-8 string (<see cref="Utf8StringFormatter"/>, <c>[NinoUtf8]</c>), as
    /// <see cref="NinoUtf16Item"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoUtf8Item : INinoItem
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref string At(ref byte item) => ref Unsafe.As<byte, string>(ref item);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => Writer.Utf8MaxSize(At(ref item));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item) => Writer.WriteUtf8At(ref destination, At(ref item));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            ref string slot = ref At(ref item);
            string current = fresh ? null : slot;
            SizedString read = Reader.ReadUtf8At(ref source, available, current);
            if (fresh || !ReferenceEquals(read.Value, current))
                slot = read.Value;
            return read.Size;
        }
    }

    /// <summary>
    /// A block of a fixed size (<see cref="INinoFormatter{T}.BlockMap"/>: a <c>DateTimeOffset</c>, a generated block
    /// struct, a tuple or pair of raw items) moved as its formatter moves it, read as one
    /// (<see cref="NinoRuns.ScattersItems{T,TFormatter}"/>). Its bytes are the formatter's
    /// <see cref="INinoFormatter{T}.FixedSize"/>: a literal of a generated formatter, its items' sizes in a generic one,
    /// never a field of Nino's read per element (#436). A raw value is a <see cref="NinoStructItem{T}"/> (its bytes
    /// <c>Unsafe.SizeOf</c>, a constant) or a <see cref="NinoBoolItem"/>, picked outside the loop
    /// (<see cref="NinoCursor"/>). Only for values without references.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoValueItem<T, TFormatter> : INinoItem where TFormatter : struct, INinoFormatter<T>
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(TFormatter).FixedSize;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item)
        {
            default(TFormatter).WriteFixed(in Unsafe.As<byte, T>(ref item), ref destination);
            return default(TFormatter).FixedSize;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
        {
            int size = default(TFormatter).FixedSize;
            if ((uint)available < (uint)size)
                NinoThrow.EndOfData();
            default(TFormatter).ReadBlock(ref Unsafe.As<byte, T>(ref item), ref source);
            return size;
        }
    }

    /// <summary>
    /// A slot's value (<see cref="NinoCursor.Item{T,TFormatter}"/>: a raw <c>T?</c>,
    /// <see cref="UnmanagedNullableFormatter{T}"/>) in a single value's bytes: a presence byte, then the value when
    /// present (<see cref="INinoFormatter{T}.WriteAt"/>, <see cref="INinoFormatter{T}.ReadAt"/>), at most
    /// <typeparamref name="TSize"/>'s bytes: a generated accessor's literal (one presence byte and the value's
    /// canonical bytes), or <c>Unsafe.SizeOf&lt;T&gt;()</c> where that bounds it (<see cref="NinoCursor.Item{T,TFormatter}"/>).
    /// A value without references.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoSlotItem<T, TFormatter, TSize> : INinoItem
        where TFormatter : struct, INinoFormatter<T>
        where TSize : struct, INinoSize
    {
        /// <inheritdoc/>
        public bool Leaves
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => false;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte item) => default(TSize).Size;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte item) =>
            default(TFormatter).WriteAt(ref destination, in Unsafe.As<byte, T>(ref item));

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte item, bool fresh) =>
            default(TFormatter).ReadAt(ref source, available, ref Unsafe.As<byte, T>(ref item));
    }

    /// <summary>
    /// The bytes of a <typeparamref name="T"/> in memory (<c>Unsafe.SizeOf</c>, a JIT-time constant on every runtime):
    /// the size of a slot's value (<see cref="NinoSlotItem{T,TFormatter,TSize}"/>) where it bounds it.
    /// </summary>
    internal readonly struct NinoMemorySize<T> : INinoSize
    {
        public int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Unsafe.SizeOf<T>();
        }
    }
}
