using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    // The fields of a raw type Untrusted mode checks.
    internal static partial class NinoLayout
    {
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
    }
}
