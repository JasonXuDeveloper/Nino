using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Nino.Core
{
    /// <summary>
    /// Reads Nino binary data. Every read is bounds checked, and lengths are validated against the remaining
    /// bytes before anything is allocated.
    /// </summary>
    public ref struct Reader
    {
        // A reader made by ReadSlice or ReadRegion continues its parent's state and shares its data: it ends at the
        // reader's end, and positions count from the start of the top-level data. So a region needs no origin of
        // its own (deferrals are keyed by position), and the section after it is found from the parent's position
        // and the region's end.
        //
        // The data is held two ways (the only code that differs per target, in the "Cursor" section below):
        // .NET 8+ keeps a cursor (ref fields: the next byte, the bytes left, and the start for positions), so a read is a
        // load at the cursor and an add to it, with one register fewer than a span and a position (no sign
        // extension and add per access). netstandard has no ref fields and keeps the span and a position. Every
        // other member uses the cursor primitives (Cur, Avail, Skip, Pos...), so the rest of the reader, the
        // formatters and generated code are the same for both.
#if NET8_0_OR_GREATER
        private ref byte _cur;
        private int _avail;
        private ref byte _origin; // not readonly: CopyTo
#else
        private ReadOnlySpan<byte> _data;
        private int _pos;
#endif

        // Open class bodies, and open regions (see NinoDepth).
        internal int Depth;
        private int _regions;

        // Back references and deferred bodies, created on first use and shared with the readers made from this
        // one: the hot struct stays four values (data, position, depth, regions) and one reference.
        private NinoReadContext _context;

        /// <exception cref="PlatformNotSupportedException">The host is big-endian.</exception>
        public Reader(ReadOnlySpan<byte> data)
        {
            // A JIT-time constant on CoreCLR, NativeAOT and Mono: the check folds away.
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            // One block clear rather than a store per field: less IL, which the top-level calls inline together
            // with the body within the JIT's inlining budget.
            this = default;
#if NET8_0_OR_GREATER
            // Through a local: a reader in memory would store a field and load it back.
            ref byte origin = ref MemoryMarshal.GetReference(data);
            _origin = ref origin;
            _cur = ref origin;
            _avail = data.Length;
#else
            _data = data;
#endif
        }

        // ---- Cursor: the data and the position, per target. Nothing else reads the fields above.

        /// <summary>
        /// A part of a reader that already passed the endianness check: the bytes of <paramref name="data"/> (from the
        /// start of the top-level data) from <paramref name="pos"/> to its end.
        /// </summary>
        private Reader(ReadOnlySpan<byte> data, int pos, int depth, int regions, NinoReadContext context)
        {
#if NET8_0_OR_GREATER
            ref byte origin = ref MemoryMarshal.GetReference(data);
            _origin = ref origin;
            _cur = ref Unsafe.Add(ref origin, (nuint)(uint)pos);
            _avail = data.Length - pos;
#else
            _data = data;
            _pos = pos;
#endif
            Depth = depth;
            _regions = regions;
            _context = context;
        }

#if NET8_0_OR_GREATER
        /// <summary>A part of a reader: <paramref name="length"/> bytes from <paramref name="cur"/>.</summary>
        private Reader(ref byte origin, ref byte cur, int length, int depth, int regions, NinoReadContext context)
        {
            _origin = ref origin;
            _cur = ref cur;
            _avail = length;
            Depth = depth;
            _regions = regions;
            _context = context;
        }
#endif

        /// <summary>
        /// Copies this reader into <paramref name="target"/> one field at a time: a loop reads through a local copy
        /// (its position stays in registers) and copies it back. A struct assignment copies the reader as one 32-byte
        /// vector and one 8-byte value, loaded right after its fields were stored one at a time (the position and the
        /// depth, by the code before it): the CPU cannot forward a load from several smaller stores and waits for them
        /// to reach the cache, about 15 cycles for each copy. For generated code.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public readonly void CopyTo(ref Reader target)
        {
#if NET8_0_OR_GREATER
            target._cur = ref _cur;
            target._avail = _avail;
            target._origin = ref _origin;
#else
            target._data = _data;
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
            get => ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), (nint)(uint)_pos);
#endif
        }

        /// <summary>The number of bytes left.</summary>
        private int Avail
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => _avail;
#else
            get => _data.Length - _pos;
