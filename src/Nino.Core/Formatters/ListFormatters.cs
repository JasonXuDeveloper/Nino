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
                var list = value ??= ListAccess.CreateOverwritten<TList, T>(length, uninitialized: true);
                var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
                reader.ReadUnmanagedRaw(span);
                ListAccess.Close(list, length, buffer);
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

            // Uninitialized only for a block copy (NinoElements.CopiedWhole).
            var list = value ??= ListAccess.CreateOverwritten<TList, T?>(length, NinoNullables<T>.Raw);
            var span = ListAccess.Open(list, length, overwritten: true, out var buffer);
            NinoNullables<T>.Read(span, ref reader);
            ListAccess.Close(list, length, buffer);
        }
    }

    /// <summary>Shared helpers for collection formatters.</summary>
    internal static partial class NinoCollections
    {
        /// <summary>
        /// Reads <paramref name="length"/> elements into a list, as <see cref="ListFormatter{TList,T,TFormatter}"/>
        /// does, for <see cref="NinoElements.HeavyElements{T,TFormatter}"/> elements: where they do not
        /// <see cref="NinoElements.Fits"/>, the list's count starts at what fits the bound (or the elements it has)
        /// and doubles as they are read, as
        /// <see cref="NinoElements.ReadGrowing{T,TFormatter}"/> does for an array. Elements it has are reused.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Reader ReadGrowing<TList, T, TFormatter>(scoped ref TList value, int length, Reader reader)
            where TList : List<T>
            where TFormatter : struct, INinoFormatter<T>
        {
            var list = value ??= ListAccess.Create<TList, T>(0);
            if (list.Count > length)
                NinoRecycler.DiscardAll(ListAccess.AsSpan<T>(list).Slice(length));
            int count = NinoElements.Fits(length, Unsafe.SizeOf<T>(), reader.Remaining)
                ? length
                : Math.Min(length, Math.Max(NinoElements.Clamped(Unsafe.SizeOf<T>(), reader.Remaining), list.Count));
            int done = 0;
            while (true)
            {
                var span = ListAccess.Open(list, count, overwritten: false, out var buffer);
                NinoElements.Read<T, TFormatter>(span.Slice(done), ref reader);
                ListAccess.Close(list, count, buffer);
                if (count == length)
                    return reader;
                done = count;
                count = (int)Math.Min(length, 2L * count);
            }
        }

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

            // As HashSetFormatter's: an empty collection skips the call, and elements with references go through the
            // writer parameter.
            if (count == 0)
            {
                writer.WriteLength(0);
                return;
            }

            if (NinoElements.WritesOnLocal<T>())
                writer = WriteEnumerated<T, TFormatter>(values, count, writer);
            else
                WriteEnumerated<T, TFormatter>(values, count, ref writer);
        }

        // The writer by value and returned, a local whose cursor stays in registers across the enumerator's calls (as
        // HashSetFormatter's).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Writer WriteEnumerated<T, TFormatter>(IEnumerable<T> values, int count, Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            WriteEnumerated<T, TFormatter>(values, count, ref writer);
            return writer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteEnumerated<T, TFormatter>(IEnumerable<T> values, int count, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            int countPos = writer.ReserveInt32();
            int written = 0;
            foreach (var item in values)
            {
                default(TFormatter).Serialize(item, ref writer);
                written++;
            }

            if (count >= 0 && count != written)
                NinoThrow.ModifiedDuringWrite();
            writer.PatchInt32(countPos, written);
        }
    }
}
