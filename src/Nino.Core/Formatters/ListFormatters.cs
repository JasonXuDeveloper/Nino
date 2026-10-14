using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>List&lt;T&gt;</c> (or a type derived from it): count (-1 = null), then the elements.
    /// An existing list is reused, and its elements are reused by index.
    /// </summary>
    public struct ListFormatter<TList, T, TFormatter> : INinoFormatter<TList>
        where TList : List<T>, new()
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TList value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            ReadOnlySpan<T> span = ListAccess.AsSpan<T>(value);
            writer.WriteLength(span.Length);
            NinoElements.Write<T, TFormatter>(span, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref TList value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var list = value;
            if (list == null)
            {
                list = typeof(TList) == typeof(List<T>) ? (TList)new List<T>(length) : new TList();
                value = list;
            }
            else if (list.Count > length)
            {
                NinoRecycler.RecycleRange(ListAccess.AsSpan<T>(list).Slice(length));
            }

            NinoElements.Read<T, TFormatter>(ListAccess.SetCount<T>(list, length), ref reader);
        }
    }

    /// <summary>Unmanaged <c>List&lt;T&gt;</c>: count (-1 = null), then one block copy.</summary>
    public struct UnmanagedListFormatter<TList, T> : INinoFormatter<TList>
        where TList : List<T>, new()
        where T : unmanaged
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TList value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.Write((ReadOnlySpan<T>)ListAccess.AsSpan<T>(value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref TList value, ref Reader reader)
        {
            int length = reader.ReadLength(Unsafe.SizeOf<T>());
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var list = value;
            if (list == null)
            {
                list = typeof(TList) == typeof(List<T>) ? (TList)new List<T>(length) : new TList();
                value = list;
            }

            reader.ReadUnmanagedRaw(ListAccess.SetCount<T>(list, length));
        }
    }

    /// <summary><c>List&lt;T?&gt;</c> of raw <typeparamref name="T"/>: same bytes as the per-element encoding.</summary>
    public struct NullableListFormatter<TList, T> : INinoFormatter<TList>
        where TList : List<T?>, new()
        where T : unmanaged
    {
        public void Serialize(TList value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            NinoNullables.Write<T>(ListAccess.AsSpan<T?>(value), ref writer);
        }

        public void Deserialize(ref TList value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var list = value;
            if (list == null)
            {
                list = typeof(TList) == typeof(List<T?>) ? (TList)new List<T?>(length) : new TList();
                value = list;
            }

            NinoNullables.Read<T>(ListAccess.SetCount<T?>(list, length), ref reader);
        }
    }

    /// <summary>
    /// Collection interfaces (<c>IEnumerable&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
    /// <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyCollection&lt;T&gt;</c>, <c>ISet&lt;T&gt;</c>, ...):
    /// count (-1 = null), then the elements. Read into <typeparamref name="TConcrete"/>.
    /// </summary>
    public struct InterfaceCollectionFormatter<TInterface, TConcrete, T, TFormatter> : INinoFormatter<TInterface>
        where TInterface : class, IEnumerable<T>
        where TConcrete : class, TInterface, ICollection<T>, new()
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TInterface value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            if (value is List<T> list)
            {
                default(ListFormatter<List<T>, T, TFormatter>).Serialize(list, ref writer);
                return;
            }

            if (value is T[] array)
            {
                default(ArrayFormatter<T, TFormatter>).Serialize(array, ref writer);
                return;
            }

            NinoCollections.WriteEnumerable<T, TFormatter>(value, ref writer);
        }

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            if (typeof(TConcrete) == typeof(List<T>))
            {
                var list = value as List<T>;
                var old = value;
                default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref list, ref reader);
                value = (TInterface)(object)list;
                if (old != null && !ReferenceEquals(old, list))
                    NinoRecycler<TInterface>.Recycle(old);
                return;
            }

            var collection = value as TConcrete;
            var previous = value;
            default(CollectionFormatter<TConcrete, T, TFormatter>).Deserialize(ref collection, ref reader);
            value = collection;
            if (previous != null && !ReferenceEquals(previous, collection))
                NinoRecycler<TInterface>.Recycle(previous);
        }
    }

    /// <summary>Shared helpers for collection formatters.</summary>
    public static partial class NinoCollections
    {
        /// <summary>Writes the count of <paramref name="values"/> (patched afterwards if unknown), then the elements.</summary>
        public static void WriteEnumerable<T, TFormatter>(IEnumerable<T> values, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int count;
            if (values is ICollection<T> c)
                count = c.Count;
            else if (values is IReadOnlyCollection<T> rc)
                count = rc.Count;
            else
                count = -1;

            int countPos = writer.ReserveInt32();
            int written = 0;
            foreach (var item in values)
            {
                default(TFormatter).Serialize(item, ref writer);
                written++;
            }

            if (count >= 0 && count != written)
                NinoThrow.InvalidData("collection was modified during serialization");
            writer.PatchInt32(countPos, written);
        }

        /// <summary>Recycles the items of a collection that is about to be cleared, if a handler exists.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RecycleItems<T>(IEnumerable<T> items)
        {
            if (!NinoRecycler<T>.IsRegistered)
                return;
            foreach (var item in items)
                NinoRecycler<T>.Recycle(item);
        }
    }
}
