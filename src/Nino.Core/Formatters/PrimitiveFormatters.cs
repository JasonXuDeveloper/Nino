using System;
using System.Runtime.CompilerServices;

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
    /// different size on x86, so it is never copied raw.
    /// </summary>
    public struct DateTimeOffsetFormatter : INinoFormatter<DateTimeOffset>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(DateTimeOffset value, ref Writer writer)
        {
            ref byte dst = ref writer.Reserve(NinoWire.DateTimeOffsetSize);
            NinoUnsafe.Write(ref dst, value.UtcTicks);
            NinoUnsafe.Write(ref dst, NinoWire.DateTimeOffsetMinutesPosition,
                (short)(value.Offset.Ticks / TimeSpan.TicksPerMinute));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref DateTimeOffset value, ref Reader reader)
        {
            ref byte src = ref reader.ReadBlock(NinoWire.DateTimeOffsetSize);
            long utc = NinoUnsafe.Read<long>(ref src);
            long offset = NinoUnsafe.Read<short>(ref src, NinoWire.DateTimeOffsetMinutesPosition) *
                          TimeSpan.TicksPerMinute;
            value = new DateTimeOffset(utc + offset, new TimeSpan(offset));
        }
    }

    /// <summary>Writes a string as UTF-16 (the default).</summary>
    public struct StringFormatter : INinoFormatter<string>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(string value, ref Writer writer) => writer.Write(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref string value, ref Reader reader) => value = reader.ReadString();
    }

    /// <summary>Writes a string as UTF-8 (<c>[NinoUtf8]</c>).</summary>
    public struct Utf8StringFormatter : INinoFormatter<string>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(string value, ref Writer writer) => writer.WriteUtf8(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref string value, ref Reader reader) => value = reader.ReadUtf8();
    }

    /// <summary>Writes a nullable value: a has-value byte, then the value.</summary>
    public struct NullableFormatter<T, TFormatter> : INinoFormatter<T?>
        where T : struct
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T? value, ref Writer writer)
        {
            if (!value.HasValue)
            {
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            if (RawFormatter<TFormatter>.Is)
            {
                // Flag and value in one reservation.
                ref byte dst = ref writer.Reserve(1 + Unsafe.SizeOf<T>());
                dst = NinoWire.NotNull;
                NinoLayout<T>.Write(ref Unsafe.Add(ref dst, 1), value.GetValueOrDefault());
                return;
            }

            writer.UnsafeWrite(NinoWire.NotNull);
            default(TFormatter).Serialize(value.GetValueOrDefault(), ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T? value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == NinoWire.Null)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            if (RawFormatter<TFormatter>.Is)
            {
                value = reader.UnsafeRead<T>();
                return;
            }

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
