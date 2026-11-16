using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#else
using System.Numerics;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// The byte-vector operations every width shares, so a kernel is written once over <typeparamref name="TVector"/>
    /// and called per width: <see cref="NinoVectors128"/>, <see cref="NinoVectors256"/> and
    /// <see cref="NinoVectors512"/> on .NET 8+, <see cref="NinoVectors"/> (<see cref="Vector{T}"/> of bytes) in the
    /// netstandard2.1 build. The masked copy (<see cref="NinoMask"/>) and the flag checks (<see cref="NinoChecks"/>) use
    /// it; a kernel whose per-width work is its own (widening Latin-1 compares, bool slots on 16-bit lanes) keeps its
    /// own structs. Empty structs called through <c>default(TOps)</c>, so every call is direct and inlined; instance
    /// members, not static abstract ones, which the netstandard2.1 build cannot declare.
    /// </summary>
    internal interface INinoVectors<TVector> where TVector : struct
    {
        /// <summary>The vector at <paramref name="offset"/> bytes from <paramref name="at"/>, unaligned.</summary>
        TVector Load(ref byte at, nuint offset);

        /// <summary>Stores <paramref name="value"/> at <paramref name="offset"/> bytes from <paramref name="at"/>, unaligned.</summary>
        void Store(TVector value, ref byte at, nuint offset);

        TVector And(TVector left, TVector right);

        TVector Or(TVector left, TVector right);
#if NET8_0_OR_GREATER

        /// <summary>The first 16 bytes.</summary>
        Vector128<byte> Lower(TVector value);

        /// <summary>Whether <paramref name="bits"/> has a bit set where <paramref name="mask"/>, repeated every 16 bytes, has one.</summary>
        bool HasMaskedBits(TVector bits, Vector128<ulong> mask);
#endif
    }

#if !NET8_0_OR_GREATER
    /// <summary><see cref="Vector{T}"/> of bytes, the netstandard2.1 build's one width.</summary>
    internal readonly struct NinoVectors : INinoVectors<Vector<byte>>
    {
        // Plain ldobj / stobj (Unsafe.As), not Unsafe.ReadUnaligned / WriteUnaligned: classic Mono compiles their
        // unaligned. prefix on a 16-byte struct to a call to memcpy through a stack slot (mini_emit_memory_load in
        // mono/mini/memory-access.c), and plain ones to movups. RyuJIT never assumes a vector aligned: NinoMask's
        // machine code on .NET 10 is the same with either form (netstandard and net10.0 builds).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector<byte> Load(ref byte at, nuint offset) =>
            Unsafe.As<byte, Vector<byte>>(ref Unsafe.Add(ref at, offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Store(Vector<byte> value, ref byte at, nuint offset) =>
            Unsafe.As<byte, Vector<byte>>(ref Unsafe.Add(ref at, offset)) = value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector<byte> And(Vector<byte> left, Vector<byte> right) => left & right;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector<byte> Or(Vector<byte> left, Vector<byte> right) => left | right;
    }
#else
    internal readonly struct NinoVectors128 : INinoVectors<Vector128<byte>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<byte> Load(ref byte at, nuint offset) => Vector128.LoadUnsafe(ref at, offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Store(Vector128<byte> value, ref byte at, nuint offset) => value.StoreUnsafe(ref at, offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<byte> And(Vector128<byte> left, Vector128<byte> right) => left & right;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<byte> Or(Vector128<byte> left, Vector128<byte> right) => left | right;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<byte> Lower(Vector128<byte> value) => value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasMaskedBits(Vector128<byte> bits, Vector128<ulong> mask) =>
            (bits.AsUInt64() & mask) != Vector128<ulong>.Zero;
    }

    internal readonly struct NinoVectors256 : INinoVectors<Vector256<byte>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector256<byte> Load(ref byte at, nuint offset) => Vector256.LoadUnsafe(ref at, offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Store(Vector256<byte> value, ref byte at, nuint offset) => value.StoreUnsafe(ref at, offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector256<byte> And(Vector256<byte> left, Vector256<byte> right) => left & right;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector256<byte> Or(Vector256<byte> left, Vector256<byte> right) => left | right;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<byte> Lower(Vector256<byte> value) => value.GetLower();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasMaskedBits(Vector256<byte> bits, Vector128<ulong> mask) =>
            (bits.AsUInt64() & Vector256.Create(mask, mask)) != Vector256<ulong>.Zero;
    }

    /// <summary>64-byte vectors: callers check <c>Vector512.IsHardwareAccelerated</c> or <see cref="NinoSimd.Prefer512"/>.</summary>
    internal readonly struct NinoVectors512 : INinoVectors<Vector512<byte>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector512<byte> Load(ref byte at, nuint offset) => Vector512.LoadUnsafe(ref at, offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Store(Vector512<byte> value, ref byte at, nuint offset) => value.StoreUnsafe(ref at, offset);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector512<byte> And(Vector512<byte> left, Vector512<byte> right) => left & right;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector512<byte> Or(Vector512<byte> left, Vector512<byte> right) => left | right;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector128<byte> Lower(Vector512<byte> value) => value.GetLower().GetLower();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasMaskedBits(Vector512<byte> bits, Vector128<ulong> mask) =>
            (bits.AsUInt64() & NinoSimd.Broadcast128To512(mask)) != Vector512<ulong>.Zero;
    }
#endif
}
