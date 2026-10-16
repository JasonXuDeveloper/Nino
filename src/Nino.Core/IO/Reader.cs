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
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;
        private NinoReferenceReadTable _refs;

        // Open class bodies and regions, and the bodies deferred below NinoDepth.Inline (see NinoDepth). A reader
        // made by ReadSlice or ReadRegion continues its parent's state; _origin is where its data starts in the
        // top-level data, and _section the size of the deferred bodies' section after a region (0 for none).
        private NinoDeferrals _deferrals;
        private int _origin;
        internal int Depth;
        private int _regions;
        private int _section;

        /// <exception cref="PlatformNotSupportedException">The host is big-endian.</exception>
        public Reader(ReadOnlySpan<byte> data)
        {
            // A JIT-time constant on CoreCLR, NativeAOT and Mono: the check folds away.
            if (!BitConverter.IsLittleEndian)
                NinoThrow.BigEndianHost();
            _data = data;
            _pos = 0;
            _refs = null;
            _deferrals = null;
            _origin = 0;
            Depth = 0;
            _regions = 0;
            _section = 0;
        }

        /// <summary>A slice of a reader that already passed the endianness check.</summary>
        private Reader(ReadOnlySpan<byte> data, NinoReferenceReadTable refs, NinoDeferrals deferrals, int origin,
            int depth, int regions)
        {
            _data = data;
            _pos = 0;
            _refs = refs;
            _deferrals = deferrals;
            _origin = origin;
            Depth = depth;
            _regions = regions;
            _section = 0;
        }

        /// <summary>
        /// Ends reference tracking for this reader. Called by the <see cref="NinoDeserializer"/> APIs that create
        /// the reader; the table is reused by the next call. Slices of this reader must not be used afterwards.
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
            NinoReferenceReadTable.Return(_refs);
            _refs = null;
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

        private int BeginReference() => (_refs ??= NinoReferenceReadTable.Rent()).Begin();

        private T GetReference<T>(int index) where T : class
        {
            if (_refs == null)
                NinoThrow.InvalidReference(index);
            var obj = _refs.Get(index);
            if (obj is T t)
                return t;
            NinoThrow.ReferenceTypeMismatch(typeof(T), obj.GetType());
            return null;
        }

        /// <summary>Records the instance of the object whose header was just read.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetReference(object value)
        {
            var refs = _refs;
            if (refs != null && refs.Pending != 0)
                refs.Set(refs.Pending, value);
        }

        /// <summary>Records the object read after <see cref="ReadReference{T}"/> returned true.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndReference(int index, object value) => _refs.Set(index, value);

        /// <summary>
        /// Call when done with a reader returned by <see cref="ReadSlice"/>: objects it read stay visible to
        /// later back references.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndSlice(in Reader slice)
        {
            if (_refs == null)
                _refs = slice._refs;
            if (_deferrals == null)
                _deferrals = slice._deferrals;
        }

        /// <summary>
        /// Generated code: reads a length-prefixed region (a version-tolerant level, a keyed body or member),
        /// written between <see cref="Writer.BeginRegion"/> and <see cref="Writer.EndRegion"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Reader ReadRegion()
        {
            int length = UnsafeRead<int>();
            if ((uint)length > (uint)Remaining)
                return ReadSectionedRegion(length);
            var region = new Reader(_data.Slice(_pos, length), _refs, _deferrals, _origin + _pos, Depth, _regions + 1);
            _pos += length;
            return region;
        }

        /// <summary>A region followed by a section of deferred bodies, or invalid data.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Reader ReadSectionedRegion(int length)
        {
            int total = length & int.MaxValue;
            if (length >= 0 || total < sizeof(int) || total > Remaining)
                NinoThrow.InvalidLength(length);
            int members = NinoUnsafe.Read<int>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data),
                _pos + total - sizeof(int)));
            if ((uint)members > (uint)(total - sizeof(int)))
                NinoThrow.InvalidData("invalid offset of deferred bodies.");
            var region = new Reader(_data.Slice(_pos, members), _refs, _deferrals, _origin + _pos, Depth, _regions + 1);
            region._section = total - sizeof(int) - members;
            _pos += total;
            return region;
        }

        /// <summary>
        /// Generated code: call when done with a reader returned by <see cref="ReadRegion"/>, right after reading
        /// it. At the end of the outermost region, reads the bodies deferred inside it.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void EndRegion(in Reader region)
        {
            if (_refs == null)
                _refs = region._refs;
            if (region._deferrals != null)
                EndDeferredRegion(in region);
        }

        /// <summary>
        /// Reads the section after the outermost region: each body deferred inside the region is matched, in
        /// order, with its entry (by the key the writer gave it); entries for objects this reader skipped (members
        /// it does not know) are skipped. Bodies deferred while reading the section are appended to the queue.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EndDeferredRegion(in Reader region)
        {
            var d = _deferrals = region._deferrals;
            if (region._regions != 1 || d.Region.IsEmpty)
                return;
            if (region._section == 0)
                NinoThrow.InvalidData("missing deferred bodies.");

            int start = _pos - sizeof(int) - region._section;
            var section = new Reader(_data.Slice(start, region._section), _refs, d, _origin + start, Depth, 1);
            int depth = Math.Min(Depth, NinoDepth.Inline - 1) + 1;
            while (!d.Region.IsEmpty)
            {
                var item = d.Region.Items[d.Region.Head++];
                while (true)
                {
                    int key = section.UnsafeRead<int>();
                    int length = section.UnsafeRead<int>();
                    if ((uint)length > (uint)section.Remaining)
                        NinoThrow.InvalidLength(length);
                    int pos = section._pos;
                    section._pos = pos + length;
                    if (region._origin + key != item.Position)
                        continue;
                    var entry = new Reader(section._data.Slice(pos, length), section._refs, d, section._origin + pos,
                        depth, 1);
                    item.Body.Read(item.Value, ref entry);
                    if (section._refs == null)
                        section._refs = entry._refs;
                    break;
                }
            }

            d.Region.Clear();
            if (_refs == null)
                _refs = section._refs;
        }

        /// <summary>Closes a class body opened by incrementing <see cref="Depth"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ExitBody()
        {
            if (--Depth == 0 && _deferrals != null)
                ReadTopDeferred();
        }

        /// <summary>Every body is closed: reads the bodies deferred outside regions, in order (as written).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ReadTopDeferred()
        {
            var d = _deferrals;
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

        /// <summary>Defers the body of <paramref name="value"/> (created, not yet read), nested too deep to read now.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Defer(object value, NinoDeferredBody body)
        {
            var d = _deferrals ??= NinoDeferrals.Rent();
            var item = new NinoDeferredItem { Value = value, Body = body, Position = _origin + _pos };
            if (_regions == 0)
                d.Top.Add(in item);
            else
                d.Region.Add(in item);
        }

        /// <summary>Number of bytes consumed so far.</summary>
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
            if ((uint)length > (uint)Remaining)
                NinoThrow.InvalidLength(length);
            var slice = new Reader(_data.Slice(_pos, length), _refs, _deferrals, _origin + _pos, Depth, _regions);
            _pos += length;
            return slice;
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

            var bytes = MemoryMarshal.AsBytes(destination);
            ReadRaw(bytes.Length).CopyTo(bytes);
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

        /// <summary>Reads <paramref name="count"/> elements of <paramref name="elementSize"/> bytes (count &gt; 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref byte ReadBlockOf(int count, int elementSize)
        {
            int pos = _pos;
            ulong size = (ulong)(uint)count * (uint)elementSize;
            if (size > (uint)(_data.Length - pos))
                NinoThrow.EndOfData();
            _pos = pos + (int)size;
            return ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), pos);
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
            int length = UnsafeRead<int>();
            if (length <= 0)
                return EmptyOrNull(length);

            // One bounds check: the string is only allocated after it.
            ref byte src = ref ReadBlockOf(length, sizeof(char));
#if NETSTANDARD2_0
            fixed (byte* p = &src)
            {
                return new string((char*)p, 0, length);
            }
#else
            return new string(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<byte, char>(ref src), length));
#endif
        }

        /// <summary>Reads a UTF-8 string written by <see cref="Writer.WriteUtf8(string)"/>.</summary>
        public unsafe string ReadUtf8()
        {
            int length = UnsafeRead<int>();
            if (length <= 0)
                return EmptyOrNull(length);

            ref byte src = ref ReadBlockOf(length, 1);
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
