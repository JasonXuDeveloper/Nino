using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
#if NET8_0_OR_GREATER
using System.Runtime.Intrinsics;
#endif

namespace Nino.Core.Internal
{
    /// <summary>
    /// Layout of raw-copied unmanaged types. The wire format of such a type is its <b>canonical</b> layout: the one
    /// CoreCLR uses on 64-bit (every primitive aligned to its size, <c>decimal</c> aligned to 8, <c>nint</c> and
    /// pointers 8 bytes; auto-layout structs such as <c>ValueTuple</c> placed by its auto-layout rule,
    /// <see cref="NinoLayoutCore"/>; <c>DateTimeOffset</c> as .NET 10 lays it out, its offset in minutes as an
    /// <c>int</c>, then its UTC date at 8), padding zeroed. Runtimes that lay the type out differently are detected
    /// once by probe structs, and their values are converted field by field instead of copied: Mono on 32-bit x86
    /// aligns 8-byte primitives to 4; .NET on
    /// 32-bit x86 aligns <c>long</c>, <c>double</c> and sequential structs holding them (<c>TimeSpan</c>, <c>decimal</c>)
    /// to 8 but <c>DateTime</c>, an auto-layout struct, to 4 (<c>{ int; DateTime }</c> is 12 bytes there, 16 on 64-bit);
    /// IL2CPP and .NET Framework align <c>decimal</c> to 4; Arm32 and .NET 7 align <c>Int128</c> to 8. .NET Framework lays out a
    /// struct holding a <c>DateTime</c> as auto-layout (fields reordered): there, wherever the model misses a type's
    /// size, and for structs holding an auto-layout struct (whose placement differs between runtimes: Mono and IL2CPP
    /// keep a tuple's declaration order, .NET 8 has a <c>short</c> in <c>DateTimeOffset</c>), the offsets are read from
    /// an instance instead.
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

        private struct DateTimeProbe
        {
#pragma warning disable CS0649
            public byte A;
            public DateTime B;
#pragma warning restore CS0649
        }

        private struct OrderProbe
        {
#pragma warning disable CS0649
            public byte A;
            public DateTime B;
            public int C;
#pragma warning restore CS0649
        }

        /// <summary>
        /// Whether this runtime lays out a sequential struct holding an auto-layout struct (<c>DateTime</c>) in
        /// declaration order, as the model assumes. .NET Framework lays such a struct out as auto-layout instead (the
        /// <c>int</c> of <c>{ byte; DateTime; int }</c> first): there the fields' offsets are read from instances
        /// (<see cref="MeasureMemory"/>).
        /// </summary>
        private static readonly bool KeepsDeclarationOrder = ProbeOrder();

        private static bool ProbeOrder()
        {
            var probe = default(OrderProbe);
            ref byte start = ref probe.A;
            return (long)Unsafe.ByteOffset(ref start, ref Unsafe.As<DateTime, byte>(ref probe.B)) > 0 &&
                   (long)Unsafe.ByteOffset(ref Unsafe.As<DateTime, byte>(ref probe.B),
                       ref Unsafe.As<int, byte>(ref probe.C)) > 0;
        }

        /// <summary>Alignment of 8-byte primitives inside structs on this runtime.</summary>
        private static readonly int LongAlign = Unsafe.SizeOf<LongProbe>() - 8;

        /// <summary>
        /// Alignment of <c>DateTime</c> inside structs on this runtime: 4 on 32-bit x86 .NET, where <see cref="LongAlign"/>
        /// is 8 (<c>DateTime</c> is an auto-layout struct, and those are aligned to the pointer size as fields there).
        /// </summary>
        internal static readonly int DateTimeAlign = Unsafe.SizeOf<DateTimeProbe>() - 8;

        /// <summary>Alignment of <c>decimal</c> inside structs on this runtime.</summary>
        private static readonly int DecimalAlign = Unsafe.SizeOf<DecimalProbe>() - 16;

