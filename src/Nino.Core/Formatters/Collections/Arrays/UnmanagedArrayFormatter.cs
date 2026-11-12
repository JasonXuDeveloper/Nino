#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>Unmanaged <c>T[]</c>: length (-1 = null), then one block copy.</summary>
    public struct UnmanagedArrayFormatter<[NinoRaw] T> : INinoFormatter<T[]>, INinoRawArrayFormatter where T : unmanaged
    {
#if NET8_0_OR_GREATER
        private const int MinFusedBytes = 4096;
#endif

        // Elements with a non-canonical layout take the formatter path, which converts them.
        int INinoRawArrayFormatter.ElementSize => NinoLayout<T>.IsCanonical ? Unsafe.SizeOf<T>() : 0;

        // The top-level calls reach these through a static readonly field of their exact type, so they are inlined
        // there with Unsafe.SizeOf<T>() a constant: no multiplication by a loaded size, no overflow branches.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoRawArrayFormatter.Write(NinoArrayBufferWriter buffer, Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            NinoLayout<T>.WriteLengthPrefixed(ref buffer.Reserve(WireSize(length)),
                ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(values)), length);
        }

        void INinoRawArrayFormatter.WriteTo(IBufferWriter<byte> buffer, Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            WriteRawTo(buffer, values, length, WireSize(length));
        }

        internal static void WriteRawTo(IBufferWriter<byte> buffer, ReadOnlySpan<T> values, int length, int size)
        {
#if NETSTANDARD2_0
            var source = MemoryMarshal.AsBytes(values);
#else
            var source = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(values)), size - NinoWire.LengthSize);
#endif
            NinoLayout<T>.WriteRawExternal(buffer, source, length, size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        byte[] INinoRawArrayFormatter.Serialize(Array array)
        {
            var values = Unsafe.As<T[]>(array);
            int length = values.Length;
            // Every byte is written below.
            var bytes = NinoUnsafe.AllocateUninitializedArray<byte>(WireSize(length));
            ref byte destination = ref NinoUnsafe.GetArrayDataReference(bytes);
            ref byte source = ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(values));
#if NET8_0_OR_GREATER
            if (Unsafe.SizeOf<T>() == 1 && !NinoLayout<T>.HasPadding && NinoUnsafe.IsArm64Short((uint)length))
            {
                NinoUnsafe.Write(ref destination, length);
                NinoUnsafe.CopyArm64Short(ref Unsafe.Add(ref destination, NinoWire.LengthSize), ref source,
                    (uint)length);
            }
            else
#endif
                NinoLayout<T>.WriteLengthPrefixed(ref destination, ref source, length);
            return bytes;
        }

        /// <summary>
        /// Whether a length read as it is (-1 for null included) is past <see cref="NinoSettings.MaxCollectionLength"/>:
        /// -1 is then too, and takes the cold path that tells null from invalid.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TooLong(int length) =>
            NinoLimits.LengthCapped && (uint)length > (uint)NinoLimits.MaxCollectionLength;

        /// <summary>The length prefix and <paramref name="length"/> elements, in bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WireSize(int length)
        {
            // In 64 bits: cannot overflow; one compare rejects what does not fit an array.
            ulong size = NinoWire.LengthSize + (ulong)(uint)length * (uint)Unsafe.SizeOf<T>();
            if (size > int.MaxValue)
                NinoThrow.TooLarge();
            return (int)size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Array INinoRawArrayFormatter.Read(ReadOnlySpan<byte> data, Array existing)
        {
            // A new array (NinoDeserializer.Deserialize<T[]>(data) passes null, a constant once inlined): no slot
            // to reuse, so no local whose address reaches the recycler and stays in memory.
#if NET8_0_OR_GREATER
            if (typeof(T) == typeof(bool) && existing == null && Vector128.IsHardwareAccelerated &&
                data.Length >= NinoWire.LengthSize + MinFusedBytes)
                return ReadNewChecked(data);
#endif
            if (existing == null && NinoLayout<T>.CopiesAsIs)
                return ReadNew(data);
            var reader = new Reader(data);
            var value = Unsafe.As<T[]>(existing);
            Deserialize(ref reader, ref value);
            return value;
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T[] ReadNewChecked(ReadOnlySpan<byte> data) => ReadNew(data);
#endif

        /// <summary>The bytes of <see cref="Deserialize"/>, into a new array (null for null).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T[] ReadNew(ReadOnlySpan<byte> data)
        {
            if (data.Length < NinoWire.LengthSize)
                NinoThrow.EndOfData();
            ref byte start = ref MemoryMarshal.GetReference(data);
            int length = NinoUnsafe.Read<int>(ref start);
            ulong bytes = (ulong)(uint)length * (uint)Unsafe.SizeOf<T>();
            if (bytes > (uint)(data.Length - NinoWire.LengthSize) || TooLong(length))
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                return null;
            }

            var array = NinoUnsafe.NewArray<T>(length, uninitialized: true);
            ref byte target = ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array));
            ref byte source = ref Unsafe.Add(ref start, NinoWire.LengthSize);
