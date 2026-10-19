#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Layout of raw-copied unmanaged types. The wire format of such a type is its <b>canonical</b> layout: the one
    /// CoreCLR uses on 64-bit (every primitive aligned to its size, <c>decimal</c> aligned to 8), padding zeroed.
    /// Runtimes that lay the type out differently (x86 aligns 8-byte primitives to 4; Mono and IL2CPP align
    /// <c>decimal</c> to 4) are detected once, and their values are converted field by field instead of copied.
    /// </summary>
    internal static class NinoLayout
    {
        private struct LongProbe
        {
#pragma warning disable CS0649
            public byte A;
            public long B;
#pragma warning restore CS0649
        }

        private struct DecimalProbe
        {
#pragma warning disable CS0649
            public byte A;
            public decimal B;
#pragma warning restore CS0649
        }

        /// <summary>Alignment of 8-byte primitives inside structs on this runtime.</summary>
        private static readonly int LongAlign = Unsafe.SizeOf<LongProbe>() - 8;

        /// <summary>Alignment of <c>decimal</c> inside structs on this runtime.</summary>
        private static readonly int DecimalAlign = Unsafe.SizeOf<DecimalProbe>() - 16;

        private static readonly ConcurrentDictionary<Type, Info> Cache = new ConcurrentDictionary<Type, Info>();

        internal sealed class Info
        {
            /// <summary>Whether the memory layout equals the canonical layout (raw copies are valid).</summary>
            public bool Canonical = true;

            /// <summary>Canonical (wire) size; 0 when unknown.</summary>
            public int Size;

            /// <summary>Canonical alignment; 0 when unknown.</summary>
            public int Align;

            /// <summary>Canonical byte mask: 0xFF for field bytes, 0 for padding. Null without padding.</summary>
            public byte[]? Mask;

            /// <summary>When not canonical: (memory offset, wire offset, size) runs.</summary>
            public int[]? Map;
        }

        /// <summary>Layout info for <paramref name="type"/>; <paramref name="runtimeSize"/> validates the model.</summary>
        public static Info Get(Type type, int runtimeSize) =>
            runtimeSize < 0 ? Create(type, runtimeSize) : Cache.GetOrAdd(type, t => Create(t, runtimeSize));

        /// <summary>Forgets the layouts of types of <paramref name="assembly"/> (<see cref="NinoRegistry.Remove"/>).</summary>
        internal static void Remove(Assembly assembly) => NinoUnload.RemoveFrom(Cache, assembly);

        private static Info Create(Type type, int runtimeSize)
        {
            try
            {
                return Compute(type, runtimeSize, LongAlign, DecimalAlign);
            }
            catch
            {
                // Reflection limits (AOT stripping): assume the memory layout is the wire layout.
                return new Info { Size = runtimeSize };
            }
        }

        /// <summary>Layout info as a runtime with the given alignments would see it (exposed for tests).</summary>
        internal static Info Compute(Type type, int runtimeSize, int longAlign, int decimalAlign)
        {
            var wire = new List<NinoLayoutCore.Leaf>();
            var memory = new List<NinoLayoutCore.Leaf>();
            if (!NinoLayoutCore.Measure<Type, RuntimeView>(default, type, 8, 8, IntPtr.Size, 0, wire,
                    out int wireSize, out int wireAlign) ||
                !NinoLayoutCore.Measure<Type, RuntimeView>(default, type, longAlign, decimalAlign, IntPtr.Size, 0,
                    memory, out int memorySize, out _) ||
                (runtimeSize >= 0 && memorySize != runtimeSize) || wire.Count != memory.Count)
                return new Info { Size = runtimeSize };

            var info = new Info { Size = wireSize, Align = wireAlign, Canonical = wireSize == memorySize };
            for (int i = 0; i < wire.Count && info.Canonical; i++)
                info.Canonical = wire[i].Offset == memory[i].Offset;

            var mask = new byte[wireSize];
            foreach (var leaf in wire)
                for (int i = 0; i < leaf.Size; i++)
                    mask[leaf.Offset + i] = 0xFF;
            if (Array.IndexOf(mask, (byte)0) >= 0)
                info.Mask = mask;

            if (!info.Canonical)
            {
                // Merge leaves that are contiguous in both layouts into runs.
                var runs = new List<int>();
                for (int i = 0; i < wire.Count; i++)
                {
                    int n = runs.Count;
                    if (n != 0 && runs[n - 3] + runs[n - 1] == memory[i].Offset &&
                        runs[n - 2] + runs[n - 1] == wire[i].Offset)
                    {
                        runs[n - 1] += wire[i].Size;
                        continue;
                    }

                    runs.Add(memory[i].Offset);
                    runs.Add(wire[i].Offset);
                    runs.Add(wire[i].Size);
                }

                info.Map = runs.ToArray();
            }

            return info;
        }

        /// <summary>Copies a value between its memory layout and its wire layout using <see cref="Info.Map"/>.</summary>
        internal static void Convert(Info info, ref byte memory, ref byte wire, bool toWire)
        {
            var map = info.Map!;
            if (toWire)
                Unsafe.InitBlockUnaligned(ref wire, 0, (uint)info.Size);
            for (int i = 0; i < map.Length; i += 3)
            {
                ref byte m = ref Unsafe.Add(ref memory, map[i]);
                ref byte w = ref Unsafe.Add(ref wire, map[i + 1]);
                if (toWire)
                    Unsafe.CopyBlockUnaligned(ref w, ref m, (uint)map[i + 2]);
                else
                    Unsafe.CopyBlockUnaligned(ref m, ref w, (uint)map[i + 2]);
            }
        }

        /// <summary>Why reading the instance fields of an unmanaged struct is safe after trimming and under NativeAOT.</summary>
        internal const string FieldsJustification =
            "Reads the instance fields of a struct the program uses. ILLink keeps every instance field of a struct " +
            "with sequential or explicit layout (the fields make up its size) and removes fields only from " +
            "auto-layout structs, storage included, so reflection describes the runtime layout. NativeAOT keeps " +
            "field metadata only when asked: generated code asks ([DynamicDependency] in the registry) for every " +
            "struct it names. For another struct the fields read as missing, the measured size does not match " +
            "Unsafe.SizeOf<T>() and the value is copied as it lies in memory, as on any reflection failure here.";

        /// <summary>The runtime's view of a type for <see cref="NinoLayoutCore"/>.</summary>
        private struct RuntimeView : INinoLayoutView<Type>
        {
            public NinoLeaf Leaf(Type type)
            {
                if (type.IsEnum)
                    type = Enum.GetUnderlyingType(type);
                if (type == typeof(decimal))
                    return NinoLeaf.Decimal;
                if (type == typeof(DateTime) || type == typeof(TimeSpan))
                    return NinoLeaf.Size8;
                if (type == typeof(Guid))
                    return NinoLeaf.Guid;
                if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
                    return NinoLeaf.Pointer;
                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.Boolean:
                    case TypeCode.Byte:
                    case TypeCode.SByte:
                        return NinoLeaf.Size1;
                    case TypeCode.Char:
                    case TypeCode.Int16:
                    case TypeCode.UInt16:
                        return NinoLeaf.Size2;
                    case TypeCode.Int32:
                    case TypeCode.UInt32:
                    case TypeCode.Single:
                        return NinoLeaf.Size4;
                    case TypeCode.Int64:
                    case TypeCode.UInt64:
                    case TypeCode.Double:
                        return NinoLeaf.Size8;
                }

                return type.IsValueType && !type.IsPrimitive ? NinoLeaf.None : NinoLeaf.Unsupported;
            }

            [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
            public bool Struct(Type type, List<KeyValuePair<Type, int>> fields, out LayoutKind kind, out int pack,
                out int minSize)
            {
                var layout = type.StructLayoutAttribute;
                kind = layout?.Value ?? LayoutKind.Sequential;
                pack = layout != null && layout.Pack > 0 ? layout.Pack : 0;
                minSize = layout?.Size ?? 0;
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    fields.Add(new KeyValuePair<Type, int>(field.FieldType,
                        kind == LayoutKind.Explicit ? (int)Marshal.OffsetOf(type, field.Name) : -1));
                return true;
            }
        }
    }

    /// <summary>
    /// A byte mask (0xFF keeps a byte, 0 clears it) applied while copying values to the wire, in one pass:
    /// SIMD when hardware accelerated, 8-byte words otherwise.
    /// </summary>
    internal sealed class NinoMask
    {
        private readonly byte[] _mask;
        private readonly ulong[] _words; // the mask repeated over lcm(length, 8) bytes
        // The mask repeated over lcm(length, vector width) bytes plus one vector more, so the mask of any offset
        // is a single unaligned load.
        private readonly byte[]? _vectorMask;
        private readonly int _vectorPeriod;
#if NET8_0_OR_GREATER
        // The same for 64-byte vectors, where they are accelerated (see CopyVectors512).
        private readonly byte[]? _mask512;
        private readonly int _period512;
#endif
        private readonly int[] _gaps; // (offset, length) of each run of cleared bytes in one element

        /// <summary>(offset, length) of each run of cleared bytes in one element.</summary>
        public int[] Gaps => _gaps;

        /// <summary>Returns a mask, or null when <paramref name="mask"/> keeps every byte.</summary>
        public static NinoMask? Create(byte[] mask) =>
            Array.IndexOf(mask, (byte)0) < 0 ? null : new NinoMask(mask);

        private NinoMask(byte[] mask)
        {
            _mask = mask;
            var gaps = new System.Collections.Generic.List<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0)
                    continue;
                int start = i;
                while (i + 1 < mask.Length && mask[i + 1] == 0)
                    i++;
                gaps.Add(start);
                gaps.Add(i + 1 - start);
            }

            _gaps = gaps.ToArray();
            var words = Repeat(mask, 8, 0, out _);
            _words = new ulong[words.Length / 8];
            for (int i = 0; i < _words.Length; i++)
                _words[i] = Unsafe.ReadUnaligned<ulong>(ref words[i * 8]);
            if (Vector.IsHardwareAccelerated)
                _vectorMask = Repeat(mask, Vector<byte>.Count, Vector<byte>.Count, out _vectorPeriod);
