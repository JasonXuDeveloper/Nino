using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary><c>ImmutableList&lt;T&gt;</c>: count (-1 = null), then the elements.</summary>
    public struct ImmutableListFormatter<T, TFormatter> : INinoFormatter<ImmutableList<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableList<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Copied out by the tree's own recursive walk, then written as a T[] (raw elements as one block): its
            // enumerator rents a stack and walks it node by node.
            var array = ArrayPool<T>.Shared.Rent(value.Count);
            value.CopyTo(array);
            ImmutableCollections.WriteRented<T, TFormatter>(array, value.Count, ref writer);
        }

        public void Deserialize(ref Reader reader, ref ImmutableList<T> value)
        {
            // Read as a T[] (raw elements as one block) and built from it in one pass: a builder would insert and
            // rebalance its tree for every element.
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableList.CreateRange(array);
        }
    }
}
