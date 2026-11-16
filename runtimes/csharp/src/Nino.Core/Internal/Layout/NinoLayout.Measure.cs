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
    }
}
