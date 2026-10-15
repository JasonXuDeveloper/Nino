using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Decides whether a struct's raw memory is the same on every runtime Nino targets. Layouts differ in two ways:
/// x86 .NET/Mono align 8-byte primitives to 4 (64-bit, ARM and wasm align them to 8), and Mono/IL2CPP's
/// <c>decimal</c> is four ints (alignment 4) where CoreCLR's has a <c>ulong</c> (alignment 8). A struct is portable
/// when its field offsets and size come out the same under both models, and it has no pointer-sized fields. The
/// layout rules are the runtime's own (<see cref="NinoLayoutCore"/>, shared source).
/// </summary>
internal static class RawLayout
{
    public static bool IsPortable(ITypeSymbol type)
    {
        var wide = new List<NinoLayoutCore.Leaf>();
        var narrow = new List<NinoLayoutCore.Leaf>();
        return NinoLayoutCore.Measure<ITypeSymbol, SymbolView>(default, type, 8, 8, 8, 0, wide, out int wideSize,
                   out _) &&
               NinoLayoutCore.Measure<ITypeSymbol, SymbolView>(default, type, 4, 4, 4, 0, narrow,
                   out int narrowSize, out _) &&
               wideSize == narrowSize && wide.Select(l => l.Offset).SequenceEqual(narrow.Select(l => l.Offset));
    }

    /// <summary>The generator's view of a type: pointer-sized fields are never portable.</summary>
    private struct SymbolView : INinoLayoutView<ITypeSymbol>
    {
        public NinoLeaf Leaf(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
                type = underlying;
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                    return NinoLeaf.Size1;
                case SpecialType.System_Char:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                    return NinoLeaf.Size2;
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Single:
                    return NinoLeaf.Size4;
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Double:
                case SpecialType.System_DateTime:
                    return NinoLeaf.Size8;
                case SpecialType.System_Decimal:
                    return NinoLeaf.Decimal;
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                    return NinoLeaf.Unsupported;
            }

            if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named)
                return NinoLeaf.Unsupported;
            var definition = named.OriginalDefinition;
            if (TypeNames.HasMetadataName(definition, "System.TimeSpan"))
                return NinoLeaf.Size8;
            if (TypeNames.HasMetadataName(definition, "System.Guid"))
                return NinoLeaf.Guid;
            return NinoLeaf.None;
        }

        public bool Struct(ITypeSymbol type, List<KeyValuePair<ITypeSymbol, int>> fields, out LayoutKind kind,
            out int pack, out int minSize)
        {
            // [StructLayout] and [FieldOffset] are visible on source types; metadata types are sequential (what the
            // compiler emits for C# structs) as far as symbols tell.
            kind = LayoutKind.Sequential;
            pack = minSize = 0;
            foreach (var attribute in type.GetAttributes())
            {
                if (attribute.AttributeClass is not { } cls ||
                    !TypeNames.HasMetadataName(cls, "System.Runtime.InteropServices.StructLayoutAttribute"))
                    continue;
                if (attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int k)
                    kind = (LayoutKind)k;
                foreach (var named in attribute.NamedArguments)
                {
                    if (named.Value.Value is not int value)
                        continue;
                    if (named.Key == "Pack")
                        pack = value;
                    else if (named.Key == "Size")
                        minSize = value;
                }
            }

            foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsConst)
                    continue;
                int offset = -1;
                foreach (var attribute in field.GetAttributes())
                {
                    if (attribute.AttributeClass is { } cls &&
                        TypeNames.HasMetadataName(cls, "System.Runtime.InteropServices.FieldOffsetAttribute") &&
                        attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int o)
                        offset = o;
                }

                fields.Add(new KeyValuePair<ITypeSymbol, int>(field.Type, offset));
            }

            return true;
        }
    }
}
