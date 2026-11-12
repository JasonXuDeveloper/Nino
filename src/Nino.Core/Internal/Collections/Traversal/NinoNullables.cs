using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>T?</c> sequences with unmanaged <c>T</c>, in the canonical (64-bit corefx) <c>Nullable&lt;T&gt;</c> layout:
    /// a presence byte, zero padding up to T's alignment, then T in its canonical layout (zero when null).
    /// Where the runtime lays <c>T?</c> out the same way the elements are copied as one block (padding cleared);
    /// elsewhere (old Mono puts the value first, x86 aligns 8-byte values to 4) they are converted one by one.
    /// </summary>
    internal static class NinoNullables<T> where T : unmanaged
    {
        private static readonly int Offset = NinoLayout<T>.Info.Align > 0 ? NinoLayout<T>.Info.Align : 1;

        /// <summary>Wire size of one element.</summary>
        public static readonly int Stride = Offset + NinoLayout<T>.WireSize;

        /// <summary>Whether <c>T?</c> memory equals its wire layout on this runtime.</summary>
        // Not for bool: its values are read one by one, checked (NinoLayout<T>.CopiesAsIs).
        internal static readonly bool Raw = NinoLayout<T>.CopiesAsIs && NullableLayout<T>.HasValueOffset == 0 &&
                                           NullableLayout<T>.ValueOffset == Offset &&
                                           Unsafe.SizeOf<T?>() == Stride;

        /// <summary>
        /// <c>bool?</c> slots laid out as on the wire: little-endian, the presence byte first and the value after it,
        /// two bytes a slot, so slots copy as they are once their bools are checked (the native slot kernels' gate).
        /// </summary>
        private static readonly bool NativeBoolSlots = BitConverter.IsLittleEndian &&
                                                       NullableLayout<T>.HasValueOffset == 0 &&
                                                       NullableLayout<T>.ValueOffset == 1 &&
                                                       Unsafe.SizeOf<T?>() == 2 && Stride == 2 && Offset == 1;

        private static readonly NinoMask Mask = BuildMask();

        private static NinoMask BuildMask()
        {
            var mask = new byte[Stride];
            mask[0] = 0xFF;
            var value = NinoLayout<T>.Info.Mask;
            for (int i = 0; i < NinoLayout<T>.WireSize; i++)
                mask[Offset + i] = value == null ? (byte)0xFF : value[i];
            return NinoMask.Create(mask);
        }

        /// <summary>
        /// Zeroes the value of each element written without one: memory a <c>T?</c> was never assigned in (a rented or
        /// uninitialized array) may hold old bytes behind a cleared flag, and the wire holds zeros there (spec §6.2).
        /// </summary>
        // Out of line: a pass over the flags after the block copy, for the strides CopyLanes does not take.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ClearAbsent(ref byte destination, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref destination, i * Stride);
                if (slot == 0)
                    NinoMask.Zero(ref Unsafe.Add(ref slot, Offset), NinoLayout<T>.WireSize);
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// Strides that tile a vector (2, 4, 8 and 16 bytes: <c>byte?</c> to <c>long?</c>, <c>double?</c>,
        /// <c>DateTime?</c>), so each element is a lane, or a pair of 8-byte lanes, at a fixed place in every vector.
        /// </summary>
        private static readonly bool Lanes = Vector128.IsHardwareAccelerated &&
                                             (Stride == 2 || Stride == 4 || Stride == 8 || Stride == 16);

        /// <summary>The flag byte of each element, over one vector.</summary>
        private static readonly Vector256<byte> Flags = Repeat(Flag());

        /// <summary>The bytes a present element keeps: all but the padding (<see cref="Mask"/>), over one vector.</summary>
        private static readonly Vector256<byte> Keep = Repeat(Keeps());

        private static byte[] Flag()
        {
            var flag = new byte[Stride];
            flag[0] = 0xFF;
            return flag;
        }

        private static byte[] Keeps()
        {
            var keep = new byte[Stride];
            keep.AsSpan().Fill(0xFF);
            if (Mask != null)
                for (int g = 0; g < Mask.Gaps.Length; g += 2)
                    keep.AsSpan(Mask.Gaps[g], Mask.Gaps[g + 1]).Clear();
            return keep;
        }

        private static Vector256<byte> Repeat(byte[] element)
        {
            var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = element[i % element.Length];
            return Vector256.Create(bytes);
        }

        /// <summary>
        /// Copies <paramref name="total"/> bytes of elements (a vector or more) as the wire holds them: padding cleared,
        /// and the value of an element without one cleared with it, in one pass. Each element's flag, compared with
        /// zero across its lane, gives the bytes it keeps. Vectors start at whole elements; the last one overlaps the
        /// one before it.
        /// </summary>
        // Out of line, as NinoMask.Copy: one call per collection.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CopyLanes(ref byte destination, ref byte source, int total)
        {
            nuint i = 0;
            if (Vector256.IsHardwareAccelerated && total >= Vector256<byte>.Count)
            {
                nuint end = (nuint)total - (nuint)Vector256<byte>.Count;
                for (; i < end; i += (nuint)Vector256<byte>.Count)
                    Vector256.StoreUnsafe(Present(Vector256.LoadUnsafe(ref source, i)), ref destination, i);
                Vector256.StoreUnsafe(Present(Vector256.LoadUnsafe(ref source, end)), ref destination, end);
                return;
            }

            nuint last = (nuint)total - (nuint)Vector128<byte>.Count;
            for (; i < last; i += (nuint)Vector128<byte>.Count)
                Vector128.StoreUnsafe(Present(Vector128.LoadUnsafe(ref source, i)), ref destination, i);
            Vector128.StoreUnsafe(Present(Vector128.LoadUnsafe(ref source, last)), ref destination, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> Present(Vector256<byte> value)
        {
            var flags = value & Flags;
            // All ones across each element whose flag is zero (the stride is a constant, so one branch remains).
            Vector256<byte> absent = Stride == 2 ? Vector256.Equals(flags.AsUInt16(), Vector256<ushort>.Zero).AsByte()
                : Stride == 4 ? Vector256.Equals(flags.AsUInt32(), Vector256<uint>.Zero).AsByte()
                : Stride == 8 ? Vector256.Equals(flags.AsUInt64(), Vector256<ulong>.Zero).AsByte()
                : Vector256.Shuffle(Vector256.Equals(flags.AsUInt64(), Vector256<ulong>.Zero),
                    Vector256.Create(0UL, 0, 2, 2)).AsByte();
            return Vector256.AndNot(value & Keep, absent);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Present(Vector128<byte> value)
        {
            var flags = value & Flags.GetLower();
            Vector128<byte> absent = Stride == 2 ? Vector128.Equals(flags.AsUInt16(), Vector128<ushort>.Zero).AsByte()
                : Stride == 4 ? Vector128.Equals(flags.AsUInt32(), Vector128<uint>.Zero).AsByte()
                : Stride == 8 ? Vector128.Equals(flags.AsUInt64(), Vector128<ulong>.Zero).AsByte()
                : Vector128.Shuffle(Vector128.Equals(flags.AsUInt64(), Vector128<ulong>.Zero),
                    Vector128.Create(0UL, 0)).AsByte();
            return Vector128.AndNot(value & Keep.GetLower(), absent);
        }
#endif

        /// <summary>Writes the count and the elements.</summary>
        public static void Write(ReadOnlySpan<T?> values, ref Writer writer)
        {
            ref byte dst = ref writer.ReserveCounted(values.Length, Stride);
            if (values.IsEmpty)
                return;
            ref T? item = ref MemoryMarshal.GetReference(values);
            if (Raw)
            {
                ref byte src = ref Unsafe.As<T?, byte>(ref item);
#if NET8_0_OR_GREATER
                if (Lanes && values.Length * Stride >= Vector128<byte>.Count)
                {
                    CopyLanes(ref dst, ref src, values.Length * Stride);
                    return;
                }
#endif
                if (Mask != null)
                    Mask.Copy(ref dst, ref src, values.Length);
                else
                    NinoUnsafe.Copy(ref dst, ref src, (uint)(values.Length * Stride));
                ClearAbsent(ref dst, values.Length);
                return;
            }

            if (typeof(T) == typeof(bool) && values.Length < 4 && BitConverter.IsLittleEndian && Stride == 2 && Offset == 1)
            {
                WriteBoolValues(ref item, ref dst, values.Length);
                return;
            }

#if NET8_0_OR_GREATER
            if (typeof(T) == typeof(bool) && values.Length >= 4 && NativeBoolSlots)
            {
                ref ushort source = ref Unsafe.As<T?, ushort>(ref item);
                ref ushort target = ref Unsafe.As<byte, ushort>(ref dst);
                if (NinoSimd.Prefer512 && values.Length >= Vector512<ushort>.Count)
                    WriteBoolSlots<Vector512<ushort>, BoolSlots512>(ref source, ref target, values.Length);
                else if (Vector256.IsHardwareAccelerated && values.Length >= Vector256<ushort>.Count)
                    WriteBoolSlots<Vector256<ushort>, BoolSlots256>(ref source, ref target, values.Length);
                else if (Vector128.IsHardwareAccelerated && values.Length >= Vector128<ushort>.Count)
                    WriteBoolSlots<Vector128<ushort>, BoolSlots128>(ref source, ref target, values.Length);
                else if (NinoSimd.HasArm64)
                    WriteBoolSlots<Vector64<ushort>, BoolSlots64>(ref source, ref target, values.Length);
                else
                    WriteBoolSlots<ulong, BoolSlotsWord>(ref source, ref target, values.Length);

                return;
            }

#endif

#if !NET8_0_OR_GREATER
            if (typeof(T) == typeof(bool) && values.Length >= NinoKernelHook.MinWriteBoolSlots &&
                TryWriteNativeBoolSlots(ref item, ref dst, values.Length))
                return;
#endif
            Unsafe.InitBlockUnaligned(ref dst, 0, (uint)(values.Length * Stride));
            for (int i = 0; i < values.Length; i++)
            {
                ref T? element = ref Unsafe.Add(ref item, i);
                if (!element.HasValue)
                    continue;
                ref byte slot = ref Unsafe.Add(ref dst, i * Stride);
                slot = NinoWire.NotNull;
                NinoLayout<T>.Write(ref Unsafe.Add(ref slot, Offset), element.GetValueOrDefault());
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBoolValues(ref T? item, ref byte destination, int count)
        {
            for (int i = 0; i < count; i++)
            {
                bool? element = Unsafe.As<T?, bool?>(ref Unsafe.Add(ref item, i));
                bool value = element.GetValueOrDefault();
                ushort slot = element.HasValue ? (ushort)(1 | Unsafe.As<bool, byte>(ref value) << 8) : (ushort)0;
                NinoUnsafe.Write(ref Unsafe.Add(ref destination, i * 2), slot);
            }
        }

#if !NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryWriteNativeBoolSlots(ref T? item, ref byte destination, int count)
        {
            if (!NativeBoolSlots)
                return false;

            ref byte source = ref Unsafe.As<T?, byte>(ref item);
            int bytes = count * Stride;
            nint distance = Unsafe.ByteOffset(ref source, ref destination);
            if (distance > -bytes && distance < bytes)
                return false;

            int completed = NinoKernelHook.TryNormalizeBoolSlots(ref source, ref destination, count);
            WriteBoolValues(ref Unsafe.Add(ref item, completed),
                ref Unsafe.Add(ref destination, completed * Stride), count - completed);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadNativeBoolSlots(ref byte source, ref bool? destination, int count, int bytes)
        {
            if (!NativeBoolSlots)
                return false;

            ref byte target = ref Unsafe.As<bool?, byte>(ref destination);
            nint distance = Unsafe.ByteOffset(ref source, ref target);
            if (distance > -bytes && distance < bytes)
                return false;

            int completed = NinoKernelHook.TryNormalizeBoolSlots(ref source, ref target, count);
            if (completed != count)
                ReadBoolValues(ref Unsafe.Add(ref source, completed * Stride),
                    ref Unsafe.Add(ref destination, completed), count - completed);
            return true;
        }
#endif

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void WriteBoolSlots<TVector, TSlots>(ref ushort source, ref ushort target, int count)
            where TVector : struct where TSlots : struct, IBoolSlots<TVector>
        {
            nuint lanes = (uint)(Unsafe.SizeOf<TVector>() / sizeof(ushort));
            nuint last = (uint)count - lanes;
            for (nuint i = 0; i < last; i += lanes)
                WriteBoolGroup<TVector, TSlots>(ref source, ref target, i);
            // End the final vector at the last slot, overlapping only bytes already written.
            WriteBoolGroup<TVector, TSlots>(ref source, ref target, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteBoolGroup<TVector, TSlots>(ref ushort source, ref ushort target, nuint offset)
            where TVector : struct where TSlots : struct, IBoolSlots<TVector>
        {
            TVector pairs = Unsafe.ReadUnaligned<TVector>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, offset)));
            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref target, offset)), TSlots.Normalize(pairs));
        }

        private const ushort InvalidBoolSlotBits = 0xFE00;

        // Extend the low-byte 0/1 flag to mask each slot. Readers check presence bytes separately.
        internal interface IBoolSlots<TVector> where TVector : struct
        {
            static abstract TVector Normalize(TVector pairs);
            static abstract bool HasInvalidValues(TVector pairs);
        }

        private readonly struct BoolSlots64 : IBoolSlots<Vector64<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector64<ushort> pairs) =>
                (pairs & Vector64.Create(InvalidBoolSlotBits)) != Vector64<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector64<ushort> Normalize(Vector64<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }

        /// <summary>
        /// Four slots in a 64-bit word, where no vector of four slots is (x64 and x86 below one 128-bit vector, hosts
        /// without vectors): the presence bit spread over its 16 bits masks the slot, as <see cref="BoolSlots64"/>'s shifts
        /// do.
        /// </summary>
        private readonly struct BoolSlotsWord : IBoolSlots<ulong>
        {
            private const ulong Presence = 0x0001_0001_0001_0001UL;
            private const ulong Invalid = InvalidBoolSlotBits * Presence;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(ulong pairs) => (pairs & Invalid) != 0;

            // (bits << 16) - bits is bits * 0xFFFF without a 64-bit multiplication, which 32-bit x86 calls a helper for.
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static ulong Normalize(ulong pairs)
            {
                ulong bits = pairs & Presence;
                return pairs & ((bits << 16) - bits);
            }
        }

        private readonly struct BoolSlots128 : IBoolSlots<Vector128<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector128<ushort> pairs) =>
                (pairs & Vector128.Create(InvalidBoolSlotBits)) != Vector128<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<ushort> Normalize(Vector128<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }

        private readonly struct BoolSlots256 : IBoolSlots<Vector256<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector256<ushort> pairs) =>
                (pairs & Vector256.Create(InvalidBoolSlotBits)) != Vector256<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector256<ushort> Normalize(Vector256<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }

        internal readonly struct BoolSlots512 : IBoolSlots<Vector512<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool HasInvalidValues(Vector512<ushort> pairs) =>
                (pairs & Vector512.Create(InvalidBoolSlotBits)) != Vector512<ushort>.Zero;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector512<ushort> Normalize(Vector512<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }
