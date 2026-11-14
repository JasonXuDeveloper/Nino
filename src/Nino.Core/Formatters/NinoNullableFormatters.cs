using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <see cref="NinoNullable{T}"/>: the bytes of <c>T?</c> (<see cref="NullableFormatter{T,TFormatter}"/> of a raw
    /// <typeparamref name="T"/>: a presence byte, then the value when present).
    /// </summary>
    public struct NinoNullableFormatter<[NinoRaw] T> : INinoFormatter<NinoNullable<T>> where T : unmanaged
    {
        // Both read and write the value's memory (presence, then value) as it is: through T? each call built a
        // Nullable from two fields and took it apart again.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(NinoNullable<T> value, ref Writer writer) => NinoNullables<T>.WriteOne(ref value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref NinoNullable<T> value) => reader.ReadNinoNullable(ref value);
    }

    /// <summary>
    /// <c>NinoNullable&lt;T&gt;[]</c>: the bytes of <see cref="NullableArrayFormatter{T}"/> (length, then every slot at
    /// the stride of <c>T?</c>), written as one block copy where the type's memory is that wire
    /// (<see cref="NinoNullables{T}"/>.Words).
    /// </summary>
    public struct NinoNullableArrayFormatter<[NinoRaw] T> : INinoFormatter<NinoNullable<T>[]> where T : unmanaged
    {
        public void Serialize(NinoNullable<T>[] value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            NinoNullables<T>.Write(value, ref writer);
        }

        public void Deserialize(ref Reader reader, ref NinoNullable<T>[] value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoNullables<T>.Stride, out int length))
                return;

            // A new array is published only after the read, which may still reject the data: it is never seen with
            // the memory it was allocated over.
            var current = value;
            var array = NinoElements.NewOrSame(current, length);
            NinoNullables<T>.Read(array, ref reader, fresh: array != current);
            NinoElements.Publish(ref value, current, array);
        }
    }

    /// <summary><c>List&lt;NinoNullable&lt;T&gt;&gt;</c>: the bytes of <see cref="NullableListFormatter{TList,T}"/>.</summary>
    public struct NinoNullableListFormatter<[NinoCreated] TList, [NinoRaw] T> : INinoFormatter<TList>
        where TList : List<NinoNullable<T>>
        where T : unmanaged
    {
        public void Serialize(TList value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            NinoNullables<T>.Write(ListAccess.AsSpan<NinoNullable<T>>(value), ref writer);
        }

        public void Deserialize(ref Reader reader, ref TList value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoNullables<T>.Stride, out int length))
                return;

            var list = value ?? ListAccess.CreateOverwritten<TList, NinoNullable<T>>(length, false);
            var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
            NinoNullables<T>.Read(span, ref reader);
            ListAccess.Close(list, length, buffer);
            value = list;
        }
    }
}
