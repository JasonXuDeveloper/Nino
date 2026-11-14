using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Reads Nino binary data. Every read is bounds checked, and lengths are validated against the remaining
    /// bytes before anything is allocated.
    /// </summary>
    public ref partial struct Reader
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
        // AggressiveInlining, as everything a top-level call inlines: the JIT counts what it inlines into a method
        // without being told to against the method's inlining budget, together with everything that inlines into
        // it in turn, and with the budget spent leaves even the helpers marked AggressiveInlining as calls.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        [EditorBrowsable(EditorBrowsableState.Never)]
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

        /// <summary>The data up to <paramref name="end"/> (at most its length, checked by the caller).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ReadOnlySpan<byte> Prefix(ReadOnlySpan<byte> data, int end)
        {
            // No second bounds check: the caller compared the end with the data.
            return MemoryMarshal.CreateReadOnlySpan(ref MemoryMarshal.GetReference(data), end);
        }
    }
}
