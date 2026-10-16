using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

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
        private readonly NinoArrayBufferWriter _owner;
        private Span<byte> _buffer;
        private int _pos;
        private NinoReferenceWriteTable _refs;

        // Open class bodies and regions, and the bodies deferred below NinoDepth.Inline (see NinoDepth).
        internal int Depth;
        private int _regions;
        private NinoDeferrals _deferrals;

        /// <exception cref="PlatformNotSupportedException">The host is big-endian.</exception>
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
            _buffer = buffer.Buffer;
            _pos = buffer.Index;
        }

        /// <summary>
        /// Ends reference tracking: later objects are not matched against the ones written so far. Called by
        /// the <see cref="NinoSerializer"/> APIs that create the writer; the table is reused by the next call.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReferences()
        {
            if (_deferrals != null)
            {
                NinoDeferrals.Return(_deferrals);
                _deferrals = null;
            }

            if (_refs == null)
                return;
            NinoReferenceWriteTable.Return(_refs);
            _refs = null;
        }

        /// <summary>
        /// Writes the reference header of an object of a type marked <c>trackReferences</c>: <c>0</c> for null,
        /// the 1-based index of an object already written by this writer, or <c>-1</c> for a new object, in which
        /// case it returns true and the caller writes the object (see <see cref="NinoWire"/>).
        /// </summary>
        public bool WriteReference(object value)
        {
            if (value == null)
            {
                UnsafeWrite(NinoWire.NullReference);
                return false;
            }

            int index = (_refs ??= NinoReferenceWriteTable.Rent()).GetOrAdd(value);
            UnsafeWrite(index == 0 ? NinoWire.NewReference : index);
            return index == 0;
        }

        /// <summary>Absolute position in the underlying buffer.</summary>
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pos;
        }

        /// <summary>Commits the written bytes to the underlying buffer.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Flush() => _owner.Index = _pos;

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
            int pos = _pos;
            if ((uint)size > (uint)(_buffer.Length - pos))
                _buffer = Grow(_owner, pos, size);
            _pos = pos + size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), pos);
        }

        /// <summary>Writes an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void UnsafeWrite<T>(T value)
        {
            int size = Unsafe.SizeOf<T>();
            int pos = _pos;
            if ((uint)size > (uint)(_buffer.Length - pos))
                _buffer = Grow(_owner, pos, size);
            NinoUnsafe.Write(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), pos), value);
            _pos = pos + size;
        }

        /// <summary>
        /// Writes an unmanaged value in its canonical layout (padding cleared, converted on runtimes that lay the
        /// type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteValue<T>(T value) => NinoLayout<T>.Write(ref Reserve(NinoLayout<T>.Size), value);

        /// <summary>Writes an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T value) where T : unmanaged => WriteValue(value);

        /// <summary>Writes a nullable unmanaged value: a has-value byte, then the value if present.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T? value) where T : unmanaged
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

        /// <summary>Reserves 4 bytes to be patched later and returns their position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReserveInt32()
        {
            int pos = _pos;
            Reserve(sizeof(int));
            return pos;
        }

        /// <summary>Overwrites 4 bytes at an absolute position previously returned by <see cref="ReserveInt32"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PatchInt32(int position, int value)
        {
            NinoUnsafe.Write(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), position), value);
        }

        /// <summary>
        /// Writes, at <paramref name="lengthPosition"/>, the number of bytes written after that 4-byte slot.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PatchLength(int lengthPosition) =>
            PatchInt32(lengthPosition, _pos - lengthPosition - NinoWire.LengthSize);

        /// <summary>
        /// Generated code: starts a length-prefixed region (a version-tolerant level, a keyed body or member) and
        /// returns the position of its length, for <see cref="EndRegion"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int BeginRegion()
        {
            _regions++;
            return ReserveInt32();
        }

        /// <summary>Generated code: ends the region <see cref="BeginRegion"/> started.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndRegion(int lengthPosition)
        {
            if (--_regions == 0 && _deferrals != null)
            {
                EndOuterRegion(lengthPosition);
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
        private void EndOuterRegion(int lengthPosition)
        {
            var d = _deferrals;
            if (d.Region.IsEmpty)
            {
                PatchLength(lengthPosition);
                return;
            }

            int start = lengthPosition + NinoWire.LengthSize;
            int section = _pos;
            int depth = Depth;
            object current = d.Current, anchor = d.CurrentAnchor;
            int generation = d.CurrentGeneration;
            _regions = 1;
            while (!d.Region.IsEmpty)
            {
                var item = d.Region.Items[d.Region.Head++];
                UnsafeWrite(item.Position - start);
                int length = ReserveInt32();
                WriteDeferred(in item, Math.Min(depth, NinoDepth.Inline - 1) + 1);
                PatchLength(length);
            }

            _regions = 0;
            Depth = depth;
            d.Current = current;
            d.CurrentAnchor = anchor;
            d.CurrentGeneration = generation;
            d.Region.Clear();
            UnsafeWrite(section - start);
            PatchInt32(lengthPosition, (_pos - start) | NinoDepth.SectionFlag);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteDeferred(in NinoDeferredItem item, int depth)
        {
            var d = _deferrals;
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
            if (depth == 0 && _deferrals != null)
                WriteTopDeferred();
        }

        /// <summary>
        /// Every body is closed and no region is open: writes the bodies deferred outside regions, in order. Each
        /// starts at depth 1 again, and those it defers are appended to the queue.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void WriteTopDeferred()
        {
            var d = _deferrals;
            if (d.TopActive || d.Top.IsEmpty)
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
            _deferrals = DeferBody<T, TFormatter>(value, _deferrals, _pos, _regions);

        // The fields by value, as in Reader.DeferBody: the writer's address reaches no call, and no copy of it is
        // made (it would be zeroed in the prolog of every caller: generated dispatch inlines one per type).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoDeferrals DeferBody<T, TFormatter>(T value, NinoDeferrals d, int pos, int regions)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T> =>
            Defer(d, pos, regions, value, NinoDeferredBody<T, TFormatter>.Instance);

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
            if (regions == 0)
                d.Top.Add(in item);
            else
                d.Region.Add(in item);
            return d;
        }

        /// <summary>Writes raw bytes without a length prefix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRaw(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
                return;
            Unsafe.CopyBlockUnaligned(ref Reserve(bytes.Length), ref MemoryMarshal.GetReference(bytes), (uint)bytes.Length);
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
            NinoLayout<T>.CopyToWire(ref Reserve(values.Length * Unsafe.SizeOf<T>()),
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

            // The size in 64 bits: it cannot overflow, and the capacity check rejects a size too large for an int
            // on the cold path (AsBytes would check the multiplication with imul/jo, CopyToWire once more).
            int count = values.Length;
            ulong size = (ulong)(uint)count * (uint)Unsafe.SizeOf<T>();
            ref byte dst = ref ReserveLarge(NinoWire.LengthSize + size);
            NinoUnsafe.Write(ref dst, count);
            if (count == 0)
                return;
            ref byte src = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values));
            if (NinoLayout<T>.HasPadding)
                NinoLayout<T>.CopyToWire(ref Unsafe.Add(ref dst, NinoWire.LengthSize), ref src, count);
            else
                Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dst, NinoWire.LengthSize), ref src, (uint)size);
        }

        /// <summary><see cref="Reserve"/> for a size that may not fit in an int (rejected when it does not).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref byte ReserveLarge(ulong size)
        {
            int pos = _pos;
            if (size > (uint)(_buffer.Length - pos))
                _buffer = GrowLarge(_owner, pos, size);
            _pos = pos + (int)size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer), pos);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static byte[] GrowLarge(NinoArrayBufferWriter owner, int pos, ulong size)
        {
            if (size > int.MaxValue)
                throw new OverflowException();
            return Grow(owner, pos, (int)size);
        }

        /// <summary>Writes a length-prefixed unmanaged array (-1 for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write<T>(T[] values) where T : unmanaged
        {
            if (values == null)
            {
                WriteNullLength();
                return;
            }

            Write((ReadOnlySpan<T>)values);
        }

        /// <summary>Writes a string as UTF-16: char count (-1 for null), then the code units.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(string value)
        {
            if (value == null)
            {
                WriteNullLength();
                return;
            }

            Write(value.AsSpan());
        }

        /// <summary>Longest string <see cref="WriteUtf8"/> reserves 3 bytes per char for.</summary>
        private const int Utf8WorstCaseMaxChars = 1024;

        /// <summary>
        /// Writes a string as UTF-8: its UTF-16 length (char count; -1 for null), then the UTF-8 bytes. Unpaired
        /// surrogates are written as U+FFFD, one char each, so the count is that of the string read back.
        /// </summary>
        // Inlined, with the counting and the encoding out of line taking values: an instance call would take the
        // writer's address, which keeps all its fields in memory in the caller.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUtf8(string value)
        {
            if (value == null)
            {
                WriteNullLength();
                return;
            }

            // Short strings reserve the worst case (3 bytes per char) and encode in one pass; long ones count their
            // bytes first, so the buffer does not grow to three times the string (or overflow the count).
            int maxBytes = value.Length <= Utf8WorstCaseMaxChars
                ? value.Length * 3
                : Utf8ByteCount(value);
            int lengthPos = _pos;
            ref byte dst = ref Reserve(NinoWire.LengthSize + maxBytes);
            NinoUnsafe.Write(ref dst, value.Length);
            int written = EncodeUtf8(value, ref Unsafe.Add(ref dst, NinoWire.LengthSize), maxBytes);
            // give back the unused part of the worst-case reservation
            _pos = lengthPos + NinoWire.LengthSize + written;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe int EncodeUtf8(string value, ref byte destination, int capacity)
        {
            if (value.Length == 0)
                return 0;
#if NET8_0_OR_GREATER
            var status = System.Text.Unicode.Utf8.FromUtf16(value.AsSpan(), MemoryMarshal.CreateSpan(ref destination, capacity),
                out _, out int written, replaceInvalidSequences: true);
            if (status != System.Buffers.OperationStatus.Done)
                NinoThrow.InvalidData("Failed to encode UTF-8 string");
            return written;
#else
            fixed (char* src = value)
            fixed (byte* dst = &destination)
            {
                return Encoding.UTF8.GetBytes(src, value.Length, dst, capacity);
            }
#endif
        }
    }
}
