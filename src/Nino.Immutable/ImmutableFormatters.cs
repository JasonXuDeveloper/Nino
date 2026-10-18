using System;
using System.Buffers;
using System.Collections.Immutable;
#if !NETSTANDARD2_0
using System.Runtime.CompilerServices;
#endif
using System.Runtime.InteropServices;
using Nino.Core;
using Nino.Immutable;

// Registered like Nino.Core's own BCL formatters: generated code and the runtime resolver both read these.
[assembly: NinoFormatter(typeof(ImmutableArrayFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableListFormatter<,>))]

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

        public void Deserialize(ref ImmutableArray<T> value, ref Reader reader)
        {
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            value = ImmutableCollectionsMarshal.AsImmutableArray(array);
        }
    }

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
            default(ArraySegmentFormatter<T, TFormatter>).Serialize(new ArraySegment<T>(array, 0, value.Count),
                ref writer);
#if NETSTANDARD2_0
            ArrayPool<T>.Shared.Return(array, clearArray: true);
#else
            ArrayPool<T>.Shared.Return(array, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
#endif
        }

        public void Deserialize(ref ImmutableList<T> value, ref Reader reader)
        {
            // Read as a T[] (raw elements as one block) and built from it in one pass: a builder would insert and
            // rebalance its tree for every element.
            T[] array = null;
            default(ArrayFormatter<T, TFormatter>).Deserialize(ref array, ref reader);
            value = array == null ? null : ImmutableList.CreateRange(array);
        }
    }
}
