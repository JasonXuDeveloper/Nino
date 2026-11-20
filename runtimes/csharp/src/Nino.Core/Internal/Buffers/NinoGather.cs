using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// A collection that is not one contiguous run (a tree, a linked list, an enumerator), walked into a chunk at a
    /// time (<see cref="NinoGather.Write{T,TFormatter,TSource}"/>). A struct, so the calls are direct.
    /// </summary>
    internal interface INinoGatherSource<T>
    {
        /// <summary>
        /// Copies the next elements into <paramref name="chunk"/>, as many as it holds, fewer only at the end; returns
        /// how many.
        /// </summary>
        int Fill(Span<T> chunk);

        /// <summary>
        /// After the last element the writer takes: throws <see cref="NinoThrow.ModifiedDuringWrite"/> where the
        /// collection holds more than it said or changed during the walk, and gives back what the walk took.
        /// </summary>
        void End();
    }

    /// <summary>
    /// A collection that takes the elements read for it a chunk at a time (<see cref="NinoGather.Read{T,TFormatter,TTarget}"/>):
    /// added, linked, or copied into its storage. A struct, so the calls are direct.
    /// </summary>
    internal interface INinoGatherTarget<T>
    {
        /// <summary>Takes <paramref name="chunk"/>'s elements, the next in the order they were written.</summary>
        void Add(Span<T> chunk);
    }

    /// <summary>
    /// The gather of the collections that are not one contiguous run (#355): their elements are walked into a chunk
    /// of Nino's own per-thread buffer (<see cref="NinoGather{T}"/>, at most <see cref="ChunkBytes"/>), and each
    /// chunk is written by the same per-kind loop every contiguous collection writes through
    /// (<see cref="NinoRuns.Write{T,TFormatter}(ReadOnlySpan{T}, ref Writer)"/>): raw values and blocks as one block,
    /// slots through their kernels, strings through the cursor loop, a polymorphic base through its generated loop.
    /// Reads fill a chunk through the read loop and hand it to the collection, which links or adopts it. A large
    /// collection never needs a buffer of its size, and nothing large is ever kept.
    /// </summary>
    internal static class NinoGather
    {
        /// <summary>
        /// The bytes of one chunk: a whole number of 64-byte vectors that stays in L1/L2 with the data around it. Not
        /// tuned yet (#355: set by an A/B on x64 and Arm64 when the storage layer lands).
        /// </summary>
        public const int ChunkBytes = 16 * 1024;

        /// <summary>
        /// Whether a known count of <typeparamref name="T"/> is gathered straight into the writer's buffer: values
        /// whose memory is their wire form (raw without padding, or a block that is its memory,
        /// <see cref="NinoBlockPlan{T,TFormatter}.IsMemory"/>), so the walk's copy is the write. A JIT-time constant
        /// for values with references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InPlace<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            !NinoUnsafe.IsReferenceOrContainsReferences<T>() && Memory<T, TFormatter>.Is;

        private static class Memory<T, TFormatter> where TFormatter : struct, INinoFormatter<T>
        {
            public static readonly bool Is = BitConverter.IsLittleEndian &&
                (NinoKind.Of<T, TFormatter>() == NinoElementKind.Raw && !NinoLayout<T>.HasPadding ||
                 NinoKind.Of<T, TFormatter>() == NinoElementKind.Block && NinoBlockPlan<T, TFormatter>.IsMemory);
        }

        /// <summary>
        /// Whether reading <typeparamref name="T"/> a chunk at a time pays: the read loop moves several at once (raw
        /// values, slots, blocks, the cursor form). A value read through its formatter alone gains nothing from a
        /// chunk but a copy, and is handed over one at a time. A JIT-time constant.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadsChunks<T, TFormatter>() where TFormatter : struct, INinoFormatter<T> =>
            NinoKind.Of<T, TFormatter>() != NinoElementKind.PerValue || NinoCursor.Own<T, TFormatter>();

        /// <summary>
        /// Writes the count (<paramref name="count"/>, or the elements the source gave where it is -1, patched
        /// after) and the elements of <paramref name="source"/>, a chunk at a time through the run loops. Out of line
        /// with the writer by value and returned, as the run loops' own drivers: a reference to the caller's writer
        /// passed to a call keeps it in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Writer Write<T, TFormatter, TSource>(TSource source, int count, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
            where TSource : struct, INinoGatherSource<T>
        {
            if (count >= 0 && InPlace<T, TFormatter>())
            {
                // The writer's free space is the chunk: no copy but the walk's.
                ref byte at = ref writer.ReserveCounted(count, Unsafe.SizeOf<T>());
                if (count != 0 && source.Fill(MemoryMarshal.CreateSpan(ref Unsafe.As<byte, T>(ref at), count)) != count)
                    NinoThrow.ModifiedDuringWrite();
                source.End();
                return writer;
            }

            int patch = -1;
            if (count >= 0)
                writer.WriteLength(count);
            else
                patch = writer.ReserveInt32();
            var chunk = NinoGather<T>.Take();
            var span = chunk.Span;
            long total = 0;
            int used = 0;
            while (true)
            {
                int filled = source.Fill(span);
                if (filled == 0)
                    break;
                total += filled;
                if (filled > used)
                    used = filled;
                if (count >= 0 ? total > count : total > int.MaxValue)
                    NinoThrow.ModifiedDuringWrite();
                NinoRuns.Write<T, TFormatter>((ReadOnlySpan<T>)span.Slice(0, filled), ref writer);
                if (filled < span.Length)
                    break;
            }

            NinoGather<T>.Return(chunk, used);
            source.End();
            if (count >= 0 && total != count)
                NinoThrow.ModifiedDuringWrite();
            if (patch >= 0)
                writer.PatchInt32(patch, (int)total);
            return writer;
        }

        /// <summary>
        /// Writes the count and the <paramref name="count"/> elements of <paramref name="collection"/> (at most a
        /// chunk of them) copied out by its <c>CopyTo</c>, one call where an enumerator makes two interface calls per
        /// element (a list-backed collection copies its array in one). Inlined, the writer in the caller's registers:
        /// nothing here takes it but the run's write, as an array's.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteCopied<T, TFormatter>(ICollection<T> collection, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            var chunk = NinoGather<T>.Take();
            collection.CopyTo(chunk.Array, chunk.Start);
            NinoCollections.WriteSpan<T, TFormatter>(chunk.Span.Slice(0, count), ref writer);
            NinoGather<T>.Return(chunk, count);
        }

        /// <summary>
        /// Reads <paramref name="length"/> elements (not 0) and hands them to <paramref name="target"/> a chunk at a
        /// time, each chunk filled by the run's read loop
        /// (<see cref="NinoRuns.Read{T,TFormatter}(Span{T}, ref Reader, bool)"/>), whose checks bound what each takes
        /// from the data. Inlined into <see cref="ReadAll{T,TFormatter,TTarget}"/>, the reader a local there. A read
        /// rejected half way leaves its chunk to the collector, never back in the buffer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Reader Read<T, TFormatter, TTarget>(scoped ref TTarget target, int length, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
            where TTarget : struct, INinoGatherTarget<T>
        {
            var chunk = NinoGather<T>.Take();
            var span = chunk.Span;
            int used = Math.Min(length, span.Length);
            for (int left = length; left > 0;)
            {
                int count = Math.Min(left, span.Length);
                var part = span.Slice(0, count);
                // The read loop reuses what the slots hold: the last chunk's values, which belong to the collection.
                if (!NinoElements.ReadOverwrites<T, TFormatter>())
                    part.Clear();
                NinoRuns.Read<T, TFormatter>(part, ref reader);
                target.Add(part);
                left -= count;
            }

            NinoGather<T>.Return(chunk, used);
            return reader;
        }

        /// <summary>
        /// Reads <paramref name="length"/> elements and hands them to <paramref name="target"/>: a chunk at a time
        /// where that pays (<see cref="ReadsChunks{T,TFormatter}"/>), else one at a time. Out of line with the reader by
        /// value, one call in the collection's read: inlined, both loops and their setup took registers and stack from
        /// every caller (asm.py: a LinkedList&lt;int&gt;'s read 29 to 62 stack accesses).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Reader ReadAll<T, TFormatter, TTarget>(scoped ref TTarget target, int length, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
            where TTarget : struct, INinoGatherTarget<T>
        {
            if (length == 0)
                return reader;
            return ReadsChunks<T, TFormatter>()
                ? Read<T, TFormatter, TTarget>(ref target, length, reader)
                : ReadEach<T, TFormatter, TTarget>(ref target, length, reader);
        }

        /// <summary>
        /// Reads <paramref name="length"/> elements one at a time, each handed to <paramref name="target"/> as it is
        /// read: the one loop of reads one by one into a store (a collection's adds, a hashed collection's entries read
        /// apart, NinoEntries' adds).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static Reader ReadEach<T, TFormatter, TTarget>(scoped ref TTarget target, int length, Reader reader)
            where TFormatter : struct, INinoFormatter<T>
            where TTarget : struct, INinoGatherTarget<T>
        {
            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref reader, ref item);
                target.Add(MemoryMarshal.CreateSpan(ref item, 1));
            }

            return reader;
        }

        /// <summary>The elements of an enumerator, its own struct one where the collection has one (direct calls).</summary>
        internal struct Enumerated<T, TEnumerator> : INinoGatherSource<T> where TEnumerator : IEnumerator<T>
        {
            private TEnumerator _items;

            public Enumerated(TEnumerator items) => _items = items;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Fill(Span<T> chunk)
            {
                // By reference: the chunk's length is not known to the JIT to bound what the enumerator gives.
                ref T at = ref MemoryMarshal.GetReference(chunk);
                int filled = 0;
                while (filled < chunk.Length && _items.MoveNext())
                {
                    Unsafe.Add(ref at, filled) = _items.Current;
                    filled++;
                }

                return filled;
            }

            public void End()
            {
                bool more = _items.MoveNext();
                _items.Dispose();
                if (more)
                    NinoThrow.ModifiedDuringWrite();
            }
        }

        /// <summary>A collection that takes each element through <typeparamref name="TAdder"/>'s <c>Add</c>.</summary>
        internal struct Adds<TCollection, T, TAdder> : INinoGatherTarget<T> where TAdder : struct, INinoAdder<TCollection, T>
        {
            private readonly TCollection _collection;

            public Adds(TCollection collection) => _collection = collection;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Add(Span<T> chunk)
            {
                foreach (var item in chunk)
                    default(TAdder).Add(_collection, item);
            }
        }
    }

    /// <summary>
    /// The chunks of <see cref="NinoGather"/> for <typeparamref name="T"/>: arrays of <see cref="NinoGather.ChunkBytes"/>
    /// (at least one element), per thread, taken and put back so a nested gather of the same type gets one of its own
    /// (a few are kept for nesting). A chunk of values without references is allocated pinned on .NET 8+ and starts at a
    /// 64-byte boundary where the element size allows, so the kernels' stores into it are aligned; one with references
    /// is cleared when it comes back, so it never keeps an object alive. An array per type rather than one byte buffer
    /// for every unmanaged type: <see cref="ICollection{T}.CopyTo"/> copies only into a <c>T[]</c>.
    /// </summary>
    internal static class NinoGather<T>
    {
        /// <summary>The elements of a chunk.</summary>
        public static readonly int Length = Math.Max(1, NinoGather.ChunkBytes / Unsafe.SizeOf<T>());

        // Room to move the start to a 64-byte boundary.
        private static readonly int Spare = NinoUnsafe.IsReferenceOrContainsReferences<T>() || Unsafe.SizeOf<T>() > 64
            ? 0
            : 64 / Unsafe.SizeOf<T>();

        private const int Kept = 4;

        [ThreadStatic] private static Chunk[] t_kept;
        [ThreadStatic] private static int t_count;

        /// <summary>A chunk: <see cref="Length"/> elements of <see cref="Array"/> from <see cref="Start"/>.</summary>
        public readonly struct Chunk
        {
            public readonly T[] Array;
            public readonly int Start;

            public Chunk(T[] array, int start)
            {
                Array = array;
                Start = start;
            }

            public Span<T> Span
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => new(Array, Start, Length);
            }
        }

        /// <summary>A chunk of this thread's, or a new one. Its slots hold no references. Out of line, as
        /// <see cref="Return"/>: a call, as renting is, where inlined the thread-static lookups and the allocation took
        /// registers and stack from the write around them (asm.py: ReadOnlyCollection&lt;int&gt;'s write).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Chunk Take()
        {
            int count = t_count;
            if (count == 0)
                return Allocate();
            t_count = --count;
            return t_kept[count];
        }

        /// <summary>
        /// Gives <paramref name="chunk"/> back, its first <paramref name="used"/> elements cleared where they may hold
        /// references. A chunk not given back (a write or read that threw) is left to the collector.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Return(Chunk chunk, int used)
        {
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                System.Array.Clear(chunk.Array, chunk.Start, used);
            int count = t_count;
            if (count == Kept)
                return;
            (t_kept ??= new Chunk[Kept])[count] = chunk;
            t_count = count + 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Chunk Allocate()
        {
#if NET8_0_OR_GREATER
            if (Spare != 0)
            {
                var pinned = GC.AllocateUninitializedArray<T>(Length + Spare, pinned: true);
                return new Chunk(pinned, AlignedStart(pinned));
            }
#endif
            return new Chunk(new T[Length + Spare], 0);
        }

#if NET8_0_OR_GREATER
        // The first element at a 64-byte boundary, where the element size divides the distance to it.
        private static unsafe int AlignedStart(T[] array)
        {
            nuint address = (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));
            int distance = (int)((64 - (address & 63)) & 63);
            return distance % Unsafe.SizeOf<T>() == 0 ? distance / Unsafe.SizeOf<T>() : 0;
        }
#endif
    }
}
