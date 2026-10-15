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
                value = list = ListAccess.Create<TList, T>(length);
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
            int length = reader.ReadLength(NinoLayout<T>.Size);
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var list = value ??= ListAccess.Create<TList, T>(length);
            reader.ReadUnmanagedRaw(ListAccess.SetCount<T>(list, length, overwritten: true));
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

            NinoNullables<T>.Write(ListAccess.AsSpan<T?>(value), ref writer);
        }

        public void Deserialize(ref TList value, ref Reader reader)
        {
            int length = NinoNullables<T>.ReadCount(ref reader);
            if (length < 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var list = value ??= ListAccess.Create<TList, T?>(length);
            NinoNullables<T>.Read(ListAccess.SetCount<T?>(list, length, overwritten: true), ref reader);
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
            var old = value;
            if (typeof(TConcrete) == typeof(List<T>))
            {
                var list = old as List<T>;
                default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref list, ref reader);
                value = (TInterface)(object)list;
            }
            else
            {
                var collection = old as TConcrete;
                default(CollectionFormatter<TConcrete, T, TFormatter>).Deserialize(ref collection, ref reader);
                value = collection;
            }

            // A TConcrete was handed to the formatter, which reused or recycled it; another collection is dropped.
            if (old != null && !(old is TConcrete))
                NinoRecycler<TInterface>.Recycle(old);
        }
    }

    /// <summary>Shared helpers for collection formatters.</summary>
    internal static partial class NinoCollections
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
    }
}
