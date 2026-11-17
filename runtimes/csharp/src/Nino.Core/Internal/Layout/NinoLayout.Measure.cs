using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    // Field offsets read from an instance, where the layout model cannot place a type (see NinoLayout).
    internal static partial class NinoLayout
    {
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
            // A generic struct whose compiled offsets the generator registered: those, never reflection's.
            if (NinoLayoutOffsets.Find(type) is { } table && table.Size == size)
            {
                var tabled = new List<Measured> { new(type, 0) };
                return AddTabled(type, table, 0, tabled) ? Resolve(tabled, size) : null;
            }

            // A T? is created as null (boxed as nothing): its values are its flag and its T's (VisitNullable).
            if (Nullable.GetUnderlyingType(type) is { } underlying)
            {
                var inner = NullableValue(underlying, out int valueOffset);
                if (inner == null)
                    return null;
                var all = new List<KeyValuePair<Type, int>>(inner.Count + 2)
                {
                    new(type, 0), new(typeof(bool), 0),
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

            return Resolve(values, size);
        }

        /// <summary>The offsets of <paramref name="values"/> from the start of a value of <paramref name="size"/> bytes.</summary>
        private static List<KeyValuePair<Type, int>> Resolve(List<Measured> values, int size)
        {
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
            if (leaf != NinoLeaf.None || TryAddTabled(type, values.Count - 1, values) ||
                AddPacked(type, values.Count - 1, values))
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

        /// <summary>
        /// The values below a struct laid out by <c>Pack</c> or <c>[FieldOffset]</c>, which may put a field where
        /// reflection cannot read it: CoreCLR's <c>FieldInfo.GetValue</c> loads an unaligned <c>long</c> or <c>double</c>
        /// with an aligned instruction on Arm32 and Arm64 (SIGBUS; a Pack = 4 struct holding a Pack = 1 one and a
        /// <c>DateTimeOffset</c>, which Arm32 measures). Added from the layout model, each at its offset from the value
        /// that holds it (<paramref name="index"/> first), where the model places the struct at this runtime's size;
        /// false otherwise, with nothing added. IL2CPP ignores <c>Pack</c>: its sizes differ, and its fields, read by
        /// reflection, lie aligned.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        private static bool AddPacked(Type type, int index, List<Measured> values)
        {
            var layout = type.StructLayoutAttribute;
            if (layout == null || !(layout.Value == System.Runtime.InteropServices.LayoutKind.Explicit ||
                                    layout.Value == System.Runtime.InteropServices.LayoutKind.Sequential && layout.Pack > 0))
                return false;
            if (!NinoLayoutCore.Measure<Type, RuntimeView>(default, type, Runtime, 0, new List<NinoLayoutCore.Leaf>(),
                    out int size, out _) || size != RuntimeSizeOf(type))
                return false;
            int start = values.Count;
            if (AddModeled(type, index, values))
                return true;
            values.RemoveRange(start, values.Count - start);
            return false;
        }

        /// <summary>The fields of <paramref name="type"/> (value <paramref name="index"/>) and theirs, as the model places them.</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = FieldsJustification)]
        private static bool AddModeled(Type type, int index, List<Measured> values)
        {
            var offsets = new List<int>();
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (!NinoLayoutCore.Measure<Type, RuntimeView>(default, type, Runtime, 0, new List<NinoLayoutCore.Leaf>(),
                    out _, out _, offsets) || offsets.Count != fields.Length)
                return false;
            for (int i = 0; i < fields.Length; i++)
            {
                var fieldType = NinoReflection.LayoutFieldType(fields[i]);
                // A Nullable's flag and value, and pointers, are measured as Visit measures them.
                if (fieldType.IsPointer || Nullable.GetUnderlyingType(fieldType) != null)
                    return false;
                int at = values.Count;
                values.Add(new Measured(fieldType, 0, index, offsets[i]));
                var leaf = default(RuntimeView).Leaf(fieldType);
                if (leaf == NinoLeaf.Unsupported || leaf == NinoLeaf.None && !AddModeled(fieldType, at, values))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// The values below <paramref name="type"/> (value <paramref name="index"/>) from its registered table
        /// (<see cref="NinoLayoutOffsets"/>); false, with nothing added, without one or where it cannot be followed.
        /// </summary>
        private static bool TryAddTabled(Type type, int index, List<Measured> values)
        {
            if (NinoLayoutOffsets.Find(type) is not { } table)
                return false;
            int start = values.Count;
            if (AddTabled(type, table, index, values))
                return true;
            values.RemoveRange(start, values.Count - start);
            return false;
        }

        /// <summary>
        /// The fields of <paramref name="type"/> (value <paramref name="index"/>) at the offsets of
        /// <paramref name="table"/>, and the values below each: from its own table, as leaves, or measured on their own
        /// (a struct that is not generic is laid out one way on every runtime). A <c>Nullable</c>'s fields are its flag
        /// and its value.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        private static bool AddTabled(Type type, NinoLayoutOffsets.Table table, int index, List<Measured> values)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fields.Length != table.Offsets.Length ||
                Nullable.GetUnderlyingType(type) != null && fields[0].FieldType != typeof(bool))
                return false;
            for (int i = 0; i < fields.Length; i++)
            {
                var fieldType = NinoReflection.LayoutFieldType(fields[i]);
                if (fieldType.IsPointer)
                    return false;
                int at = values.Count;
                values.Add(new Measured(fieldType, 0, index, table.Offsets[i]));
                var leaf = default(RuntimeView).Leaf(fieldType);
                if (leaf == NinoLeaf.Unsupported)
                    return false;
                if (leaf != NinoLeaf.None)
                    continue;
                if (NinoLayoutOffsets.Find(fieldType) is { } inner)
                {
                    if (!AddTabled(fieldType, inner, at, values))
                        return false;
                    continue;
                }

                var below = MeasureMemory(fieldType, RuntimeSizeOf(fieldType));
                if (below == null)
                    return false;
                for (int j = 1; j < below.Count; j++)
                    values.Add(new Measured(below[j].Key, 0, at, below[j].Value));
            }

            return true;
        }

        /// <summary>
        /// Whether a registered table (<see cref="NinoLayoutOffsets"/>) lays out <paramref name="type"/> or a struct it
        /// holds: its offsets are then measured (<see cref="MeasureMemory"/>), never modeled.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = FieldsJustification)]
        private static bool HoldsTabled(Type type, int depth = 0)
        {
            if (NinoLayoutOffsets.IsEmpty || depth > 32 || !type.IsValueType || type.IsPrimitive || type.IsEnum)
                return false;
            if (NinoLayoutOffsets.Find(type) != null)
                return true;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (HoldsTabled(field.FieldType, depth + 1))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// On IL2CPP, the struct <paramref name="type"/> is or holds whose fields IL2CPP places in two ways
        /// (<see cref="NinoReflection.IsSplitGeneric"/>) and that no generated registry registered offsets for: its
        /// layout cannot be known, and <see cref="NinoLayout{T}"/> refuses it. Null elsewhere and for every other type.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = FieldsJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = FieldsJustification)]
        private static Type UnregisteredSplit(Type type, int depth = 0)
        {
            if (!NinoReflection.IsIl2Cpp || depth > 32 || !type.IsValueType || type.IsPrimitive || type.IsEnum)
                return null;
            if (NinoReflection.IsSplitGeneric(type) && NinoLayoutOffsets.Find(type) == null)
                return type;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (UnregisteredSplit(field.FieldType, depth + 1) is { } found)
                    return found;
            }

            return null;
        }

        /// <summary>A <c>Nullable</c> field whose flag (its first byte) is <paramref name="set"/> in this pass.</summary>
        private static bool VisitNullable(bool set, Type nullable, Type underlying, int bit, List<Measured> values)
        {
            if (NinoLayoutOffsets.Find(nullable) is { } table)
            {
                values.Add(new Measured(nullable, set ? 1 << bit : 0));
                return AddTabled(nullable, table, values.Count - 1, values);
            }

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
            int size = RuntimeSizeOf(underlying);
            valueOffset = RuntimeSizeOf(nullable) - size;
            return valueOffset > 0 ? MeasureMemory(underlying, size) : null;
        }

        /// <summary><c>Unsafe.SizeOf</c> of a type known at run time.</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Measured only where reflection works; null otherwise.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Measured only where reflection works; null otherwise.")]
        private static int RuntimeSizeOf(Type type) =>
            (int)typeof(NinoLayout).GetMethod(nameof(SizeOf), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type).Invoke(null, null)!;

        private static int SizeOf<T>() => Unsafe.SizeOf<T>();
    }
}
