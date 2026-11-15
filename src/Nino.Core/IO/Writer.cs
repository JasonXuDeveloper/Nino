using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    public ref partial struct Writer
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
        [EditorBrowsable(EditorBrowsableState.Never)]
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
            return owner.Grow(size);
        }

        /// <summary>
        /// Reserves <paramref name="size"/> bytes and returns a reference to the first one, for a custom formatter that
        /// encodes them itself, little-endian as all of Nino's data (with <c>BinaryPrimitives</c>, for instance). The
        /// bytes are not cleared: write every one. The reference is valid only until the next write to this writer,
        /// which may move the output to a larger buffer.
        /// </summary>
        /// <exception cref="OverflowException"><paramref name="size"/> is negative.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref byte Reserve(int size)
        {
            ref byte block = ref Ensure(size);
            Skip(size);
            return ref block;
        }

#if !NET10_0_OR_GREATER
        // Some BCL copy APIs take an array and offset instead of a span.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte[] ReserveArray(int size, out int offset)
        {
            Ensure(size);
            offset = Pos;
            Skip(size);
            return _owner.Buffer;
        }
#endif

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
    }
}
