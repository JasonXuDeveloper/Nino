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
                if (Mask != null)
                    Mask.Copy(ref dst, ref src, values.Length);
                else
                    NinoUnsafe.Copy(ref dst, ref src, (uint)(values.Length * Stride));
                return;
            }

#if NET8_0_OR_GREATER
            if (typeof(T) == typeof(bool) && values.Length >= 4 && NinoSimd.HasArm64 &&
                BitConverter.IsLittleEndian && NullableLayout<T>.HasValueOffset == 0 &&
                NullableLayout<T>.ValueOffset == 1 && Stride == 2 && Offset == 1)
            {
                ref ushort source = ref Unsafe.As<T?, ushort>(ref item);
                ref ushort target = ref Unsafe.As<byte, ushort>(ref dst);
                if (values.Length >= Vector128<ushort>.Count)
                    WriteBoolSlots<Vector128<ushort>, BoolSlots128>(ref source, ref target, values.Length);
                else
                    WriteBoolSlots<Vector64<ushort>, BoolSlots64>(ref source, ref target, values.Length);

                return;
            }

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

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        private static void WriteBoolSlots<TVector, TSlots>(ref ushort source, ref ushort target, int count)
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

        // The low byte is the managed 0/1 presence flag. Extend it to mask each entire slot.
        private interface IBoolSlots<TVector> where TVector : struct
        {
            static abstract TVector Normalize(TVector pairs);
        }

        private readonly struct BoolSlots64 : IBoolSlots<Vector64<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector64<ushort> Normalize(Vector64<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }

        private readonly struct BoolSlots128 : IBoolSlots<Vector128<ushort>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static Vector128<ushort> Normalize(Vector128<ushort> pairs)
            {
                var active = (pairs << 15).AsInt16() >> 15;
                return pairs & active.AsUInt16();
            }
        }
#endif

        /// <summary>Reads the elements after the count check has bounded the full fixed-stride payload.</summary>
        public static void ReadPrechecked(Span<T?> destination, ref Reader reader)
        {
            int bytes = destination.Length * Stride;
            ref byte src = ref reader.UnsafeAdvance(bytes);
            if (destination.IsEmpty)
                return;
            // Presence bytes other than 0 or 1 are invalid data (copied as they are, they would make HasValue true
            // and the value unequal to any other).
            NinoChecks.Flags(ref src, destination.Length, Stride);
            ref T? item = ref MemoryMarshal.GetReference(destination);
            if (Raw)
            {
                NinoUnsafe.CopyToNew(ref Unsafe.As<T?, byte>(ref item), ref src, (uint)bytes);
                return;
            }

            // Long bool reads benefit from the original conversion loop after OSR.
            if (typeof(T) == typeof(bool) && destination.Length <= 1024)
            {
                ReadBoolValues(ref src, ref Unsafe.As<T?, bool?>(ref item), destination.Length);
                return;
            }

            for (int i = 0; i < destination.Length; i++)
            {
                ref byte slot = ref Unsafe.Add(ref src, i * Stride);
                Unsafe.Add(ref item, i) = slot != NinoWire.Null
                    ? NinoLayout<T>.Read(ref Unsafe.Add(ref slot, Offset))
                    : default(T?);
            }
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