#endif
        }

        /// <summary>Moves past <paramref name="count"/> bytes (checked by the caller).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Skip(int count)
        {
#if NET8_0_OR_GREATER
            // Counts are never negative: zero-extended (free) rather than sign-extended.
            _cur = ref Unsafe.Add(ref _cur, (nuint)(uint)count);
            _avail -= count;
#else
            _pos += count;
#endif
        }

        /// <summary>The position: bytes from the start of the top-level data.</summary>
        private int Pos
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => (int)(nint)Unsafe.ByteOffset(ref _origin, ref _cur);
#else
            get => _pos;
#endif
        }

        /// <summary>The top-level data up to this reader's end.</summary>
        private ReadOnlySpan<byte> Data
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => MemoryMarshal.CreateReadOnlySpan(ref _origin, Pos + _avail);
#else
            get => _data;
#endif
        }

        /// <summary>A reader over the next <paramref name="length"/> bytes (checked by the caller), in this one's state.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Reader Sub(int length, int regions)
        {
#if NET8_0_OR_GREATER
            return new Reader(ref _origin, ref _cur, length, Depth, regions, _context);
#else
            return new Reader(Prefix(_data, _pos + length), _pos, Depth, regions, _context);
#endif
        }

        /// <summary>The next <paramref name="length"/> bytes (checked by the caller).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ReadOnlySpan<byte> Peek(int length)
        {
#if NET8_0_OR_GREATER
            return MemoryMarshal.CreateReadOnlySpan(ref _cur, length);
#else
            return _data.Slice(_pos, length);
#endif
        }

        /// <summary>The data up to <paramref name="end"/> (at most its length, checked by the caller).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ReadOnlySpan<byte> Prefix(ReadOnlySpan<byte> data, int end)
        {
#if NETSTANDARD2_0
            return data.Slice(0, end);
#else
            // No second bounds check: the caller compared the end with the data.
            return MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetReference(data), end);
