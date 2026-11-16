using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.InteropServices;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// The elements of a <see cref="List{T}"/> as a span: <see cref="CollectionsMarshal"/> on .NET 8 and later, its
    /// fields where <see cref="CollectionViews.List"/> found them, otherwise copies through the public API.
    /// </summary>
    internal static class ListAccess
    {
        /// <summary>
        /// Whether spans of a list are its own backing array: then <see cref="Open{T}"/> hands out no buffer to
        /// <see cref="Close{T}"/>. A constant on .NET 8 and later.
        /// </summary>
        public static bool Direct
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET8_0_OR_GREATER
            get => true;
#else
            get => CollectionViews.List;
#endif
        }

        /// <summary>
        /// A new, empty <typeparamref name="TList"/> (through <see cref="NinoActivator{T}"/>); a plain
        /// <see cref="List{T}"/> gets <paramref name="capacity"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TList Create<TList, T>(int capacity) where TList : List<T> =>
            typeof(TList) == typeof(List<T>) ? (TList)new List<T>(capacity) : NinoActivator<TList>.Create();

        /// <summary>
        /// A new <typeparamref name="TList"/> that the caller fills with <paramref name="count"/> elements (not
        /// negative), overwriting each one; it has them already when <see cref="Direct"/>, otherwise the caller adds
        /// them through <see cref="Open{T}"/>. A plain <see cref="List{T}"/> whose fields are known gets a backing
        /// array of exactly that length, allocated uninitialized when it is large and <paramref name="uninitialized"/>
        /// (<see cref="NinoUnsafe.NewArray{T}"/>, <see cref="NinoElements.CopiedWhole{T, TFormatter}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static TList CreateOverwritten<TList, T>(int count, bool uninitialized) where TList : List<T>
        {
            if (typeof(TList) == typeof(List<T>) && CollectionViews.List)
            {
                var created = new List<T>();
                CollectionFields.Items(created) = NinoUnsafe.NewArray<T>(count, uninitialized);
                CollectionFields.Size(created) = count;
                return (TList)created;
            }

            var list = Create<TList, T>(count);
            if (Direct)
                Open(list, count, overwritten: true, out _);
            return list;
        }

        /// <summary>The list's elements as a span (a copy unless <see cref="Direct"/>), to read.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<T> AsSpan<T>(List<T> list)
        {
#if NET8_0_OR_GREATER
            return CollectionsMarshal.AsSpan(list);
#else
            if (!CollectionViews.List)
                return list.ToArray();
            return new ReadOnlySpan<T>(CollectionFields.Items(list), 0, CollectionFields.Size(list));
#endif
        }

        /// <summary>
        /// The first <paramref name="count"/> elements of a list (a copy unless <see cref="Direct"/>), to write, for
        /// <see cref="Close{T}"/> to make them its contents. Elements it already has are kept in place for the caller
        /// to reuse; new slots are default, unless <paramref name="overwritten"/>: the caller writes every element.
        /// <para>
        /// Slots past the count of a list of references are already default: every <see cref="List{T}"/> (CoreCLR,
        /// .NET Framework, Mono, IL2CPP) clears them when it removes elements, so the garbage collector does not keep
        /// what they referenced, and a grown backing array is new. Only unmanaged slots can hold stale values.
        /// </para>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> Open<T>(List<T> list, int count, bool overwritten, out T[] buffer)
        {
            buffer = null;
#if NET8_0_OR_GREATER
            int old = list.Count;
            CollectionsMarshal.SetCount(list, count);
            var span = CollectionsMarshal.AsSpan(list);
            if (count > old && !overwritten && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                span.Slice(old).Clear();
            return span;
#else
            if (!CollectionViews.List)
                return OpenCopy(list, count, out buffer);
            if (list.Capacity < count)
                list.Capacity = count;
            var items = CollectionFields.Items(list);
            ref int size = ref CollectionFields.Size(list);
            if (count < size && NinoUnsafe.IsReferenceOrContainsReferences<T>())
                Array.Clear(items, count, size - count);
            else if (count > size && !overwritten && !NinoUnsafe.IsReferenceOrContainsReferences<T>())
                Array.Clear(items, size, count - size);
            size = count;
            return new Span<T>(items, 0, count);
#endif
        }

        /// <summary>
        /// Makes the span <see cref="Open{T}"/> returned the list's contents, when it was a copy (a no-op when
        /// <see cref="Direct"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Close<T>(List<T> list, int count, T[] buffer)
        {
            if (buffer != null)
                CloseCopy(list, count, buffer);
        }

        /// <summary>
        /// Sets the count of a list of unmanaged <typeparamref name="T"/> that the caller then overwrites entirely, and
        /// returns a reference to its first element; only when <see cref="Direct"/>. No span: its bounds check (the
        /// count against the backing array, which SetCount just sized) keeps the count live in a second register
        /// through the list's allocation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T SetCountOverwritten<T>(List<T> list, int count)
        {
#if NET8_0_OR_GREATER
            CollectionsMarshal.SetCount(list, count);
            return ref CollectionViews.List
                ? ref NinoUnsafe.GetArrayDataReference(CollectionFields.Items(list))
                : ref MemoryMarshal.GetReference(CollectionsMarshal.AsSpan(list));
#else
            if (list.Capacity < count)
                list.Capacity = count;
            CollectionFields.Size(list) = count;
            return ref NinoUnsafe.GetArrayDataReference(CollectionFields.Items(list));
#endif
        }

        /// <summary>
        /// The first element of <paramref name="list"/> (a new one when null) holding <paramref name="count"/>
        /// unmanaged elements that the caller then overwrites entirely; only when <see cref="Direct"/>. A new list's
        /// backing array comes straight from its field (no count to set again).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T OverwriteAll<TList, T>(ref TList list, int count) where TList : List<T> =>
            ref list == null && CollectionViews.List
                ? ref NinoUnsafe.GetArrayDataReference(
                    CollectionFields.Items<T>(list = CreateOverwritten<TList, T>(count, uninitialized: true)))
                : ref SetCountOverwritten(list ??= Create<TList, T>(count), count);

        private static Span<T> OpenCopy<T>(List<T> list, int count, out T[] buffer)
        {
            buffer = ArrayPool<T>.Shared.Rent(count);
            int kept = Math.Min(count, list.Count);
            list.CopyTo(0, buffer, 0, kept);
            Array.Clear(buffer, kept, count - kept);
            return new Span<T>(buffer, 0, count);
        }

        private static void CloseCopy<T>(List<T> list, int count, T[] buffer)
        {
            list.Clear();
            list.AddRange(new ArraySegment<T>(buffer, 0, count));
            NinoCollections.ReturnRented(buffer);
        }
    }
}