        /// <summary>
        /// Alignment of <c>Int128</c> and <c>UInt128</c> inside structs on this runtime, probed: 16 on .NET 8 and later
        /// on 64-bit, 8 on Arm32 and on .NET 7. The canonical layout aligns them to 16, as CoreCLR does on 64-bit.
        /// </summary>
        internal static readonly int Int128Align = ProbeInt128Align();

        /// <summary>
        /// This runtime's layout rules, probed (<see cref="LongAlign"/>, <see cref="DecimalAlign"/>, the pointer size,
        /// <see cref="Int128Align"/>, <see cref="DateTimeAlign"/>), without the auto-layout rule: auto-layout structs are
        /// measured (<see cref="MeasureMemory"/>).
        /// </summary>
        private static readonly NinoLayoutModel Runtime =
            new NinoLayoutModel(LongAlign, DecimalAlign, IntPtr.Size, Int128Align, DateTimeAlign);

#if NET8_0_OR_GREATER
        private struct Int128Probe
        {
#pragma warning disable CS0649
            public byte A;
            public Int128 B;
#pragma warning restore CS0649
        }

        private static int ProbeInt128Align() => Unsafe.SizeOf<Int128Probe>() - 16;
#else
        // The netstandard builds name no Int128 (it exists from .NET 7): the probe struct is made for the runtime's own.
        private struct Int128Probe<T>
        {
#pragma warning disable CS0649
            public byte A;
            public T B;
#pragma warning restore CS0649
        }

        private static int ProbeInt128Align()
        {
            try
            {
                var wide = typeof(object).Assembly.GetType("System.Int128");
                if (wide == null)
                    return 16; // no Int128 on this runtime: nothing is laid out with it
                return (int)typeof(NinoLayout).GetMethod(nameof(Int128ProbeSize), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(wide).Invoke(null, null)! - 16;
            }
            catch
            {
                return 0; // unknown: a struct with one is not laid out (Measure fails, the runtime size is used)
            }
        }

        private static int Int128ProbeSize<T>() => Unsafe.SizeOf<Int128Probe<T>>();
#endif

        /// <summary>A byte, then a <typeparamref name="T"/>: its alignment inside structs on this runtime.</summary>
        private struct AlignProbe<T>
        {
#pragma warning disable CS0649
            public byte A;
            public T B;
#pragma warning restore CS0649
        }

        private static int AlignOf<T>() => Unsafe.SizeOf<AlignProbe<T>>() - Unsafe.SizeOf<T>();

        /// <summary>
        /// The alignments of <c>Vector64&lt;T&gt;</c> to <c>Vector512&lt;T&gt;</c> inside structs on this runtime (0 where
        /// the type does not exist or could not be probed). The canonical ones are x64 CoreCLR's: their sizes (Arm64
        /// aligns the 32- and 64-byte vectors to 16, Arm32 every one of them to 8).
        /// </summary>
#if NET8_0_OR_GREATER
        private static readonly int[] IntrinsicAligns =
        {
            AlignOf<Vector64<byte>>(), AlignOf<Vector128<byte>>(), AlignOf<Vector256<byte>>(),
            AlignOf<Vector512<byte>>(),
        };
#else
        private static readonly int[] IntrinsicAligns = ProbeIntrinsicAligns();

        // The netstandard builds name none of them (they exist from .NET Core 3.0, .NET 8 for Vector512).
        private static int[] ProbeIntrinsicAligns()
        {
            var aligns = new int[4];
            for (int i = 0; i < aligns.Length; i++)
            {
                try
                {
                    var definition = typeof(object).Assembly.GetType("System.Runtime.Intrinsics.Vector" + (64 << i) + "`1");
                    if (definition != null)
                        aligns[i] = (int)typeof(NinoLayout).GetMethod(nameof(AlignOf), BindingFlags.NonPublic | BindingFlags.Static)!
                            .MakeGenericMethod(definition.MakeGenericType(typeof(byte))).Invoke(null, null)!;
                }
                catch
                {
                    // Unknown: a struct with one is not laid out (Measure fails, the runtime size is used).
                }
            }

            return aligns;
        }
#endif

        /// <summary>
        /// Which vector <paramref name="type"/> is: 1 to 4 for <c>Vector64&lt;T&gt;</c> to <c>Vector512&lt;T&gt;</c>, -1 for
        /// none. <c>Vector&lt;T&gt;</c>, 16, 32 or 64 bytes by the CPU, is never laid out: <see cref="Create"/> refuses it
        /// (docs/spec/wire-format.md §3). The generator's mirror is <c>RawLayout.IsVector</c>.
        /// </summary>
        private static int VectorIndex(Type type)
        {
            if (!type.IsGenericType || type.IsGenericTypeDefinition)
                return -1;
            var definition = type.GetGenericTypeDefinition();
            if (definition.Assembly != typeof(object).Assembly)
                return -1;
            switch (definition.FullName)
            {
                case "System.Runtime.Intrinsics.Vector64`1": return 1;
                case "System.Runtime.Intrinsics.Vector128`1": return 2;
                case "System.Runtime.Intrinsics.Vector256`1": return 3;
                case "System.Runtime.Intrinsics.Vector512`1": return 4;
                default: return -1;
            }
        }

        /// <summary>
        /// The size and alignment of a vector (<see cref="VectorIndex"/>) canonically (x64's: its size) or on this
        /// runtime; its size is the same everywhere.
        /// </summary>
        private static bool VectorLayout(Type type, bool canonical, out int size, out int align)
        {
            int index = VectorIndex(type);
            size = index > 0 ? 4 << index : 0; // 8, 16, 32, 64
            align = index <= 0 ? 0 : canonical ? size : IntrinsicAligns[index - 1];
            return size > 0 && align > 0;
        }

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

            /// <summary>
            /// When not canonical, the integers whose size differs between memory and wire (<c>nint</c> and
            /// <c>nuint</c> on 32-bit, the minutes of a <c>DateTimeOffset</c> where they are a <c>short</c>): (memory
            /// offset, memory size, wire offset, wire size, 1 when signed) quintuples, the unsigned ones zero-extended;
            /// null for none.
            /// </summary>
            public int[] Resized;

            /// <summary>
            /// Whether the type is or holds <c>Vector&lt;T&gt;</c> (<see cref="NinoReflection.HostSizedVectorIn"/>): not
            /// canonical and of no size, and <see cref="NinoLayout{T}"/> refuses to write or read it.
            /// </summary>
            public bool HostSizedVector;
        }

