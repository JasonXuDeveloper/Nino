using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableSortedSet&lt;T&gt;</c>: count (-1 = null), then the elements in order, as a <c>SortedSet&lt;T&gt;</c>
    /// writes them. Read with the default comparer.
    /// </summary>
    public struct ImmutableSortedSetFormatter<T, TFormatter> : INinoFormatter<ImmutableSortedSet<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableSortedSet<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Copied out of its tree in order (SortedTrees), else with its enumerator, then written as an array.
            int count = value.Count;
            ImmutableCollections.WriteRented<T, TFormatter>(Copy(value, count), count, ref writer);
        }

        // Out of line: renting inlines a large path into the caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T[] Copy(ImmutableSortedSet<T> value, int count)
        {
            var array = ArrayPool<T>.Shared.Rent(count);
            if (SortedTrees.Usable)
            {
                SortedSetTree<T>.CopyTo(value, array);
                return array;
            }

            // By reference: the set enumerates exactly Count elements, which the JIT cannot tell.
            ref T slot = ref MemoryMarshal.GetReference(array.AsSpan());
            foreach (var item in value)
            {
                slot = item;
                slot = ref Unsafe.Add(ref slot, 1);
            }

            return array;
        }

        public void Deserialize(ref Reader reader, ref ImmutableSortedSet<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : Build(array);
        }

        // Built as its tree where it can be (SortedTrees), else by CreateRange, which sorts the elements.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImmutableSortedSet<T> Build(T[] items) =>
            items.Length == 0 ? ImmutableSortedSet<T>.Empty
            : SortedTrees.Usable && SortedSetTree<T>.Create(items) is { } set
                ? set
                : ImmutableCollections.Distinct(ImmutableSortedSet.CreateRange(items), items.Length);
    }
}
