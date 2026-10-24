using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>Writes an unmanaged value (primitive, enum, or plain struct) as its raw bytes.</summary>
    public struct UnmanagedFormatter<[NinoRaw] T> : INinoFormatter<T>, INinoRawFormatter where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer) => writer.WriteValue(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T value, ref Reader reader) => value = reader.ReadValue<T>();
    }

    /// <summary>
    /// <see cref="UnmanagedFormatter{T}"/> without the <c>unmanaged</c> constraint (the same bytes), for code that only
    /// knows <typeparamref name="T"/> is unmanaged at run time and cannot close a generic type then (NativeAOT).
    /// </summary>
    internal struct RawValueFormatter<T> : INinoFormatter<T>, INinoRawFormatter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer) => writer.WriteValue(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T value, ref Reader reader) => value = reader.ReadValue<T>();
    }

    /// <summary><c>IntPtr</c>/<c>nint</c>: always 8 bytes, so 32-bit and 64-bit processes share data.</summary>
    public struct IntPtrFormatter : INinoFormatter<IntPtr>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(IntPtr value, ref Writer writer) => writer.UnsafeWrite(value.ToInt64());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref IntPtr value, ref Reader reader) => value = new IntPtr(reader.UnsafeRead<long>());
    }

    /// <summary><c>UIntPtr</c>/<c>nuint</c>: always 8 bytes, so 32-bit and 64-bit processes share data.</summary>
    public struct UIntPtrFormatter : INinoFormatter<UIntPtr>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(UIntPtr value, ref Writer writer) => writer.UnsafeWrite(value.ToUInt64());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref UIntPtr value, ref Reader reader) => value = new UIntPtr(reader.UnsafeRead<ulong>());
    }

    /// <summary>
    /// <c>DateTimeOffset</c>: UTC ticks (int64) and offset minutes (int16). Its raw memory has padding and a
    /// different size on x86, so it is never copied raw. Its two fields are read and written directly where their
    /// positions are known (<see cref="ValueViews.DateTimeOffsetTicks"/>): the public API converts the offset to and
    /// from a <c>TimeSpan</c>, and the constructor validates through several calls.
    /// </summary>
    public struct DateTimeOffsetFormatter : INinoFormatter<DateTimeOffset>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(DateTimeOffset value, ref Writer writer) =>
            Write(ref writer.Reserve(NinoWire.DateTimeOffsetSize), ref value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref DateTimeOffset value, ref Reader reader) =>
            Read(ref reader.ReadBlock(NinoWire.DateTimeOffsetSize), ref value);

        /// <summary>Writes <paramref name="count"/> values into one reserved block.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void WriteAll(ref byte dst, ref DateTimeOffset first, int count)
        {
            ref byte src = ref Unsafe.As<DateTimeOffset, byte>(ref first);
            for (int i = 0; i < count; i++)
            {
                Write(ref dst, ref Unsafe.As<byte, DateTimeOffset>(ref src));
                src = ref Unsafe.Add(ref src, Unsafe.SizeOf<DateTimeOffset>());
                dst = ref Unsafe.Add(ref dst, NinoWire.DateTimeOffsetSize);
            }
        }

        /// <summary>Reads <paramref name="count"/> values from one block (into slots that need not be zeroed).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ReadAll(ref byte src, ref DateTimeOffset first, int count)
        {
            ref byte dst = ref Unsafe.As<DateTimeOffset, byte>(ref first);
            for (int i = 0; i < count; i++)
            {
                Read(ref src, ref Unsafe.As<byte, DateTimeOffset>(ref dst));
                src = ref Unsafe.Add(ref src, NinoWire.DateTimeOffsetSize);
                dst = ref Unsafe.Add(ref dst, Unsafe.SizeOf<DateTimeOffset>());
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Write(ref byte dst, ref DateTimeOffset value)
        {
            int ticksAt = ValueViews.DateTimeOffsetTicks;
            if (ticksAt >= 0)
            {
                ref byte fields = ref Unsafe.As<DateTimeOffset, byte>(ref value);
                NinoUnsafe.Write(ref dst, NinoUnsafe.Read<long>(ref fields, ticksAt));
                NinoUnsafe.Write(ref dst, NinoWire.DateTimeOffsetMinutesPosition,
                    NinoUnsafe.Read<short>(ref fields, 8 - ticksAt));
                return;
            }

            NinoUnsafe.Write(ref dst, value.UtcTicks);
            NinoUnsafe.Write(ref dst, NinoWire.DateTimeOffsetMinutesPosition,
                (short)(value.Offset.Ticks / TimeSpan.TicksPerMinute));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Read(ref byte src, ref DateTimeOffset value)
        {
            long utc = NinoUnsafe.Read<long>(ref src);
            short minutes = NinoUnsafe.Read<short>(ref src, NinoWire.DateTimeOffsetMinutesPosition);
            // The constructor's checks, as two unsigned compares: the UTC time is a valid date at least 14 hours
            // from either end (so the local time is one too), the offset is at most 14 hours. The ends of the range
            // and anything invalid take the constructor, out of line, after the same checks made exactly (what it
            // rejects is invalid data, NinoDataException rather than its ArgumentOutOfRangeException).
            int ticksAt = ValueViews.DateTimeOffsetTicks;
            if (ticksAt >= 0 && (ulong)(utc - MaxOffsetTicks) <= MaxTicks - 2 * MaxOffsetTicks &&
                (uint)(minutes + MaxOffsetMinutes) <= 2 * MaxOffsetMinutes)
            {
                ref byte fields = ref Unsafe.As<DateTimeOffset, byte>(ref value);
                NinoUnsafe.Write(ref fields, ticksAt, utc);
                // The minutes as an int (the whole field where it is one, the padding after it where it is a
                // short); as 8 bytes, which also clear the padding, where the value is 16.
                if (Unsafe.SizeOf<DateTimeOffset>() == 16)
                    NinoUnsafe.Write(ref fields, 8 - ticksAt, (long)(uint)minutes);
                else
                    NinoUnsafe.Write(ref fields, 8 - ticksAt, (int)minutes);
                return;
            }

            value = Construct(utc, minutes);
        }

        /// <summary>The ends of the range, through the constructor; anything it rejects is invalid data.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static DateTimeOffset Construct(long utc, short minutes)
        {
            long offset = minutes * TimeSpan.TicksPerMinute;
            if ((uint)(minutes + MaxOffsetMinutes) > 2 * MaxOffsetMinutes || (ulong)utc > MaxTicks ||
                (ulong)(utc + offset) > MaxTicks)
                NinoThrow.InvalidData("invalid DateTimeOffset.");
            return new DateTimeOffset(utc + offset, new TimeSpan(offset));
        }

        private const ulong MaxTicks = 3_155_378_975_999_999_999; // DateTime.MaxValue.Ticks
        private const int MaxOffsetMinutes = 14 * 60;
        private const long MaxOffsetTicks = MaxOffsetMinutes * TimeSpan.TicksPerMinute;
    }

    /// <summary>Writes a string: one byte per char for Latin-1 text, UTF-16 otherwise (the default).</summary>
    public struct StringFormatter : INinoFormatter<string>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(string value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref string value, ref Reader reader) => value = reader.ReadString(value);
    }

    /// <summary>Writes a string as UTF-8 (<c>[NinoUtf8]</c>).</summary>
    public struct Utf8StringFormatter : INinoFormatter<string>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(string value, ref Writer writer) => writer.WriteUtf8(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref string value, ref Reader reader) => value = reader.ReadUtf8(value);
    }

    /// <summary>Writes a nullable value: a has-value byte, then the value.</summary>
    public struct NullableFormatter<T, TFormatter> : INinoFormatter<T?>
        where T : struct
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T? value, ref Writer writer)
        {
            if (RawFormatter.Is<T, TFormatter>())
            {
                // Flag and value in one reservation.
                writer.WriteNullableRaw(value);
                return;
            }

            if (!value.HasValue)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            writer.UnsafeWrite(NinoWire.NotNull);
            default(TFormatter).Serialize(value.GetValueOrDefault(), ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T? value, ref Reader reader)
        {
            if (RawFormatter.Is<T, TFormatter>())
            {
                // Flag and value under one bounds check; a value replaced by null is discarded, as TryReadPresent does.
                T? read = reader.ReadNullableRaw<T>();
                if (!read.HasValue)
                    NinoRecycler.Discard(ref value);
                value = read;
                return;
            }

            if (!reader.TryReadPresent(ref value))
                return;

            T item = value.GetValueOrDefault();
            default(TFormatter).Deserialize(ref item, ref reader);
            value = item;
        }
    }

    /// <summary>
    /// Writes an <see cref="object"/> whose values are <c>[NinoType]</c>s as type id plus body, dispatching through
    /// <see cref="NinoPolymorphic{TBase}"/>. Not generic: a formatter generic over a reference type runs as shared
    /// code, which reaches the registry through the generic dictionary on every value (about 1.5x slower).
    /// </summary>
    /// <remarks>
    /// Values that are not <c>[NinoType]</c>s are rejected. Dispatch goes through the global registry of every
    /// <c>[NinoType]</c>, which is slower than the generated dispatch of a common <c>[NinoType]</c> base class or
    /// interface: prefer that base as the member or argument type.
    /// </remarks>
    public struct ObjectFormatter : INinoFormatter<object>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(object value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite(NinoWire.NullTypeId);
                return;
            }

            NinoPolymorphic<object>.Serialize(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref object value, ref Reader reader)
        {
            NinoPolymorphic<object>.Deserialize(reader.UnsafeRead<int>(), ref value, ref reader);
        }
    }
}
