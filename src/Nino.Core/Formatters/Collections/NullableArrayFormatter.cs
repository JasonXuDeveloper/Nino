using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>T?[]</c> of raw <typeparamref name="T"/>: length, then every element at a fixed stride in the canonical
    /// <c>Nullable&lt;T&gt;</c> layout (see <see cref="NinoNullables{T}"/>), written into one reservation and read
    /// without per-element calls. Not the bytes of <see cref="ArrayFormatter{T,TFormatter}"/> with
    /// <see cref="NullableFormatter{T,TFormatter}"/> (a presence byte, then the value only when present), which
    /// sequences declared as interfaces (<c>IList&lt;T?&gt;</c>, <c>IEnumerable&lt;T?&gt;</c>) write.
    /// </summary>
    public struct NullableArrayFormatter<[NinoRaw] T> : INinoFormatter<T?[]> where T : unmanaged
    {
        public void Serialize(T?[] value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            NinoNullables<T>.Write(value, ref writer);
        }

        public void Deserialize(ref Reader reader, ref T?[] value)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoNullables<T>.Stride, out int length))
                return;

            // Publish a new raw array only after its presence bytes have been checked and its data copied.
            if (NinoNullables<T>.Raw)
            {
                var current = value;
#if NET8_0_OR_GREATER
                // Reused arrays with smaller strides benefit from the shared read path.
                if (Unsafe.SizeOf<T?>() is 8 or 16)
                {
                    if (current != null && current.Length == length)
                        NinoNullables<T>.ReadPrechecked(current, ref reader);
                    else
                        ReadNew(current, length, ref reader, ref value);
                    return;
                }
#endif
                var array = NinoElements.NewOrSame(current, length);
                NinoNullables<T>.ReadPrechecked(array, ref reader);
                NinoElements.Publish(ref value, current, array);
                return;
            }

            NinoNullables<T>.ReadPrechecked(NinoElements.EnsureLength(ref value, length, overwritten: true), ref reader);
        }

#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadNew(T?[] current, int length, ref Reader reader, ref T?[] value)
        {
            var array = NinoUnsafe.NewArray<T?>(length, uninitialized: true);
            if (length < 128 || !NinoNullables<T>.TryReadFresh(array, length, ref reader))
                NinoNullables<T>.ReadPrechecked(array, ref reader);
            NinoElements.Publish(ref value, current, array);
        }
#endif
    }
}
