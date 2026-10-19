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
    public struct ListFormatter<[NinoCreated] TList, T, TFormatter> : INinoFormatter<TList>, INinoSizedFormatter<TList>
        where TList : List<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        int INinoSizedFormatter<TList>.Size(TList value) =>
            NinoElements.SizedBytes<T, TFormatter>(value == null ? -1 : value.Count);

        void INinoSizedFormatter<TList>.WriteSized(TList value, ref byte destination) =>
            NinoElements.WriteSized<T, TFormatter>(ref destination,
                value == null ? default : (ReadOnlySpan<T>)ListAccess.AsSpan<T>(value), value == null);

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
            // Fixed-size values are each read in full: a new list's backing array need not be cleared first.
            bool overwritten = false;
            if (list == null)
            {
                overwritten = NinoBlock.Size<T, TFormatter>() > 0;
                value = list = overwritten
                    ? ListAccess.CreateOverwritten<TList, T>(length)
                    : ListAccess.Create<TList, T>(length);
            }
            // The dropped tail (a factory may hand out a pooled list that is not empty: treated the same way).
            else if (list.Count > length)
                NinoRecycler.DiscardAll(ListAccess.AsSpan<T>(list).Slice(length));

            var span = ListAccess.Open(list, length, overwritten, out var buffer);
            NinoElements.Read<T, TFormatter>(span, ref reader);
            ListAccess.Close(list, length, buffer);
        }
    }

    /// <summary>Unmanaged <c>List&lt;T&gt;</c>: count (-1 = null), then one block copy.</summary>
    public struct UnmanagedListFormatter<[NinoCreated] TList, [NinoRaw] T> : INinoFormatter<TList>,
        INinoSizedFormatter<TList>
        where TList : List<T>
        where T : unmanaged
    {
        // The layout of Writer.Write(ReadOnlySpan<T>) where T's memory is its wire layout.
        int INinoSizedFormatter<TList>.Size(TList value) =>
            !NinoLayout<T>.IsCanonical ? -1
            : value == null ? NinoWire.LengthSize
            : (long)value.Count * Unsafe.SizeOf<T>() + NinoWire.LengthSize is var size && size <= int.MaxValue
                ? (int)size
                : -1;

        void INinoSizedFormatter<TList>.WriteSized(TList value, ref byte destination)
        {
            if (value == null)
                NinoUnsafe.Write(ref destination, NinoWire.NullLength);
            else
                NinoLayout<T>.WriteLengthPrefixed(ref destination,
                    ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(ListAccess.AsSpan<T>(value))), value.Count);
        }

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

            if (!ListAccess.Direct || !NinoLayout<T>.IsCanonical)
            {
                var list = value ??= ListAccess.CreateOverwritten<TList, T>(length);
                var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
                reader.ReadUnmanagedRaw(span);
                ListAccess.Close(list, length, buffer);
                return;
            }

            // A new list's backing array straight from its field (no count to set again).
            ref T first = ref value == null && CollectionViews.List
                ? ref NinoUnsafe.GetArrayDataReference(
                    CollectionFields.Items<T>(value = ListAccess.CreateOverwritten<TList, T>(length)))
                : ref ListAccess.SetCountOverwritten(value ??= ListAccess.Create<TList, T>(length), length);

            // The length was checked against the remaining bytes above: one block copy, no second bounds check.
            uint bytes = (uint)length * (uint)Unsafe.SizeOf<T>();
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref first), ref reader.UnsafeAdvance((int)bytes), bytes);
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

            var list = value ??= ListAccess.CreateOverwritten<TList, T?>(length);
            var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
            NinoNullables<T>.Read(span, ref reader);
            ListAccess.Close(list, length, buffer);
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
