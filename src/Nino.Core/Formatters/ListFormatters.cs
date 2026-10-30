using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoSizedFormatter<TList>.Size(TList value) =>
            NinoElements.SizedBytes<T, TFormatter>(value == null ? -1 : value.Count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void INinoSizedFormatter<TList>.WriteSized(TList value, ref byte destination) =>
            NinoElements.WriteSized<T, TFormatter>(ref destination,
                value == null ? default : (ReadOnlySpan<T>)ListAccess.AsSpan<T>(value), value == null);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TList value, ref Writer writer)
        {
            if (!writer.TryWriteNullLength(value))
                NinoCollections.WriteSpan<T, TFormatter>(ListAccess.AsSpan<T>(value), ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref TList value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoElements.MinWireSize<T, TFormatter>(), out int length))
                return;

            // Elements far larger in memory than on the wire: out of line, grown as they are read where the data
            // could not fill as many (see ArrayFormatter). A constant false for the others, and nothing compiled.
            if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
            {
                reader = NinoCollections.ReadGrowing<TList, T, TFormatter>(ref value, length, reader);
                return;
            }

            // A new list over an uninitialized array the read may reject half way takes the slot once filled (see
            // ArrayFormatter).
            if (typeof(T).IsValueType && NinoElements.Rejecting<T, TFormatter>.Value)
            {
                reader = ReadPublished(ref value, length, reader);
                return;
            }

            var list = value;
            // Values each read in full: neither a new list's backing array nor an existing one's new slots need be
            // cleared first.
            bool overwritten = NinoElements.ReadOverwrites<T, TFormatter>();
            if (list == null)
            {
                value = list = overwritten
                    ? ListAccess.CreateOverwritten<TList, T>(length, NinoElements.CopiedWhole<T, TFormatter>())
                    : ListAccess.Create<TList, T>(length);
            }
            // The dropped tail (a factory may hand out a pooled list that is not empty: treated the same way).
            else if (list.Count > length)
                NinoRecycler.DiscardAll(ListAccess.AsSpan<T>(list).Slice(length));

            var span = ListAccess.Open(list, length, overwritten, out var buffer);
            NinoElements.Read<T, TFormatter>(span, ref reader);
            ListAccess.Close(list, length, buffer);
        }

        /// <summary>
        /// <see cref="Deserialize"/> of <see cref="NinoElements.Rejecting{T, TFormatter}"/> elements: a new list, over
        /// an uninitialized array, takes the slot only once filled. Out of line, the reader by value and returned.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Reader ReadPublished(scoped ref TList value, int length, Reader reader)
        {
            var list = value;
            if (list == null)
                list = ListAccess.CreateOverwritten<TList, T>(length, uninitialized: true);
            else if (list.Count > length)
                NinoRecycler.DiscardAll(ListAccess.AsSpan<T>(list).Slice(length));
            var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
            NinoElements.Read<T, TFormatter>(span, ref reader);
            ListAccess.Close(list, length, buffer);
            value = list;
            return reader;
        }
    }

    /// <summary>Unmanaged <c>List&lt;T&gt;</c>: count (-1 = null), then one block copy.</summary>
    public struct UnmanagedListFormatter<[NinoCreated] TList, [NinoRaw] T> : INinoFormatter<TList>,
        INinoSizedFormatter<TList>
        where TList : List<T>
        where T : unmanaged
    {
        // The layout of Writer.Write(ReadOnlySpan<T>) where T's memory is its wire layout.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int INinoSizedFormatter<TList>.Size(TList value) =>
            !NinoLayout<T>.IsCanonical ? -1
            : value == null ? NinoWire.LengthSize
            : (long)value.Count * Unsafe.SizeOf<T>() + NinoWire.LengthSize is var size && size <= int.MaxValue
                ? (int)size
                : -1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
            if (writer.TryWriteNullLength(value))
                return;

            writer.Write((ReadOnlySpan<T>)ListAccess.AsSpan<T>(value));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref TList value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoLayout<T>.Size, out int length))
                return;

            // Bools too: ReadUnmanagedRaw checks them (NinoLayout<T>.CopiesAsIs).
            if (!ListAccess.Direct || !NinoLayout<T>.CopiesAsIs)
            {
                // The read checks bools and Untrusted mode's values: a new list over an uninitialized array takes the
                // slot once filled.
                var list = value ?? ListAccess.CreateOverwritten<TList, T>(length, uninitialized: true);
                var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
                reader.ReadUnmanagedRaw(span);
                ListAccess.Close(list, length, buffer);
                value = list;
                return;
            }

            ref T first = ref ListAccess.OverwriteAll<TList, T>(ref value, length);

            // The length was checked against the remaining bytes above: one block copy, no second bounds check. Up to
            // 64 bytes Memmove: CopyToNew's call and stack frame cost more there than it saves (a list of 8 classes
            // holding 8 ints each, read in place: 530 ns instead of 460, AVX-512 Xeon).
            uint bytes = (uint)length * (uint)Unsafe.SizeOf<T>();
            if (bytes <= 64)
                Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref first), ref reader.UnsafeAdvance((int)bytes), bytes);
            else
                NinoUnsafe.CopyToNew(ref Unsafe.As<T, byte>(ref first), ref reader.UnsafeAdvance((int)bytes), bytes);
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
            if (writer.TryWriteNullLength(value))
                return;

            NinoNullables<T>.Write(ListAccess.AsSpan<T?>(value), ref writer);
        }

        public void Deserialize(ref TList value, ref Reader reader)
        {
            if (!reader.TryReadLengthOrDiscard(ref value, NinoNullables<T>.Stride, out int length))
                return;

            // Uninitialized only for a block copy, after which the has-value bytes are checked: a new list takes the
            // slot once filled (NinoElements.Rejects).
            var list = value ?? ListAccess.CreateOverwritten<TList, T?>(length, NinoNullables<T>.Raw);
            var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
            NinoNullables<T>.Read(span, ref reader);
            ListAccess.Close(list, length, buffer);
            value = list;
        }
    }
}