#if NET8_0_OR_GREATER
            if (Vector512.IsHardwareAccelerated)
                _mask512 = Repeat(mask, Vector512<byte>.Count, Vector512<byte>.Count, out _period512);
#endif
        }

        /// <summary>
        /// <paramref name="mask"/> repeated over its <paramref name="period"/>, the least multiple of its length that
        /// is a multiple of <paramref name="multiple"/>, and <paramref name="extra"/> bytes more.
        /// </summary>
        private static byte[] Repeat(byte[] mask, int multiple, int extra, out int period)
        {
            period = mask.Length;
            while (period % multiple != 0)
                period += mask.Length;
            var bytes = new byte[period + extra];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = mask[i % mask.Length];
            return bytes;
        }

        /// <summary>Copies <paramref name="count"/> masked elements (the mask's length each).</summary>
        public void Copy(ref byte destination, ref byte source, int count)
        {
            int total = checked(count * _mask.Length);
#if NET8_0_OR_GREATER
            if (_mask512 != null && total >= Vector512<byte>.Count)
            {
                CopyVectors512(ref destination, ref source, total);
                return;
            }
#endif
            if (_vectorMask != null && total >= Vector<byte>.Count)
            {
                CopyVectors(ref destination, ref source, total);
                return;
            }

            int i = 0;
            int period = _words.Length * 8;
            ref ulong word = ref _words[0];
            for (; i + period <= total; i += period)
            {
                ref byte d = ref Unsafe.Add(ref destination, i);
                ref byte s = ref Unsafe.Add(ref source, i);
                for (int j = 0; j < _words.Length; j++)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, j * 8),
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, j * 8)) & Unsafe.Add(ref word, j));
            }

            for (; i < total; i++)
                Unsafe.Add(ref destination, i) = (byte)(Unsafe.Add(ref source, i) & _mask[i % _mask.Length]);
        }

        /// <summary>
        /// Vector copy with aligned stores: the destination usually follows a 4-byte length, and unaligned vector
        /// stores straddle cache lines every other store. The first and last vectors are unaligned and overlap
        /// the aligned body; the mask of each vector is loaded at its offset.
        /// </summary>
