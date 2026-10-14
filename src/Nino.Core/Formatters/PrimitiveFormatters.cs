using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>Writes an unmanaged value (primitive, enum, or plain struct) as its raw bytes.</summary>
    public struct UnmanagedFormatter<T> : INinoFormatter<T>, INinoRawFormatter where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(T value, ref Writer writer) => writer.UnsafeWrite(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref T value, ref Reader reader) => value = reader.UnsafeRead<T>();
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
