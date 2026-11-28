using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    // The generator seam of #355: a class whose generated body is flat (raw members, bools, strings and the flat bodies
    // of other generated types) is a chain of items at its members (NinoItemChain.cs, the one chain every composite is), moved by the run
    // kernels. The generator emits a member accessor per member, a layout struct (INinoFlatLayout: construction, the
    // exact type test, the type id) and a class deriving NinoFlat<TLayout, TBody> whose static members it calls. The one
    // fact only the process knows (whether consecutive raw members lie in the object as on the wire) is read by the
    // generated hooks once per call (#436). A body nested in another chain is one item there, named through a
    // non-generic struct the generator emits beside each chain class (Nested), never through the chain's own type: a
    // node whose type arguments hold a whole chain passes the JIT's generic-context limit and is refused as a
    // "recursive" inline (#451).

    /// <summary>The layout of a type whose body is flat (generated code): its construction and its header's facts.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoFlatLayout
    {
        /// <summary>A new instance (<c>new T()</c>).</summary>
        object Create();

        /// <summary>Whether <paramref name="value"/> (not null) is exactly the type: always for a sealed class.</summary>
        bool IsExact(object value);

        /// <summary>The type id (a constant), written in front of each body of a class that is not sealed.</summary>
        int TypeId { get; }
    }

    /// <summary>
    /// What a generated formatter of a flat class calls, over its layout and its chain of items (a generated class
    /// derives this one to name both once): the runs of a collection (<see cref="INinoFormatter.WriteFlat"/>,
    /// <see cref="INinoFormatter.ReadFlat"/>) and one body's write (the formatter's <c>WriteBody</c>, a base's
    /// <c>WriteMembers</c>; one body is read member by member).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class NinoFlat<TLayout, TBody>
        where TLayout : struct, INinoFlatLayout
        where TBody : struct, INinoItem
    {
        /// <summary>
        /// Writes the values of <paramref name="run"/> (not empty, every one in use; the count is the caller's) of a
        /// sealed class: each its presence byte, then its body; an element the items do not take goes through
        /// <typeparamref name="TFormatter"/>. <paramref name="depth"/>: the class bodies an element opens, its own and
        /// those of its members nested in its chain (a generator literal); a run deeper than that leaves nothing
        /// inline goes element by element, its bodies deferred.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteSealed<T, TFormatter>(in NinoRun run, ref Writer writer, int depth)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.WriteRun<T, TFormatter, Sealed>(in run, ref writer, depth);

        /// <summary>As <see cref="WriteSealed{T,TFormatter}"/>, a class that is not sealed: each its type id, then its
        /// body; a subtype through <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteTyped<T, TFormatter>(in NinoRun run, ref Writer writer, int depth)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.WriteRun<T, TFormatter, Typed>(in run, ref writer, depth);

        /// <summary>
        /// Reads the values of <paramref name="run"/> (not empty, values in a row) of a sealed class in place: an
        /// element that holds an object (a read into existing elements) goes through <typeparamref name="TFormatter"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadSealed<T, TFormatter>(in NinoRun run, ref Reader reader, int depth)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.ReadRun<T, TFormatter, Sealed>(in run, ref reader, depth);

        /// <summary>As <see cref="ReadSealed{T,TFormatter}"/>, a class that is not sealed: another type id through
        /// <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadTyped<T, TFormatter>(in NinoRun run, ref Reader reader, int depth)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.ReadRun<T, TFormatter, Typed>(in run, ref reader, depth);

        /// <summary>
        /// Writes the body of <paramref name="value"/> (not null), out of line on a local copy of the writer's cursor
        /// (its fields in registers, not in the caller's memory): a body too large to inline into its callers.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteBody(object value, ref Writer writer)
        {
            Writer local = default;
            writer.CopyCursorTo(ref local);
            WriteInline(value, ref local);
            local.CopyCursorTo(ref writer);
        }

        /// <summary>
        /// A sealed class whose body is flat, at a reference to it: its presence byte, then <typeparamref name="TBody"/>.
        /// The element of a run (<see cref="WriteSealed{T,TFormatter}"/>), and, behind the generated <c>__Flat.Nested</c>,
        /// a member of another flat type (#355). Reads go into the object held, or make one
        /// (<see cref="INinoFlatLayout.Create"/>).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public readonly struct Sealed : INinoItem
        {
            /// <inheritdoc/>
            public bool Leaves
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public long MaxSize(ref byte item)
            {
                object value = Unsafe.As<byte, object>(ref item);
                if (value == null)
                    return 1;
                return default(TBody).MaxSize(ref Unsafe.As<object, byte>(ref value)) + 1;
            }

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int WriteAt(ref byte destination, ref byte item)
            {
                object value = Unsafe.As<byte, object>(ref item);
                if (value == null)
                {
                    destination = NinoWire.Null;
                    return 1;
                }

                destination = NinoWire.NotNull;
                return 1 + default(TBody).WriteAt(ref Unsafe.Add(ref destination, 1), ref Unsafe.As<object, byte>(ref value));
            }

            /// <summary>
            /// Reads the element into the object it holds (its strings kept where unchanged), or into a new one. Null data
            /// over a held object leaves the element (<see cref="NinoFlatRuns.Left"/>): the formatter drops it and hands it
            /// to the recycler (<see cref="NinoRecycler"/>).
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
            {
                if (available < 1)
                    NinoThrow.EndOfData();
                byte flag = source;
                if (flag != NinoWire.NotNull)
                {
                    if (flag != NinoWire.Null)
                        NinoThrow.InvalidPresence(flag);
                    if (Unsafe.As<byte, object>(ref item) != null)
                        return NinoFlatRuns.Left;
                    return 1;
                }

                // One copy of the body's reads for new and existing objects: a second inlined into the loop (fresh as a
                // constant) spilled it to 87 stack accesses (ScatteredTest's read into a million items, 1.6 times the base).
                object value = Unsafe.As<byte, object>(ref item);
                bool made = value == null;
                if (made)
                    value = default(TLayout).Create();
                int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 1), available - 1,
                    ref Unsafe.As<object, byte>(ref value), made);
                // After the read: a rejected or left one leaves no new object (the size tested only for a body that may
                // leave: a test after every body read a flat class's runs in 1.05 times their time).
                if (made && (!default(TBody).Leaves || size >= 0))
                    Unsafe.As<byte, object>(ref item) = value;
                return size + 1;
            }
        }

        /// <summary>
        /// As <see cref="Sealed"/>, a class that is not sealed: its type id (0 for null), its exact type checked per
        /// element; a subtype, or another type id on a read, leaves the run (or the element of an outer run, a member).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public readonly struct Typed : INinoItem
        {
            /// <inheritdoc/>
            public bool Leaves
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => true;
            }

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public long MaxSize(ref byte item)
            {
                object value = Unsafe.As<byte, object>(ref item);
                if (value == null)
                    return 4;
                if (!default(TLayout).IsExact(value))
                    return NinoFlatRuns.Leave;
                return default(TBody).MaxSize(ref Unsafe.As<object, byte>(ref value)) + 4;
            }

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int WriteAt(ref byte destination, ref byte item)
            {
                object value = Unsafe.As<byte, object>(ref item);
                if (value == null)
                {
                    Unsafe.WriteUnaligned(ref destination, NinoWire.NullTypeId);
                    return 4;
                }

                Unsafe.WriteUnaligned(ref destination, default(TLayout).TypeId);
                return 4 + default(TBody).WriteAt(ref Unsafe.Add(ref destination, 4), ref Unsafe.As<object, byte>(ref value));
            }

            /// <summary>
            /// Reads the element into the object it holds where that is of exactly the type, or into a new one where it
            /// holds none; returns <see cref="NinoFlatRuns.Left"/> (nothing read) for another type id, or an element
            /// holding a subtype or null data, which the formatter reads.
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int ReadAt(ref byte source, int available, ref byte item, bool fresh)
            {
                if (available < 4)
                    NinoThrow.EndOfData();
                int id = Unsafe.ReadUnaligned<int>(ref source);
                object value = Unsafe.As<byte, object>(ref item);
                if (id != default(TLayout).TypeId)
                    return id == NinoWire.NullTypeId && value == null ? 4 : NinoFlatRuns.Left;
                bool made = value == null;
                if (made)
                    value = default(TLayout).Create();
                else if (!default(TLayout).IsExact(value))
                    return NinoFlatRuns.Left;
                int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 4), available - 4,
                    ref Unsafe.As<object, byte>(ref value), made);
                // After the read: a rejected or left one leaves no new object (the size tested only for a body that may
                // leave: a test after every body read a flat class's runs in 1.05 times their time).
                if (made && (!default(TBody).Leaves || size >= 0))
                    Unsafe.As<byte, object>(ref item) = value;
                return size + 4;
            }
        }

        /// <summary>As <see cref="WriteBody"/>, inlined into the caller: a small body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInline(object value, ref Writer writer)
        {
            // The items' base is a reference to the object (INinoMember): the parameter's address, which the JIT turns
            // back into the parameter once the items are inlined.
            ref byte item = ref Unsafe.As<object, byte>(ref value);
            long max = default(TBody).MaxSize(ref item);
            writer.Commit(default(TBody).WriteAt(ref writer.Ensure(max > int.MaxValue ? -1 : (int)max), ref item));
        }
    }

    /// <summary>
    /// What a generated formatter of a struct whose body is flat calls over its chain of items (a generated class derives
    /// this one to name it once): the runs of a collection, each element read and written in place
    /// (<see cref="INinoFormatter.WriteFlat"/>, <see cref="INinoFormatter.ReadFlat"/>) by the flat kernels, an element
    /// the items leave (a keyed entry out of order, a nested class's subtype) through <typeparamref name="TFormatter"/>.
    /// One value is written and read member by member.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class NinoFlatValues<TBody> where TBody : struct, INinoItem
    {
        /// <summary>The body as a member of another flat type (#355: a generated chain nests it, the member in place):
        /// <typeparamref name="TBody"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public readonly struct Item : INinoItem
        {
            /// <inheritdoc/>
            public bool Leaves
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => default(TBody).Leaves;
            }

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public long MaxSize(ref byte item) => default(TBody).MaxSize(ref item);

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int WriteAt(ref byte destination, ref byte item) => default(TBody).WriteAt(ref destination, ref item);

            /// <inheritdoc/>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int ReadAt(ref byte source, int available, ref byte item, bool fresh) =>
                default(TBody).ReadAt(ref source, available, ref item, fresh);
        }

        /// <summary>Writes the values of <paramref name="run"/> (not empty; free entries skipped);
        /// <paramref name="depth"/> as <see cref="NinoFlat{TLayout,TBody}.WriteSealed{T,TFormatter}"/>'s.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Write<T, TFormatter>(in NinoRun run, ref Writer writer, int depth)
            where T : struct
            where TFormatter : struct, INinoFormatter<T>
        {
            // A chain no element leaves (a positional struct's, no class nested): the string kernels' write, which has
            // no stop (through the flat kernels' loop ManagedStruct's runs wrote in 1.1 times step B's time). Here,
            // not in NinoFlatRuns: its IL there kept the class runs' driver from inlining into their hooks (asm.py,
            // TC=0: "too many il bytes", a call more per run).
            if (default(TBody).Leaves || depth != 0)
                NinoFlatRuns.WriteRun<T, TFormatter, TBody>(in run, ref writer, depth);
            else
                NinoStringRuns.Write(run, default(TBody), ref writer);
        }

        /// <summary>Reads the values of <paramref name="run"/> (not empty, every one in use) in place.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Read<T, TFormatter>(in NinoRun run, ref Reader reader, int depth)
            where T : struct
            where TFormatter : struct, INinoFormatter<T>
        {
            // As Write: the string kernels' read loop, no stop (through the flat loop, with its index for a stop, a
            // flat struct's runs read in 1.04 times step B's time; through this one 0.96).
            if (default(TBody).Leaves || depth != 0)
                NinoFlatRuns.ReadRun<T, TFormatter, TBody>(in run, ref reader, depth);
            else
                reader.UnsafeAdvance(NinoStringRuns.Read(run, default(TBody), ref reader.Peek(), reader.Remaining,
                    default(NinoStringRuns.InPlace)));
        }
    }
}
