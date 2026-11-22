using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
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
        /// What an item's ReadAt returns for an element the run leaves (<see cref="NinoKeyItem{TKey,TItem}"/>,
        /// <see cref="NinoRegionItem{TBody}"/>, a nested class's element item): so far below zero that the bytes the
        /// items before it read, added to it, keep the chain's sum negative, with no test per node.
        /// </summary>
        public const int Left = int.MinValue;

        /// <summary>
        /// The write of a run of <typeparamref name="T"/> (not empty; a class's every element in use, a struct's free
        /// entries skipped) through <typeparamref name="TItem"/>; an element it leaves through
        /// <typeparamref name="TFormatter"/>.
        /// </summary>
        public static Writer WriteRun<T, TFormatter, TItem>(NinoRun run, Writer writer, int depth)
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoItem
        {
            // Bodies too deep are deferred by the formatter: the whole run goes that way. Depth: the class bodies an
            // element opens (its own and its nested members', a generator literal), the last at Depth + depth - 1.
            if (writer.Depth + depth > NinoDepth.Inline)
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
                ulong left = run.InUse == NinoRun.AllInUse
                    ? WriteFitting<TItem>(run, from, ref writer.FreeSpace(out int available), available)
                    : WriteSkipping<TItem>(run, from, ref writer.FreeSpace(out available), available);
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

        /// <summary>As <see cref="WriteFitting{TItem}"/>, skipping free entries (a set's or a dictionary's structs).
        /// Its own copy of the loop, compiled only where such a run is written.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteSkipping<TItem>(scoped NinoRun run, int next, ref byte destination, int available)
            where TItem : struct, INinoItem =>
            NinoStringRuns.WriteFitting(run, default(TItem), next, ref destination, available, run.InUse);

        /// <summary>An element the items leave, through its formatter, out of line.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteOne<T, TFormatter>(scoped ref byte element, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            default(TFormatter).Serialize(Unsafe.As<byte, T>(ref element), ref writer);
            return writer;
        }

        /// <summary>As <see cref="WriteRun{T,TFormatter,TItem}"/>, the read in place (values in a row).</summary>
        public static Reader ReadRun<T, TFormatter, TItem>(NinoRun run, Reader reader, int depth)
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoItem
        {
            int count = run.Count, stride = run.Stride;
            if (reader.Depth + depth > NinoDepth.Inline)
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
                // Not fresh: a struct's strings equal to those it holds are kept (a class's element item tells a
                // new object itself).
                int read = default(TItem).ReadAt(ref source, available, ref element, false);
                if (default(TItem).Leaves && read < 0)
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
            where TFormatter : struct, INinoFormatter<T>
        {
            default(TFormatter).Deserialize(ref reader, ref Unsafe.As<byte, T>(ref element));
            return reader;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadEach<T, TFormatter>(scoped ref byte first, int count, int stride, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            NinoRuns.ReadLoop<T, TFormatter>(ref first, count, stride, ref reader);
            return reader;
        }
    }
}
