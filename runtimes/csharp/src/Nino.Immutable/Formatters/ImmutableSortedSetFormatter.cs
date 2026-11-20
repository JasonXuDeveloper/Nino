using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System;
using Nino.Core;
using Nino.Core.CompilerServices;

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

            writer = Write(value, writer);
        }

        // Out of line, the writer by value and returned, as Nino.Core's own trees (BclCollections.WriteSorted): inlined,
        // the walk's setup (its source built and passed by value) took registers and stack from every caller (asm.py,
        // a top-level ImmutableSortedSet<int> write: 43 stack accesses, 33 out of line, the writer's copy in and out).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer Write(ImmutableSortedSet<T> value, Writer writer)
        {
            // Its tree walked in order a chunk at a time (SortedTrees), as ImmutableList<T>'s, else its enumerator.
            if (SortedTrees.Usable)
                NinoGathered.Write<T, TFormatter, NinoTreeItems<T>>(SortedSetTree<T>.Items(value), value.Count,
                    ref writer);
            else
                NinoGathered.Write<T, TFormatter, NinoGathered.Enumerated<T, ImmutableSortedSet<T>.Enumerator>>(
                    new(value.GetEnumerator()), value.Count, ref writer);

            return writer;
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
