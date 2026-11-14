using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary><c>ArraySegment&lt;T&gt;</c>: written like an array; read into a new array.</summary>
    public struct ArraySegmentFormatter<T, TFormatter> : INinoFormatter<ArraySegment<T>>
        , INinoExternalFormatter<ArraySegment<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool INinoExternalFormatter<ArraySegment<T>>.TryWriteExternal(ArraySegment<T> value, IBufferWriter<byte> buffer) =>
            NinoCollections.TryWriteRawExternal<T, TFormatter>(value, buffer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ArraySegment<T> value, ref Writer writer)
        {
            var array = value.Array;
            if (writer.TryWriteNullLength(array))
                return;

#if NET8_0_OR_GREATER
            // ArraySegment already validated the range. Reference types retain AsSpan's array type check.
            if (typeof(T).IsValueType)
            {
                ref T start = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), value.Offset);
                NinoCollections.WriteSpan<T, TFormatter>(MemoryMarshal.CreateReadOnlySpan(ref start, value.Count),
                    ref writer);
                return;
            }
#endif
            NinoCollections.WriteSpan<T, TFormatter>(value.AsSpan(), ref writer);
        }

        public void Deserialize(ref Reader reader, ref ArraySegment<T> value)
        {
            var array = NinoCollections.ReadArray<T, TFormatter>(ref reader);
            value = array == null ? default : new ArraySegment<T>(array);
        }
    }
}
