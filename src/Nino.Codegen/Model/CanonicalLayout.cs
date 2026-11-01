using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Nino.Core.Internal;

namespace Nino.Codegen.Model
{
    /// <summary>
    /// The canonical layout (wire-format spec §5.2) of a type of the loaded assembly, by Nino.Core's own algorithm
    /// (<see cref="NinoLayoutCore"/>, linked source) under <see cref="NinoLayoutModel.Canonical"/>, as Nino.Core's
    /// wire layout: what a layout does not publish (a raw type's alignment, which a <c>T?</c> element's stride
    /// depends on).
    /// </summary>
    internal static class CanonicalLayout
    {
        /// <summary>The canonical alignment of <paramref name="type"/>; 0 when it has no canonical layout.</summary>
        public static int Align(Type type) =>
            NinoLayoutCore.Measure(default(View), type, NinoLayoutModel.Canonical, 0, new List<NinoLayoutCore.Leaf>(),
                out _, out int align)
                ? align
                : 0;

        /// <summary><see cref="INinoLayoutView{TType}"/> over the types of a MetadataLoadContext.</summary>
        private struct View : INinoLayoutView<Type>
        {
            public NinoLeaf Leaf(Type type)
            {
                if (type.IsEnum)
                    type = type.GetEnumUnderlyingType();
                if (type.IsPointer)
                    return NinoLeaf.Pointer;
                if (VectorSize(type) > 0)
                    return NinoLeaf.Vector;
                if (CodegenModel.PrimitiveOf(type) is { } primitive)
                    return primitive switch
                    {
                        PrimitiveKind.Decimal => NinoLeaf.Decimal,
                        PrimitiveKind.Guid => NinoLeaf.Guid,
                        PrimitiveKind.DateTime => NinoLeaf.DateTime,
                        PrimitiveKind.I128 or PrimitiveKind.U128 => NinoLeaf.Int128,
                        PrimitiveKind.NInt => NinoLeaf.SignedPointer,
                        PrimitiveKind.NUInt => NinoLeaf.Pointer,
                        _ => CodegenModel.PrimitiveSize(primitive) switch
                        {
                            1 => NinoLeaf.Size1,
                            2 => NinoLeaf.Size2,
                            4 => NinoLeaf.Size4,
                            _ => NinoLeaf.Size8,
                        },
                    };
                // Vector<T> and reference types have no canonical layout.
                return type.IsValueType && !CodegenModel.IsHostSizedVector(type) ? NinoLeaf.None : NinoLeaf.Unsupported;
            }

            public bool TryGetFields(Type type, List<KeyValuePair<Type, int>> fields, out LayoutKind kind, out int pack,
                out int minSize)
            {
                pack = minSize = 0;
                if (type.FullName == "System.DateTimeOffset")
                {
                    // .NET 10's fields, as Nino.Core's canonical layout takes them (.NET 8 has a short for the minutes).
                    kind = LayoutKind.Auto;
                    var core = type.Assembly;
                    fields.Add(new KeyValuePair<Type, int>(core.GetType("System.DateTime")!, -1));
                    fields.Add(new KeyValuePair<Type, int>(core.GetType("System.Int32")!, -1));
                    return true;
                }

                var layout = type.StructLayoutAttribute;
                kind = layout?.Value ?? LayoutKind.Sequential;
                if (layout != null)
                {
                    pack = layout.Pack > 0 ? layout.Pack : 0;
                    minSize = layout.Size;
                }

                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    int offset = -1;
                    if (kind == LayoutKind.Explicit &&
                        field.GetCustomAttributesData().FirstOrDefault(a =>
                            a.AttributeType.FullName == "System.Runtime.InteropServices.FieldOffsetAttribute") is { } at)
                        offset = (int)at.ConstructorArguments[0].Value!;
                    fields.Add(new KeyValuePair<Type, int>(field.FieldType, offset));
                }

                return true;
            }

            public bool IsAutoPrimitive(Type type) => type.IsPrimitive || type.IsEnum || type.IsPointer;

            public bool TryGetVector(Type type, out int size, out int align)
            {
                // x64 CoreCLR's: each aligned to its size.
                size = align = VectorSize(type);
                return size > 0;
            }

            private static int VectorSize(Type type) =>
                type.IsGenericType && type.Namespace == "System.Runtime.Intrinsics"
                    ? type.GetGenericTypeDefinition().Name switch
                    {
                        "Vector64`1" => 8,
                        "Vector128`1" => 16,
                        "Vector256`1" => 32,
                        "Vector512`1" => 64,
                        _ => 0,
                    }
                    : 0;
        }
    }
}