#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private unsafe void CopyVectors(ref byte destination, ref byte source, int total)
        {
            int width = Vector<byte>.Count;
            int period = _vectorPeriod;
            ref byte mask = ref _vectorMask![0];
            Unsafe.WriteUnaligned(ref destination,
                Unsafe.ReadUnaligned<Vector<byte>>(ref source) & Unsafe.ReadUnaligned<Vector<byte>>(ref mask));

            int i = width - (int)((nuint)Unsafe.AsPointer(ref destination) & (nuint)(width - 1));
            int end = total - width;
            int m = i; // i % period (i <= width <= period)
            if (period == width)
            {
                var v = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref mask, m));
                for (; i <= end - 3 * width; i += 4 * width)
                {
                    ref byte d = ref Unsafe.Add(ref destination, i);
                    ref byte s = ref Unsafe.Add(ref source, i);
                    var a = Unsafe.ReadUnaligned<Vector<byte>>(ref s) & v;
                    var b = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref s, width)) & v;
                    var c = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref s, 2 * width)) & v;
                    var e = Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref s, 3 * width)) & v;
                    Unsafe.WriteUnaligned(ref d, a);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, width), b);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 2 * width), c);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, 3 * width), e);
                }

                for (; i <= end; i += width)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                        Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref source, i)) & v);
            }
            else
            {
                if (m >= period)
                    m -= period;
                for (; i <= end; i += width)
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                        Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref source, i)) &
                        Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref mask, m)));
                    m += width;
                    if (m >= period)
                        m -= period;
                }
            }

            Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, end),
                Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref source, end)) &
                Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref mask, end % period)));
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// <see cref="CopyVectors"/> with 64-byte vectors, where the runtime reports them accelerated (not on CPUs
        /// that slow down for them). Measured on an AVX-512 Xeon, structs of 8 bytes with a padding byte, source
        /// and destination of different alignments: 8,000 bytes in 75 ns (110 ns with 32-byte vectors, 75 ns for a
        /// plain copy), 1,200 bytes in 14 ns (19.5 ns, and 18.6 ns).
        /// </summary>
        // Fully optimized from the first call: a profile taken on short copies (lists of a few elements) would lay
        // out the unrolled loop as cold code for the long ones.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private unsafe void CopyVectors512(ref byte destination, ref byte source, int total)
        {
            int width = Vector512<byte>.Count;
            int period = _period512;
            ref byte mask = ref _mask512![0];
            (Vector512.LoadUnsafe(ref source) & Vector512.LoadUnsafe(ref mask)).StoreUnsafe(ref destination);

            int i = width - (int)((nuint)Unsafe.AsPointer(ref destination) & (nuint)(width - 1));
            int end = total - width;
            int m = i; // i % period (i <= width <= period)
            if (period == width)
            {
                var v = Vector512.LoadUnsafe(ref mask, (nuint)m);
                for (; i <= end - 3 * width; i += 4 * width)
                {
                    var a = Vector512.LoadUnsafe(ref source, (nuint)i) & v;
                    var b = Vector512.LoadUnsafe(ref source, (nuint)(i + width)) & v;
                    var c = Vector512.LoadUnsafe(ref source, (nuint)(i + 2 * width)) & v;
                    var e = Vector512.LoadUnsafe(ref source, (nuint)(i + 3 * width)) & v;
                    a.StoreUnsafe(ref destination, (nuint)i);
                    b.StoreUnsafe(ref destination, (nuint)(i + width));
                    c.StoreUnsafe(ref destination, (nuint)(i + 2 * width));
                    e.StoreUnsafe(ref destination, (nuint)(i + 3 * width));
                }

                for (; i <= end; i += width)
                    (Vector512.LoadUnsafe(ref source, (nuint)i) & v).StoreUnsafe(ref destination, (nuint)i);
            }
            else
            {
                if (m >= period)
                    m -= period;
                for (; i <= end; i += width)
                {
                    (Vector512.LoadUnsafe(ref source, (nuint)i) & Vector512.LoadUnsafe(ref mask, (nuint)m))
                        .StoreUnsafe(ref destination, (nuint)i);
                    m += width;
                    if (m >= period)
                        m -= period;
                }
            }

            (Vector512.LoadUnsafe(ref source, (nuint)end) & Vector512.LoadUnsafe(ref mask, (nuint)(end % period)))
                .StoreUnsafe(ref destination, (nuint)end);
        }
