using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Writes Nino binary data into a <see cref="NinoArrayBufferWriter"/>.
    /// <para>
    /// The writer caches the destination buffer; call <see cref="Flush"/> (or <see cref="Dispose"/>) when done so
    /// the buffer's <see cref="NinoArrayBufferWriter.WrittenCount"/> is updated. The top-level
    /// <see cref="NinoSerializer"/> APIs do this for you.
    /// </para>
    /// </summary>
    public ref struct Writer
    {
        private NinoArrayBufferWriter _owner; // not readonly: CopyTo

        // The buffer and the position, per target (see Reader): .NET 8+ keeps a cursor (ref fields: the next byte,
        // the end of the buffer and its start), netstandard the buffer and a position. Only the "Cursor" section
        // below reads them.
#if NET8_0_OR_GREATER
        private ref byte _cur;
        private int _avail;
        private ref byte _start;
#else
        private Span<byte> _buffer;
        private int _pos;
#endif

        // Open class bodies and regions (see NinoDepth).
        internal int Depth;
        private int _regions;

        // Back references and the bodies deferred below NinoDepth.Inline, created on first use: one reference in
        // the writer rather than two (fewer fields to copy, clear and keep in registers), as in Reader.
        private NinoWriteContext _context;

        /// <exception cref="PlatformNotSupportedException">The host is big-endian.</exception>
        // AggressiveInlining, as everything a top-level call inlines: the JIT counts what it inlines into a method
        // without being told to against the method's inlining budget, together with everything that inlines into
        // it in turn, and with the budget spent leaves even the helpers marked AggressiveInlining as calls.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Writer(NinoArrayBufferWriter buffer)
        {
            // A JIT-time constant on CoreCLR, NativeAOT and Mono: the check folds away.
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            // One block clear rather than a store per field (see Reader's constructor).
            this = default;
            _owner = buffer;
            SetBuffer(buffer.Buffer, buffer.Index);
        }

        // ---- Cursor: the buffer and the position, per target. Nothing else reads the fields above.

        /// <summary>Writes into <paramref name="buffer"/> (null: none yet) from <paramref name="pos"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetBuffer(byte[] buffer, int pos)
        {
#if NET8_0_OR_GREATER
            Span<byte> span = buffer;
            // Through a local: a writer in memory would store a field and load it back.
            ref byte start = ref MemoryMarshal.GetReference(span);
            _start = ref start;
            _cur = ref Unsafe.Add(ref start, (nuint)(uint)pos);
            _avail = span.Length - pos;
#else
            _buffer = buffer;
            _pos = pos;
#endif
        }

        /// <summary>
        /// Copies this writer into <paramref name="target"/> one field at a time, for a local copy in a loop and back
        /// (see <see cref="Reader.CopyTo"/>: a struct assignment loads it as one vector right after its fields were
        /// stored one at a time, which the CPU cannot forward). For generated code.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public readonly void CopyTo(ref Writer target)
        {
            target._owner = _owner;
#if NET8_0_OR_GREATER
            target._cur = ref _cur;
            target._avail = _avail;
            target._start = ref _start;
#else
            target._buffer = _buffer;
            target._pos = _pos;
#endif
            target.Depth = Depth;
            target._regions = _regions;
            target._context = _context;
        }

        /// <summary>The next byte.</summary>
        private ref byte Cur
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => ref _cur;
#else
            // Zero-extended: positions are never negative.
            get => ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)_pos);
#endif
        }

        /// <summary>The number of free bytes.</summary>
        private int Avail
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => _avail;
#else
            get => _buffer.Length - _pos;
#endif
        }

        /// <summary>Moves past <paramref name="count"/> bytes (checked by the caller).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Skip(int count)
        {
#if NET8_0_OR_GREATER
            // Counts are never negative: zero-extended rather than sign-extended.
            _cur = ref Unsafe.Add(ref _cur, (nuint)(uint)count);
            _avail -= count;
#else
            _pos += count;
#endif
        }

        /// <summary>The position in the buffer.</summary>
        private int Pos
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => (int)(nint)Unsafe.ByteOffset(ref _start, ref _cur);
#else
            get => _pos;
