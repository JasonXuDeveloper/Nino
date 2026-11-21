using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    // The generator seam of #355: a class whose generated body is flat (raw members, bools and strings, nothing nested)
    // is written and read through items that the string run kernels move, one kernel source for every such type. The
    // generator emits only facts: a member accessor struct per item (INinoFlatMember: typed field access, a constant
    // offset on every backend, reached by a constrained call), a layout struct (INinoFlatLayout: construction, the exact
    // type test, the type id), the chain of items as nested type arguments, and a class deriving NinoFlat<TLayout, TBody>
    // whose static members it calls. Nothing is read per element from a static readonly field (#436): every fact a
    // kernel needs per element is a literal or a struct type argument; the one fact only the process knows (whether
    // consecutive raw members lie in the object as on the wire) is read by the generated hooks once per call.

    /// <summary>
    /// A member of a flat body (generated code): its memory in an object of the type, reached through typed field access
    /// (<c>ref Unsafe.As&lt;T&gt;(value).Field</c>), and for a block the bytes of its consecutive raw members (a
    /// constant). The object is passed as <see cref="object"/>, so the kernels stay specialized per type rather than code
    /// shared over a class.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoFlatMember
    {
        /// <summary>The member's memory in <paramref name="value"/> (never null).</summary>
        ref byte Ref(object value);

        /// <summary>The bytes of a block that starts at the member (a constant); unused by other items.</summary>
        int Size { get; }
    }

    /// <summary>What a generated <see cref="INinoFlatMember"/> reaches its field through.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoFlatAccess
    {
        /// <summary><paramref name="value"/> as a <typeparamref name="T"/>, unchecked: it is one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T As<T>(object value) where T : class => Unsafe.As<T>(value);

        /// <summary>The memory of <paramref name="field"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte Bytes<TField>(ref TField field) => ref Unsafe.As<TField, byte>(ref field);
    }

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
    /// An item of a flat body and the items after it (a list nested in type arguments, ended by
    /// <see cref="NinoFlatEnd"/>): its bytes at most, written and read for an object of the type.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoFlatItem
    {
        /// <summary>
        /// The most bytes <see cref="WriteAt"/> writes, as a long: the items add theirs without a check each (the
        /// constants fold into one), and the body's caller checks the total once.
        /// </summary>
        long MaxSize(object value);

        /// <summary>Writes the items of <paramref name="value"/> at <paramref name="destination"/> (room for
        /// <see cref="MaxSize"/>); returns the bytes written.</summary>
        int WriteAt(ref byte destination, object value);

        /// <summary>
        /// Reads the items at <paramref name="source"/> (<paramref name="available"/> bytes) into
        /// <paramref name="value"/>; returns the bytes read. Each item checks its bytes before it reads them. A string
        /// equal to the one the object holds is kept unless <paramref name="fresh"/> (a constant: a new object).
        /// </summary>
        int ReadAt(ref byte source, int available, object value, bool fresh);
    }

    /// <summary>The end of a list of items: nothing.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoFlatEnd : INinoFlatItem
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => 0;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value) => 0;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value, bool fresh) => 0;
    }

    /// <summary>
    /// Consecutive raw members from <typeparamref name="TMember"/> on, laid out in the object as on the wire: one copy of
    /// <see cref="INinoFlatMember.Size"/> bytes. Only in the chain the generated hooks take where a startup check proved
    /// the layout, and for reads where nothing in it is checked (Untrusted mode).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoFlatBlock<TMember, TNext> : INinoFlatItem
        where TMember : struct, INinoFlatMember
        where TNext : struct, INinoFlatItem
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => default(TMember).Size + default(TNext).MaxSize(value);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            int size = default(TMember).Size;
            NinoUnsafe.Copy(ref destination, ref default(TMember).Ref(value), (uint)size);
            return size + default(TNext).WriteAt(ref Unsafe.Add(ref destination, size), value);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value, bool fresh)
        {
            int size = default(TMember).Size;
            if ((uint)available < (uint)size)
                NinoThrow.EndOfData();
            NinoUnsafe.Copy(ref default(TMember).Ref(value), ref source, (uint)size);
            return size + default(TNext).ReadAt(ref Unsafe.Add(ref source, size), available - size, value, fresh);
        }
    }

    /// <summary>
    /// A raw member of a primitive or enum type <typeparamref name="TValue"/> (every bit pattern a value), stored and
    /// loaded as the generated bodies did.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoFlatValue<TMember, TValue, TNext> : INinoFlatItem
        where TMember : struct, INinoFlatMember
        where TNext : struct, INinoFlatItem
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => Unsafe.SizeOf<TValue>() + default(TNext).MaxSize(value);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            NinoUnsafe.Write(ref destination, Unsafe.As<byte, TValue>(ref default(TMember).Ref(value)));
            return Unsafe.SizeOf<TValue>() +
                   default(TNext).WriteAt(ref Unsafe.Add(ref destination, Unsafe.SizeOf<TValue>()), value);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value, bool fresh)
        {
            if ((uint)available < (uint)Unsafe.SizeOf<TValue>())
                NinoThrow.EndOfData();
            Unsafe.As<byte, TValue>(ref default(TMember).Ref(value)) = NinoUnsafe.Read<TValue>(ref source);
            return Unsafe.SizeOf<TValue>() + default(TNext).ReadAt(ref Unsafe.Add(ref source, Unsafe.SizeOf<TValue>()),
                available - Unsafe.SizeOf<TValue>(), value, fresh);
        }
    }

    /// <summary>
    /// A raw member of a struct type <typeparamref name="TValue"/> (a <c>DateTime</c>, a <c>decimal</c>, a raw struct):
    /// written with its padding cleared and read with Untrusted mode's checks, as the generated bodies did.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoFlatStruct<TMember, TValue, TNext> : INinoFlatItem
        where TMember : struct, INinoFlatMember
        where TNext : struct, INinoFlatItem
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => Unsafe.SizeOf<TValue>() + default(TNext).MaxSize(value);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            NinoUnsafe.WriteValueIn(ref destination, 0, in Unsafe.As<byte, TValue>(ref default(TMember).Ref(value)));
            return Unsafe.SizeOf<TValue>() +
                   default(TNext).WriteAt(ref Unsafe.Add(ref destination, Unsafe.SizeOf<TValue>()), value);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value, bool fresh)
        {
            if ((uint)available < (uint)Unsafe.SizeOf<TValue>())
                NinoThrow.EndOfData();
            Unsafe.As<byte, TValue>(ref default(TMember).Ref(value)) = NinoUnsafe.ReadChecked<TValue>(ref source, 0);
            return Unsafe.SizeOf<TValue>() + default(TNext).ReadAt(ref Unsafe.Add(ref source, Unsafe.SizeOf<TValue>()),
                available - Unsafe.SizeOf<TValue>(), value, fresh);
        }
    }

    /// <summary>A bool member: one byte, checked on every read (0 or 1), as the generated bodies check it.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoFlatBool<TMember, TNext> : INinoFlatItem
        where TMember : struct, INinoFlatMember
        where TNext : struct, INinoFlatItem
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => 1 + default(TNext).MaxSize(value);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            destination = default(TMember).Ref(value);
            return 1 + default(TNext).WriteAt(ref Unsafe.Add(ref destination, 1), value);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value, bool fresh)
        {
            if (available < 1)
                NinoThrow.EndOfData();
            byte flag = source;
            if (flag > 1)
                NinoThrow.InvalidBool(flag);
            default(TMember).Ref(value) = flag;
            return 1 + default(TNext).ReadAt(ref Unsafe.Add(ref source, 1), available - 1, value, fresh);
        }
    }

    /// <summary>A string member in the default encoding, through the string run kernels' item.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoFlatString<TMember, TNext> : INinoFlatItem
        where TMember : struct, INinoFlatMember
        where TNext : struct, INinoFlatItem
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) =>
            default(NinoUtf16Item).MaxSize(ref default(TMember).Ref(value)) + default(TNext).MaxSize(value);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            int size = default(NinoUtf16Item).WriteAt(ref destination, ref default(TMember).Ref(value));
            return size + default(TNext).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), value);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value, bool fresh)
        {
            int size = default(NinoUtf16Item).ReadAt(ref source, available, ref default(TMember).Ref(value), fresh);
            return size + default(TNext).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, value,
                fresh);
        }
    }

    /// <summary>A UTF-8 string member (<c>[NinoUtf8]</c>), as <see cref="NinoFlatString{TMember,TNext}"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoFlatUtf8<TMember, TNext> : INinoFlatItem
        where TMember : struct, INinoFlatMember
        where TNext : struct, INinoFlatItem
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) =>
            default(NinoUtf8Item).MaxSize(ref default(TMember).Ref(value)) + default(TNext).MaxSize(value);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            int size = default(NinoUtf8Item).WriteAt(ref destination, ref default(TMember).Ref(value));
            return size + default(TNext).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), value);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value, bool fresh)
        {
            int size = default(NinoUtf8Item).ReadAt(ref source, available, ref default(TMember).Ref(value), fresh);
            return size + default(TNext).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, value,
                fresh);
        }
    }

    /// <summary>
    /// What a generated formatter of a flat class calls, over its layout and its chain of items (a generated class
    /// derives this one to name both once): the runs of a collection (<see cref="INinoFormatter.WriteFlat"/>,
    /// <see cref="INinoFormatter.ReadFlat"/>) and one body (the formatter's <c>WriteBody</c> / <c>ReadBody</c>, a
    /// base's <c>WriteMembers</c> / <c>ReadMembers</c>).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class NinoFlat<TLayout, TBody>
        where TLayout : struct, INinoFlatLayout
        where TBody : struct, INinoFlatItem
    {
        /// <summary>
        /// Writes the values of <paramref name="run"/> (not empty, every one in use; the count is the caller's) of a
        /// sealed class: each its presence byte, then its body; an element the items do not take goes through
        /// <typeparamref name="TFormatter"/>.
        /// </summary>
        public static Writer WriteSealed<T, TFormatter>(NinoRun run, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.WriteRun<T, TFormatter, NinoFlatSealedItem<TLayout, TBody>>(run, writer);

        /// <summary>As <see cref="WriteSealed{T,TFormatter}"/>, a class that is not sealed: each its type id, then its
        /// body; a subtype through <typeparamref name="TFormatter"/>.</summary>
        public static Writer WriteTyped<T, TFormatter>(NinoRun run, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.WriteRun<T, TFormatter, NinoFlatTypedItem<TLayout, TBody>>(run, writer);

        /// <summary>
        /// Reads the values of <paramref name="run"/> (not empty, values in a row) of a sealed class in place: an
        /// element that holds an object (a read into existing elements) goes through <typeparamref name="TFormatter"/>.
        /// </summary>
        public static Reader ReadSealed<T, TFormatter>(NinoRun run, Reader reader)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.ReadRun<T, TFormatter, NinoFlatSealedItem<TLayout, TBody>>(run, reader);

        /// <summary>As <see cref="ReadSealed{T,TFormatter}"/>, a class that is not sealed: another type id through
        /// <typeparamref name="TFormatter"/>.</summary>
        public static Reader ReadTyped<T, TFormatter>(NinoRun run, Reader reader)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            NinoFlatRuns.ReadRun<T, TFormatter, NinoFlatTypedItem<TLayout, TBody>>(run, reader);

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

        /// <summary>As <see cref="WriteBody"/>, inlined into the caller: a small body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteInline(object value, ref Writer writer)
        {
            long max = default(TBody).MaxSize(value);
            writer.Commit(default(TBody).WriteAt(ref writer.Ensure(max > int.MaxValue ? -1 : (int)max), value));
        }

        /// <summary>
        /// Reads the body into <paramref name="value"/> (not null; its strings kept where the data holds the same), out
        /// of line on a local copy of the reader's cursor.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadBody(object value, ref Reader reader)
        {
            Reader local = default;
            reader.CopyCursorTo(ref local);
            ReadInline(value, ref local);
            local.CopyCursorTo(ref reader);
        }

        /// <summary>As <see cref="ReadBody"/>, inlined into the caller: a small body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadInline(object value, ref Reader reader) =>
            reader.UnsafeAdvance(default(TBody).ReadAt(ref reader.Peek(), reader.Remaining, value, false));

        /// <summary>
        /// As <see cref="ReadBody"/>, into a new object (<paramref name="value"/> just made, every reference field
        /// null): its strings stored without a look at what the field holds.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadNew(object value, ref Reader reader)
        {
            Reader local = default;
            reader.CopyCursorTo(ref local);
            ReadNewInline(value, ref local);
            local.CopyCursorTo(ref reader);
        }

        /// <summary>As <see cref="ReadNew"/>, inlined into the caller.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadNewInline(object value, ref Reader reader) =>
            reader.UnsafeAdvance(default(TBody).ReadAt(ref reader.Peek(), reader.Remaining, value, true));
    }
}
