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
        /// <summary>What an element item's MaxSize returns for an element the run leaves (ReadAt returns any negative).</summary>
        public const long Leave = long.MinValue;

        /// <summary>
        /// What an item's ReadAt returns for an element the run leaves (<see cref="NinoKeyItem{TKey,TItem}"/>,
        /// <see cref="NinoRegionItem{TBody}"/>, a nested class's element item): so far below zero that the bytes the
        /// items before it read, added to it, keep the chain's sum negative, with no test per node.
        /// </summary>
        public const int Left = int.MinValue;

        /// <summary>
        /// The fewest elements a flat struct's run takes the kernels with (<c>NinoRuns.ShortFlatValues</c>); a class's
        /// runs take them at every length.
        /// </summary>
        public const int MinRun = 16;

        /// <summary>
        /// The write of a run of <typeparamref name="T"/> (not empty; a class's every element in use, a struct's free
        /// entries skipped) through <typeparamref name="TItem"/>; an element it leaves through
        /// <typeparamref name="TFormatter"/>. Inlined into the hook, on its writer: the run by <c>in</c> and the writer
        /// by reference, never by value (a copy right after the stores that built it waits for them, a fixed cost
        /// per run).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteRun<T, TFormatter, TItem>(in NinoRun run, ref Writer writer, int depth)
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoItem
        {
            // Bodies too deep are deferred by the formatter: the whole run goes that way. Depth: the class bodies an
            // element opens (its own and its nested members', a generator literal), the last at Depth + depth - 1.
            if (writer.Depth + depth > NinoDepth.Inline)
            {
                writer = NinoRuns.WriteEach<T, TFormatter>(run, NinoRuns.NoLength, writer);
                return;
            }

            int next = 0, count = run.Count, left = 0;
            while (true)
            {
                Write<TItem>(run, ref next, ref writer);
                if (next >= count)
                    return;
                writer = WriteOne<T, TFormatter>(ref Unsafe.Add(ref run.First, (nint)next * run.Stride), writer);
                if (++next == count)
                    return;
                // Many elements leave (a base's run mixed with its subtypes): the rest element by element. Entering the
                // kernels again after each one, a run of a flat base, every other element a subtype, wrote in 1.6 to 2.0
                // times the time of the base's run loop (32 and 1,000 elements), where one in 13 took 0.67 to 0.83 (#440).
                if (++left * MixedRun > next + MixedGrace)
                {
                    writer = WriteRest<T, TFormatter>(ref Unsafe.Add(ref run.First, (nint)next * run.Stride),
                        count - next, run.Stride, run.InUse, writer);
                    return;
                }
            }
        }

        /// <summary>
        /// A run whose elements leave once in more than <see cref="MixedRun"/> (after the first
        /// <see cref="MixedGrace"/> / <see cref="MixedRun"/> leaves) takes the rest element by element
        /// (<see cref="WriteRun{T,TFormatter,TItem}"/>).
        /// </summary>
        private const int MixedRun = 8, MixedGrace = 16;

        /// <summary>The rest of a run whose elements leave often, element by element through the formatter, out of line.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteRest<T, TFormatter>(scoped ref byte first, int count, int stride, int inUse,
            Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            NinoRuns.WriteLoop<T, TFormatter>(ref first, count, stride, NinoRun.NoKey, inUse, ref writer);
            return writer;
        }

        /// <summary>
        /// Writes <paramref name="run"/>'s elements from <paramref name="next"/> on until one leaves the run
        /// (<paramref name="next"/> is then its index) or all are written (<paramref name="next"/> the count).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Write<TItem>(scoped in NinoRun run, scoped ref int next, ref Writer writer)
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
        }

        /// <summary>The string run kernels' write loop, every element in use (one copy of the loop).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteFitting<TItem>(scoped in NinoRun run, int next, ref byte destination, int available)
            where TItem : struct, INinoItem =>
            NinoStringRuns.WriteFitting(run, default(TItem), next, ref destination, available, NinoRun.AllInUse);

        /// <summary>As <see cref="WriteFitting{TItem}"/>, skipping free entries (a set's or a dictionary's structs).
        /// Its own copy of the loop, compiled only where such a run is written.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ulong WriteSkipping<TItem>(scoped in NinoRun run, int next, ref byte destination, int available)
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReadRun<T, TFormatter, TItem>(in NinoRun run, ref Reader reader, int depth)
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoItem
        {
            int count = run.Count, stride = run.Stride;
            if (reader.Depth + depth > NinoDepth.Inline)
            {
                reader = ReadEach<T, TFormatter>(ref run.First, count, stride, reader);
                return;
            }

            int next = 0;
            while (true)
            {
                reader.UnsafeAdvance(Read<TItem>(run, ref next, ref reader.Peek(), reader.Remaining));
                if (next >= count)
                    return;
                reader = ReadOne<T, TFormatter>(ref Unsafe.Add(ref run.First, (nint)next * stride), reader);
                if (++next == count)
                    return;
            }
        }

        /// <summary>
        /// Reads <paramref name="run"/>'s elements from <paramref name="next"/> on from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes) until one leaves the run (<paramref name="next"/> is
        /// then its index) or all are read; returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Read<TItem>(scoped in NinoRun run, scoped ref int next, ref byte data, int end)
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
