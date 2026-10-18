using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// Mirrors the field layout of <see cref="List{T}"/> (the same on .NET Framework, .NET, Mono and IL2CPP).
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
#pragma warning disable CS0649 // assigned through Unsafe.As
    internal sealed class ListView<T>
    {
        public T[] _items; // Do not rename (binary serialization)
        public int _size; // Do not rename (binary serialization)
    }

    /// <summary>
    /// Mirrors the leading fields of <see cref="Stack{T}"/> (the same on .NET Framework, .NET, Mono and IL2CPP;
    /// checked by <see cref="CollectionViews.Stack"/>). The elements are <c>_array[0.._size)</c>, bottom to top.
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal sealed class StackView<T>
    {
        public T[] _array;
        public int _size;
        public int _version;
    }

    /// <summary>
    /// Mirrors the leading fields of <see cref="Queue{T}"/> (the same on .NET Framework, .NET, Mono and IL2CPP;
    /// checked by <see cref="CollectionViews.Queue"/>). The elements are a ring from <c>_head</c>, <c>_size</c>
    /// long; <c>_tail</c> is the next free slot (0 past the end).
    /// </summary>
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal sealed class QueueView<T>
    {
        public T[] _array;
        public int _head;
        public int _tail;
        public int _size;
        public int _version;
    }
#pragma warning restore CS0649

    /// <summary>
    /// Whether <see cref="Stack{T}"/> and <see cref="Queue{T}"/> have the layouts of <see cref="StackView{T}"/> and
    /// <see cref="QueueView{T}"/> on this runtime, checked once on instances whose contents are known; otherwise their
    /// formatters use the public API. Static readonly: a constant in Tier1 code.
    /// </summary>
    internal static class CollectionViews
    {
        public static readonly bool Stack = CheckStack();
        public static readonly bool Queue = CheckQueue();

        private static bool CheckStack()
        {
            try
            {
                var stack = new Stack<int>(4);
                stack.Push(7);
                stack.Push(9);
                var view = Unsafe.As<StackView<int>>(stack);
                return view._size == 2 && view._array is { Length: 4 } array && array[0] == 7 && array[1] == 9;
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckQueue()
        {
            try
            {
                var queue = new Queue<int>(4);
                queue.Enqueue(5);
                queue.Enqueue(7);
                queue.Enqueue(9);
                queue.Dequeue();
                var view = Unsafe.As<QueueView<int>>(queue);
                return view._head == 1 && view._tail == 3 && view._size == 2 &&
                       view._array is { Length: 4 } array && array[1] == 7 && array[2] == 9;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Where the fields of value types are on this runtime (constants once the class is initialized).</summary>
    internal static class ValueViews
    {
        /// <summary>
        /// Byte position of the UTC ticks inside a <see cref="DateTimeOffset"/> (its date, stored with no kind bits),
        /// 0 or 8; its offset in minutes (an int, or a short followed by padding) is at the other one. -1 when the
        /// layout is neither: .NET Framework, Mono and .NET 8 have the date first and a short, .NET 10 an int first.
        /// </summary>
        public static readonly int DateTimeOffsetTicks = FindDateTimeOffsetTicks();

        private static int FindDateTimeOffsetTicks()
        {
            try
            {
                if (DateTimeOffsetMatches(0))
                    return 0;
                if (DateTimeOffsetMatches(8))
                    return 8;
            }
            catch
            {
                // Not this layout.
            }

            return -1;
        }

        private static bool DateTimeOffsetMatches(int ticksAt)
        {
            int minutesAt = 8 - ticksAt;
            if (Unsafe.SizeOf<DateTimeOffset>() < Math.Max(ticksAt + 8, minutesAt + 4))
                return false;
            var value = new DateTimeOffset(637_000_000_000_000_000, new TimeSpan(5, 30, 0));
            ref byte bytes = ref Unsafe.As<DateTimeOffset, byte>(ref value);
            if (Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref bytes, ticksAt)) != value.UtcTicks ||
                Unsafe.ReadUnaligned<short>(ref Unsafe.Add(ref bytes, minutesAt)) != 330)
                return false;
            // Written as the formatter writes them, read back through the API.
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, ticksAt), 600_000_000_000_000_000);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, minutesAt), -90);
            return value.UtcTicks == 600_000_000_000_000_000 && value.Offset == new TimeSpan(-1, -30, 0) &&
                   value.Ticks == 600_000_000_000_000_000 - 90 * TimeSpan.TicksPerMinute;
        }
    }

    /// <summary>Direct access to the backing array of a <see cref="List{T}"/>.</summary>
    internal static class ListAccess
    {
        /// <summary>
        /// A new, empty <typeparamref name="TList"/> (through <see cref="NinoActivator{T}"/>); a plain
        /// <see cref="List{T}"/> gets <paramref name="capacity"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TList Create<TList, T>(int capacity) where TList : List<T> =>
            typeof(TList) == typeof(List<T>) ? (TList)new List<T>(capacity) : NinoActivator<TList>.Create();

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
        /// Sets the count of a list of unmanaged <typeparamref name="T"/> that the caller then overwrites entirely, and
        /// returns a reference to its first element. No span: its bounds check (the count against the backing array,
        /// which SetCount just sized) keeps the count live in a second register through the list's allocation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T SetCountOverwritten<T>(List<T> list, int count)
        {
#if NET8_0_OR_GREATER
            CollectionsMarshal.SetCount(list, count);
#else
            if (list.Capacity < count)
                list.Capacity = count;
            Unsafe.As<ListView<T>>(list)._size = count;
#endif
            return ref NinoUnsafe.GetArrayDataReference(Unsafe.As<ListView<T>>(list)._items);
        }

        /// <summary>
        /// Sets the list's count, keeping the first elements. New slots are default; dropped slots are cleared.
        /// Returns the elements as a span. Pass <paramref name="overwritten"/> when the caller writes every
        /// element of the span, so new slots are not cleared first.
        /// <para>
        /// Slots past the count of a list of references are already default: every <see cref="List{T}"/> (CoreCLR,
        /// .NET Framework, Mono, IL2CPP) clears them when it removes elements, so the garbage collector does not keep
        /// what they referenced, and a grown backing array is new. Only unmanaged slots can hold stale values.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> SetCount<T>(List<T> list, int count, bool overwritten = false)
        {
#if NET8_0_OR_GREATER
            int old = list.Count;
            CollectionsMarshal.SetCount(list, count);
            var span = CollectionsMarshal.AsSpan(list);
            if (count > old && !overwritten && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                span.Slice(old).Clear(); // unmanaged slots past the old count may hold stale values
            return span;
#else
            if (list.Capacity < count)
                list.Capacity = count;
            var view = Unsafe.As<ListView<T>>(list);
            if (count < view._size && NinoUnsafe.IsReferenceOrContainsReferences<T>())
                Array.Clear(view._items, count, view._size - count);
            else if (count > view._size && !overwritten && !NinoUnsafe.IsReferenceOrContainsReferences<T>())
                Array.Clear(view._items, view._size, count - view._size);
            view._size = count;
            return new Span<T>(view._items, 0, count);
#endif
        }
    }
}
