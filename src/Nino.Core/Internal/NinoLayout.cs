using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
#endif

namespace Nino.Core.Internal
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

        /// <summary>
        /// Alignment of <c>Int128</c> and <c>UInt128</c> inside structs on this runtime, probed: 16 on .NET 8 and later
        /// on 64-bit, 8 on Arm32 and on .NET 7. The canonical layout aligns them to 16, as CoreCLR does on 64-bit.
        /// </summary>
        internal static readonly int WideAlign = ProbeWideAlign();

#if NET8_0_OR_GREATER
        private struct WideProbe
        {
#pragma warning disable CS0649
            public byte A;
            public Int128 B;
#pragma warning restore CS0649
        }

        private static int ProbeWideAlign() => Unsafe.SizeOf<WideProbe>() - 16;
#else
        // The netstandard builds name no Int128 (it exists from .NET 7): the probe struct is made for the runtime's own.
        private struct WideProbe<T>
        {
#pragma warning disable CS0649
            public byte A;
            public T B;
#pragma warning restore CS0649
        }

        private static int ProbeWideAlign()
        {
            try
            {
                var wide = typeof(object).Assembly.GetType("System.Int128");
                if (wide == null)
                    return 16; // no Int128 on this runtime: nothing is laid out with it
                return (int)typeof(NinoLayout).GetMethod(nameof(WideProbeSize), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(wide).Invoke(null, null)! - 16;
            }
            catch
            {
                return 0; // unknown: a struct with one is not laid out (Measure fails, the runtime size is used)
            }
        }

        private static int WideProbeSize<T>() => Unsafe.SizeOf<WideProbe<T>>();
#endif

        private static readonly ConcurrentDictionary<Type, Info> Cache = NinoUnload.Cache<Info>();

        internal sealed class Info
        {
            /// <summary>Whether the memory layout equals the canonical layout (raw copies are valid).</summary>
            public bool Canonical = true;

            /// <summary>Canonical (wire) size; 0 when unknown.</summary>
            public int Size;

            /// <summary>Canonical alignment; 0 when unknown.</summary>
            public int Align;

            /// <summary>Canonical byte mask: 0xFF for field bytes, 0 for padding. Null without padding.</summary>
            public byte[] Mask;

            /// <summary>When not canonical: (memory offset, wire offset, size) runs.</summary>
            public int[] Map;
        }

        /// <summary>Layout info for <paramref name="type"/>; <paramref name="runtimeSize"/> validates the model.</summary>
        public static Info Get(Type type, int runtimeSize) =>
            runtimeSize < 0 ? Create(type, runtimeSize) : GetCached(type, runtimeSize);

        // The size as the factory's argument where the runtime has that overload: no closure per call.
#if NETSTANDARD2_0
        private static Info GetCached(Type type, int runtimeSize) => Cache.GetOrAdd(type, t => Create(t, runtimeSize));
#else
        private static Info GetCached(Type type, int runtimeSize) => Cache.GetOrAdd(type, Create, runtimeSize);
#endif

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
        internal static Info Compute(Type type, int runtimeSize, int longAlign, int decimalAlign, int wideAlign = 0)
        {
            var wire = new List<NinoLayoutCore.Leaf>();
            var memory = new List<NinoLayoutCore.Leaf>();
            if (wideAlign == 0)
                wideAlign = WideAlign;
            if (!NinoLayoutCore.Measure<Type, RuntimeView>(default, type, 8, 8, IntPtr.Size, 0, wire,
                    out int wireSize, out int wireAlign, null, 16) ||
                !NinoLayoutCore.Measure<Type, RuntimeView>(default, type, longAlign, decimalAlign, IntPtr.Size, 0,
                    memory, out int memorySize, out _, null, wideAlign) ||
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

        /// <summary>
        /// The fields of <paramref name="type"/> (an unmanaged struct, or one of the types below itself) that can
        /// hold bytes no constructor makes, as pairs of their offset in memory on this runtime and kind: <c>bool</c>,
        /// <see cref="DateTime"/>, <c>decimal</c>, <c>DateOnly</c>, <c>TimeOnly</c>, at any depth of nested structs.
        /// Values are checked once read (in memory): where the wire layout is the memory layout, also before. Null for
        /// none, or when the fields cannot be read (then nothing is checked, as nothing is converted: see
        /// <see cref="Create"/>).
        /// </summary>
        internal static NinoValueChecks ValueChecks(Type type)
        {
            try
            {
                var checks = new List<int>();
                return AddChecks(type, 0, checks) && checks.Count != 0 ? new NinoValueChecks(checks.ToArray()) : null;
            }
            catch
            {
                return null;
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        private static bool AddChecks(Type type, int offset, List<int> checks)
        {
            int kind = CheckOf(type);
            if (kind != 0)
            {
                checks.Add(offset);
                checks.Add(kind);
                return true;
            }

            if (type.IsEnum || type.IsPrimitive || type.IsPointer || !type.IsValueType || type == typeof(Guid) ||
                type == typeof(TimeSpan))
                return true;
            // The offsets of its own fields in memory, in the order RuntimeView.Struct lists them.
            var offsets = new List<int>();
            if (!NinoLayoutCore.Measure<Type, RuntimeView>(default, type, LongAlign, DecimalAlign, IntPtr.Size, 0,
                    new List<NinoLayoutCore.Leaf>(), out _, out _, offsets))
                return false;
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fields.Length != offsets.Count)
                return false;
            for (int i = 0; i < fields.Length; i++)
                if (!AddChecks(fields[i].FieldType, offset + offsets[i], checks))
                    return false;
            return true;
        }

        private static int CheckOf(Type type)
        {
            if (type == typeof(bool))
                return NinoValueChecks.Bool;
            if (type == typeof(DateTime))
                return NinoValueChecks.DateTime;
            if (type == typeof(decimal))
                return NinoValueChecks.Decimal;
            // By name: netstandard has neither.
            if (type.FullName == "System.DateOnly" && type.Assembly == typeof(object).Assembly)
                return NinoValueChecks.DateOnly;
            if (type.FullName == "System.TimeOnly" && type.Assembly == typeof(object).Assembly)
                return NinoValueChecks.TimeOnly;
            return 0;
        }

        /// <summary>
        /// Checks the fields <paramref name="checks"/> lists (<see cref="ValueChecks"/>) of <paramref name="count"/>
        /// values of <paramref name="type"/> in memory (or in the data, where it lays them out the same),
        /// <paramref name="stride"/> bytes apart, at <paramref name="first"/>: invalid data throws
        /// <see cref="NinoDataException"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Check(NinoValueChecks checks, ref byte first, int count, int stride, Type type) =>
            checks.Check(ref first, count, stride, type);

        /// <summary>Copies a value between its memory layout and its wire layout using <see cref="Info.Map"/>.</summary>
        internal static void Convert(Info info, ref byte memory, ref byte wire, bool toWire)
        {
            var map = info.Map;
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
                // By name: the netstandard builds name no Int128. Without a known alignment (the probe failed), its
                // two ulongs (8-byte aligned), as before.
                if (WideAlign > 0 && type.Assembly == typeof(object).Assembly &&
                    (type.FullName == "System.Int128" || type.FullName == "System.UInt128"))
                    return NinoLeaf.Wide;
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
    /// SIMD where it is fast (<see cref="NinoUnsafe.VectorMasks"/>), 8-byte words otherwise.
    /// </summary>
    internal sealed class NinoMask
    {
        private readonly byte[] _mask;
        private readonly ulong[] _words; // the mask repeated over lcm(length, 8) bytes
        // The mask repeated over lcm(length, vector width) bytes plus one vector more, so the mask of any offset
        // is a single unaligned load.
        private readonly byte[] _vectorMask;
        private readonly int _vectorPeriod;
#if NET8_0_OR_GREATER
        // The same for 64-byte vectors, where they are accelerated (see CopyVectors).
        private readonly byte[] _mask512;
        private readonly int _period512;
#endif
        private readonly int[] _gaps; // (offset, length) of each run of cleared bytes in one element
#if !NET8_0_OR_GREATER
        // For the registered kernels (NinoKernels.Permute): each byte from itself, padding cleared (0xFF). Zero for
        // elements above 64 bytes.
        private readonly IntPtr _kernelTable;
#endif

        /// <summary>(offset, length) of each run of cleared bytes in one element.</summary>
        public int[] Gaps => _gaps;

        /// <summary>Returns a mask, or null when <paramref name="mask"/> keeps every byte.</summary>
        public static NinoMask Create(byte[] mask) =>
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
#if !NET8_0_OR_GREATER
            if (mask.Length <= 64)
            {
                _kernelTable = NinoKernelHook.Table(mask.Length);
                unsafe
                {
                    for (int j = 0; j < mask.Length; j++)
                        if (mask[j] != 0)
                            ((byte*)_kernelTable)[j] = (byte)j;
                }
            }
#endif
            var words = Repeat(mask, 8, 0, out _);
            _words = new ulong[words.Length / 8];
            for (int i = 0; i < _words.Length; i++)
                _words[i] = Unsafe.ReadUnaligned<ulong>(ref words[i * 8]);
            if (NinoUnsafe.VectorMasks)
                _vectorMask = Repeat(mask, Vector<byte>.Count, Vector<byte>.Count, out _vectorPeriod);
#if NET8_0_OR_GREATER
            // Not on Ice Lake (NinoUnsafe.Wide512): GitHub's Xeon Platinum 8370C wrote 1,000 8-byte structs with a
            // padding byte in 116 ns with 64-byte vectors, 83 with 32-byte ones (MemoryPack's copy 88).
            if (Vector512.IsHardwareAccelerated && NinoUnsafe.Wide512)
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
        // Out of line: one call per collection, around loops for any count. Inlined (the JIT did, on its profile
        // data), its loops took the inlining budget of the generated body writing the collection, which then left
        // helpers such as Writer.SetBuffer as calls.
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Copy(ref byte destination, ref byte source, int count)
        {
            int total = checked(count * _mask.Length);
#if !NET8_0_OR_GREATER
            // The netstandard builds (Unity, .NET Framework): the registered kernels, for runs long enough.
            if (count >= NinoKernelHook.MinCopies && _kernelTable != IntPtr.Zero &&
                NinoKernelHook.TryPermute(ref source, _mask.Length, ref destination, _mask.Length, _kernelTable, count))
                return;
#endif
#if NET8_0_OR_GREATER
            if (_mask512 != null && total >= Vector512<byte>.Count)
            {
                CopyVectors<Vector512<byte>, Vectors512>(ref destination, ref source, total, _mask512, _period512);
                return;
            }
#endif
            if (_vectorMask != null && total >= Vector<byte>.Count)
            {
                CopyVectors<Vector<byte>, Vectors>(ref destination, ref source, total, _vectorMask, _vectorPeriod);
                return;
            }

            int i = 0;
            int words = _words.Length;
            int period = words * 8;
            ref ulong word = ref NinoUnsafe.GetArrayDataReference(_words);
            for (; i + period <= total; i += period)
            {
                ref byte d = ref Unsafe.Add(ref destination, i);
                ref byte s = ref Unsafe.Add(ref source, i);
                for (int j = 0; j < words; j++)
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref d, j * 8),
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, j * 8)) & Unsafe.Add(ref word, j));
            }

            // The rest is shorter than a period, which starts with the first element: its mask is the words' first
            // bytes, with no division and no bounds checks.
            ref byte mask = ref Unsafe.As<ulong, byte>(ref word);
            for (int k = 0; i < total; i++, k++)
                Unsafe.Add(ref destination, i) = (byte)(Unsafe.Add(ref source, i) & Unsafe.Add(ref mask, k));
        }

        /// <summary>
        /// Vector copy with aligned stores (aligned loads on Arm64, <see cref="AlignedSide"/>): the destination usually
        /// follows a 4-byte length, and unaligned vector stores straddle cache lines every other store. The first and
        /// last vectors are unaligned and overlap the aligned body; the mask of each vector is loaded at its offset
        /// in <paramref name="masks"/>, the mask repeated over <paramref name="repeat"/> bytes and one vector more.
        /// Offsets are native-sized (nuint): int offsets took a sign extension and an address add per vector, measured
        /// on an AVX-512 Xeon as 8,000 bytes in 74 ns instead of 66 (64-byte vectors). <typeparamref name="TVector"/>
        /// is <see cref="Vector{T}"/> of bytes, or a 64-byte vector where those are accelerated (not on CPUs that slow
        /// down for them): measured on an AVX-512 Xeon, structs of 8 bytes with a padding byte, source and destination
        /// of different alignments, 8,000 bytes in 66 ns (97 ns with 32-byte vectors, 57 ns for a plain copy, 75 ns
        /// Memmove), 1,200 bytes in 14 ns (19.5 ns, and 18.6 ns).
        /// </summary>
        // Fully optimized from the first call: a profile taken on short copies (lists of a few elements) would lay
        // out the unrolled loop as cold code for the long ones.
#if NET8_0_OR_GREATER
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        private static unsafe void CopyVectors<TVector, TOps>(ref byte destination, ref byte source, int total,
            byte[] masks, int repeat)
            where TVector : struct
            where TOps : struct, IMaskVectors<TVector>
        {
            var ops = default(TOps);
            nuint width = (nuint)Unsafe.SizeOf<TVector>();
            nuint period = (nuint)repeat;
            ref byte mask = ref NinoUnsafe.GetArrayDataReference(masks);
            ops.Store(ops.And(ops.Load(ref source, 0), ops.Load(ref mask, 0)), ref destination, 0);

            nuint i = width - ((nuint)Unsafe.AsPointer(ref AlignedSide(ref destination, ref source)) & (width - 1));
            nuint end = (nuint)total - width;
            nuint m = i; // i % period (i <= width <= period)
            if (period == width)
            {
                var v = ops.Load(ref mask, m);
#if NET8_0_OR_GREATER
                if (AdvSimd.Arm64.IsSupported && Unsafe.SizeOf<TVector>() == 16)
                    i = CopyPairs(ref destination, ref source, i, end, ops.Lower(v));
#endif
                for (; i + 3 * width <= end; i += 4 * width)
                {
                    var a = ops.And(ops.Load(ref source, i), v);
                    var b = ops.And(ops.Load(ref source, i + width), v);
                    var c = ops.And(ops.Load(ref source, i + 2 * width), v);
                    var e = ops.And(ops.Load(ref source, i + 3 * width), v);
                    ops.Store(a, ref destination, i);
                    ops.Store(b, ref destination, i + width);
                    ops.Store(c, ref destination, i + 2 * width);
                    ops.Store(e, ref destination, i + 3 * width);
                }

                for (; i <= end; i += width)
                    ops.Store(ops.And(ops.Load(ref source, i), v), ref destination, i);
            }
            else
            {
                if (m >= period)
                    m -= period;
                for (; i <= end; i += width)
                {
                    ops.Store(ops.And(ops.Load(ref source, i), ops.Load(ref mask, m)), ref destination, i);
                    m += width;
                    if (m >= period)
                        m -= period;
                }
            }

            ops.Store(ops.And(ops.Load(ref source, end), ops.Load(ref mask, TailOffset(m, i, end, period))),
                ref destination, end);
        }

        /// <summary>
        /// The side <see cref="CopyVectors{TVector, TOps}"/> aligns: the destination (unaligned vector stores straddle cache lines),
        /// but the source on Arm64, where aligned loads beat aligned stores: on GitHub's Arm64 runners (Neoverse N2, .NET
        /// 10) a 16-byte vector copy took 0.76 to 1.00 of memcpy's time for 8,000 bytes with its loads aligned, and up to
        /// 1.31 times with its stores aligned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref byte AlignedSide(ref byte destination, ref byte source)
        {
#if NET8_0_OR_GREATER
            if (AdvSimd.Arm64.IsSupported)
                return ref source;
#endif
            return ref destination;
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// The body of <see cref="CopyVectors{TVector, TOps}"/> on Arm64, 64 bytes at a time as two load pairs and two store pairs
        /// (ldp/stp of q registers, as the C library's memcpy copies): from <paramref name="i"/> while a whole 64
        /// bytes fits before <paramref name="end"/> + 16; returns where it stopped.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe nuint CopyPairs(ref byte destination, ref byte source, nuint i, nuint end,
            Vector128<byte> v)
        {
            fixed (byte* d = &destination, s = &source)
            {
                for (; i + 48 <= end; i += 64)
                {
                    var (a, b) = AdvSimd.Arm64.LoadPairVector128(s + i);
                    var (c, e) = AdvSimd.Arm64.LoadPairVector128(s + i + 32);
                    AdvSimd.Arm64.StorePair(d + i, a & v, b & v);
                    AdvSimd.Arm64.StorePair(d + i + 32, c & v, e & v);
                }
            }

            return i;
        }
#endif

        /// <summary>
        /// The vector operations of <see cref="CopyVectors{TVector, TOps}"/> on <typeparamref name="TVector"/>: a
        /// struct, so each call is direct and inlined.
        /// </summary>
        private interface IMaskVectors<TVector> where TVector : struct
        {
            /// <summary>The vector at <paramref name="offset"/> bytes from <paramref name="at"/>, unaligned.</summary>
            TVector Load(ref byte at, nuint offset);

            /// <summary>Stores <paramref name="value"/> at <paramref name="offset"/> bytes from <paramref name="at"/>, unaligned.</summary>
            void Store(TVector value, ref byte at, nuint offset);

            TVector And(TVector left, TVector right);
#if NET8_0_OR_GREATER

            /// <summary>The first 16 bytes (<see cref="CopyPairs"/>, for a 16-byte vector).</summary>
            Vector128<byte> Lower(TVector value);
#endif
        }

        /// <summary><see cref="Vector{T}"/> of bytes, on every runtime where it is hardware accelerated.</summary>
        private readonly struct Vectors : IMaskVectors<Vector<byte>>
        {
            // Plain ldobj / stobj (Unsafe.As), not Unsafe.ReadUnaligned / WriteUnaligned: classic Mono compiles their
            // unaligned. prefix on a 16-byte struct to a call to memcpy through a stack slot (mini_emit_memory_load in
            // mono/mini/memory-access.c), and plain ones to movups. RyuJIT never assumes a vector aligned: CopyVectors'
            // machine code on .NET 10 is the same with either form (netstandard2.0 and net10.0 builds).
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector<byte> Load(ref byte at, nuint offset) =>
                Unsafe.As<byte, Vector<byte>>(ref Unsafe.Add(ref at, offset));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Store(Vector<byte> value, ref byte at, nuint offset) =>
                Unsafe.As<byte, Vector<byte>>(ref Unsafe.Add(ref at, offset)) = value;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector<byte> And(Vector<byte> left, Vector<byte> right) => left & right;
#if NET8_0_OR_GREATER

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Lower(Vector<byte> value) => value.AsVector128();
#endif
        }
#if NET8_0_OR_GREATER

        /// <summary>64-byte vectors, where the runtime reports them accelerated.</summary>
        private readonly struct Vectors512 : IMaskVectors<Vector512<byte>>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector512<byte> Load(ref byte at, nuint offset) => Vector512.LoadUnsafe(ref at, offset);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Store(Vector512<byte> value, ref byte at, nuint offset) => value.StoreUnsafe(ref at, offset);

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector512<byte> And(Vector512<byte> left, Vector512<byte> right) => left & right;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public Vector128<byte> Lower(Vector512<byte> value) => value.GetLower().GetLower();
        }
#endif

        /// <summary>
        /// <c>end % period</c> from the loop's state, without a division (an <c>idiv</c> of about 12 cycles, 5% of
        /// copying 1,000 8-byte structs): <paramref name="m"/> is <paramref name="i"/> % <paramref name="period"/>,
        /// and the last vector at <paramref name="end"/> starts less than a vector before <paramref name="i"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint TailOffset(nuint m, nuint i, nuint end, nuint period)
        {
            nuint back = i - end;
            return m >= back ? m - back : m + period - back;
        }

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

        /// <summary>
        /// In <see cref="NinoSettings.Untrusted"/> mode, the fields of <typeparamref name="T"/> with invalid values
        /// (<see cref="NinoLayout.ValueChecks"/>: offset and kind pairs in the wire layout); null otherwise, and for a
        /// type without such fields. Declared before <see cref="CopiesAsIs"/>, which reads it.
        /// </summary>
        internal static readonly NinoValueChecks Checks = NinoLimits.Untrusted && typeof(T) != typeof(bool) &&
                                                !NinoUnsafe.IsReferenceOrContainsReferences<T>()
            ? NinoLayout.ValueChecks(typeof(T))
            : null;

        /// <summary>
        /// Whether values are read as their bytes, one (<see cref="Read"/>) or a block of them (arrays, lists): canonical,
        /// not a <c>bool</c>, whose byte is checked (0 or 1), and without <see cref="Checks"/>. The read paths test this field where they tested
        /// <see cref="IsCanonical"/> and take their other path for the rest, so the check costs the other types nothing,
        /// not even code to fold (in a large method the inliner counts that too).
        /// </summary>
        internal static readonly bool CopiesAsIs = Info.Canonical && typeof(T) != typeof(bool) && Checks == null;


        private static readonly NinoMask Mask = Info.Mask == null ? null : NinoMask.Create(Info.Mask);

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

            Mask.Clear(ref destination, count);
        }

        // The first two padding runs, as JIT-time constants for single values.
        private static readonly int GapCount = Mask == null ? 0 : Mask.Gaps.Length / 2;
        private static readonly int Gap0Offset = GapCount > 0 ? Mask.Gaps[0] : 0;
        private static readonly int Gap0Length = GapCount > 0 ? Mask.Gaps[1] : 0;
        private static readonly int Gap1Offset = GapCount > 1 ? Mask.Gaps[2] : 0;
        private static readonly int Gap1Length = GapCount > 1 ? Mask.Gaps[3] : 0;

        /// <summary>
        /// Copies <paramref name="count"/> canonical values to the wire, padding cleared in the same pass (masking
        /// after a block copy would reload freshly stored bytes and stall on store forwarding). The destination is
        /// reserved by the caller, so the size fits an int: not checked again.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyToWire(ref byte destination, ref byte source, int count)
        {
            if (HasPadding)
                Mask.Copy(ref destination, ref source, count);
            else
                NinoUnsafe.Copy(ref destination, ref source, (uint)count * (uint)Unsafe.SizeOf<T>());
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

        /// <summary>Reads a value stored in its wire layout (a <c>bool</c> other than 0 or 1 is invalid data).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read(ref byte source) =>
            CopiesAsIs ? NinoUnsafe.Read<T>(ref source)
            : typeof(T) == typeof(bool) ? ReadBool(ref source)
            : ReadConverted(ref source);

        // Out of line: a bool's byte is checked (0 or 1), which the other types never pay for.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static T ReadBool(ref byte source)
        {
            if (source > 1)
                NinoThrow.InvalidBool(source);
            return NinoUnsafe.Read<T>(ref source);
        }

        // Its own method, as WriteConverted: where CopiesAsIs does not fold (Tier0, Mono, IL2CPP), a local whose
        // address is taken here would be one in every inlined caller.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T ReadConverted(ref byte source)
        {
            T value = default;
            if (Info.Canonical)
                value = NinoUnsafe.Read<T>(ref source);
            else
                NinoLayout.Convert(Info, ref Unsafe.As<T, byte>(ref value), ref source, false);
            if (Checks != null)
                NinoLayout.Check(Checks, ref Unsafe.As<T, byte>(ref value), 1, 0, typeof(T));
            return value;
        }
    }
}