#endif
        }

        /// <summary>The byte at <paramref name="position"/> (already written).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref byte At(int position)
        {
#if NET8_0_OR_GREATER
            return ref Unsafe.Add(ref _start, (nuint)(uint)position);
#else
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), (nint)(uint)position);
#endif
        }

        // ---- End of the cursor.

        /// <summary>
        /// Ends reference tracking: later objects are not matched against the ones written so far. Called by
        /// the <see cref="NinoSerializer"/> APIs that create the writer; the table is reused by the next call.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReferences()
        {
            var context = _context;
            if (context == null)
                return;
            _context = null;
            NinoWriteContext.Return(context);
        }

        /// <summary>
        /// Writes the reference header of an object of a type marked <c>trackReferences</c>: <c>0</c> for null,
        /// the 1-based index of an object already written by this writer, or <c>-1</c> for a new object, in which
        /// case it returns true and the caller writes the object (see <see cref="NinoWire"/>).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool WriteReference(object value)
        {
            if (value == null)
            {
                UnsafeWrite(NinoWire.NullReference);
                return false;
            }

            var context = _context ??= NinoWriteContext.Rent();
            int index = (context.Refs ??= NinoReferenceWriteTable.Rent()).GetOrAdd(value);
            UnsafeWrite(index == 0 ? NinoWire.NewReference : index);
            return index == 0;
        }

        /// <summary>
        /// Generated code: opens the body of a struct that can hold itself (through a collection, a nullable, a
        /// <c>Lazy</c> or an interface), which no class body counts, and each level of a collection that holds itself
        /// (<see cref="NestingFormatter{T,TFormatter}"/>): with the class bodies, more than
        /// <see cref="NinoSettings.MaxDepth"/> levels, or a stack running low, throw
        /// <see cref="InvalidOperationException"/> (the object graph is nested too deep, or holds itself) instead of
        /// overflowing the stack. Call <see cref="ExitStruct"/> after the body. Out of line, as these bodies are.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnterStruct(Type type)
        {
            var context = _context ??= NinoWriteContext.Rent();
            int open = ++context.Structs;
            if (open + Depth > NinoLimits.MaxDepth || ((open & 15) == 0 && !NinoDepth.StackLeft()))
                NinoThrow.WriteTooDeep(type);
        }

        /// <summary>Generated code: closes a body opened by <see cref="EnterStruct"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExitStruct() => _context.Structs--;

        /// <summary>The struct bodies <see cref="EnterStruct"/> opened, which the class body depth adds to.</summary>
        internal int OpenStructs => _context?.Structs ?? 0;

        /// <summary>Absolute position in the underlying buffer.</summary>
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Pos;
        }

        /// <summary>Commits the written bytes to the underlying buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Flush() => _owner.Index = Pos;

        /// <summary>Same as <see cref="Flush"/>; enables <c>using var writer = ...</c>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose() => Flush();

        // Static, taking and returning values: an instance method would take the writer's address, and a writer
        // whose address escapes keeps every field in memory (and is zeroed in the prolog) in all inlined callers.
        // The array, not a Span: a returned Span is a struct temp holding a reference at every call site, and
        // generated dispatch inlines dozens of them, past the locals the JIT tracks: untracked GC temps are zeroed
        // in the prolog on every call.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] Grow(NinoArrayBufferWriter owner, int pos, int size)
        {
            // A negative size is one that overflowed an int (a string builder or UTF-8 text past 2 GB): rejected
            // here, on the cold path, rather than taken as room for it.
            if (size < 0)
                NinoThrow.TooLarge();
            owner.Index = pos;
            owner.Grow(size);
            return owner.Buffer;
        }

        /// <summary>
        /// Reserves <paramref name="size"/> bytes and returns a reference to the first one.
        /// Use <see cref="NinoUnsafe.Write{T}(ref byte, int, T)"/> to fill the block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte Reserve(int size)
        {
            ref byte block = ref Ensure(size);
            Skip(size);
            return ref block;
        }

        /// <summary>Makes room for <paramref name="size"/> bytes and returns a reference to the first one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte Ensure(int size)
        {
            if ((uint)size > (uint)Avail)
                Grow(size);
            return ref Cur;
        }

        // Only the position lives across the call (the cursor is rebuilt from the new buffer): with the cursor live
        // across it, callers kept it in callee-saved registers (two more pushes and pops, and copies).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Grow(int size)
        {
            int pos = Pos;
            SetBuffer(Grow(_owner, pos, size), pos);
        }

        /// <summary>
        /// The free part of the buffer (<paramref name="available"/> bytes from the position), for a loop out of line
        /// that writes into it by value (the writer's address reaching it would keep the caller's writer in memory)
        /// and then <see cref="Commit"/>s what it wrote, or takes another path when it does not fit.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte FreeSpace(out int available)
        {
            available = Avail;
            return ref Cur;
        }

        /// <summary>Advances past <paramref name="count"/> bytes written into <see cref="FreeSpace"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Commit(int count) => Skip(count);

        /// <summary>Writes an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeWrite<T>(T value)
        {
            int size = Unsafe.SizeOf<T>();
            NinoUnsafe.Write(ref Ensure(size), value);
            Skip(size);
        }

        /// <summary>
        /// Writes an unmanaged value in its canonical layout (padding cleared, converted on runtimes that lay the
        /// type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteValue<T>(in T value) => NinoLayout<T>.Write(ref Reserve(NinoLayout<T>.Size), in value);

        /// <summary>Writes an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(in T value) where T : unmanaged => WriteValue(in value);

        /// <summary>Writes a nullable unmanaged value: a has-value byte, then the value if present.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? value) where T : unmanaged => WriteNullableRaw(value);

        /// <summary>
        /// <see cref="Write{T}(T?)"/> of a value whose memory is its wire layout (unmanaged values, raw structs): the
        /// has-value byte and the value in one reservation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void WriteNullableRaw<T>(T? value) where T : struct
        {
            if (!value.HasValue)
            {
                UnsafeWrite(NinoWire.Null);
                return;
            }

            ref byte dst = ref Reserve(1 + NinoLayout<T>.Size);
            dst = NinoWire.NotNull;
            NinoLayout<T>.Write(ref Unsafe.Add(ref dst, 1), value.GetValueOrDefault());
        }

        /// <summary>Writes a collection length; use -1 for null.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteLength(int length) => UnsafeWrite(length);

        /// <summary>Writes the null collection marker (-1).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteNullLength() => UnsafeWrite(NinoWire.NullLength);

        /// <summary>
        /// Writes the null collection marker when <paramref name="value"/> is null, and returns whether it did: the
        /// prologue of the formatters of collections, whose length follows otherwise.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryWriteNullLength(object value)
        {
            if (value != null)
                return false;
            WriteNullLength();
            return true;
        }

        /// <summary>
        /// Writes the 1-byte null flag of a sealed class (or tuple), and returns whether <paramref name="value"/> is
        /// present: its body follows then.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool WritePresence(object value)
        {
            if (value == null)
            {
                UnsafeWrite(NinoWire.Null);
                return false;
            }

            UnsafeWrite(NinoWire.NotNull);
            return true;
        }

        /// <summary>Reserves 4 bytes to be patched later and returns their position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReserveInt32()
        {
            int pos = Pos;
            Reserve(sizeof(int));
            return pos;
        }

        /// <summary>Overwrites 4 bytes at an absolute position previously returned by <see cref="ReserveInt32"/>.</summary>
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

        /// <summary>Writes raw bytes without a length prefix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRaw(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return;
            NinoUnsafe.Copy(ref Reserve(bytes.Length), ref MemoryMarshal.GetReference(bytes), (uint)bytes.Length);
        }

        /// <summary>Writes the elements of an unmanaged span as one block, without a length prefix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUnmanagedRaw<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            if (!NinoLayout<T>.IsCanonical)
            {
                foreach (var item in values)
                    WriteValue(item);
                return;
            }

            if (values.IsEmpty)
                return;
            NinoLayout<T>.CopyToWire(ref ReserveElements(values.Length, Unsafe.SizeOf<T>()),
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), values.Length);
        }

        /// <summary>Writes a length-prefixed block of unmanaged values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            if (!NinoLayout<T>.IsCanonical)
            {
                WriteLength(values.Length);
                WriteUnmanagedRaw(values);
                return;
            }

            // AsBytes would check the multiplication with imul/jo (see ReserveCounted).
            int count = values.Length;
            ref byte dst = ref ReserveCounted(count, Unsafe.SizeOf<T>());
            if (count == 0)
                return;
            ref byte src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
            if (NinoLayout<T>.HasPadding)
                NinoLayout<T>.CopyToWire(ref dst, ref src, count);
            else
                NinoUnsafe.Copy(ref dst, ref src, (uint)count * (uint)Unsafe.SizeOf<T>());
        }

        /// <summary>
        /// Writes the length <paramref name="count"/> and reserves the <paramref name="elementSize"/>-byte elements
        /// after it, in one reservation; returns a reference to the first element. The caller writes every element.
        /// </summary>
        // The size in 64 bits: it cannot overflow, and the capacity check rejects a size too large for an int on the
        // cold path, where a checked int size would test the multiplication and the addition on every call.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveCounted(int count, int elementSize)
        {
            ref byte dst = ref ReserveLarge(NinoWire.LengthSize + (ulong)(uint)count * (uint)elementSize);
            NinoUnsafe.Write(ref dst, count);
            return ref Unsafe.Add(ref dst, NinoWire.LengthSize);
        }

        /// <summary>
        /// Reserves <paramref name="count"/> (not negative) elements of <paramref name="elementSize"/> bytes each and
        /// returns a reference to the first one; the size in 64 bits, as in <see cref="ReserveCounted"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveElements(int count, int elementSize) =>
            ref ReserveLarge((ulong)(uint)count * (uint)elementSize);

        /// <summary><see cref="Reserve"/> for a size that may not fit in an int (rejected when it does not).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveLarge(ulong size)
        {
            if (size > (uint)Avail)
            {
                int pos = Pos;
                SetBuffer(GrowLarge(_owner, pos, size), pos);
            }
            ref byte block = ref Cur;
            Skip((int)size);
            return ref block;
        }

        /// <summary>
        /// Writes the string header <paramref name="header"/> (see <see cref="NinoStrings.ReadHeader"/>), reserves
        /// the <paramref name="header"/> bytes after it (up to 4 GB, rejected past 2 GB) and returns a reference to
        /// them. A header of one or three bytes (up to 32,767 chars) is written inline, the three as one store of four
        /// bytes (its last the body's first, which the caller overwrites); a longer one out of line. The body is found
        /// from the cursor, so only the header stays live past the reserve.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReserveText(uint header)
        {
            int start = header < 0x80 ? 1 : header <= ushort.MaxValue ? 3 : NinoStrings.MaxHeaderSize;
            ref byte block = ref ReserveLarge((ulong)start + header);
            if (header < 0x80)
                block = (byte)header;
            else if (header <= ushort.MaxValue)
                NinoUnsafe.Write(ref block, (header << 8) | NinoStrings.Header16);
            else
                NinoStrings.WriteLongHeader(ref block, header);
            return ref Unsafe.Subtract(ref Cur, (nuint)header);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] GrowLarge(NinoArrayBufferWriter owner, int pos, ulong size)
        {
            if (size > int.MaxValue)
                NinoThrow.TooLarge();
            return Grow(owner, pos, (int)size);
        }

        /// <summary>Writes a length-prefixed unmanaged array (-1 for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] values) where T : unmanaged
        {
            if (!TryWriteNullLength(values))
                Write((ReadOnlySpan<T>)values);
        }

        /// <summary>
        /// Writes a string: one byte per char when all its chars are Latin-1 (ASCII included), UTF-16 otherwise
        /// (see <see cref="NinoWire"/>).
        /// </summary>
        // Inlined, with the encoding out of line taking values (see WriteUtf8).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string value) => Skip(NinoStrings.Write(ref Ensure(NinoStrings.MaxSize(value)), value));

        /// <summary>Longest string <see cref="WriteUtf8"/> reserves 3 bytes per char for.</summary>
        private const int Utf8WorstCaseMaxChars = 1024;

        /// <summary>
        /// Writes a string as UTF-8: its UTF-16 length (char count) plus one (0 for null, 1 for empty, with nothing
        /// after either), then the UTF-8 byte count and the bytes, both counts headers (see <see cref="NinoStrings"/>).
        /// Unpaired surrogates are written as U+FFFD, one char each, so the char count is that of the string read back.
        /// </summary>
        // Inlined, with the counting and the encoding out of line taking values: an instance call would take the
        // writer's address, which keeps all its fields in memory in the caller.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUtf8(string value)
        {
            if (value == null)
            {
                Ensure(1) = NinoStrings.NullHeader;
                Skip(1);
                return;
            }

            // Short strings reserve the worst case (3 bytes per char) and encode in one pass; long ones count their
            // bytes first, so the buffer does not grow to three times the string (or overflow the count).
            int maxBytes = value.Length <= Utf8WorstCaseMaxChars
                ? value.Length * 3
                : Utf8ByteCount(value);
            // Room for the worst case; only the bytes written (counts included) are kept.
            ref byte dst = ref Ensure(2 * NinoStrings.MaxHeaderSize + maxBytes);
            Skip(EncodeUtf8(value, ref dst, maxBytes));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Utf8ByteCount(string value)
        {
            int count = Encoding.UTF8.GetByteCount(value);
            // The counts and the bytes must fit in an int (see Grow).
            return count > int.MaxValue - 2 * NinoStrings.MaxHeaderSize ? -1 : count;
        }

        /// <summary>
        /// Writes the counts and the UTF-8 bytes of <paramref name="value"/> (at most <paramref name="capacity"/>)
        /// at <paramref name="destination"/> (room for 10 bytes more); returns the bytes written, counts included.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int EncodeUtf8(string value, ref byte destination, int capacity)
        {
            int length = value.Length;
            if (length == 0)
            {
                destination = NinoStrings.EmptyHeader;
                return 1;
            }

            int countSize = NinoStrings.WriteHeader(ref destination, (uint)length + 1);
            ref byte sizeAt = ref Unsafe.Add(ref destination, countSize);
            // The bytes go after a byte count as long as the char count's, the fewest it can take (there are at
            // least as many bytes as chars): exactly that for ASCII text. When the byte count turns out longer,
            // they move up by the difference (into the room for 10 bytes of counts).
            int sizeSize = NinoStrings.HeaderSize((uint)length);
            ref byte bytes = ref Unsafe.Add(ref sizeAt, sizeSize);
#if NET8_0_OR_GREATER
            // Without replacement, the transcoder's call is all that is inlined; an unpaired surrogate stops it.
            var status = System.Text.Unicode.Utf8.FromUtf16(value.AsSpan(), MemoryMarshal.CreateSpan(ref bytes, capacity),
                out _, out int written, replaceInvalidSequences: false);
            if (status != System.Buffers.OperationStatus.Done)
                written = EncodeUtf8Replacing(value, ref bytes, capacity);
#else
            int written = EncodeUtf8Replacing(value, ref bytes, capacity);
#endif
            int actual = NinoStrings.HeaderSize((uint)written);
            if (actual != sizeSize)
            {
                Move(ref bytes, ref Unsafe.Add(ref sizeAt, actual), written);
                sizeSize = actual;
            }

            NinoStrings.WriteHeader(ref sizeAt, (uint)written);
            return countSize + sizeSize + written;
        }

        /// <summary>Moves <paramref name="count"/> bytes up to <paramref name="to"/>, overlapping (rare: out of line).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void Move(ref byte from, ref byte to, int count)
        {
            fixed (byte* source = &from)
            fixed (byte* destination = &to)
                Buffer.MemoryCopy(source, destination, count, count);
        }

        /// <summary>The UTF-8 bytes of <paramref name="value"/>, each unpaired surrogate written as U+FFFD.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int EncodeUtf8Replacing(string value, ref byte destination, int capacity)
        {
            fixed (char* src = value)
            fixed (byte* dst = &destination)
            {
                int length = value.Length, ascii = 0;
#if !NET8_0_OR_GREATER
                // The netstandard builds (Unity, .NET Framework): the registered kernel writes the leading ASCII chars
                // (a byte each, their UTF-8), the encoder the rest from the first other char, which is the start of a
                // char's bytes: the same bytes as the encoder's alone.
                if (length >= NinoKernelHook.MinNarrowAscii && capacity >= length)
                {
                    ascii = NinoKernelHook.NarrowAsciiPrefix(src, dst, length);
                    if (ascii == length)
                        return length;
                }
#endif
                return ascii + Encoding.UTF8.GetBytes(src + ascii, length - ascii, dst + ascii, capacity - ascii);
            }
        }
    }
}
