using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>List&lt;T&gt;</c> (or a type derived from it): count (-1 = null), then the elements.
    /// An existing list is reused, and its elements are reused by index.
    /// </summary>
    public struct ListFormatter<[NinoCreated] TList, T, TFormatter> : INinoFormatter<TList>
        where TList : List<T>
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
            if (!reader.TryReadLength(1, out int length))
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var list = value;
            if (list == null)
                value = list = ListAccess.Create<TList, T>(length);
            // The dropped tail (a factory may hand out a pooled list that is not empty: treated the same way).
            if (list.Count > length)
                NinoRecycler.DiscardAll(ListAccess.AsSpan<T>(list).Slice(length));

            NinoElements.Read<T, TFormatter>(ListAccess.SetCount<T>(list, length), ref reader);
        }
    }

    /// <summary>Unmanaged <c>List&lt;T&gt;</c>: count (-1 = null), then one block copy.</summary>
    public struct UnmanagedListFormatter<[NinoCreated] TList, [NinoRaw] T> : INinoFormatter<TList>
        where TList : List<T>
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
            if (!reader.TryReadLength(NinoLayout<T>.Size, out int length))
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var list = value ??= ListAccess.Create<TList, T>(length);
            if (!NinoLayout<T>.IsCanonical)
            {
                reader.ReadUnmanagedRaw(ListAccess.SetCount<T>(list, length, overwritten: true));
                return;
            }

            // The length was checked against the remaining bytes above: one block copy, no second bounds check.
            uint bytes = (uint)length * (uint)Unsafe.SizeOf<T>();
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref ListAccess.SetCountOverwritten(list, length)),
                ref reader.UnsafeAdvance((int)bytes), bytes);
        }
    }

    /// <summary>
    /// <c>List&lt;T?&gt;</c> of raw <typeparamref name="T"/>: the bytes of <see cref="NullableArrayFormatter{T}"/>
    /// (fixed stride), not the per-element encoding of <c>IList&lt;T?&gt;</c>.
    /// </summary>
    public struct NullableListFormatter<[NinoCreated] TList, [NinoRaw] T> : INinoFormatter<TList>
        where TList : List<T?>
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
