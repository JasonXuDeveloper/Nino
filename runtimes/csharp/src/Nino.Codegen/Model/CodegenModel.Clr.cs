using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Nino.Codegen.Layouts;

namespace Nino.Codegen.Model
{
    public sealed partial class CodegenModel
    {
        internal static PrimitiveKind? PrimitiveOf(Type clr) => clr.FullName switch
        {
            "System.Boolean" => PrimitiveKind.Bool,
            "System.SByte" => PrimitiveKind.I8,
            "System.Byte" => PrimitiveKind.U8,
            "System.Int16" => PrimitiveKind.I16,
            "System.UInt16" => PrimitiveKind.U16,
            "System.Int32" => PrimitiveKind.I32,
            "System.UInt32" => PrimitiveKind.U32,
            "System.Int64" => PrimitiveKind.I64,
            "System.UInt64" => PrimitiveKind.U64,
            "System.IntPtr" => PrimitiveKind.NInt,
            "System.UIntPtr" => PrimitiveKind.NUInt,
            "System.Half" => PrimitiveKind.F16,
            "System.Single" => PrimitiveKind.F32,
            "System.Double" => PrimitiveKind.F64,
            "System.Char" => PrimitiveKind.Char,
            "System.Decimal" => PrimitiveKind.Decimal,
            "System.Guid" => PrimitiveKind.Guid,
            "System.DateTime" => PrimitiveKind.DateTime,
            "System.TimeSpan" => PrimitiveKind.TimeSpan,
            "System.DateOnly" => PrimitiveKind.DateOnly,
            "System.TimeOnly" => PrimitiveKind.TimeOnly,
            "System.Int128" => PrimitiveKind.I128,
            "System.UInt128" => PrimitiveKind.U128,
            "System.Text.Rune" => PrimitiveKind.Rune,
            _ => null,
        };

        public static int PrimitiveSize(PrimitiveKind kind) => kind switch
        {
            PrimitiveKind.Bool or PrimitiveKind.I8 or PrimitiveKind.U8 => 1,
            PrimitiveKind.I16 or PrimitiveKind.U16 or PrimitiveKind.F16 or PrimitiveKind.Char => 2,
            PrimitiveKind.I32 or PrimitiveKind.U32 or PrimitiveKind.F32 or PrimitiveKind.DateOnly
                or PrimitiveKind.Rune => 4,
            PrimitiveKind.Decimal or PrimitiveKind.Guid or PrimitiveKind.I128 or PrimitiveKind.U128 => 16,
            _ => 8,
        };

        private static string? Generic(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition().FullName : null;

        internal static Type? Implements(Type type, string definition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == definition)
                return type;
            return type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition().FullName == definition);
        }

        private static Type? FieldType(Type type, string name)
        {
            // DateTimeOffset's layout publishes its canonical fields (spec §5.2: .NET 10's, the minutes an int at 0, the
            // UTC ticks at 8), which a runtime declares its own way (.NET 8 holds a short, Mono puts it at 8).
            if (type.FullName == "System.DateTimeOffset")
                return name switch
                {
                    "offsetMinutes" => type.Assembly.GetType("System.Int32"),
                    "dateTime" => type.Assembly.GetType("System.DateTime"),
                    _ => null,
                };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            return (type.GetField(name, flags) ?? type.GetField($"<{name}>k__BackingField", flags))?.FieldType;
        }
    }
}