        /// <summary>Layout info for <paramref name="type"/>; <paramref name="runtimeSize"/> validates the model.</summary>
        public static Info Get(Type type, int runtimeSize) =>
            runtimeSize < 0 ? Create(type, runtimeSize) : GetCached(type, runtimeSize);

#if NETSTANDARD2_0
        // This target lacks the GetOrAdd overload with factory state. Compute only on a miss without capturing the size.
        private static Info GetCached(Type type, int runtimeSize) =>
            Cache.TryGetValue(type, out var info) ? info : Cache.GetOrAdd(type, Create(type, runtimeSize));
#else
        private static Info GetCached(Type type, int runtimeSize) => Cache.GetOrAdd(type, Create, runtimeSize);
#endif

        private static Info Create(Type type, int runtimeSize)
        {
            // A Vector<T> has no canonical layout, and copying it as it lies in memory (the fallback below) would write
            // 16 bytes on one host and 32 on another: neither raw nor convertible (RawFormatter.Is is false), and no
            // formatter of it exists (NinoReflection.RefuseHostSizedVector throws when one is resolved). Not thrown here:
            // Mono and IL2CPP run NinoLayout<T>'s initializer whenever a method naming it is compiled.
            if (NinoReflection.HostSizedVectorIn(type) != null)
                return new Info { Canonical = false, HostSizedVector = true };
            try
            {
                return Compute(type, runtimeSize, Runtime, !KeepsDeclarationOrder);
            }
            catch
            {
                // Reflection limits (AOT stripping): assume the memory layout is the wire layout.
                return new Info { Size = runtimeSize };
            }
        }