#endif
        }

        // ---- End of the cursor.

        /// <summary>
        /// Ends reference tracking for this reader. Called by the <see cref="NinoDeserializer"/> APIs that create
        /// the reader; the table is reused by the next call. Slices of this reader must not be used afterwards.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReleaseReferences()
        {
            var context = _context;
            if (context == null)
                return;
            _context = null;
            NinoReadContext.Return(context);
        }

        /// <summary>
        /// Reads what <see cref="Writer.WriteReference"/> wrote. Returns true when a new object follows: the
        /// caller reads it, calls <see cref="SetReference"/> as soon as the instance exists (so cycles resolve),
        /// and <see cref="EndReference"/> when done. Otherwise <paramref name="value"/> is now null or the
        /// object read earlier.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadReference<T>(ref T value, out int index) where T : class
        {
            int head = UnsafeRead<int>();
            index = 0;
            if (head == NinoWire.NewReference)
            {
                index = BeginReference();
                return true;
            }

            if (head == NinoWire.NullReference)
                NinoRecycler.Discard(ref value);
            else
                value = GetReference<T>(head);
            return false;
        }

        private int BeginReference()
        {
            var context = _context ??= NinoReadContext.Rent();
            return (context.Refs ??= NinoReferenceReadTable.Rent()).Begin();
        }

        private T GetReference<T>(int index) where T : class
        {
            var refs = _context?.Refs;
            if (refs == null)
                NinoThrow.InvalidReference(index);
            var obj = refs.Get(index);
            if (obj is T t)
                return t;
            NinoThrow.ReferenceTypeMismatch(typeof(T), obj.GetType());
            return null;
        }

        /// <summary>Records the instance of the object whose header was just read.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetReference(object value)
        {
            NinoReferenceReadTable.SetPending(_context?.Refs, value);
        }

        /// <summary>Records the object read after <see cref="ReadReference{T}"/> returned true.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndReference(int index, object value) => _context.Refs.Set(index, value);

        /// <summary>
        /// Generated code: opens the body of a struct that can hold itself (through a collection, a nullable, a
        /// <c>Lazy</c> or an interface). No class body counts such nesting (<see cref="NinoDepth"/>), so it is counted
        /// here, with the open class bodies, up to <see cref="NinoSettings.MaxDepth"/>, and the stack is checked every 16
        /// levels: deeply nested data throws instead of overflowing the stack. Call <see cref="ExitStruct"/> after the
        /// body. Out of line, as these bodies are: the reader's address reaches a call there already. A body that
        /// throws leaves the count raised, in a context the failed call drops. Collections that hold themselves count
        /// their levels here too (<see cref="NestingFormatter{T,TFormatter}"/>).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void EnterStruct(Type type)
        {
            var context = _context ??= NinoReadContext.Rent();
            int open = ++context.Structs;
            if (open + Depth > NinoLimits.MaxDepth || ((open & 15) == 0 && !NinoDepth.StackLeft()))
                NinoThrow.TooDeep(type);
        }

        /// <summary>Generated code: closes a body opened by <see cref="EnterStruct"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExitStruct() => _context.Structs--;

        /// <summary>The struct bodies <see cref="EnterStruct"/> opened, which the class body depth adds to.</summary>
        internal int OpenStructs => _context?.Structs ?? 0;

        /// <summary>
        /// Call when done with a reader returned by <see cref="ReadSlice"/>: objects it read stay visible to
        /// later back references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndSlice(in Reader slice)
        {
            if (_context == null)
                _context = slice._context;
        }

        /// <summary>
        /// Generated code: reads a length-prefixed region (a version-tolerant body, a keyed body or member),
        /// written between <see cref="Writer.BeginRegion"/> and <see cref="Writer.EndRegion"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader ReadRegion()
        {
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
        /// Position in the data: bytes consumed so far. A reader from <see cref="ReadSlice"/> counts from the start
        /// of the data of the reader it was made from.
        /// </summary>
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Pos;
        }

        /// <summary>Number of bytes left.</summary>
        public int Remaining
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Avail;
        }

        /// <summary>Whether all bytes have been consumed.</summary>
        public bool Eof
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Avail <= 0;
        }

        /// <summary>
        /// Consumes <paramref name="size"/> bytes and returns a reference to the first one.
        /// Use <see cref="NinoUnsafe.Read{T}(ref byte, int)"/> to decode the block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte ReadBlock(int size)
        {
            ref byte block = ref Cur;
            if ((uint)size > (uint)Avail)
                NinoThrow.EndOfData();
            Skip(size);
            return ref block;
        }

        /// <summary>
        /// Consumes <paramref name="count"/> (not negative) elements of <paramref name="elementSize"/> bytes each and
        /// returns a reference to the first one. The size in 64 bits, so it cannot overflow: one compare, where a
        /// checked int product would test the multiplication too.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte ReadElements(int count, int elementSize)
        {
            ulong size = (ulong)(uint)count * (uint)elementSize;
            ref byte block = ref Cur;
            if (size > (uint)Avail)
                NinoThrow.EndOfData();
            Skip((int)size);
            return ref block;
        }

        /// <summary>The next byte, not consumed: the caller already checked the bytes it looks at are there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte Peek() => ref Cur;

        /// <summary>Consumes <paramref name="size"/> bytes the caller already checked are there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte UnsafeAdvance(int size)
        {
            ref byte block = ref Cur;
            Skip(size);
            return ref block;
        }

        /// <summary>Skips <paramref name="count"/> bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count) => ReadBlock(count);

        /// <summary>Reads an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T UnsafeRead<T>()
        {
            int size = Unsafe.SizeOf<T>();
            ref byte block = ref Cur;
            if ((uint)size > (uint)Avail)
                NinoThrow.EndOfData();
            Skip(size);
            return NinoUnsafe.Read<T>(ref block);
        }

        /// <summary>
        /// Generated code: <see cref="UnsafeRead{T}"/> of a raw member whose type may hold values no constructor makes,
        /// as <see cref="NinoUnsafe.ReadChecked{T}"/>.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadChecked<T>()
        {
            T value = UnsafeRead<T>();
            return NinoLayout<T>.Checks == null ? value : NinoChecks.Checked(value);
        }

        /// <summary>
        /// Reads an unmanaged value written by <see cref="Writer.WriteValue{T}"/> (converted on runtimes that lay
        /// the type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadValue<T>() => NinoLayout<T>.Read(ref ReadBlock(NinoLayout<T>.Size));

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>() where T : unmanaged => ReadValue<T>();

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T value) where T : unmanaged => value = ReadValue<T>();

        /// <summary>Reads a bool: a byte of 0 or 1 (any other is invalid data).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out bool value) => value = ReadBool();

        /// <summary>
        /// Generated code: reads a bool member, a byte of 0 or 1; any other is invalid data. Its own method rather
        /// than a check in <see cref="UnsafeRead{T}"/>, which every other raw member is read with.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool()
        {
            byte value = UnsafeRead<byte>();
            if (value > 1)
                NinoThrow.InvalidBool(value);
            return value != 0;
        }

        /// <summary>Reads a nullable unmanaged value written by <see cref="Writer.Write{T}(T?)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T? value) where T : unmanaged => value = ReadNullableRaw<T>();

        /// <summary>
        /// Reads what <see cref="Writer.WriteNullableRaw{T}"/> wrote: the has-value byte and the value with one bounds
        /// check where both fit in the remaining bytes, else (near the end, where a null takes only its byte) one each.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T? ReadNullableRaw<T>() where T : struct
        {
            int size = NinoLayout<T>.Size;
            ref byte block = ref Cur;
            if ((uint)size < (uint)Avail)
            {
                if (block != NinoWire.NotNull)
                {
                    if (block != NinoWire.Null)
                        NinoThrow.InvalidPresence(block);
                    Skip(1);
                    return null;
                }

                Skip(1 + size);
                return NinoLayout<T>.Read(ref Unsafe.Add(ref block, 1));
            }

            if (!ReadPresent())
                return null;
            return ReadValue<T>();
        }

        /// <summary>Consumes 4 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt32(int expected)
        {
            if (Avail < sizeof(int) || NinoUnsafe.Read<int>(ref Cur) != expected)
                return false;
            Skip(sizeof(int));
            return true;
        }

        /// <summary>Consumes 8 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt64(long expected)
        {
            if (Avail < sizeof(long) || NinoUnsafe.Read<long>(ref Cur) != expected)
                return false;
            Skip(sizeof(long));
            return true;
        }

        /// <summary>
        /// Reads a collection length. Returns -1 for null. Throws if the payload cannot possibly contain
        /// <c>length * minElementSize</c> more bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadLength(int minElementSize = 1) =>
            // A constant in the formatters: one compare covers null, invalid and too long.
            TryReadLength(Math.Max(minElementSize, 0), out int count) ? count : NinoWire.NullLength;

        /// <summary>
        /// Reads a collection length into <paramref name="length"/>; false for null. Throws if the payload cannot
        /// possibly contain <c>length * minElementSize</c> more bytes (<paramref name="minElementSize"/> not negative).
        /// The sign is tested first: the callers then know the length is not negative, which drops the checks of
        /// <c>new List&lt;T&gt;(length)</c>, <c>CollectionsMarshal.SetCount</c> and the like.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadLength(int minElementSize, out int length)
        {
            length = UnsafeRead<int>();
            if (length < 0)
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                return false;
            }

            if ((ulong)(uint)length * (uint)minElementSize > (uint)Avail || NinoLimits.TooLong(length))
                NinoThrow.InvalidLength(length);
            return true;
        }

        /// <summary>
        /// <see cref="TryReadLength(int, out int)"/> in the prologue of a collection formatter: for null, the
        /// collection in <paramref name="value"/> is dropped (<see cref="NinoRecycler.Discard{T}"/>) and false
        /// returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadLengthOrDiscard<T>(ref T value, int minElementSize, out int length)
        {
            if (TryReadLength(minElementSize, out length))
                return true;
            NinoRecycler.Discard(ref value);
            return false;
        }

        /// <summary>
        /// Reads the 1-byte null flag of a sealed class (or tuple, or nullable value): false for null, after
        /// dropping the instance in <paramref name="value"/> (<see cref="NinoRecycler.Discard{T}"/>); true when the
        /// body follows.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryReadPresent<T>(ref T value)
        {
            if (ReadPresent())
                return true;
            NinoRecycler.Discard(ref value);
            return false;
        }

        /// <summary>
        /// Reads a presence byte (a null flag): true for 1, a value follows; false for 0, null. Any other byte is
        /// invalid data. The common case, a value, takes one compare.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadPresent()
        {
            byte flag = UnsafeRead<byte>();
            if (flag == NinoWire.NotNull)
                return true;
            if (flag != NinoWire.Null)
                NinoThrow.InvalidPresence(flag);
            return false;
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

        /// <summary>Reads <paramref name="length"/> raw bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> ReadRaw(int length)
        {
            if ((uint)length > (uint)Avail)
                NinoThrow.EndOfData();
            var span = Peek(length);
            Skip(length);
            return span;
        }

        /// <summary>Copies a block of unmanaged values (no length prefix) into <paramref name="destination"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUnmanagedRaw<T>(Span<T> destination) where T : unmanaged
        {
            if (!NinoLayout<T>.CopiesAsIs)
            {
                this = ReadUnmanagedOther(destination, this);
                return;
            }

            // One bounds check and a block copy (NinoUnsafe.CopyToNew): AsBytes, Slice and CopyTo would add an overflow
            // check, two more bounds checks and a call through SpanHelpers.Memmove's indirection cell.
            int count = destination.Length;
            NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination)),
                ref ReadElements(count, Unsafe.SizeOf<T>()), (uint)count * (uint)Unsafe.SizeOf<T>());
        }

        /// <summary>
        /// <see cref="ReadUnmanagedRaw{T}"/> for values not copied as they are (<see cref="NinoLayout{T}.CopiesAsIs"/>):
        /// bools, checked in the data before one block copy (a byte other than 0 or 1 is invalid data), and values of a
        /// layout this runtime converts, one by one. Out of line, with the reader by value.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadUnmanagedOther<T>(Span<T> destination, Reader reader) where T : unmanaged
        {
            if (NinoLayout<T>.IsCanonical)
            {
                int count = destination.Length;
                ref byte src = ref reader.ReadElements(count, Unsafe.SizeOf<T>());
                NinoChecks.Values<T>(ref src, count, Unsafe.SizeOf<T>());
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination)), ref src,
                    (uint)count * (uint)Unsafe.SizeOf<T>());
                return reader;
            }

            for (int i = 0; i < destination.Length; i++)
                destination[i] = reader.ReadValue<T>();
            return reader;
        }

        /// <summary>Reads a length-prefixed unmanaged array written by <see cref="Writer.Write{T}(T[])"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T[] value) where T : unmanaged
        {
            value = null;
            default(UnmanagedArrayFormatter<T>).Deserialize(ref value, ref this);
        }

        private static string EmptyOrNull(int length)
        {
            if (length == 0)
                return string.Empty;
            if (length != NinoWire.NullLength)
                NinoThrow.InvalidLength(length);
            return null;
        }

        /// <summary>Reads a string written by <see cref="Writer.Write(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out string value) => value = ReadString();

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUtf8(out string value) => value = ReadUtf8();

        /// <summary>Reads a string written by <see cref="Writer.Write(string)"/>.</summary>
        // The header and the chars with the position and the data loaded once and stored once. Not inlined: a
        // formatter reading several strings stays small.
        public string ReadString()
        {
            string value = NinoStrings.Read(ref Cur, Avail, null, out int size);
            Skip(size);
            return value;
        }

        /// <summary>
        /// Reads <paramref name="count"/> strings into the slots starting at <paramref name="first"/> (the
        /// elements of a string collection): <see cref="ReadString"/> in one loop that keeps the position and the
        /// data in registers, where through the reader's reference they are loaded, and the position stored, for
        /// every string.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadStrings(ref string first, int count) =>
            Skip(ReadStrings(ref Cur, Avail, ref first, count));

        // Static, the loop's state in five values (data, position, end, slot, last slot): as many as x64 has
        // callee-saved registers to keep them in across the string allocations. Returns the bytes read.
        private static int ReadStrings(ref byte data, int end, ref string first, int count)
        {
            int pos = 0;
            ref string slot = ref first;
            ref string last = ref Unsafe.Add(ref first, count);
            while (Unsafe.IsAddressLessThan(ref slot, ref last))
            {
                // An element read in place keeps its string when the data holds the same chars.
                slot = NinoStrings.Read(ref Unsafe.Add(ref data, pos), end - pos, slot, out int size);
                pos += size;
                slot = ref Unsafe.Add(ref slot, 1);
            }

            return pos;
        }

        /// <summary>
        /// Reads <paramref name="count"/> pairs of a raw key (its memory is its wire layout) and a string into
        /// <paramref name="dictionary"/>: <see cref="ReadStrings"/> for a <c>Dictionary&lt;TKey, string&gt;</c>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadStringPairs<TKey>(System.Collections.Generic.Dictionary<TKey, string> dictionary, int count) =>
            Skip(ReadStringPairs(dictionary, ref Cur, Avail, count));

        // Generic over the key only, so a value-type key makes this unshared code (the dictionary's methods inlined
        // with no run-time lookups), with its state (dictionary, data, position, end, pairs left) kept in registers
        // across the string and dictionary allocations.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadStringPairs<TKey>(System.Collections.Generic.Dictionary<TKey, string> dictionary,
            ref byte data, int end, int count)
        {
            // Returns the bytes read.
            int keySize = Unsafe.SizeOf<TKey>();
            int pos = 0;
            // Added here where the dictionary is as expected, else by it and the key's bucket walked after (hash
            // flooding, HashTables).
            bool insert = Internal.HashTables.CanInsert(dictionary, count);
            while (count-- > 0)
            {
                if ((uint)(end - pos) < (uint)keySize)
                    NinoThrow.EndOfData();
                if (!NinoLayout<TKey>.CopiesAsIs)
                    NinoChecks.Values<TKey>(ref Unsafe.Add(ref data, pos), 1, keySize);
                TKey key = NinoUnsafe.Read<TKey>(ref Unsafe.Add(ref data, pos));
                pos += keySize;
                string value = NinoStrings.Read(ref Unsafe.Add(ref data, pos), end - pos, null, out int size);
                pos += size;
                if (insert)
                {
                    Internal.HashTables.Insert(dictionary, key, value);
                }
                else
                {
                    NinoCollections.AddUnique(dictionary, key, value);
                    Internal.HashTables.Added(dictionary, key);
                }
            }

            return pos;
        }

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        // Inlined, with the transcoding out of line taking values: an instance call would take the reader's address,
        // which keeps all its fields in memory in the caller.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadUtf8()
        {
            int remaining = Avail;
            ref byte start = ref Cur;
            int length;
            if ((uint)remaining >= 2 * sizeof(int))
            {
                // The char count and the byte count in one load.
                ulong counts = NinoUnsafe.Read<ulong>(ref start);
                length = (int)counts;
                if (length > 0)
                {
                    int size = (int)(counts >> 32);
                    if ((uint)size > (uint)(remaining - 2 * sizeof(int)))
                        NinoThrow.EndOfData();
                    // Moved past the string before decoding it, so nothing is kept across the call (a throw ends
                    // the read).
                    Skip(2 * sizeof(int) + size);
                    return DecodeUtf8(ref Unsafe.Add(ref start, 2 * sizeof(int)), size, length);
                }
            }
            else
            {
                length = Utf8ShortCount(ref start, remaining);
            }

            Skip(sizeof(int));
            return EmptyOrNull(length);
        }

        /// <summary>
        /// Reads a string like <see cref="ReadString()"/>, but returns <paramref name="current"/> when it holds the
        /// same chars: reading into an existing object keeps an unchanged string instead of allocating a copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadString(string current) => current == null ? ReadString() : ReadStringOver(current);

        private string ReadStringOver(string current)
        {
            string value = NinoStrings.Read(ref Cur, Avail, current, out int size);
            Skip(size);
            return value;
        }

        /// <summary>
        /// Reads a UTF-8 string like <see cref="ReadUtf8()"/>, but returns <paramref name="current"/> when it holds
        /// the same text (checked for ASCII text on .NET 8 and later): reading into an existing object keeps an
        /// unchanged string instead of allocating a copy.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadUtf8(string current) => current == null ? ReadUtf8() : ReadUtf8Over(current);

        private string ReadUtf8Over(string current)
        {
            if (current.Length != 0 && Avail >= 2 * sizeof(int))
            {
                ref byte start = ref Cur;
                ulong counts = NinoUnsafe.Read<ulong>(ref start);
                // ASCII text: as many bytes as chars, compared without decoding.
                if ((int)counts == current.Length && (int)(counts >> 32) == current.Length &&
                    current.Length <= Avail - 2 * sizeof(int) &&
                    NinoStrings.SameAscii(ref Unsafe.Add(ref start, 2 * sizeof(int)), current))
                {
                    Skip(2 * sizeof(int) + current.Length);
                    return current;
                }
            }

            return ReadUtf8();
        }

        /// <summary>
        /// The char count of a UTF-8 string in the last 0 to 7 bytes, where only a null or empty one (a count alone)
        /// fits; anything else is data that ends early.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Utf8ShortCount(ref byte src, int remaining)
        {
            if (remaining < sizeof(int))
                NinoThrow.EndOfData();
            int length = NinoUnsafe.Read<int>(ref src);
            if (length > 0)
                NinoThrow.EndOfData();
            return length;
        }

        /// <summary>
        /// Decodes exactly the <paramref name="size"/> bytes at <paramref name="src"/> into a string of
        /// <paramref name="length"/> (&gt; 0) chars, in one pass. Bytes that are not UTF-8, or that decode to another
        /// number of chars, throw; a char count above the byte count throws before the string is allocated.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe string DecodeUtf8(ref byte src, int size, int length)
        {
            // Every char (UTF-16 code unit) takes 1 to 3 bytes (a surrogate pair 4 for 2): size >= length bounds the
            // allocation by the data. size > 3 * length decodes to more chars than the string holds, which the
            // transcoder reports (DestinationTooSmall).
            if (size < length)
                NinoThrow.InvalidData("invalid UTF-8 string.");
            var value = NinoStrings.Allocate(length);
#if NET8_0_OR_GREATER
            // Done means every byte was decoded; the chars must then fill the string exactly.
            var status = System.Text.Unicode.Utf8.ToUtf16(MemoryMarshal.CreateReadOnlySpan(ref src, size),
                MemoryMarshal.CreateSpan(ref Unsafe.AsRef(in value.GetPinnableReference()), length),
                out _, out int written, replaceInvalidSequences: false);
            if (status != System.Buffers.OperationStatus.Done || written != length)
                NinoThrow.InvalidData("invalid UTF-8 string.");
#else
            fixed (byte* p = &src)
            fixed (char* d = value)
            {
                int written;
                try
                {
                    // Strict: invalid bytes, or more chars than the string holds, throw ArgumentException.
                    written = StrictUtf8.GetChars(p, size, d, length);
                }
                catch (ArgumentException)
                {
                    written = -1;
                }

                if (written != length)
                    NinoThrow.InvalidData("invalid UTF-8 string.");
            }
#endif
            return value;
        }

#if !NET8_0_OR_GREATER
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
#endif
    }
}
