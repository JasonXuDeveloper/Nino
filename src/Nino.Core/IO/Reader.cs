using System;
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
        // A reader made by ReadSlice or ReadRegion continues its parent's state and shares its data: its span is
        // the parent's cut at the reader's end, and positions count from the start of the top-level data. So a
        // region needs no origin of its own (deferrals are keyed by position), and the section after it is found
        // from the parent's position and the region's end.
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

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
            _data = data;
        }

        /// <summary>A part of a reader that already passed the endianness check, from <paramref name="pos"/>.</summary>
        private Reader(ReadOnlySpan<byte> data, int pos, int depth, int regions, NinoReadContext context)
        {
            _data = data;
            _pos = pos;
            Depth = depth;
            _regions = regions;
            _context = context;
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetReference(object value)
        {
            var refs = _context?.Refs;
            if (refs != null && refs.Pending != 0)
                refs.Set(refs.Pending, value);
        }

        /// <summary>Records the object read after <see cref="ReadReference{T}"/> returned true.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndReference(int index, object value) => _context.Refs.Set(index, value);

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
        /// Generated code: reads a length-prefixed region (a version-tolerant level, a keyed body or member),
        /// written between <see cref="Writer.BeginRegion"/> and <see cref="Writer.EndRegion"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader ReadRegion()
        {
            int length = UnsafeRead<int>();
            int pos = _pos;
            int members = length;
            if ((uint)length > (uint)(_data.Length - pos))
            {
                // A region followed by a section of deferred bodies, or invalid data: the region ends where its
                // members do, and this reader skips the section too.
                members = SectionedRegion(ref MemoryMarshal.GetReference(_data), pos, _data.Length - pos, length);
                length &= int.MaxValue;
            }

            _pos = pos + length;
            return new Reader(Prefix(_data, pos + members), pos, Depth, _regions + 1, _context);
        }

        /// <summary>
        /// The length of the members of a region whose length has <see cref="NinoDepth.SectionFlag"/> set (a section of
        /// deferred bodies follows them, then the section's offset); throws on any other invalid length.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SectionedRegion(ref byte data, int pos, int remaining, int length)
        {
            int total = length & int.MaxValue;
            if (length >= 0 || total < sizeof(int) || total > remaining)
                NinoThrow.InvalidLength(length);
            int members = NinoUnsafe.Read<int>(ref Unsafe.Add(ref data, pos + total - sizeof(int)));
            if ((uint)members > (uint)(total - sizeof(int)))
                NinoThrow.InvalidData("invalid offset of deferred bodies.");
            return members;
        }

        /// <summary>
        /// Generated code: call when done with a reader returned by <see cref="ReadRegion"/>, right after reading
        /// it. At the end of the outermost region, reads the bodies deferred inside it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndRegion(in Reader region)
        {
            var context = region._context;
            if (context == null)
                return;
            // The region's context is this reader's, or one the region created (this reader had none).
            _context = context;
            if (_regions == 0 && context.Deferrals != null)
                EndDeferredRegion(_data, _pos, region._data.Length, context, Depth);
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
            int entryDepth = Math.Min(depth, NinoDepth.Inline - 1) + 1;
            while (!d.Region.IsEmpty)
            {
                var item = d.Region.Items[d.Region.Head++];
                while (true)
                {
                    int key = section.UnsafeRead<int>();
                    int length = section.UnsafeRead<int>();
                    if ((uint)length > (uint)section.Remaining)
                        NinoThrow.InvalidLength(length);
                    int entryPos = section._pos;
                    section._pos = entryPos + length;
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
            _context = DeferBody<T, TFormatter>(value, _context, _pos, _regions);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoReadContext DeferBody<T, TFormatter>(T value, NinoReadContext context, int position,
            int regions)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            context ??= NinoReadContext.Rent();
            // A tracked object is recorded now: back references to it may come before its body.
            var refs = context.Refs;
            if (refs != null && refs.Pending != 0)
                refs.Set(refs.Pending, value);
            var d = context.Deferrals ??= NinoDeferrals.Rent();
            var item = new NinoDeferredItem
                { Value = value, Body = NinoDeferredBody<T, TFormatter>.Instance, Position = position };
            if (regions == 0)
                d.Top.Add(in item);
            else
                d.Region.Add(in item);
            return context;
        }

        /// <summary>
        /// Position in the data: bytes consumed so far. A reader from <see cref="ReadSlice"/> counts from the start
        /// of the data of the reader it was made from.
        /// </summary>
        public int Position
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pos;
        }

        /// <summary>Number of bytes left.</summary>
        public int Remaining
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _data.Length - _pos;
        }

        /// <summary>Whether all bytes have been consumed.</summary>
        public bool Eof
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pos >= _data.Length;
        }

        /// <summary>
        /// Consumes <paramref name="size"/> bytes and returns a reference to the first one.
        /// Use <see cref="NinoUnsafe.Read{T}(ref byte, int)"/> to decode the block.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte ReadBlock(int size)
        {
            int pos = _pos;
            if ((uint)size > (uint)(_data.Length - pos))
                NinoThrow.EndOfData();
            _pos = pos + size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos);
        }

        /// <summary>Consumes <paramref name="size"/> bytes the caller already checked are there.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ref byte UnsafeAdvance(int size)
        {
            int pos = _pos;
            _pos = pos + size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos);
        }

        /// <summary>Skips <paramref name="count"/> bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(int count) => ReadBlock(count);

        /// <summary>Reads an unmanaged value. <typeparamref name="T"/> must not contain references.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T UnsafeRead<T>()
        {
            int size = Unsafe.SizeOf<T>();
            int pos = _pos;
            if ((uint)size > (uint)(_data.Length - pos))
                NinoThrow.EndOfData();
            _pos = pos + size;
            return NinoUnsafe.Read<T>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos));
        }

        /// <summary>
        /// Reads an unmanaged value written by <see cref="Writer.WriteValue{T}"/> (converted on runtimes that lay
        /// the type out differently). <typeparamref name="T"/> must not contain references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T ReadValue<T>() => NinoLayout<T>.Read(ref ReadBlock(NinoLayout<T>.Size));

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>() where T : unmanaged => ReadValue<T>();

        /// <summary>Reads an unmanaged value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T value) where T : unmanaged => value = ReadValue<T>();

        /// <summary>Reads a bool (any non-zero byte is true).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out bool value) => value = UnsafeRead<byte>() != 0;

        /// <summary>Reads a nullable unmanaged value written by <see cref="Writer.Write{T}(T?)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read<T>(out T? value) where T : unmanaged
        {
            if (UnsafeRead<byte>() == NinoWire.Null)
            {
                value = null;
                return;
            }

            value = ReadValue<T>();
        }

        /// <summary>Consumes 4 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt32(int expected)
        {
            int pos = _pos;
            if (_data.Length - pos < sizeof(int) ||
                NinoUnsafe.Read<int>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos)) != expected)
                return false;
            _pos = pos + sizeof(int);
            return true;
        }

        /// <summary>Consumes 8 bytes if they equal <paramref name="expected"/>; otherwise consumes nothing.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryReadInt64(long expected)
        {
            int pos = _pos;
            if (_data.Length - pos < sizeof(long) ||
                NinoUnsafe.Read<long>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos)) != expected)
                return false;
            _pos = pos + sizeof(long);
            return true;
        }

        /// <summary>
        /// Reads a collection length. Returns -1 for null. Throws if the payload cannot possibly contain
        /// <c>length * minElementSize</c> more bytes.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadLength(int minElementSize = 1)
        {
            int length = UnsafeRead<int>();
            if (length < 0)
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                return NinoWire.NullLength;
            }

            if ((long)length * minElementSize > Remaining)
                NinoThrow.InvalidLength(length);
            return length;
        }

        /// <summary>Reads a length-prefixed sub-reader (used for version-tolerant bodies).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader ReadSlice()
        {
            int length = UnsafeRead<int>();
            int pos = _pos;
            if ((uint)length > (uint)(_data.Length - pos))
                NinoThrow.InvalidLength(length);
            _pos = pos + length;
            return new Reader(Prefix(_data, pos + length), pos, Depth, _regions, _context);
        }

        /// <summary>Reads <paramref name="length"/> raw bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<byte> ReadRaw(int length)
        {
            if ((uint)length > (uint)Remaining)
                NinoThrow.EndOfData();
            var span = _data.Slice(_pos, length);
            _pos += length;
            return span;
        }

        /// <summary>Copies a block of unmanaged values (no length prefix) into <paramref name="destination"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUnmanagedRaw<T>(Span<T> destination) where T : unmanaged
        {
            if (!NinoLayout<T>.IsCanonical)
            {
                for (int i = 0; i < destination.Length; i++)
                    destination[i] = ReadValue<T>();
                return;
            }

            // One bounds check and a block copy (the JIT's memcpy helper): AsBytes, Slice and CopyTo would add an
            // overflow check, two more bounds checks and a call through SpanHelpers.Memmove's indirection cell.
            ulong size = (ulong)(uint)destination.Length * (uint)Unsafe.SizeOf<T>();
            int pos = _pos;
            if (size > (uint)(_data.Length - pos))
                NinoThrow.EndOfData();
            _pos = pos + (int)size;
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(destination)),
                ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos), (uint)size);
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

        /// <summary>Reads a UTF-16 string written by <see cref="Writer.Write(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Read(out string value) => value = ReadString();

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ReadUtf8(out string value) => value = ReadUtf8();

        /// <summary>Reads a UTF-16 string written by <see cref="Writer.Write(string)"/>.</summary>
        public unsafe string ReadString()
        {
            // The length and the characters with the position and the data loaded once and stored once: through
            // UnsafeRead and ReadBlockOf, the position is stored, reloaded and stored again.
            int pos = _pos;
            int remaining = _data.Length - pos;
            if ((uint)remaining < sizeof(int))
                NinoThrow.EndOfData();
            ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos);
            int length = NinoUnsafe.Read<int>(ref start);
            if (length <= 0)
            {
                _pos = pos + sizeof(int);
                return EmptyOrNull(length);
            }

            // One bounds check: the string is only allocated after it.
            ulong size = (ulong)(uint)length * sizeof(char);
            if (size > (uint)(remaining - sizeof(int)))
                NinoThrow.EndOfData();
            _pos = pos + sizeof(int) + (int)size;
            ref byte src = ref Unsafe.Add(ref start, sizeof(int));
