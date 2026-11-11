using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableArray&lt;T&gt;</c>: length (-1 = default), then the elements, as <c>T[]</c> writes them. Its array
    /// is written and read as a <c>T[]</c> (raw elements as one block) and wrapped without a copy
    /// (<see cref="ImmutableCollectionsMarshal"/>); the array of the old value is never reused, since others may
    /// hold it.
    /// </summary>
    public struct ImmutableArrayFormatter<T, TFormatter> : INinoFormatter<ImmutableArray<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableArray<T> value, ref Writer writer)
        {
            // References (strings, classes) element by element: the array formatter's loop is slower here, reached
            // out of line with the writer behind a ref (100 strings: 290 ns against 264). Values as an array, raw
            // ones as one block.
            if (!typeof(T).IsValueType)
            {
                if (value.IsDefault)
                {
                    writer.WriteNullLength();
                    return;
                }

                writer.WriteLength(value.Length);
                foreach (var item in value)
                    default(TFormatter).Serialize(item, ref writer);
                return;
            }

            default(ArrayFormatter<T, TFormatter>).Serialize(ImmutableCollectionsMarshal.AsArray(value), ref writer);
        }

        public void Deserialize(ref Reader reader, ref ImmutableArray<T> value) =>
            value = ImmutableCollectionsMarshal.AsImmutableArray(ImmutableCollections.Read<T, TFormatter>(ref reader));
    }
}
