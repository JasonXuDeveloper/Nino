using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The element of a run of a sealed class whose body is flat (<see cref="NinoFlat{TLayout,TBody}"/>): its presence
    /// byte, then its body. Reads make each object (<see cref="INinoFlatLayout.Create"/>); an element that holds one already
    /// leaves the run (a read into existing elements goes through the formatter, which keeps or recycles it).
    /// </summary>
    internal readonly struct NinoFlatSealedItem<TLayout, TBody> : INinoRunItem
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

        /// <summary>Returns -1 (nothing read) where the element holds an object.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if (Unsafe.As<byte, object>(ref element) != null)
                return NinoFlatRuns.Leave;
            if (available < 1)
                NinoThrow.EndOfData();
            byte flag = source;
            if (flag != NinoWire.NotNull)
            {
                if (flag != NinoWire.Null)
                    NinoThrow.InvalidPresence(flag);
                return 1;
            }

            object value = default(TLayout).Create();
            int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 1), available - 1, value, true);
            Unsafe.As<byte, object>(ref element) = value;
            return size + 1;
        }
    }

    /// <summary>
    /// As <see cref="NinoFlatSealedItem{TLayout,TBody}"/>, a class that is not sealed: its type id (0 for null), its
    /// exact type checked per element; a subtype, or another type id on a read, leaves the run.
    /// </summary>
    internal readonly struct NinoFlatTypedItem<TLayout, TBody> : INinoRunItem
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
                return NinoFlatRuns.Leave;
            if (available < 4)
                NinoThrow.EndOfData();
            int id = Unsafe.ReadUnaligned<int>(ref source);
            if (id != default(TLayout).TypeId)
                return id == NinoWire.NullTypeId ? 4 : NinoFlatRuns.Leave;

            object value = default(TLayout).Create();
            int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 4), available - 4, value, true);
            Unsafe.As<byte, object>(ref element) = value;
            return size + 4;
        }
    }

    /// <summary>
    /// The kernels of runs of flat objects (#355, the generator seam): the string run kernels' write loop
    /// (<see cref="NinoStringRuns"/>' WriteFitting, every element in use) and a read loop, both stopping at an element the
    /// items leave (a subtype, another type id, an element that holds an object), which goes through the formatter out
    /// of line before the loop goes on. Generic over the element item only (structs): code specialized per type, never
    /// shared over a class; the drivers over <c>T</c> hold no loop.
    /// </summary>
    internal static class NinoFlatRuns
    {
        /// <summary>What an element item's MaxSize or ReadAt returns for an element the run leaves.</summary>
        public const int Leave = int.MinValue;

        /// <summary>
        /// The write of a run of <typeparamref name="T"/> (not empty, every element in use) through
        /// <typeparamref name="TItem"/>; an element it leaves through <typeparamref name="TFormatter"/>.
        /// </summary>
        public static Writer WriteRun<T, TFormatter, TItem>(NinoRun run, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoRunItem
        {
            // Bodies too deep are deferred by the formatter: the whole run goes that way.
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

        /// <summary>
        /// Writes <paramref name="run"/>'s elements from <paramref name="next"/> on until one leaves the run
        /// (<paramref name="next"/> is then its index) or all are written (<paramref name="next"/> the count).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer Write<TItem>(scoped NinoRun run, scoped ref int next, Writer writer)
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

        /// <summary>The string run kernels' write loop, every element in use (one copy of the loop).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteFitting<TItem>(scoped NinoRun run, int next, ref byte destination, int available)
            where TItem : struct, INinoRunItem =>
            NinoStringRuns.WriteFitting(run, default(TItem), next, ref destination, available, NinoRun.AllInUse);

        /// <summary>An element the items leave, through its formatter, out of line.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteOne<T, TFormatter>(scoped ref byte element, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
        {
            default(TFormatter).Serialize(Unsafe.As<byte, T>(ref element), ref writer);
            return writer;
        }

        /// <summary>As <see cref="WriteRun{T,TFormatter,TItem}"/>, the read in place (values in a row).</summary>
        public static Reader ReadRun<T, TFormatter, TItem>(NinoRun run, Reader reader)
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

        /// <summary>
        /// Reads <paramref name="run"/>'s elements from <paramref name="next"/> on from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes) until one leaves the run (<paramref name="next"/> is
        /// then its index) or all are read; returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Read<TItem>(scoped NinoRun run, scoped ref int next, ref byte data, int end)
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
    }
}
