using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

        /// <summary>A primitive (or opaque leaf such as <c>decimal</c>) field.</summary>
        private struct Leaf
        {
            public int Offset;
            public int Size;
        }

        internal sealed class Info
        {
            /// <summary>Whether the memory layout equals the canonical layout (raw copies are valid).</summary>
            public bool Canonical = true;

            /// <summary>Canonical (wire) size; 0 when unknown.</summary>
            public int Size;

            /// <summary>Canonical byte mask: 0xFF for field bytes, 0 for padding. Null without padding.</summary>
            public byte[]? Mask;

            /// <summary>When not canonical: (memory offset, wire offset, size) runs.</summary>
            public int[]? Map;
        }

        /// <summary>Layout info for <paramref name="type"/>; <paramref name="runtimeSize"/> validates the model.</summary>
        public static Info Get(Type type, int runtimeSize) =>
            runtimeSize < 0 ? Create(type, runtimeSize) : Cache.GetOrAdd(type, t => Create(t, runtimeSize));

        private static Info Create(Type type, int runtimeSize)
        {
            try
            {
                return Compute(type, runtimeSize);
            }
            catch
            {
                // Reflection limits (AOT stripping): assume the memory layout is the wire layout.
                return new Info { Size = runtimeSize };
            }
        }

        private static Info Compute(Type type, int runtimeSize) =>
            Compute(type, runtimeSize, LongAlign, DecimalAlign);

        /// <summary>Layout info as a runtime with the given alignments would see it (exposed for tests).</summary>
        internal static Info Compute(Type type, int runtimeSize, int longAlign, int decimalAlign)
        {
            var wire = new List<Leaf>();
            var memory = new List<Leaf>();
            if (!Measure(type, 8, 8, 0, wire, out int wireSize, out _) ||
                !Measure(type, longAlign, decimalAlign, 0, memory, out int memorySize, out _) ||
                (runtimeSize >= 0 && memorySize != runtimeSize) || wire.Count != memory.Count)
                return new Info { Size = runtimeSize };

            var info = new Info { Size = wireSize, Canonical = wireSize == memorySize };
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

        /// <summary>
        /// Lays out <paramref name="type"/> at <paramref name="baseOffset"/> with the given alignments of 8-byte
        /// primitives and of <c>decimal</c>, collecting its leaves in field order.
        /// </summary>
        private static bool Measure(Type type, int longAlign, int decimalAlign, int baseOffset, List<Leaf> leaves,
            out int size, out int align)
        {
            if (type.IsEnum)
                type = Enum.GetUnderlyingType(type);
            size = LeafSize(type, longAlign, decimalAlign, out align);
            if (size > 0)
            {
                leaves.Add(new Leaf { Offset = baseOffset, Size = size });
                return true;
            }

            if (!type.IsValueType || type.IsPrimitive || type.IsPointer)
                return false;
            var layout = type.StructLayoutAttribute;
            var kind = layout?.Value ?? LayoutKind.Sequential;
            int pack = layout != null && layout.Pack > 0 ? layout.Pack : int.MaxValue;
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (kind == LayoutKind.Auto && fields.Length > 1)
                return false;

            int end = 0;
            align = 1;
            foreach (var field in fields)
            {
                int start = leaves.Count;
                if (!Measure(field.FieldType, longAlign, decimalAlign, 0, leaves, out int fieldSize,
                        out int fieldAlign))
                    return false;
                fieldAlign = Math.Min(fieldAlign, pack);
                int offset = kind == LayoutKind.Explicit
                    ? (int)Marshal.OffsetOf(type, field.Name)
                    : (end + fieldAlign - 1) / fieldAlign * fieldAlign;
                for (int i = start; i < leaves.Count; i++)
                {
                    var leaf = leaves[i];
                    leaf.Offset += baseOffset + offset;
                    leaves[i] = leaf;
                }

                end = Math.Max(end, offset + fieldSize);
                align = Math.Max(align, fieldAlign);
            }

            size = (end + align - 1) / align * align;
            if (layout != null && layout.Size > size)
                size = layout.Size;
            return size > 0;
        }

        private static int LeafSize(Type type, int longAlign, int decimalAlign, out int align)
        {
            int size;
            if (type == typeof(decimal))
            {
                align = decimalAlign;
                return 16;
            }

            if (type == typeof(DateTime) || type == typeof(TimeSpan))
            {
                align = longAlign;
                return 8;
            }

            if (type == typeof(Guid))
            {
                align = 4;
                return 16;
            }

            if (type.IsPointer || type == typeof(IntPtr) || type == typeof(UIntPtr))
            {
                size = IntPtr.Size;
            }
            else
            {
                switch (Type.GetTypeCode(type))
                {
                    case TypeCode.Boolean:
                    case TypeCode.Byte:
                    case TypeCode.SByte:
                        size = 1;
                        break;
                    case TypeCode.Char:
                    case TypeCode.Int16:
                    case TypeCode.UInt16:
                        size = 2;
                        break;
                    case TypeCode.Int32:
                    case TypeCode.UInt32:
                    case TypeCode.Single:
                        size = 4;
                        break;
                    case TypeCode.Int64:
                    case TypeCode.UInt64:
                    case TypeCode.Double:
                        align = longAlign;
                        return 8;
                    default:
                        align = 0;
                        return 0;
                }
            }

            align = size;
            return size;
        }
    }

    /// <summary>
    /// Canonical layout of <typeparamref name="T"/> (see <see cref="NinoLayout"/>). The flags are JIT-time constants:
    /// on runtimes where the layout is canonical and the type has no padding, raw writes cost nothing extra.
    /// </summary>
    internal static class NinoLayout<T>
    {
        private static readonly NinoLayout.Info Info = NinoUnsafe.IsReferenceOrContainsReferences<T>()
            ? new NinoLayout.Info { Size = 0 }
            : NinoLayout.Get(typeof(T), Unsafe.SizeOf<T>());

        /// <summary>Whether values can be copied as raw memory on this runtime.</summary>
        public static readonly bool IsCanonical = Info.Canonical;

        /// <summary>Whether the canonical layout has padding bytes (cleared on write).</summary>
        public static readonly bool HasPadding = Info.Mask != null;

        /// <summary>Size of a value on the wire.</summary>
        public static readonly int WireSize = Info.Size;

        // The mask repeated over lcm(size, 8) bytes, as 8-byte words.
        private static readonly ulong[]? Words = BuildWords(Info.Mask);

        // The mask repeated over lcm(size, vector size) bytes, when SIMD is hardware accelerated.
        private static readonly Vector<byte>[]? Vectors = BuildVectors(Info.Mask);

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
            if (HasPadding)
                ClearSlow(ref destination, count);
        }

        /// <summary>
        /// Copies <paramref name="count"/> canonical values to the wire, padding cleared in the same pass (masking
        /// after a block copy would reload freshly stored bytes and stall on store forwarding).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyToWire(ref byte destination, ref byte source, int count)
        {
            if (HasPadding)
                CopyMasked(ref destination, ref source, count);
            else
                Unsafe.CopyBlockUnaligned(ref destination, ref source, (uint)checked(count * Unsafe.SizeOf<T>()));
        }

        /// <summary>Writes <paramref name="value"/> in its wire layout (<see cref="Size"/> bytes).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write(ref byte destination, T value)
        {
            if (IsCanonical)
            {
                NinoUnsafe.Write(ref destination, value);
                ClearPadding(ref destination);
            }
            else
            {
                NinoLayout.Convert(Info, ref Unsafe.As<T, byte>(ref value), ref destination, true);
            }
        }

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

        private static void CopyMasked(ref byte destination, ref byte source, int count)
        {
            var mask = Info.Mask!;
            int total = checked(count * mask.Length);
            int i = 0;
            if (Vectors != null)
            {
                var vectors = Vectors;
                int width = Vector<byte>.Count;
                int vectorPeriod = vectors.Length * width;
                ref Vector<byte> v = ref vectors[0];
                if (vectors.Length == 1)
                {
                    // The common case (sizes dividing the vector width): one mask register.
                    var m = v;
                    for (; i + width <= total; i += width)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref destination, i),
                            Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref source, i)) & m);
                }

                for (; i + vectorPeriod <= total; i += vectorPeriod)
                {
                    ref byte d = ref Unsafe.Add(ref destination, i);
                    ref byte s = ref Unsafe.Add(ref source, i);
                    for (int j = 0; j < vectors.Length; j++)
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, j * width),
                            Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref s, j * width)) & Unsafe.Add(ref v, j));
                }
            }

            var words = Words!;
            int period = words.Length * 8;
            ref ulong word = ref words[0];
            for (; i + period <= total; i += period)
            {
                ref byte d = ref Unsafe.Add(ref destination, i);
                ref byte s = ref Unsafe.Add(ref source, i);
                for (int j = 0; j < words.Length; j++)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, j * 8),
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, j * 8)) & Unsafe.Add(ref word, j));
            }

            for (; i < total; i++)
                Unsafe.Add(ref destination, i) = (byte)(Unsafe.Add(ref source, i) & mask[i % mask.Length]);
        }

        private static void ClearSlow(ref byte destination, int count)
        {
            var mask = Info.Mask!;
            var words = Words!;
            int total = checked(count * mask.Length);
            int period = words.Length * 8;
            ref ulong word = ref words[0];
            int i = 0;
            // Whole periods: the mask words line up with the data.
            for (; i + period <= total; i += period)
            {
                ref byte b = ref Unsafe.Add(ref destination, i);
                for (int j = 0; j < words.Length; j++)
                {
                    ref byte p = ref Unsafe.Add(ref b, j * 8);
                    Unsafe.WriteUnaligned(ref p, Unsafe.ReadUnaligned<ulong>(ref p) & Unsafe.Add(ref word, j));
                }
            }

            for (; i < total; i++)
                Unsafe.Add(ref destination, i) &= mask[i % mask.Length];
        }

        private static Vector<byte>[]? BuildVectors(byte[]? mask)
        {
            if (mask == null || !Vector.IsHardwareAccelerated)
                return null;
            int width = Vector<byte>.Count;
            int period = mask.Length;
            while (period % width != 0)
                period += mask.Length;
            var bytes = new byte[period];
            for (int i = 0; i < period; i++)
                bytes[i] = mask[i % mask.Length];
            var vectors = new Vector<byte>[period / width];
            for (int i = 0; i < vectors.Length; i++)
                vectors[i] = new Vector<byte>(bytes, i * width);
            return vectors;
        }

        private static ulong[]? BuildWords(byte[]? mask)
        {
            if (mask == null)
                return null;
            int period = mask.Length;
            while (period % 8 != 0)
                period += mask.Length;
            var bytes = new byte[period];
            for (int i = 0; i < period; i++)
                bytes[i] = mask[i % mask.Length];
            var words = new ulong[period / 8];
            for (int i = 0; i < words.Length; i++)
                words[i] = Unsafe.ReadUnaligned<ulong>(ref bytes[i * 8]);
            return words;
        }
    }
}
