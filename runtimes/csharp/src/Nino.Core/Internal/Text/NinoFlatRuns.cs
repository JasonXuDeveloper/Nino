using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// The element of a run of a sealed class whose body is flat (<see cref="NinoFlat{TLayout,TBody}"/>): its presence
    /// byte, then its body. Reads go into the object an element holds, or make one (<see cref="INinoFlatLayout.Create"/>).
    /// </summary>
    internal readonly struct NinoFlatSealedItem<TLayout, TBody> : INinoItem
        where TLayout : struct, INinoFlatLayout
        where TBody : struct, INinoItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte element)
        {
            object value = Unsafe.As<byte, object>(ref element);
            if (value == null)
                return 1;
            return default(TBody).MaxSize(ref Unsafe.As<object, byte>(ref value)) + 1;
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
            return 1 + default(TBody).WriteAt(ref Unsafe.Add(ref destination, 1), ref Unsafe.As<object, byte>(ref value));
        }

        /// <summary>
        /// Reads the element into the object it holds (its strings kept where unchanged), or into a new one; null
        /// clears it, as the formatter's read into an existing value does.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if (available < 1)
                NinoThrow.EndOfData();
            byte flag = source;
            if (flag != NinoWire.NotNull)
            {
                if (flag != NinoWire.Null)
                    NinoThrow.InvalidPresence(flag);
                Unsafe.As<byte, object>(ref element) = null;
                return 1;
            }

            // One copy of the body's reads for new and existing objects: a second inlined into the loop (fresh as a
            // constant) spilled it to 87 stack accesses (ScatteredTest's read into a million items, 1.6 times the base).
            object value = Unsafe.As<byte, object>(ref element);
            bool made = value == null;
            if (made)
                value = default(TLayout).Create();
            int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 1), available - 1,
                ref Unsafe.As<object, byte>(ref value), made);
            if (made)
                Unsafe.As<byte, object>(ref element) = value; // after the read: a rejected one leaves no new object
            return size + 1;
        }
    }

    /// <summary>
    /// As <see cref="NinoFlatSealedItem{TLayout,TBody}"/>, a class that is not sealed: its type id (0 for null), its
    /// exact type checked per element; a subtype, or another type id on a read, leaves the run.
    /// </summary>
    internal readonly struct NinoFlatTypedItem<TLayout, TBody> : INinoItem
        where TLayout : struct, INinoFlatLayout
        where TBody : struct, INinoItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long MaxSize(ref byte element)
        {
            object value = Unsafe.As<byte, object>(ref element);
            if (value == null)
                return 4;
            if (!default(TLayout).IsExact(value))
                return NinoFlatRuns.Leave;
            return default(TBody).MaxSize(ref Unsafe.As<object, byte>(ref value)) + 4;
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
            return 4 + default(TBody).WriteAt(ref Unsafe.Add(ref destination, 4), ref Unsafe.As<object, byte>(ref value));
        }

        /// <summary>
        /// Reads the element into the object it holds where that is of exactly the type, or into a new one where it
        /// holds none; returns -1 (nothing read) for another type id, or an element holding a subtype or null data,
        /// which the formatter reads.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if (available < 4)
                NinoThrow.EndOfData();
            int id = Unsafe.ReadUnaligned<int>(ref source);
            object value = Unsafe.As<byte, object>(ref element);
            if (id != default(TLayout).TypeId)
                return id == NinoWire.NullTypeId && value == null ? 4 : -1;
            bool made = value == null;
            if (made)
                value = default(TLayout).Create();
            else if (!default(TLayout).IsExact(value))
                return -1;
            int size = default(TBody).ReadAt(ref Unsafe.Add(ref source, 4), available - 4,
                ref Unsafe.As<object, byte>(ref value), made);
            if (made)
                Unsafe.As<byte, object>(ref element) = value; // after the read: a rejected one leaves no new object
            return size + 4;
        }
    }

    /// <summary>
    /// The kernels of runs of flat objects (#355, the generator seam): the string run kernels' write loop
    /// (<see cref="NinoStringRuns"/>' WriteFitting, every element in use) and a read loop, both stopping at an element the
    /// items leave (a subtype, another type id, an element holding a subtype), which goes through the formatter out
    /// of line before the loop goes on. Generic over the element item only (structs): code specialized per type, never
    /// shared over a class; the drivers over <c>T</c> hold no loop.
    /// </summary>
    internal static class NinoFlatRuns
    {
        /// <summary>
        /// The fewest elements a run takes the flat kernels with: their calls (the hook, the driver, the loops out of
        /// line, the writer or reader by value through each) cost a constant per run that a short run does not win
        /// back, so shorter ones keep their element-by-element loop. 2 elements of a class with one block and a string
        /// were written in 2.2 times the time, 8 on par, 32 in 0.6 (local x64, Tier1, #355).
        /// </summary>
        public const int MinRun = 16;

        /// <summary>What an element item's MaxSize returns for an element the run leaves (ReadAt returns any negative).</summary>
        public const long Leave = long.MinValue;

        /// <summary>
        /// The write of a run of <typeparamref name="T"/> (not empty, every element in use) through
        /// <typeparamref name="TItem"/>; an element it leaves through <typeparamref name="TFormatter"/>.
        /// </summary>
        public static Writer WriteRun<T, TFormatter, TItem>(NinoRun run, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoItem
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
            where TItem : struct, INinoItem
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
                int room = NinoStringRuns.RoomFor(run, default(TItem), from);
                if (room == int.MinValue)
                    break; // Leave
                writer.Ensure(room);
            }

            next = from;
            return writer;
        }

        /// <summary>The string run kernels' write loop, every element in use (one copy of the loop).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteFitting<TItem>(scoped NinoRun run, int next, ref byte destination, int available)
            where TItem : struct, INinoItem =>
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
            where TItem : struct, INinoItem
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
            where TItem : struct, INinoItem
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
