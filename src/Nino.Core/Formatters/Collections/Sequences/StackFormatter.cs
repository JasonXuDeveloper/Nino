using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary><c>Stack&lt;T&gt;</c>: count (-1 = null), then the elements from bottom to top.</summary>
    public struct StackFormatter<T, TFormatter> : INinoFormatter<Stack<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(Stack<T> value, ref Writer writer) =>
            default(StackFormatter<Stack<T>, T, TFormatter>).Serialize(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref Stack<T> value) =>
            default(StackFormatter<Stack<T>, T, TFormatter>).Deserialize(ref reader, ref value);
    }

    /// <summary>
    /// A type derived from <c>Stack&lt;T&gt;</c> (and <c>Stack&lt;T&gt;</c> itself): count (-1 = null), then the
    /// elements from bottom to top, so reading is a sequence of pushes. An existing stack is cleared and refilled.
    /// </summary>
    public struct StackFormatter<[NinoCreated] TStack, T, TFormatter> : INinoFormatter<TStack>
        where TStack : Stack<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TStack value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            int count = value.Count;
            if (CollectionViews.Stack)
            {
                // The backing array is bottom to top already (a block for raw elements).
                NinoCollections.WriteSpan<T, TFormatter>(new ReadOnlySpan<T>(CollectionFields.Array<T>(value), 0, count),
                    ref writer);
                return;
            }

            writer.WriteLength(count);
            if (count != 0)
                writer = WriteReversed(value, count, writer);
        }

        /// <summary>
        /// Enumeration is top to bottom; written bottom to top so reading is a sequence of pushes: the snapshot
        /// reversed, then written as a span (a block for elements of a fixed size). Out of line, where the stack's
        /// fields are not known, the writer by value and returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteReversed(TStack value, int count, Writer writer)
        {
            var buffer = ArrayPool<T>.Shared.Rent(count);
            try
            {
                value.CopyTo(buffer, 0);
                var items = new Span<T>(buffer, 0, count);
                items.Reverse();
                NinoElements.Write<T, TFormatter>(items, ref writer);
            }
            finally
            {
                NinoCollections.ReturnRented(buffer);
            }

            return writer;
        }

        public void Deserialize(ref Reader reader, ref TStack value)
        {
            // Each element takes at least its fewest bytes (NinoElements.MinWireSize), which bounds what the
            // collection allocates ahead of them (NinoElements.Heavy).
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            var stack = value ??= typeof(TStack) == typeof(Stack<T>)
                ? (TStack)new Stack<T>(Unsafe.SizeOf<T>() > NinoElements.Amplification &&
                    NinoElements.HeavyElements<T, TFormatter>.Value
                    ? NinoElements.Capacity(length, Unsafe.SizeOf<T>(), reader.Remaining)
                    : length)
                : NinoActivator<TStack>.Create();
            if (NinoRecycler.DiscardAll(stack, stack.Count))
                stack.Clear();

            if (CollectionViews.Stack)
            {
                // Bottom to top is the backing array's order: read in place, then set the count.
                NinoCollectionViews.ReadInto<T, TFormatter>(ref CollectionFields.Array<T>(stack), length, ref reader);
                CollectionFields.Size<T>(stack) = length;
                CollectionFields.Version<T>(stack)++;
                return;
            }

            for (int i = 0; i < length; i++)
            {
                T item = default;
                NinoElements.ReadOne<T, TFormatter>(ref reader, ref item);
                stack.Push(item);
            }
        }
    }
}
