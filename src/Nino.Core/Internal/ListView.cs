using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace Nino.Core.Internal
{
#if !NET8_0_OR_GREATER
    /// <summary>Mirrors the field layout of <see cref="List{T}"/> (the same on .NET Framework, Mono and IL2CPP).</summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
#pragma warning disable CS0649 // assigned through Unsafe.As
    internal sealed class ListView<T>
    {
        public T[] _items; // Do not rename (binary serialization)
        public int _size; // Do not rename (binary serialization)
    }
#pragma warning restore CS0649
#endif

    /// <summary>Direct access to the backing array of a <see cref="List{T}"/>.</summary>
    internal static class ListAccess
    {
        /// <summary>
        /// A new, empty <typeparamref name="TList"/>; a plain <see cref="List{T}"/> gets <paramref name="capacity"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TList Create<TList, T>(int capacity) where TList : List<T>, new() =>
            typeof(TList) == typeof(List<T>) ? (TList)new List<T>(capacity) : new TList();

        /// <summary>The list's elements as a span. Do not add or remove items while using it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> AsSpan<T>(List<T> list)
        {
#if NET8_0_OR_GREATER
            return CollectionsMarshal.AsSpan(list);
#else
            var view = Unsafe.As<ListView<T>>(list);
            return new Span<T>(view._items, 0, view._size);
#endif
        }

        /// <summary>
        /// Sets the list's count, keeping the first elements. New slots are default; dropped slots are cleared.
        /// Returns the elements as a span. Pass <paramref name="overwritten"/> when the caller writes every
        /// element of the span, so new slots are not cleared first.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> SetCount<T>(List<T> list, int count, bool overwritten = false)
        {
#if NET8_0_OR_GREATER
            int old = list.Count;
            CollectionsMarshal.SetCount(list, count);
            var span = CollectionsMarshal.AsSpan(list);
            if (count > old && !overwritten)
                span.Slice(old).Clear(); // unmanaged slots past the old count may hold stale values
            return span;
#else
            if (list.Capacity < count)
                list.Capacity = count;
            var view = Unsafe.As<ListView<T>>(list);
            if (count < view._size && NinoUnsafe.IsReferenceOrContainsReferences<T>())
                Array.Clear(view._items, count, view._size - count);
            else if (count > view._size && !overwritten)
                Array.Clear(view._items, view._size, count - view._size);
            view._size = count;
            return new Span<T>(view._items, 0, count);
#endif
        }
    }
}