        /// <summary>
        /// Layout info as a runtime laid out by <paramref name="memoryModel"/> would see it (<see cref="Runtime"/>; other
        /// models for tests). The memory layout is modeled, or, with <paramref name="measured"/> or where the model
        /// misses the runtime's size, read from instances of this runtime (<see cref="MeasureMemory"/>).
        /// </summary>
        internal static Info Compute(Type type, int runtimeSize, in NinoLayoutModel memoryModel, bool measured = false)
        {
            var wire = new List<NinoLayoutCore.Leaf>();
            var memory = new List<NinoLayoutCore.Leaf>();
            if (!NinoLayoutCore.Measure(new RuntimeView { Canonical = true }, type, NinoLayoutModel.Canonical, 0, wire,
                    out int wireSize, out int wireAlign))
                return new Info { Size = runtimeSize };
            // Auto-layout structs are not modeled for this runtime (AutoPointerSize 0): their offsets are measured.
            if (measured ||
                !NinoLayoutCore.Measure<Type, RuntimeView>(default, type, memoryModel, 0, memory, out int memorySize,
                    out _) ||
                (runtimeSize >= 0 && memorySize != runtimeSize))
            {
                // The model does not describe this runtime (or was not asked): the offsets this runtime gives.
                memorySize = runtimeSize;
                if (!MeasuredLeaves(type, runtimeSize, memory) || memory.Count != wire.Count)
                    // Neither: the value is copied as it lies in memory, which other runtimes may lay out
                    // differently. tests/Nino.RuntimeTests checks the canonical bytes on every runtime CI has.
                    return new Info { Size = runtimeSize };
            }

            if (wire.Count != memory.Count)
                return new Info { Size = runtimeSize };
            // A leaf of another size is an integer of another width (nint, a DateTimeOffset's minutes), and only that.
            for (int i = 0; i < wire.Count; i++)
                if (wire[i].Size != memory[i].Size && !(Resizable(wire[i].Size) && Resizable(memory[i].Size)))
                    return new Info { Size = runtimeSize };

            var info = new Info
            {
                Size = wireSize, Align = wireAlign, Canonical = wireSize == memorySize, Mask = PaddingMask(wire, wireSize),
            };
            for (int i = 0; i < wire.Count && info.Canonical; i++)
                info.Canonical = wire[i].Offset == memory[i].Offset && wire[i].Size == memory[i].Size;
            if (!info.Canonical)
                MapLeaves(wire, memory, info);
            return info;
        }

        /// <summary>
        /// Replaces <paramref name="memory"/> with the leaves this runtime gives a value of <paramref name="type"/>
        /// (<see cref="MeasureMemory"/>); false when they cannot be measured.
        /// </summary>
        private static bool MeasuredLeaves(Type type, int runtimeSize, List<NinoLayoutCore.Leaf> memory)
        {
            memory.Clear();
            var values = runtimeSize > 0 ? MeasureMemory(type, runtimeSize) : null;
            if (values == null)
                return false;
            foreach (var value in values)
            {
                int size = LeafSize(value.Key);
                if (size > 0)
                    memory.Add(new NinoLayoutCore.Leaf
                    {
                        Offset = value.Value, Size = size,
                        Signed = value.Key == typeof(IntPtr) || value.Key == typeof(short) || value.Key == typeof(int),
                    });
            }

            return true;
        }

        /// <summary>0xFF for the bytes of <paramref name="wire"/>'s leaves, 0 for padding; null without padding.</summary>
        private static byte[] PaddingMask(List<NinoLayoutCore.Leaf> wire, int wireSize)
        {
            var mask = new byte[wireSize];
            foreach (var leaf in wire)
                for (int i = 0; i < leaf.Size; i++)
                    mask[leaf.Offset + i] = 0xFF;
            return Array.IndexOf(mask, (byte)0) >= 0 ? mask : null;
        }

