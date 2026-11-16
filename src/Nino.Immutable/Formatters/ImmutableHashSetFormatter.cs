using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary>
    /// <c>ImmutableHashSet&lt;T&gt;</c>: count (-1 = null), then the elements, as a <c>HashSet&lt;T&gt;</c> writes them.
    /// Read with the default comparer.
    /// </summary>
    public struct ImmutableHashSetFormatter<T, TFormatter> : INinoFormatter<ImmutableHashSet<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableHashSet<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Copied out of its tree (HashTrees) in the order its enumerator yields them, and written as an array
            // (raw elements as one block): the enumerator keeps a pooled stack of nodes and checks it per element.
            int count = value.Count;
            if (count != 0 && HashTrees.Usable && SetTree<T>.Copy(value, count) is { } array)
            {
                ImmutableCollections.WriteRented<T, TFormatter>(array, count, ref writer);
                return;
            }

            if (count != 0 && ImmutableCollections.Slots<T>.Possible)
            {
                ImmutableCollections.WriteEnumerated<T, TFormatter>(value, ref writer);
                return;
            }

            // Element by element with its own (struct) enumerator: through IEnumerable<T>, each step is an
            // interface call.
            writer = WriteItems(value, count, writer);
        }

        // Out of line, the count too: inlined into the caller, the large enumerator is kept in memory instead of
        // registers, and this path (empty sets, runtimes whose trees HashTrees cannot read) takes the inlining
        // budget of the common one. The writer by value and returned: one passed by reference would keep the
        // caller's in memory.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteItems(ImmutableHashSet<T> value, int count, Writer writer)
        {
            writer.WriteLength(count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
            return writer;
        }

        public void Deserialize(ref Reader reader, ref ImmutableHashSet<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            if (array == null)
            {
                value = null;
                return;
            }

            value = Build(array);
        }

        // Built as its tree where it can be (HashTrees), else added one by one to a builder (CreateRange enumerates
        // through IEnumerable<T>); out of line like WriteItems.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static ImmutableHashSet<T> Build(T[] items)
        {
            // Empty: the shared instance, which a builder would return too, without allocating one.
            if (items.Length == 0)
                return ImmutableHashSet<T>.Empty;
            if (HashTrees.Usable && SetTree<T>.Create(items) is { } set)
                return set;

            // Added one by one: a hash shared by many items is checked first (hash flooding).
            HashTrees.CheckCollisions(items, typeof(ImmutableHashSet<T>));
            var builder = ImmutableHashSet.CreateBuilder<T>();
            foreach (var item in items)
            {
                if (!builder.Add(item))
                    ImmutableCollections.DuplicateKey(typeof(ImmutableHashSet<T>));
            }

            return builder.ToImmutable();
        }
    }
}
