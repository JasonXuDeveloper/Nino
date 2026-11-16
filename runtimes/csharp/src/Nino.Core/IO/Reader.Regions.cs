using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Slices, length-prefixed regions and deferred bodies; the fields and the cursor are in Reader.cs.
    public ref partial struct Reader
    {
        /// <summary>
        /// Call when done with a reader returned by <see cref="ReadSlice"/>: objects it read stay visible to
        /// later back references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndSlice(in Reader slice)
        {
            _context ??= slice._context;
        }

        /// <summary>
        /// Generated code: reads a length-prefixed region (a version-tolerant body, a keyed body or member),
        /// written between <see cref="Writer.BeginRegion"/> and <see cref="Writer.EndRegion"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader ReadRegion()
        {
            // Two bodies, the one exception to the cursor section: the .NET 8+ body written against the fields is
            // the IL size its keyed callers' inlining was measured at; one body through cursor primitives (a ref and
            // the bytes left) changed what they inline (optimize skill, jit-io.md).
#if NET8_0_OR_GREATER
            int remaining = _avail - sizeof(int);
            if (remaining < 0)
                NinoThrow.EndOfData();
            ref byte data = ref Unsafe.Add(ref _cur, sizeof(int));
            int length = NinoUnsafe.Read<int>(ref _cur);
            int members = length;
            if ((uint)length > (uint)remaining)
            {
                // Keep the consumed header visible if the section fails validation.
                _cur = ref data;
                _avail = remaining;
                members = SectionedRegion(ref data, remaining, length, _regions);
                length &= int.MaxValue;
            }

            var region = new Reader(ref _origin, ref data, members, Depth, _regions + 1, _context);
            _cur = ref Unsafe.Add(ref data, (nuint)(uint)length);
            _avail = remaining - length;
            return region;
#else
            int length = UnsafeRead<int>();
            int members = length;
            if ((uint)length > (uint)Avail)
            {
                // A region followed by a section of deferred bodies, or invalid data: the region ends where its
                // members do, and this reader skips the section too.
                members = SectionedRegion(ref Cur, Avail, length, _regions);
                length &= int.MaxValue;
            }

            var region = Sub(members, _regions + 1);
            Skip(length);
            return region;
#endif
        }

        /// <summary>
        /// The length of the members of a region whose length has <see cref="NinoDepth.SectionFlag"/> set (a section of
        /// deferred bodies follows them, then the section's offset); throws on any other invalid length, and on a
        /// section no writer writes: one in a region inside another (only the outermost holds one), an empty one, or
        /// one whose entries do not fill it exactly, each keyed by a position in the region no later than the entry
        /// itself, in increasing order (where the body deferred to it would have begun: in the members, at most at their
        /// end, or in the body of an earlier entry).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SectionedRegion(ref byte data, int remaining, int length, int regions)
        {
            int total = length & int.MaxValue;
            if (length >= 0 || total < sizeof(int) || total > remaining)
                NinoThrow.InvalidLength(length);
            if (regions != 0)
                NinoThrow.InvalidData("deferred bodies in a region inside another region.");
            int end = total - sizeof(int);
            int members = NinoUnsafe.Read<int>(ref Unsafe.Add(ref data, end));
            if ((uint)members >= (uint)end)
                NinoThrow.InvalidData("invalid offset of deferred bodies.");
            int last = -1;
            for (int at = members; at != end;)
            {
                if (end - at < 2 * sizeof(int))
                    NinoThrow.InvalidData("invalid section of deferred bodies.");
                int key = NinoUnsafe.Read<int>(ref Unsafe.Add(ref data, at));
                int size = NinoUnsafe.Read<int>(ref Unsafe.Add(ref data, at + sizeof(int)));
                if (key <= last || key > at || (uint)size > (uint)(end - at - 2 * sizeof(int)))
                    NinoThrow.InvalidData("invalid section of deferred bodies.");
                last = key;
                at += 2 * sizeof(int) + size;
            }

            return members;
        }

        /// <summary>
        /// Generated code: call when done with a reader returned by <see cref="ReadRegion"/>, right after reading
        /// it. At the end of the outermost region, reads the bodies deferred inside it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndRegion(in Reader region)
        {
            var context = region._context;
            if (context == null)
                return;
            // The region's context is this reader's, or one the region created (this reader had none).
            _context = context;
            if (_regions == 0 && context.Deferrals != null)
                EndDeferredRegion(Data, Pos, region.Pos + region.Avail, context, Depth);
        }

        /// <summary>
        /// Generated code: <see cref="EndRegion"/> for a keyed member, whose value must fill its entry exactly: bytes
        /// left over are invalid data (no writer leaves any).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndMember(in Reader member)
        {
            if (member.Avail != 0)
                NinoThrow.InvalidData("bytes left over at the end of a keyed member.");
            EndRegion(in member);
        }

        /// <summary>
        /// Reads the section after the outermost region (ending at <paramref name="regionEnd"/>; this reader is at
        /// <paramref name="pos"/>, past the section and its offset): each body deferred inside the region is matched,
        /// in order, with its entry (by the key the writer gave it); entries for objects this reader skipped (members
        /// it does not know) are skipped. Bodies deferred while reading the section are appended to the queue.
        /// Static, with the reader's fields by value: the reader's address reaches no call.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void EndDeferredRegion(ReadOnlySpan<byte> data, int pos, int regionEnd,
            NinoReadContext context, int depth)
        {
            var d = context.Deferrals;
            if (d.Region.IsEmpty)
                return;
            // A region without a section ends where this reader is.
            int sectionEnd = pos - sizeof(int);
            if (sectionEnd <= regionEnd)
                NinoThrow.InvalidData("missing deferred bodies.");

            // Keys are relative to the start of the region's members (validated by ReadRegion).
            int start = regionEnd - NinoUnsafe.Read<int>(ref Unsafe.Add(ref MemoryMarshal.GetReference(data), sectionEnd));
            var section = new Reader(Prefix(data, sectionEnd), regionEnd, depth, 1, context);
            int entryDepth = NinoDepth.Deferred(depth);
            while (!d.Region.IsEmpty)
            {
                var item = d.Region.Items[d.Region.Head++];
                while (true)
                {
                    int key = section.UnsafeRead<int>();
                    int length = section.UnsafeRead<int>();
                    if ((uint)length > (uint)section.Remaining)
                        NinoThrow.InvalidLength(length);
                    int entryPos = section.Pos;
                    section.Skip(length);
                    if (start + key != item.Position)
                        continue;
                    var entry = new Reader(Prefix(data, entryPos + length), entryPos, entryDepth, 1, context);
                    item.Body.Read(item.Value, ref entry);
                    break;
                }
            }

            d.Region.Clear();
        }

        /// <summary>
        /// Closes a class body opened at <paramref name="depth"/>, the depth before it (kept in a register by the
        /// caller, and stored back rather than decremented).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ExitBody(int depth)
        {
            Depth = depth;
            // No copy of the reader, as in Writer.ExitBody: only a body that nests others gets here, and such a
            // body is not inlined, so its caller passed the reader's address to a call already.
            if (depth == 0 && _context != null && _context.Deferrals != null)
                ReadTopDeferred();
        }

        /// <summary>Every body is closed: reads the bodies deferred outside regions, in order (as written).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadTopDeferred()
        {
            var d = _context.Deferrals;
            if (d.TopActive || d.Top.IsEmpty)
                return;
            d.TopActive = true;
            while (!d.Top.IsEmpty)
            {
                var item = d.Top.Items[d.Top.Head++];
                Depth = 1;
                item.Body.Read(item.Value, ref this);
            }

            Depth = 0;
            d.Top.Clear();
            d.TopActive = false;
        }

        /// <summary>
        /// Defers the body of <paramref name="value"/> (the instance its slot now holds), nested too deep to read
        /// now. Only the context changes: the other fields go by value, so the reader's address reaches no call.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DeferBody<T, TFormatter>(T value)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T> =>
            _context = DeferBody<T, TFormatter>(value, _context, Pos, _regions);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoReadContext DeferBody<T, TFormatter>(T value, NinoReadContext context, int position,
            int regions)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            context ??= NinoReadContext.Rent();
            // A tracked object is recorded now: back references to it may come before its body.
            NinoReferenceReadTable.SetPending(context.Refs, value);
            (context.Deferrals ??= NinoDeferrals.Rent()).Add(new NinoDeferredItem
                { Value = value, Body = NinoDeferredBody<T, TFormatter>.Instance, Position = position }, regions);
            return context;
        }

        /// <summary>
        /// Reads a length-prefixed sub-reader: an <c>int32</c> byte length (written with
        /// <see cref="Writer.PatchLength"/>, for instance), then the bytes the returned reader reads. For custom
        /// formatters; call <see cref="EndSlice"/> when done with it. Generated bodies use regions instead.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader ReadSlice()
        {
            int length = UnsafeRead<int>();
            if ((uint)length > (uint)Avail)
                NinoThrow.InvalidLength(length);
            var slice = Sub(length, _regions);
            Skip(length);
            return slice;
        }
    }
}