#if NETSTANDARD2_0
            fixed (byte* p = &src)
            {
                return new string((char*)p, 0, length);
            }
#else
            return new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref src), length));
#endif
        }

        /// <summary>
        /// Reads <paramref name="count"/> UTF-16 strings into the slots starting at <paramref name="first"/> (the
        /// elements of a string collection): <see cref="ReadString"/> in one loop that keeps the position and the
        /// data in registers, where through the reader's reference they are loaded, and the position stored, for
        /// every string.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ReadStrings(ref string first, int count) =>
            _pos = ReadStrings(ref MemoryMarshal.GetReference(_data), _pos, _data.Length, ref first, count);

        // Static, the loop's state in five values (data, position, end, slot, last slot): as many as x64 has
        // callee-saved registers to keep them in across the string allocations.
        private static unsafe int ReadStrings(ref byte data, int pos, int end, ref string first, int count)
        {
            ref string slot = ref first;
            ref string last = ref Unsafe.Add(ref first, count);
            while (Unsafe.IsAddressLessThan(ref slot, ref last))
            {
                if ((uint)(end - pos) < sizeof(int))
                    NinoThrow.EndOfData();
                int length = NinoUnsafe.Read<int>(ref Unsafe.Add(ref data, pos));
                pos += sizeof(int);
                if (length <= 0)
                {
                    slot = EmptyOrNull(length);
                }
                else
                {
                    // One bounds check: the string is only allocated after it.
                    ulong size = (ulong)(uint)length * sizeof(char);
                    if (size > (uint)(end - pos))
                        NinoThrow.EndOfData();
                    ref byte src = ref Unsafe.Add(ref data, pos);
                    pos += (int)size;
#if NETSTANDARD2_0
                    fixed (byte* p = &src)
                    {
                        slot = new string((char*)p, 0, length);
                    }
#else
                    slot = new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref src), length));
#endif
                }

                slot = ref Unsafe.Add(ref slot, 1);
            }

            return pos;
        }

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        // Inlined, with the transcoding out of line taking values: an instance call would take the reader's address,
        // which keeps all its fields in memory in the caller.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public string ReadUtf8()
        {
            int pos = _pos;
            int remaining = _data.Length - pos;
            if ((uint)remaining < sizeof(int))
                NinoThrow.EndOfData();
            ref byte start = ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos);
            int length = NinoUnsafe.Read<int>(ref start);
            if (length <= 0)
            {
                _pos = pos + sizeof(int);
                return EmptyOrNull(length);
            }

            if ((uint)length > (uint)(remaining - sizeof(int)))
                NinoThrow.EndOfData();
            _pos = pos + sizeof(int) + length;
            return DecodeUtf8(ref Unsafe.Add(ref start, sizeof(int)), length);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe string DecodeUtf8(ref byte src, int length)
        {
#if NETSTANDARD2_0
            fixed (byte* p = &src)
            {
                return Encoding.UTF8.GetString(p, length);
            }
#else
            return Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpan(ref src, length));
#endif
        }
    }
}
