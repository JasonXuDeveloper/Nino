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
        // Its array as a T[] (a default value's is null: the null length), so its elements are a run of the one layer.
        public void Serialize(ImmutableArray<T> value, ref Writer writer) =>
            default(ArrayFormatter<T, TFormatter>).Serialize(ImmutableCollectionsMarshal.AsArray(value), ref writer);

        public void Deserialize(ref Reader reader, ref ImmutableArray<T> value) =>
            value = ImmutableCollectionsMarshal.AsImmutableArray(ImmutableCollections.Read<T, TFormatter>(ref reader));
    }
}