#endif

        /// <summary>
        /// Zeroes the padding of <paramref name="count"/> elements in place. Only stores: reading back bytes just
        /// written by narrower field stores would stall on store forwarding.
        /// </summary>
        public void Clear(ref byte destination, int count)
        {
            int[] gaps = _gaps;
            for (int e = 0; e < count; e++)
            {
                ref byte element = ref Unsafe.Add(ref destination, e * _mask.Length);
                for (int g = 0; g < gaps.Length; g += 2)
                    Zero(ref Unsafe.Add(ref element, gaps[g]), gaps[g + 1]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Zero(ref byte destination, int length)
        {
            switch (length)
            {
                case 1:
                    destination = 0;
                    return;
                case 2:
                    Unsafe.WriteUnaligned(ref destination, (ushort)0);
                    return;
                case 3:
                    Unsafe.WriteUnaligned(ref destination, (ushort)0);
                    Unsafe.Add(ref destination, 2) = 0;
                    return;
                case 4:
                    Unsafe.WriteUnaligned(ref destination, 0u);
                    return;
                case 5:
                case 6:
                case 7:
                    // Two overlapping stores, both inside the gap.
                    Unsafe.WriteUnaligned(ref destination, 0u);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, length - 4), 0u);
                    return;
                case 8:
                    Unsafe.WriteUnaligned(ref destination, 0ul);
                    return;
                default:
                    Unsafe.InitBlockUnaligned(ref destination, 0, (uint)length);
                    return;
            }
        }
    }

    /// <summary>
    /// Canonical layout of <typeparamref name="T"/> (see <see cref="NinoLayout"/>). The flags are JIT-time constants:
    /// on runtimes where the layout is canonical and the type has no padding, raw writes cost nothing extra.
    /// </summary>
    internal static class NinoLayout<T>
    {
        internal static readonly NinoLayout.Info Info = NinoUnsafe.IsReferenceOrContainsReferences<T>()
            ? new NinoLayout.Info { Size = 0 }
            : NinoLayout.Get(typeof(T), Unsafe.SizeOf<T>());

        /// <summary>Whether values can be copied as raw memory on this runtime.</summary>
        public static readonly bool IsCanonical = Info.Canonical;

        private static readonly NinoMask? Mask = Info.Mask == null ? null : NinoMask.Create(Info.Mask);

        /// <summary>Whether the canonical layout has padding bytes (cleared on write).</summary>
        public static readonly bool HasPadding = Mask != null;

        /// <summary>Size of a value on the wire.</summary>
        public static readonly int WireSize = Info.Size;

        /// <summary>Wire size, as a JIT-time constant on runtimes with the canonical layout.</summary>
        public static int Size
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IsCanonical ? Unsafe.SizeOf<T>() : WireSize;
        }

        /// <summary>Clears the padding of <paramref name="count"/> consecutive wire values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearPadding(ref byte destination, int count = 1)
        {
            if (!HasPadding)
                return;
            // A single value with at most two padding runs (the usual case): constant stores, inlined.
            if (count == 1 && GapCount <= 2)
            {
                NinoMask.Zero(ref Unsafe.Add(ref destination, Gap0Offset), Gap0Length);
                if (GapCount == 2)
                    NinoMask.Zero(ref Unsafe.Add(ref destination, Gap1Offset), Gap1Length);
                return;
            }

            Mask!.Clear(ref destination, count);
        }

        // The first two padding runs, as JIT-time constants for single values.
        private static readonly int GapCount = Mask == null ? 0 : Mask.Gaps.Length / 2;
        private static readonly int Gap0Offset = GapCount > 0 ? Mask!.Gaps[0] : 0;
        private static readonly int Gap0Length = GapCount > 0 ? Mask!.Gaps[1] : 0;
        private static readonly int Gap1Offset = GapCount > 1 ? Mask!.Gaps[2] : 0;
        private static readonly int Gap1Length = GapCount > 1 ? Mask!.Gaps[3] : 0;

        /// <summary>
        /// Copies <paramref name="count"/> canonical values to the wire, padding cleared in the same pass (masking
        /// after a block copy would reload freshly stored bytes and stall on store forwarding).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyToWire(ref byte destination, ref byte source, int count)
        {
            if (HasPadding)
                Mask!.Copy(ref destination, ref source, count);
            else
                NinoUnsafe.Copy(ref destination, ref source, (uint)checked(count * Unsafe.SizeOf<T>()));
        }

        /// <summary>Writes <paramref name="count"/> as an <c>int32</c>, then <see cref="CopyToWire"/>s the values.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteLengthPrefixed(ref byte destination, ref byte source, int count)
        {
            NinoUnsafe.Write(ref destination, count);
            if (count != 0)
                CopyToWire(ref Unsafe.Add(ref destination, NinoWire.LengthSize), ref source, count);
        }

        /// <summary>Writes <paramref name="value"/> in its wire layout (<see cref="Size"/> bytes).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(ref byte destination, in T value)
        {
            if (IsCanonical)
            {
                NinoUnsafe.WriteIn(ref destination, in value);
                ClearPadding(ref destination);
            }
            else
            {
                WriteConverted(ref destination, value);
            }
        }

        // Its own method: taking the parameter's address in Write, even on a branch the JIT folds away, makes every
        // inlined caller pass the value through a stack temporary.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteConverted(ref byte destination, in T value) =>
            NinoLayout.Convert(Info, ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), ref destination, true);

        /// <summary>Reads a value stored in its wire layout.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read(ref byte source)
        {
            if (IsCanonical)
                return NinoUnsafe.Read<T>(ref source);
            T value = default!;
            NinoLayout.Convert(Info, ref Unsafe.As<T, byte>(ref value), ref source, false);
            return value;
        }
    }
}
