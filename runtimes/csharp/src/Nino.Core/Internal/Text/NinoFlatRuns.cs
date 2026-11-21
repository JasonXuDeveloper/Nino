using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    // PROTOTYPE (#355, the generator seam), v2: a run of user-type elements whose generated body is flat (raw blocks,
    // bools and strings, no nesting) moved by the string run kernels through a chain of items, one kernel source for
    // every type. The items hold no data: each reaches its field through a member accessor struct the generator emits
    // (INinoMember: `ref Unsafe.As<T>(value).Field`, a constant offset on every backend and an address the JIT knows is
    // in the heap, reached by a constrained call), and the header's facts and construction (an inlined new T()) through
    // the type's layout struct (INinoFlatLayout). No static readonly is read per element (#436). The kernels are
    // instantiated per type over those structs (DRY is the source, as the formatter generics are). A generated formatter
    // opts in through INinoFormatter.HasFlat / WriteFlat / ReadFlat (internal here; public and hidden in the real seam).

    /// <summary>
    /// The layout of a type whose body is flat, emitted by the generator: its construction and its header's facts, as
    /// constants and inlined code.
    /// </summary>
    internal interface INinoFlatLayout
    {
        /// <summary>A new instance (<c>new T()</c>).</summary>
        object New();

        /// <summary>Whether <paramref name="value"/> is exactly the type (a class that is not sealed).</summary>
        bool IsExact(object value);

        /// <summary>The type id (a class that is not sealed).</summary>
        int TypeId { get; }
    }

    /// <summary>
    /// A member of a flat body, emitted by the generator as a struct: its memory in an object of the type (typed field
    /// access through <c>Unsafe.As&lt;T&gt;</c>, so the type stays out of the kernels' type arguments, which would make
    /// them code shared over a class), and for a block the bytes of its consecutive raw members (a constant).
    /// </summary>
    internal interface INinoMember
    {
        ref byte Ref(object value);

        int Size { get; }
    }

    /// <summary>An item of a flat body: its bytes at most, written, and read, for an object of the type.</summary>
    internal interface INinoFlatItem
    {
        /// <summary>The most bytes <see cref="WriteAt"/> writes, as a long: a chain adds its items' without a check
        /// per item, so the constants fold into one, and its element checks the total once.</summary>
        long MaxSize(object value);

        int WriteAt(ref byte destination, object value);

        /// <summary>Reads into a new object (every reference field still null); returns the bytes read.</summary>
        int ReadAt(ref byte source, int available, object value);
    }

    /// <summary>
    /// Consecutive raw members from <typeparamref name="TMember"/> on, laid out in the object as on the wire (checked
    /// once at startup by the generated hooks, out of the loop): one copy of a constant size. Values Untrusted mode checks never
    /// reach it (the type leaves the kernels in that mode).
    /// </summary>
    internal readonly struct NinoBlockItem<TMember> : INinoFlatItem
        where TMember : struct, INinoMember
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => default(TMember).Size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            int size = default(TMember).Size;
            NinoUnsafe.Copy(ref destination, ref default(TMember).Ref(value), (uint)size);
            return size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value)
        {
            int size = default(TMember).Size;
            if ((uint)available < (uint)size)
                NinoThrow.EndOfData();
            NinoUnsafe.Copy(ref default(TMember).Ref(value), ref source, (uint)size);
            return size;
        }
    }

    /// <summary>A bool member: one byte, checked on every read (0 or 1), as the generated body checks it.</summary>
    internal readonly struct NinoBoolItem<TMember> : INinoFlatItem
        where TMember : struct, INinoMember
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            destination = default(TMember).Ref(value);
            return 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value)
        {
            if (available < 1)
                NinoThrow.EndOfData();
            byte flag = source;
            if (flag > 1)
                NinoThrow.InvalidBool(flag);
            default(TMember).Ref(value) = flag;
            return 1;
        }
    }

    /// <summary>A string member, as <see cref="NinoUtf16Item"/> or <see cref="NinoUtf8Item"/>
    /// (<typeparamref name="TString"/>), the string run kernels' own items.</summary>
    internal readonly struct NinoStringItem<TMember, TString> : INinoFlatItem
        where TMember : struct, INinoMember
        where TString : struct, INinoRunItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) =>
            default(TString).MaxSize(ref default(TMember).Ref(value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value) =>
            default(TString).WriteAt(ref destination, ref default(TMember).Ref(value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value) =>
            default(TString).ReadAt(ref source, available, ref default(TMember).Ref(value),
                true);
    }

    /// <summary>The items composed so far, then one more; no data.</summary>
    internal readonly struct NinoFlatChain<THead, TItem> : INinoFlatItem
        where THead : struct, INinoFlatItem
        where TItem : struct, INinoFlatItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(object value) => default(THead).MaxSize(value) + default(TItem).MaxSize(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, object value)
        {
            int size = default(THead).WriteAt(ref destination, value);
            return size + default(TItem).WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, object value)
        {
            int size = default(THead).ReadAt(ref source, available, value);
            return size + default(TItem).ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, value);
        }
    }

    /// <summary>
    /// The element of a run of a sealed class: its presence byte (a fixed block item: 0, or 1 then the body), then its
    /// body. Reads make each object (<see cref="INinoFlatLayout.New"/>); an element that holds one already leaves the
    /// run (reuse stays on the generated body).
    /// </summary>
    internal readonly struct NinoSealedItem<TLayout, TBody> : INinoRunItem
        where TLayout : struct, INinoFlatLayout
        where TBody : struct, INinoFlatItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            object value = Unsafe.As<byte, object>(ref element);
            if (value == null)
                return 1;
            long body = default(TBody).MaxSize(value);
            return body >= int.MaxValue ? -1 : (int)body + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            object value = Unsafe.As<byte, object>(ref element);
            if (value == null)
            {
                destination = NinoWire.Null;
                return 1;
            }

            destination = NinoWire.NotNull;
            return 1 + default(TBody).WriteAt(ref Unsafe.Add(ref destination, 1), value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            // An element that holds an object (a read into existing elements) leaves the run: the generated body
            // keeps or recycles it as reuse does.
            if (Unsafe.As<byte, object>(ref element) != null)
                return -1;
            if (available < 1)
                NinoThrow.EndOfData();
            byte flag = source;
            if (flag != NinoWire.NotNull)
            {
                if (flag != NinoWire.Null)
                    NinoThrow.InvalidPresence(flag);
                return 1;
            }

            object value = default(TLayout).New();
            int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 1), available - 1, value);
            Unsafe.As<byte, object>(ref element) = value;
            return size + 1;
        }
    }

    /// <summary>
    /// As <see cref="NinoSealedItem{TLayout,TBody}"/>, a class that is not sealed: its type id (a fixed block item: 0
    /// for null), its exact type checked per element; a subtype, or another type id on a read, leaves the run for the
    /// generated body.
    /// </summary>
    internal readonly struct NinoTypedItem<TLayout, TBody> : INinoRunItem
        where TLayout : struct, INinoFlatLayout
        where TBody : struct, INinoFlatItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            object value = Unsafe.As<byte, object>(ref element);
            if (value == null)
                return 4;
            if (!default(TLayout).IsExact(value))
                return NinoFlatRuns.Leave;
            long body = default(TBody).MaxSize(value);
            return body > int.MaxValue - 4 ? -1 : (int)body + 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            object value = Unsafe.As<byte, object>(ref element);
            if (value == null)
            {
                Unsafe.WriteUnaligned(ref destination, NinoWire.NullTypeId);
                return 4;
            }

            Unsafe.WriteUnaligned(ref destination, default(TLayout).TypeId);
            return 4 + default(TBody).WriteAt(ref Unsafe.Add(ref destination, 4), value);
        }

        /// <summary>Returns -1 (nothing read) where the element holds an object or the id is another type's.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if (Unsafe.As<byte, object>(ref element) != null)
                return -1;
            if (available < 4)
                NinoThrow.EndOfData();
            int id = Unsafe.ReadUnaligned<int>(ref source);
            if (id != default(TLayout).TypeId)
                return id == NinoWire.NullTypeId ? 4 : -1;

            object value = default(TLayout).New();
            int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 4), available - 4, value);
            Unsafe.As<byte, object>(ref element) = value;
            return size + 4;
        }
    }

    /// <summary>
    /// The kernels of runs of flat objects: the string run kernels' write loop (NinoStringRuns.WriteFitting, every
    /// element in use: a run of references) and a read loop, both leaving the run at an element the items do not take
    /// (a subtype, another type id), which the caller writes or reads through the generated body out of line and then
    /// goes on.
    /// </summary>
    internal static class NinoFlatRuns
    {
        /// <summary>The <see cref="INinoRunItem.MaxSize"/> of an element the run leaves (a subtype).</summary>
        public const int Leave = int.MinValue;

        /// <summary>
        /// Writes <paramref name="run"/>'s elements from <paramref name="next"/> on until one leaves the run
        /// (<paramref name="next"/> is then its index) or all are written (<paramref name="next"/> the count).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer Write<TItem>(scoped NinoRun run, scoped ref int next, Writer writer)
            where TItem : struct, INinoRunItem
        {
            int from = next;
            while (true)
            {
                ulong left = WriteFitting<TItem>(run, from, ref writer.FreeSpace(out int available), available);
                writer.Commit(available - (int)left);
                int entries = (int)(left >> 32);
                from = run.Count - entries;
                if (entries == 0)
                    break;
                int room = default(TItem).MaxSize(ref Unsafe.Add(ref run.First, (nint)from * run.Stride));
                if (room == Leave)
                    break;
                writer.Ensure(room);
            }

            next = from;
            return writer;
        }

        /// <summary>The string run kernels' write loop with every element in use (a constant: one copy of the
        /// loop).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteFitting<TItem>(scoped NinoRun run, int next, ref byte destination, int available)
            where TItem : struct, INinoRunItem =>
            NinoStringRuns.WriteFitting(run, default(TItem), next, ref destination, available, NinoRun.AllInUse);

        /// <summary>
        /// Reads <paramref name="run"/>'s elements from <paramref name="next"/> on from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes) until one leaves the run (<paramref name="next"/> is
        /// then its index) or all are read; returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Read<TItem>(scoped NinoRun run, scoped ref int next, ref byte data, int end)
            where TItem : struct, INinoRunItem
        {
            ref byte source = ref data;
            int stride = run.Stride, available = end;
            ref byte element = ref Unsafe.Add(ref run.First, (nint)next * stride);
            int left = run.Count - next;
            for (; left > 0; left--)
            {
                int read = default(TItem).ReadAt(ref source, available, ref element, true);
                if (read < 0)
                    break;
                source = ref Unsafe.Add(ref source, (nint)(uint)read);
                available -= read;
                element = ref NinoRun.Next(ref element, stride);
            }

            next = run.Count - left;
            return end - available;
        }

        /// <summary>
        /// The write of a run of <typeparamref name="T"/> (not empty) through <typeparamref name="TItem"/>, an element
        /// the items leave written by <typeparamref name="TFormatter"/>'s generated body: what a generated
        /// <see cref="INinoFormatter.WriteFlat"/> calls.
        /// </summary>
        public static Writer WriteRun<T, TFormatter, TItem>(scoped NinoRun run, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoRunItem
        {
            // Bodies too deep are deferred by the generated body: the whole run goes that way.
            if (writer.Depth >= NinoDepth.Inline)
                return NinoRuns.WriteEach<T, TFormatter>(run, NinoRuns.NoLength, writer);
            int next = 0, count = run.Count;
            while (true)
            {
                writer = Write<TItem>(run, ref next, writer);
                if (next >= count)
                    return writer;
                writer = WriteOne<T, TFormatter>(ref Unsafe.Add(ref run.First, (nint)next * run.Stride), writer);
                if (++next == count)
                    return writer;
            }
        }

        /// <summary>An element the items leave, through the generated body, out of line.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteOne<T, TFormatter>(scoped ref byte element, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
        {
            default(TFormatter).Serialize(Unsafe.As<byte, T>(ref element), ref writer);
            return writer;
        }

        /// <summary>
        /// As <see cref="WriteRun{T,TFormatter,TItem}"/>, the read in place (a run in a row). An element that holds an
        /// object (a read into existing elements) leaves the run for the generated body, which keeps or recycles it as
        /// reuse does.
        /// </summary>
        public static Reader ReadRun<T, TFormatter, TItem>(scoped NinoRun run, Reader reader)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoRunItem
        {
            int count = run.Count, stride = run.Stride;
            if (reader.Depth >= NinoDepth.Inline)
                return ReadEach<T, TFormatter>(ref run.First, count, stride, reader);

            int next = 0;
            while (true)
            {
                reader.UnsafeAdvance(Read<TItem>(run, ref next, ref reader.Peek(), reader.Remaining));
                if (next >= count)
                    return reader;
                reader = ReadOne<T, TFormatter>(ref Unsafe.Add(ref run.First, (nint)next * stride), reader);
                if (++next == count)
                    return reader;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadOne<T, TFormatter>(scoped ref byte element, Reader reader)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
        {
            default(TFormatter).Deserialize(ref reader, ref Unsafe.As<byte, T>(ref element));
            return reader;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadEach<T, TFormatter>(scoped ref byte first, int count, int stride, Reader reader)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
        {
            NinoRuns.ReadLoop<T, TFormatter>(ref first, count, stride, ref reader);
            return reader;
        }

        /// <summary>
        /// One body through the items (no header): what a formatter whose generated body is removed would call for a
        /// single value (a member, a top-level value). The value is exactly the type. Out of line on a local copy of
        /// the writer, as the generated body (its fields in registers, not in the caller's memory).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void WriteBody<TBody>(object value, ref Writer writer) where TBody : struct, INinoFlatItem
        {
            Writer local = default;
            writer.CopyCursorTo(ref local);
            long max = default(TBody).MaxSize(value);
            local.Commit(default(TBody).WriteAt(ref local.Ensure(max > int.MaxValue ? -1 : (int)max), value));
            local.CopyCursorTo(ref writer);
        }

        /// <summary>As <see cref="WriteBody{TBody}"/>, the read into a new object.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ReadBody<TBody>(object value, ref Reader reader) where TBody : struct, INinoFlatItem
        {
            Reader local = default;
            reader.CopyCursorTo(ref local);
            local.UnsafeAdvance(default(TBody).ReadAt(ref local.Peek(), local.Remaining, value));
            local.CopyCursorTo(ref reader);
        }
    }
}
