using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    // The generator seam of #355: a class whose generated body is flat (raw members, bools and strings, nothing nested)
    // is a chain of items at its members (NinoItemChain.cs, the one chain every composite is), moved by the run
    // kernels. The generator emits a member accessor per member, a layout struct (INinoFlatLayout: construction, the
    // exact type test, the type id) and a class deriving NinoFlat<TLayout, TBody> whose static members it calls. The one
    // fact only the process knows (whether consecutive raw members lie in the object as on the wire) is read by the
    // generated hooks once per call (#436).

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
        /// <typeparamref name="TFormatter"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteSealed<T, TFormatter>(NinoRun run, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            writer = NinoFlatRuns.WriteRun<T, TFormatter, NinoFlatSealedItem<TLayout, TBody>>(run, writer);

        /// <summary>As <see cref="WriteSealed{T,TFormatter}"/>, a class that is not sealed: each its type id, then its
        /// body; a subtype through <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteTyped<T, TFormatter>(NinoRun run, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            writer = NinoFlatRuns.WriteRun<T, TFormatter, NinoFlatTypedItem<TLayout, TBody>>(run, writer);

        /// <summary>
        /// Reads the values of <paramref name="run"/> (not empty, values in a row) of a sealed class in place: an
        /// element that holds an object (a read into existing elements) goes through <typeparamref name="TFormatter"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadSealed<T, TFormatter>(NinoRun run, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            reader = NinoFlatRuns.ReadRun<T, TFormatter, NinoFlatSealedItem<TLayout, TBody>>(run, reader);

        /// <summary>As <see cref="ReadSealed{T,TFormatter}"/>, a class that is not sealed: another type id through
        /// <typeparamref name="TFormatter"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadTyped<T, TFormatter>(NinoRun run, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoFormatter<T> =>
            reader = NinoFlatRuns.ReadRun<T, TFormatter, NinoFlatTypedItem<TLayout, TBody>>(run, reader);

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
    /// (<see cref="INinoFormatter.WriteFlat"/>, <see cref="INinoFormatter.ReadFlat"/>), as a pair's or a value tuple's
    /// are. One value is written and read member by member.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class NinoFlatValues<TBody> where TBody : struct, INinoItem
    {
        /// <summary>Writes the values of <paramref name="run"/> (not empty; free entries skipped).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Write(NinoRun run, ref Writer writer) =>
            NinoStringRuns.Write(run, default(TBody), ref writer);

        /// <summary>Reads the values of <paramref name="run"/> (not empty, every one in use) in place.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Read(NinoRun run, ref Reader reader) =>
            reader.UnsafeAdvance(NinoStringRuns.Read(run, default(TBody), ref reader.Peek(), reader.Remaining,
                default(NinoStringRuns.InPlace)));
    }
}