        /// <summary>
        /// <see cref="Info.Map"/> and <see cref="Info.Resized"/> of a layout that is not canonical: leaves contiguous
        /// in both layouts merged into runs, integers of another width apart.
        /// </summary>
        private static void MapLeaves(List<NinoLayoutCore.Leaf> wire, List<NinoLayoutCore.Leaf> memory, Info info)
        {
            var runs = new List<int>();
            var resized = new List<int>();
            for (int i = 0; i < wire.Count; i++)
            {
                if (wire[i].Size != memory[i].Size)
                {
                    resized.Add(memory[i].Offset);
                    resized.Add(memory[i].Size);
                    resized.Add(wire[i].Offset);
                    resized.Add(wire[i].Size);
                    resized.Add(memory[i].Signed ? 1 : 0);
                    continue;
                }

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
            if (resized.Count != 0)
                info.Resized = resized.ToArray();
        }

        private static bool Resizable(int size) => size == 2 || size == 4 || size == 8;

        /// <summary>The size in memory of a leaf of <paramref name="type"/> on this runtime; 0 for a struct of fields.</summary>
        private static int LeafSize(Type type)
        {
            var leaf = default(RuntimeView).Leaf(type);
            if (leaf == NinoLeaf.Vector)
                return VectorLayout(type, false, out int size, out _) ? size : 0;
            return NinoLayoutCore.SizeOf(leaf, IntPtr.Size);
        }

        /// <summary>
        /// The fields of <paramref name="type"/> (an unmanaged struct, or one of the types below itself) that can
        /// hold bytes no constructor makes, as pairs of their offset in memory on this runtime and kind: <c>bool</c>,
        /// <see cref="DateTime"/>, <c>decimal</c>, <c>DateOnly</c>, <c>TimeOnly</c>, at any depth of nested structs.
        /// Values are checked once read (in memory): where the wire layout is the memory layout, also before. Null for
        /// none, or when the fields cannot be read (then nothing is checked, as nothing is converted: see
        /// <see cref="Create"/>).
        /// </summary>
        internal static NinoValueChecks ValueChecks(Type type, int size)
        {
            try
            {
                var checks = new List<int>();
                if (KeepsDeclarationOrder && AddChecks(type, 0, checks))
                    return checks.Count != 0 ? new NinoValueChecks(checks.ToArray()) : null;
                // Offsets read from an instance, as Compute reads them where the model does not describe the type
                // (.NET Framework, auto-layout structs inside).
                checks.Clear();
                var values = MeasureMemory(type, size);
                if (values == null)
                    return null;
                foreach (var value in values)
                {
                    int kind = CheckOf(value.Key);
                    if (kind == 0)
                        continue;
                    checks.Add(value.Value);
                    checks.Add(kind);
                }

                return checks.Count != 0 ? new NinoValueChecks(checks.ToArray()) : null;
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
                type == typeof(TimeSpan) || VectorIndex(type) >= 0)
                return true;
            // The offsets of its own fields in memory, in the order RuntimeView.Struct lists them.
            var offsets = new List<int>();
            if (!NinoLayoutCore.Measure<Type, RuntimeView>(default, type, Runtime, 0, new List<NinoLayoutCore.Leaf>(),
                    out _, out _, offsets))
                return false;
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fields.Length != offsets.Count)
                return false;
            for (int i = 0; i < fields.Length; i++)
                if (!AddChecks(NinoReflection.LayoutFieldType(fields[i]), offset + offsets[i], checks))
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
            if (type == typeof(DateTimeOffset) && ValueViews.DateTimeOffsetUtc >= 0)
                return NinoValueChecks.DateTimeOffset;
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

        /// <summary>
        /// The values that make up a value of the struct <paramref name="type"/> (<paramref name="size"/> bytes) on this
        /// runtime, itself first, then each field's, depth first in the order <see cref="RuntimeView"/> lists them, down
        /// to its leaves: each with its type and its offset in memory, read from an instance. One bit of each byte's
        /// own offset at a time, every byte of the instance is set to that bit (0 or 1, which a <c>bool</c> keeps), and
        /// the first byte of each value, copied out by reflection, gives that bit of its offset. A <c>Nullable</c>
        /// (boxed as null or as its value) gives the bit of its flag, which comes first, and its value lies where
        /// <c>T?</c> and <c>T</c> differ in size, laid out as a <c>T</c> on its own. Null where a field cannot be read
        /// so (a pointer is read as a <c>Pointer</c>).
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "A struct's default instance needs no constructor.")]
        private static List<KeyValuePair<Type, int>> MeasureMemory(Type type, int size)
        {
            // A T? is created as null (boxed as nothing): its values are its flag and its T's (VisitNullable).
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                var inner = NullableValue(underlying, out int valueOffset);
                if (inner == null)
                    return null;
                var all = new List<KeyValuePair<Type, int>>(inner.Count + 2)
                {
                    new KeyValuePair<Type, int>(type, 0), new KeyValuePair<Type, int>(typeof(bool), 0),
                };
                foreach (var value in inner)
                    all.Add(new KeyValuePair<Type, int>(value.Key, valueOffset + value.Value));
                return all;
            }

            object instance = Activator.CreateInstance(type);
            List<Measured> values = null;
            int bit = 0;
            do
            {
                for (int i = 0; i < size; i++)
                    ObjectFields.At<byte>(instance, i) = (byte)(i >> bit & 1);
                var pass = new List<Measured>();
                if (!Visit(instance, type, bit, pass))
                    return null;
                if (values != null)
                {
                    if (pass.Count != values.Count)
                        return null;
                    for (int i = 0; i < pass.Count; i++)
                        pass[i] = new Measured(pass[i].Type, pass[i].Offset | values[i].Offset, pass[i].Base,
                            pass[i].Delta);
                }

                values = pass;
            } while ((size - 1) >> ++bit != 0);

            var offsets = new List<KeyValuePair<Type, int>>(values.Count);
            foreach (var value in values)
            {
                int offset = value.Base < 0 ? value.Offset : offsets[value.Base].Value + value.Delta;
                if (offset >= size)
                    return null;
                offsets.Add(new KeyValuePair<Type, int>(value.Type, offset));
            }

            return offsets;
        }

        /// <summary>
        /// A value <see cref="MeasureMemory"/> found: its type and the bits of its offset found so far, or, with a
        /// <see cref="Base"/>, its offset from that of an earlier value.
        /// </summary>
        private readonly struct Measured
        {
            public readonly Type Type;
            public readonly int Offset, Base, Delta;

            public Measured(Type type, int offset, int @base = -1, int delta = 0)
            {
                Type = type;
                Offset = offset;
                Base = @base;
                Delta = delta;
            }
        }

        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = FieldsJustification)]
        private static bool Visit(object value, Type type, int bit, List<Measured> values)
        {
            var leaf = default(RuntimeView).Leaf(type);
            if (leaf == NinoLeaf.Unsupported || value == null)
                return false;
            values.Add(new Measured(type, (ObjectFields.At<byte>(value, 0) & 1) << bit));
            if (leaf != NinoLeaf.None)
                return true;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                // A function pointer reads as a boxed IntPtr, measured as the UIntPtr it is laid out as.
                var fieldType = NinoReflection.LayoutFieldType(field);
                if (fieldType.IsPointer)
                    return false;
                var underlying = Nullable.GetUnderlyingType(fieldType);
                if (underlying == null ? !Visit(field.GetValue(value), fieldType, bit, values)
                    : !VisitNullable(field.GetValue(value) != null, fieldType, underlying, bit, values))
                    return false;
            }

            return true;
        }

        /// <summary>A <c>Nullable</c> field whose flag (its first byte) is <paramref name="set"/> in this pass.</summary>
        private static bool VisitNullable(bool set, Type nullable, Type underlying, int bit, List<Measured> values)
        {
            var inner = NullableValue(underlying, out int valueOffset);
            if (inner == null)
                return false;
            int index = values.Count;
            values.Add(new Measured(nullable, set ? 1 << bit : 0));
            values.Add(new Measured(typeof(bool), 0, index));
            foreach (var value in inner)
                values.Add(new Measured(value.Key, 0, index, valueOffset + value.Value));
            return true;
        }

        /// <summary>
        /// The values of a <typeparamref name="T"/> (<see cref="MeasureMemory"/>) and where it lies in a <c>T?</c>,
        /// whose flag comes first (corefx, and Mono from Unity 2021.3); null where that is not so or not measured.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Measured only where reflection works; null otherwise.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Measured only where reflection works; null otherwise.")]
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2090", Justification = FieldsJustification)]
        private static List<KeyValuePair<Type, int>> NullableValue(Type underlying, out int valueOffset)
        {
            valueOffset = 0;
            var nullable = typeof(Nullable<>).MakeGenericType(underlying);
            var fields = nullable.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fields.Length != 2 || fields[0].FieldType != typeof(bool))
                return null;
            var sizeOf = typeof(NinoLayout).GetMethod(nameof(SizeOf), BindingFlags.NonPublic | BindingFlags.Static)!;
            int size = (int)sizeOf.MakeGenericMethod(underlying).Invoke(null, null)!;
            valueOffset = (int)sizeOf.MakeGenericMethod(nullable).Invoke(null, null)! - size;
            return valueOffset > 0 ? MeasureMemory(underlying, size) : null;
        }

        private static int SizeOf<T>() => Unsafe.SizeOf<T>();

        /// <summary>
        /// Copies a value between its memory layout and its wire layout using <see cref="Info.Map"/> and
        /// <see cref="Info.Resized"/>. A wire integer that does not fit its narrower field in memory (an <c>nint</c>
        /// past 32 bits on a 32-bit runtime) is invalid data there.
        /// </summary>
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

            var resized = info.Resized;
            if (resized == null)
                return;
            for (int i = 0; i < resized.Length; i += 5)
            {
                ref byte m = ref Unsafe.Add(ref memory, resized[i]);
                ref byte w = ref Unsafe.Add(ref wire, resized[i + 2]);
                int memorySize = resized[i + 1], wireSize = resized[i + 3];
                if (resized[i + 4] == 0)
                {
                    // Unsigned integers (nuint on 32-bit): their low bytes, zero-extended on the wire (cleared above);
                    // a wire value with bytes set past this runtime's field is invalid data.
                    if (toWire)
                    {
                        Unsafe.CopyBlockUnaligned(ref w, ref m, (uint)memorySize);
                        continue;
                    }

                    if (!Zero(ref Unsafe.Add(ref w, memorySize), wireSize - memorySize))
                        NinoThrow.InvalidData("an integer too large for this runtime's field.");
                    Unsafe.CopyBlockUnaligned(ref m, ref w, (uint)memorySize);
                    continue;
                }

                if (toWire)
                {
                    Store(ref w, wireSize, Load(ref m, memorySize));
                    continue;
                }

                long value = Load(ref w, wireSize);
                if (Load(ref Unsafe.As<long, byte>(ref value), memorySize) != value)
                    NinoThrow.InvalidData("an integer too large for this runtime's field.");
                Store(ref m, memorySize, value);
            }
        }

        /// <summary>Whether the <paramref name="length"/> bytes at <paramref name="at"/> are all zero.</summary>
        private static bool Zero(ref byte at, int length)
        {
            for (int i = 0; i < length; i++)
                if (Unsafe.Add(ref at, i) != 0)
                    return false;
            return true;
        }

        /// <summary>The little-endian signed integer of <paramref name="size"/> bytes (2, 4 or 8) at <paramref name="at"/>.</summary>
        private static long Load(ref byte at, int size) => size switch
        {
            2 => Unsafe.ReadUnaligned<short>(ref at),
            4 => Unsafe.ReadUnaligned<int>(ref at),
            _ => Unsafe.ReadUnaligned<long>(ref at),
        };

        private static void Store(ref byte at, int size, long value)
        {
            switch (size)
            {
                case 2:
                    Unsafe.WriteUnaligned(ref at, (short)value);
                    break;
                case 4:
                    Unsafe.WriteUnaligned(ref at, (int)value);
                    break;
                default:
                    Unsafe.WriteUnaligned(ref at, value);
                    break;
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

        /// <summary>
        /// The runtime's view of a type for <see cref="NinoLayoutCore"/>; with <see cref="Canonical"/>, the canonical
        /// layout's: <c>DateTimeOffset</c> as the fields of .NET 10, whatever this runtime declares.
        /// </summary>
        private struct RuntimeView : INinoLayoutView<Type>
        {
            public bool Canonical;

            public bool IsAutoPrimitive(Type type) => type.IsPrimitive || type.IsEnum || type.IsPointer;

            public bool TryGetVector(Type type, out int size, out int align) =>
                VectorLayout(type, Canonical, out size, out align);

            public NinoLeaf Leaf(Type type)
            {
                if (type.IsEnum)
                    type = Enum.GetUnderlyingType(type);
                if (type == typeof(decimal))
                    return NinoLeaf.Decimal;
                if (type == typeof(DateTime))
                    return NinoLeaf.DateTime;
                if (type == typeof(TimeSpan))
                    return NinoLeaf.Size8;
                if (type == typeof(Guid))
                    return NinoLeaf.Guid;
                if (type == typeof(IntPtr))
                    return NinoLeaf.SignedPointer;
                if (type.IsPointer || type == typeof(UIntPtr))
                    return NinoLeaf.Pointer;
                if (VectorIndex(type) >= 0)
                    return NinoLeaf.Vector;
                // By name: the netstandard builds name no Int128. Without a known alignment (the probe failed), its
                // two ulongs (8-byte aligned), as before.
                if (Int128Align > 0 && type.Assembly == typeof(object).Assembly &&
                    (type.FullName == "System.Int128" || type.FullName == "System.UInt128"))
                    return NinoLeaf.Int128;
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
            public bool TryGetFields(Type type, List<KeyValuePair<Type, int>> fields, out LayoutKind kind, out int pack,
                out int minSize)
            {
                if (Canonical && type == typeof(DateTimeOffset))
                {
                    // .NET 10's fields (.NET 8, .NET Framework and Mono have a short for the minutes).
                    kind = LayoutKind.Auto;
                    pack = minSize = 0;
                    fields.Add(new KeyValuePair<Type, int>(typeof(DateTime), -1));
                    fields.Add(new KeyValuePair<Type, int>(typeof(int), -1));
                    return true;
                }

                var layout = type.StructLayoutAttribute;
                kind = layout?.Value ?? LayoutKind.Sequential;
                pack = layout != null && layout.Pack > 0 ? layout.Pack : 0;
                minSize = layout?.Size ?? 0;
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    fields.Add(new KeyValuePair<Type, int>(NinoReflection.LayoutFieldType(field),
                        kind == LayoutKind.Explicit ? (int)Marshal.OffsetOf(type, field.Name) : -1));
                return true;
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
        /// Whether <typeparamref name="T"/> is or holds <c>Vector&lt;T&gt;</c>: its converted paths throw
        /// <see cref="NotSupportedException"/>, so code that writes it without the resolver (a hand-made
        /// <see cref="UnmanagedFormatter{T}"/>, <see cref="Writer.WriteValue{T}"/>) is refused as the resolver refuses it.
        /// A JIT-time constant, false for every other type and folded away on canonical ones; the throw is never in this
        /// initializer, which Mono runs when a method naming the class is compiled.
        /// </summary>
        private static readonly bool HoldsHostSizedVector = Info.HostSizedVector;

        /// <summary>
        /// In <see cref="NinoSettings.Untrusted"/> mode, the fields of <typeparamref name="T"/> with invalid values
        /// (<see cref="NinoLayout.ValueChecks"/>: offset and kind pairs in the wire layout); null otherwise, and for a
        /// type without such fields. Declared before <see cref="CopiesAsIs"/>, which reads it.
        /// </summary>
        internal static readonly NinoValueChecks Checks = NinoLimits.Untrusted && typeof(T) != typeof(bool) &&
                                                !NinoUnsafe.IsReferenceOrContainsReferences<T>()
            ? NinoLayout.ValueChecks(typeof(T), Unsafe.SizeOf<T>())
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
        private static void WriteConverted(ref byte destination, in T value)
        {
            if (HoldsHostSizedVector)
                NinoReflection.RefuseHostSizedVector(typeof(T));
            NinoLayout.Convert(Info, ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), ref destination, true);
        }

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
            if (HoldsHostSizedVector)
                NinoReflection.RefuseHostSizedVector(typeof(T));
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