#if NET8_0_OR_GREATER
            if (typeof(T) == typeof(bool))
            {
                if (bytes >= MinFusedBytes && NinoChecks.TryCopyBools(ref target, ref source, (int)bytes))
                    return array;
                NinoChecks.Bools(ref source, length);
            }
#endif
            NinoUnsafe.CopyToNew(ref target, ref source, (uint)bytes);
            return array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T[] value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref T[] value)
        {
            // Also bools, checked there (NinoLayout<T>.CopiesAsIs).
            if (!NinoLayout<T>.CopiesAsIs)
            {
                DeserializeConverted(ref reader, ref value);
                return;
            }

            // One bounds check for the count and the elements: a negative count (null, or invalid) is a huge
            // unsigned size and takes the cold path.
            int length = reader.UnsafeRead<int>();
            ulong bytes = (ulong)(uint)length * (uint)Unsafe.SizeOf<T>();
            if (bytes > (uint)reader.Remaining || TooLong(length))
            {
                if (length != NinoWire.NullLength)
                    NinoThrow.InvalidLength(length);
                NinoRecycler.Discard(ref value);
                return;
            }

            var array = NinoElements.EnsureLength(ref value, length, uninitialized: true);
            ref byte destination = ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array));
            ref byte source = ref reader.UnsafeAdvance((int)bytes);
#if NET8_0_OR_GREATER
            if (NinoUnsafe.IsArm64Short((nuint)bytes))
                NinoUnsafe.CopyArm64Short(ref destination, ref source, (uint)bytes);
            else
#endif
                NinoUnsafe.CopyToNew(ref destination, ref source, (uint)bytes);
        }

        private static void DeserializeConverted(ref Reader reader, ref T[] value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoLayout<T>.Size, out int length))
                return;

            // Bools and Untrusted mode's values are checked by the read: a new array takes the slot once filled.
            var current = value;
            var array = NinoElements.NewOrSame(current, length);
#if NET8_0_OR_GREATER
            if (NinoLayout<T>.Checks is not { } checks || !NinoLayout<T>.IsCanonical ||
                array == current || !TryReadFreshChecked(array, length, checks, ref reader))
#endif
                reader.ReadUnmanagedRaw(array.AsSpan());
            NinoElements.Publish(ref value, current, array);
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadFreshChecked(T[] array, int length, NinoValueChecks checks, ref Reader reader)
        {
            int stride = Unsafe.SizeOf<T>();
            int bytes = length * stride;
            if (bytes < MinFusedBytes || !checks.TryCopy(ref reader.Peek(),
                    ref Unsafe.As<T, byte>(ref NinoUnsafe.GetArrayDataReference(array)), bytes, stride))
                return false;

            reader.UnsafeAdvance(bytes);
            return true;
        }
#endif
    }
}
