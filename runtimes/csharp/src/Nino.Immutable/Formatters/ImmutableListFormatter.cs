using System.Collections.Immutable;
using Nino.Core;
using Nino.Core.CompilerServices;

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

            // Its tree walked in order a chunk at a time (SortedTrees), each chunk written by Nino.Core's run loops (raw
            // elements as one block): its enumerator rents a stack and checks it per element. Else that enumerator.
            if (SortedTrees.Usable)
                NinoGathered.Write<T, TFormatter, NinoTreeItems<T>>(ListTree<T>.Items(value), value.Count, ref writer);
            else
                NinoGathered.Write<T, TFormatter, NinoGathered.Enumerated<T, ImmutableList<T>.Enumerator>>(
                    new(value.GetEnumerator()), value.Count, ref writer);
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
