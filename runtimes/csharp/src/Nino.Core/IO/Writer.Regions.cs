using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    // Patched lengths, length-prefixed regions and deferred bodies; the cursor is in Writer.cs.
    public ref partial struct Writer
    {
        /// <summary>Reserves 4 bytes to be patched later and returns their position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReserveInt32()
        {
            int pos = Pos;
            Reserve(sizeof(int));
            return pos;
        }

        /// <summary>
        /// Overwrites 4 bytes at an absolute position previously returned by <see cref="ReserveInt32"/>. The position is
        /// not checked: pass only one that call returned for this writer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PatchInt32(int position, int value)
        {
            NinoUnsafe.Write(ref At(position), value);
        }

        /// <summary>
        /// Writes, at <paramref name="lengthPosition"/>, the number of bytes written after that 4-byte slot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PatchLength(int lengthPosition) =>
            PatchInt32(lengthPosition, Pos - lengthPosition - NinoWire.LengthSize);

        /// <summary>
        /// Generated code: starts a length-prefixed region (a version-tolerant body, a keyed body or member) and
        /// returns the position of its length, for <see cref="EndRegion"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int BeginRegion()
        {
            _regions++;
            return ReserveInt32();
        }

        /// <summary>
        /// Generated code: <see cref="BeginRegion()"/> together with the region's first <paramref name="size"/> - 4
        /// bytes (raw members), in one reservation of <paramref name="size"/> bytes that starts with the region's
        /// length. Returns that reservation, and in <paramref name="lengthPosition"/> the position for
        /// <see cref="EndRegion"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte BeginRegion(int size, out int lengthPosition)
        {
            _regions++;
            lengthPosition = Pos;
            return ref Reserve(size);
        }

        /// <summary>Generated code reserves a complete raw region and returns a reference to its written length.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte ReserveFixedRegion(int payloadSize)
        {
            ref byte block = ref ReserveLarge(NinoWire.LengthSize + (ulong)(uint)payloadSize);
            NinoUnsafe.Write(ref block, payloadSize);
            return ref block;
        }

        /// <summary>
        /// Generated code: <see cref="BeginRegion()"/> after <paramref name="prefix"/> bytes (a keyed member's key, and
        /// the raw members before it), in one reservation of <paramref name="prefix"/> + 4 bytes that ends with the
        /// region's length. Returns that reservation, and in <paramref name="lengthPosition"/> the position for
        /// <see cref="EndRegion"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte BeginRegionAfter(int prefix, out int lengthPosition)
        {
            _regions++;
            lengthPosition = Pos + prefix;
            return ref Reserve(prefix + NinoWire.LengthSize);
        }

        /// <summary>Generated code: ends the region <see cref="BeginRegion"/> started.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndRegion(int lengthPosition)
        {
            if (--_regions == 0 && _context != null)
            {
                // The fields by value, not the writer: its address (or a copy of it) reaching a call keeps all its
                // fields in memory in the caller (a generated body's local copy), also on the path that never gets
                // here. The section is written by a writer of its own; this one continues where it ended.
                int pos = EndOuterRegion(_owner, Pos, Depth, _context, lengthPosition);
                SetBuffer(_owner.Buffer, pos);
                return;
            }

            PatchLength(lengthPosition);
        }

        /// <summary>
        /// Ends the outermost region: writes the bodies deferred inside it as a section of
        /// <c>(int32 key, int32 length, body)</c> entries, then the section's offset (see <see cref="NinoDepth"/>).
        /// Bodies deferred while writing the section are appended to it, so the stack stays bounded.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int EndOuterRegion(NinoArrayBufferWriter owner, int pos, int depth, NinoWriteContext context,
            int lengthPosition)
        {
            owner.Index = pos;
            // Only the length to patch when nothing was deferred inside the region, as when references are tracked
            // and no body nests below NinoDepth.Inline (the context exists for the references): no writer to make.
            var d = context.Deferrals;
            if (d == null || d.Region.IsEmpty)
            {
                NinoUnsafe.Write(ref owner.Buffer[lengthPosition], pos - lengthPosition - NinoWire.LengthSize);
                return pos;
            }

            var writer = new Writer(owner) { Depth = depth, _context = context };
            writer.EndOuterRegionCore(lengthPosition);
            return writer.Pos;
        }

        /// <summary>The section of <see cref="EndOuterRegion"/>, which returned already when nothing was deferred.</summary>
        private void EndOuterRegionCore(int lengthPosition)
        {
            var d = _context.Deferrals;
            int start = lengthPosition + NinoWire.LengthSize;
            int section = Pos;
            int depth = Depth;
            object current = d.Current, anchor = d.CurrentAnchor;
            int generation = d.CurrentGeneration;
            _regions = 1;
            while (!d.Region.IsEmpty)
            {
                var item = d.Region.Items[d.Region.Head++];
                UnsafeWrite(item.Position - start);
                int length = ReserveInt32();
                WriteDeferred(in item, NinoDepth.Deferred(depth));
                PatchLength(length);
            }

            _regions = 0;
            Depth = depth;
            d.Current = current;
            d.CurrentAnchor = anchor;
            d.CurrentGeneration = generation;
            d.Region.Clear();
            UnsafeWrite(section - start);
            PatchInt32(lengthPosition, (Pos - start) | NinoDepth.SectionFlag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteDeferred(in NinoDeferredItem item, int depth)
        {
            var d = _context.Deferrals;
            d.Current = item.Value;
            d.CurrentAnchor = item.Anchor;
            d.CurrentGeneration = item.Generation;
            Depth = depth;
            item.Body.Write(item.Value, ref this);
        }

        /// <summary>
        /// Closes a class body opened at <paramref name="depth"/>, the depth before it (kept in a register by the
        /// caller, and stored back rather than decremented).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ExitBody(int depth)
        {
            Depth = depth;
            // No copy of the writer: only a body that nests others gets here, and such a body is not inlined
            // (BodyEmitter), so the caller passed the writer's address to a call already; a copy holds references
            // and would be zeroed in the prolog of every method this is inlined into.
            if (depth == 0 && _context != null)
                WriteTopDeferred();
        }

        /// <summary>
        /// Every body is closed and no region is open: writes the bodies deferred outside regions, in order. Each
        /// starts at depth 1 again, and those it defers are appended to the queue.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteTopDeferred()
        {
            var d = _context.Deferrals;
            if (d == null || d.TopActive || d.Top.IsEmpty)
                return;
            d.TopActive = true;
            while (!d.Top.IsEmpty)
                WriteDeferred(in d.Top.Items[d.Top.Head++], 1);
            Depth = 0;
            d.Top.Clear();
            d.TopActive = false;
            d.Current = d.CurrentAnchor = null;
            d.CurrentGeneration = 0;
        }

        /// <summary>
        /// Defers the body of <paramref name="value"/>, nested too deep to write now. Fails on a cycle: the
        /// deferred objects form chains (each deferred while writing the previous one), and an object that comes
        /// back in its own chain would be written forever. The check compares it with the chain's object at the
        /// last power-of-two length (Brent), which finds any repetition in a number of steps proportional to the
        /// cycle.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void DeferBody<T, TFormatter>(T value)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T> =>
            _context = DeferBody<T, TFormatter>(value, _context, Pos, _regions);

        // The fields by value, as in Reader.DeferBody: the writer's address reaches no call, and no copy of it is
        // made (it would be zeroed in the prolog of every caller: generated dispatch inlines one per type).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoWriteContext DeferBody<T, TFormatter>(T value, NinoWriteContext context, int pos,
            int regions)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            context ??= NinoWriteContext.Rent();
            context.Deferrals = Defer(context.Deferrals, pos, regions, value, NinoDeferredBody<T, TFormatter>.Instance);
            return context;
        }

        private static NinoDeferrals Defer(NinoDeferrals d, int pos, int regions, object value,
            NinoDeferredBody body)
        {
            d ??= NinoDeferrals.Rent();
            int generation = d.CurrentGeneration;
            object anchor = generation != 0 && (generation & (generation - 1)) == 0 ? d.Current : d.CurrentAnchor;
            if (ReferenceEquals(value, d.Current) || ReferenceEquals(value, anchor))
                NinoThrow.Cycle(value.GetType());
            var item = new NinoDeferredItem
            {
                Value = value, Body = body, Position = pos, Anchor = anchor, Generation = generation + 1
            };
            d.Add(in item, regions);
            return d;
        }
    }
}
