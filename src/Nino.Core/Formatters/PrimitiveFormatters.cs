using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>Writes an unmanaged value (primitive, enum, or plain struct) as its raw bytes.</summary>
    public struct UnmanagedFormatter<T> : INinoFormatter<T>, INinoRawFormatter where T : unmanaged
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
            ref byte dst = ref writer.Reserve(10);
            NinoUnsafe.Write(ref dst, value.UtcTicks);
            NinoUnsafe.Write(ref dst, 8, (short)(value.Offset.Ticks / TimeSpan.TicksPerMinute));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref DateTimeOffset value, ref Reader reader)
        {
            ref byte src = ref reader.ReadBlock(10);
            long utc = NinoUnsafe.Read<long>(ref src);
            long offset = NinoUnsafe.Read<short>(ref src, 8) * TimeSpan.TicksPerMinute;
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
                writer.UnsafeWrite((byte)0);
                return;
            }

            if (RawFormatter<TFormatter>.Is)
            {
                // Flag and value in one reservation.
                ref byte dst = ref writer.Reserve(1 + Unsafe.SizeOf<T>());
                dst = 1;
                NinoUnsafe.Write(ref Unsafe.Add(ref dst, 1), value.GetValueOrDefault());
                NinoLayout<T>.ClearPadding(ref Unsafe.Add(ref dst, 1));
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TFormatter).Serialize(value.GetValueOrDefault(), ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T? value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
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
    /// Writes an <see cref="object"/> (or any other declared type whose values are <c>[NinoType]</c>s) as type id
    /// plus body, dispatching through <see cref="NinoPolymorphic{TBase}"/>.
    /// </summary>
    public struct PolymorphicFormatter<T> : INinoFormatter<T> where T : class
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite(0);
                return;
            }

            NinoPolymorphic<T>.Serialize(value, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T value, ref Reader reader)
        {
            NinoPolymorphic<T>.Deserialize(reader.UnsafeRead<int>(), ref value, ref reader);
        }
    }
}