#endif

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryReadFresh(T?[] destination, int length, ref Reader reader)
        {
            if (!Vector128.IsHardwareAccelerated)
                return false;

            int stride = Unsafe.SizeOf<T?>();
            int bytes = length * stride;
            ref byte target = ref Unsafe.As<T?, byte>(ref NinoUnsafe.GetArrayDataReference(destination));
            if (!NinoChecks.TryCopyFlags(ref target, ref reader.Peek(), bytes, stride))
                return false;
            reader.UnsafeAdvance(bytes);
            return true;
        }
#endif

        /// <summary>Reads the elements after the count check has bounded the full fixed-stride payload.</summary>
        public static void ReadPrechecked(Span<T?> destination, ref Reader reader)
        {
            int bytes = destination.Length * Stride;
            ref byte src = ref reader.UnsafeAdvance(bytes);
            if (destination.IsEmpty)
                return;
            if (typeof(T) == typeof(bool) && destination.Length == 1)
            {
                ReadSingleBool(ref src, ref Unsafe.As<T?, bool?>(ref MemoryMarshal.GetReference(destination)));
                return;
            }

            // Presence bytes other than 0 or 1 are invalid data (copied as they are, they would make HasValue true
            // and the value unequal to any other).
            NinoChecks.Flags(ref src, destination.Length, Stride);
            ref T? item = ref MemoryMarshal.GetReference(destination);
            if (Raw)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T?, byte>(ref item), ref src, (uint)bytes);
                return;
            }

            if (typeof(T) == typeof(bool))
            {
                int count = destination.Length;
                ref bool? booleans = ref Unsafe.As<T?, bool?>(ref item);
#if NET8_0_OR_GREATER
                if (count < Vector128<ushort>.Count)
                {
                    if (count >= Vector64<ushort>.Count && TryReadShortBoolSlots(ref src, ref booleans, count, bytes))
                        return;
                    ReadBoolValues(ref src, ref booleans, count);
                    return;
                }

                if (NativeBoolSlots)
                {
                    ref byte target = ref Unsafe.As<bool?, byte>(ref booleans);
                    nint distance = Unsafe.ByteOffset(ref src, ref target);
                    if (distance <= -bytes || distance >= bytes)
                    {
                        ref ushort source = ref Unsafe.As<byte, ushort>(ref src);
                        ref ushort destinationSlots = ref Unsafe.As<byte, ushort>(ref target);
                        if (NinoSimd.Prefer512 && count >= Vector512<ushort>.Count)
                            ReadBoolSlots<Vector512<ushort>, BoolSlots512>(ref source, ref destinationSlots, count);
                        else if (Vector256.IsHardwareAccelerated && count >= Vector256<ushort>.Count)
                            ReadBoolSlots<Vector256<ushort>, BoolSlots256>(ref source, ref destinationSlots, count);
                        else if (Vector128.IsHardwareAccelerated)
                            ReadBoolSlots<Vector128<ushort>, BoolSlots128>(ref source, ref destinationSlots, count);
                        else
                            ReadBoolSlots<ulong, BoolSlotsWord>(ref source, ref destinationSlots, count);
                        return;
                    }
                }
#else
                if (count >= NinoKernelHook.MinBoolSlots &&
                    TryReadNativeBoolSlots(ref src, ref booleans, count, bytes))
                    return;
#endif
                // Keep the original conversion loop for long reads outside the vector path.
                if (count <= 1024)
                {
                    ReadBoolValues(ref src, ref booleans, count);
                    return;
                }
            }

            for (int i = 0; i < destination.Length; i++)
            {
                ref byte slot = ref Unsafe.Add(ref src, i * Stride);
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset))
                    : default(T?);
            }
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadShortBoolSlots(ref byte source, ref bool? destination, int count, int bytes)
        {
            if (!NativeBoolSlots)
                return false;

            ref byte target = ref Unsafe.As<bool?, byte>(ref destination);
            nint distance = Unsafe.ByteOffset(ref source, ref target);
            if (distance > -bytes && distance < bytes)
                return false;

            ref ushort from = ref Unsafe.As<byte, ushort>(ref source);
            ref ushort to = ref Unsafe.As<byte, ushort>(ref target);
            if (NinoSimd.HasArm64)
                ReadShortBoolSlots<Vector64<ushort>, BoolSlots64>(ref from, ref to, count);
            else
                ReadShortBoolSlots<ulong, BoolSlotsWord>(ref from, ref to, count);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void ReadShortBoolSlots<TVector, TSlots>(ref ushort source, ref ushort target, int count)
            where TVector : struct
            where TSlots : struct, IBoolSlots<TVector>
        {
            // Four slots, then the last four (overlapping): 4 to 7 slots.
            ReadBoolGroup<TVector, TSlots>(ref source, ref target, 0);
            if (count > 4)
                ReadBoolGroup<TVector, TSlots>(ref source, ref target, (uint)(count - 4));
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        internal static void ReadBoolSlots<TVector, TSlots>(ref ushort source, ref ushort target, int count)
            where TVector : struct
            where TSlots : struct, IBoolSlots<TVector>
        {
            nuint lanes = (uint)(Unsafe.SizeOf<TVector>() / sizeof(ushort));
            nuint last = (uint)count - lanes;
            for (nuint i = 0; i < last; i += lanes)
                ReadBoolGroup<TVector, TSlots>(ref source, ref target, i);
            ReadBoolGroup<TVector, TSlots>(ref source, ref target, last);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReadBoolGroup<TVector, TSlots>(ref ushort source, ref ushort target, nuint offset)
            where TVector : struct
            where TSlots : struct, IBoolSlots<TVector>
        {
            var pairs = TSlots.Normalize(Unsafe.ReadUnaligned<TVector>(
                ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, offset))));
            if (TSlots.HasInvalidValues(pairs))
            {
                // Use the scalar order for the first invalid value and any preceding stores.
                ReadBoolValues(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref source, offset)),
                    ref Unsafe.As<ushort, bool?>(ref Unsafe.Add(ref target, offset)), Unsafe.SizeOf<TVector>() / sizeof(ushort));
                return;
            }

            Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref target, offset)), pairs);
        }
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadSingleBool(ref byte source, ref bool? destination)
        {
            byte flag = source;
            if (flag > 1)
                NinoThrow.InvalidPresence(flag);

            destination = flag != NinoWire.Null
                ? NinoUnsafe.CheckedBool(Unsafe.Add(ref source, Offset))
                : default(bool?);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadBoolValues(ref byte source, ref bool? item, int count)
        {
            for (int i = 0; i < count; i++)
            {
                ref byte slot = ref Unsafe.Add(ref source, i * Stride);
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? NinoUnsafe.CheckedBool(Unsafe.Add(ref slot, Offset))
                    : default(bool?);
            }
        }
    }

    /// <summary>
    /// Field offsets of <c>Nullable&lt;T&gt;</c> in memory on the running runtime. Old Mono stores the value first
    /// while corefx stores the flag first, so the layout is probed rather than assumed.
    /// </summary>
    internal static class NullableLayout<T> where T : unmanaged
    {
        public static readonly int HasValueOffset = ProbeHasValue();

        public static readonly int ValueOffset =
            HasValueOffset == 0 ? Unsafe.SizeOf<T?>() - Unsafe.SizeOf<T>() : 0;

        private static int ProbeHasValue()
        {
            T? probe = default(T); // has value, zero value and padding
            ref byte b = ref Unsafe.As<T?, byte>(ref probe);
            for (int i = 0; i < Unsafe.SizeOf<T?>(); i++)
                if (Unsafe.Add(ref b, i) != 0)
                    return i;
            return 0;
        }
    }
}
