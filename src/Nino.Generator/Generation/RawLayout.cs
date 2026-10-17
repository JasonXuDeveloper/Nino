using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Decides whether a struct's raw memory is the same on every runtime Nino targets. Layouts differ in two ways:
/// x86 .NET/Mono align 8-byte primitives to 4 (64-bit, ARM and wasm align them to 8), and Mono/IL2CPP's
/// <c>decimal</c> is four ints (alignment 4) where CoreCLR's has a <c>ulong</c> (alignment 8). A struct is portable
/// when its field offsets and size come out the same under both models, and it has no pointer-sized fields. The
/// layout rules are the runtime's own (<see cref="NinoLayoutCore"/>, shared source), and so is the canonical layout
/// <see cref="Describe"/> publishes.
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

    /// <summary>
    /// The bytes of a raw value of <paramref name="type"/> in the canonical layout, for its published layout
    /// (<c>NinoTypeLayoutAttribute</c>): <c>raw(size)</c> for a primitive, enum, <c>decimal</c>, <c>Guid</c>,
    /// <c>DateTime</c> or <c>TimeSpan</c>; <c>raw(size){name:type@offset:encoding,...}</c> for a struct, its fields
    /// in memory order, each described the same way; <c>raw</c> when the layout is not known at compile time (a type
    /// parameter). Anything that changes the copied bytes (a field, <c>Pack</c>, <c>Size</c>, an explicit offset)
    /// changes the string.
    /// </summary>
    public static string Describe(ITypeSymbol type)
    {
        var sb = new StringBuilder();
        return Describe(type, sb) ? sb.ToString() : "raw";
    }

    private static bool Describe(ITypeSymbol type, StringBuilder sb)
    {
        var offsets = new List<int>();
        if (!NinoLayoutCore.Measure<ITypeSymbol, SymbolView>(default, type, 8, 8, 8, 0,
                new List<NinoLayoutCore.Leaf>(), out int size, out _, offsets))
            return false;
        sb.Append("raw(").Append(size.ToString(CultureInfo.InvariantCulture)).Append(')');
        if (default(SymbolView).Leaf(type) != NinoLeaf.None)
            return true;

        var fields = Fields(type);
        sb.Append('{');
        for (int i = 0; i < fields.Count; i++)
        {
            if (i != 0)
                sb.Append(',');
            var field = fields[i];
            sb.Append((field.AssociatedMember() ?? field).Name).Append(':')
                .Append(TypeNames.Global(field.Type).Replace("global::", "")).Append('@')
                .Append(offsets[i].ToString(CultureInfo.InvariantCulture)).Append(':');
            if (!Describe(field.Type, sb))
                return false;
        }

        sb.Append('}');
        return true;
    }

    /// <summary>The instance fields of a struct, in declaration (metadata) order.</summary>
    private static List<IFieldSymbol> Fields(ITypeSymbol type) =>
        type.GetMembers().OfType<IFieldSymbol>().Where(static f => !f.IsStatic && !f.IsConst).ToList();

    /// <summary>
    /// The definition of a type or field in the metadata it was read from, or null for source. <c>[StructLayout]</c>
    /// and <c>[FieldOffset]</c> are not custom attributes in metadata, so symbols of referenced assemblies do not
    /// show them; their layout is read from the type and field tables instead.
    /// </summary>
    private static MetadataReader? Metadata(ISymbol symbol)
    {
        if (symbol.MetadataToken == 0 || symbol.Locations.Any(static l => l.IsInSource))
            return null;
        try
        {
            return symbol.ContainingModule?.GetMetadata()?.GetMetadataReader();
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>[StructLayout]</c> kind of a struct, from source or metadata alike (a raw <c>[NinoType]</c> struct
    /// must be sequential).
    /// </summary>
    public static LayoutKind KindOf(ITypeSymbol type)
    {
        ReadLayout(type, out var kind, out _, out _);
        return kind;
    }

    /// <summary>
    /// Reads a struct's <c>[StructLayout]</c>: from the type table when the type comes from metadata (whose reader is
    /// returned, for the field offsets), else from its attributes.
    /// </summary>
    private static MetadataReader? ReadLayout(ITypeSymbol type, out LayoutKind kind, out int pack, out int minSize)
    {
        kind = LayoutKind.Sequential;
        pack = minSize = 0;
        var definition = type.OriginalDefinition;
        var metadata = Metadata(definition);
        if (metadata != null)
        {
            var typeDef = metadata.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(definition.MetadataToken));
            kind = (typeDef.Attributes & TypeAttributes.LayoutMask) switch
            {
                TypeAttributes.ExplicitLayout => LayoutKind.Explicit,
                TypeAttributes.SequentialLayout => LayoutKind.Sequential,
                _ => LayoutKind.Auto,
            };
            var layout = typeDef.GetLayout();
            pack = layout.PackingSize;
            minSize = layout.Size;
            return metadata;
        }

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

        return null;
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
            var metadata = ReadLayout(type, out kind, out pack, out minSize);
            foreach (var field in Fields(type))
            {
                int offset = -1;
                if (metadata != null)
                {
                    offset = metadata.GetFieldDefinition(
                        MetadataTokens.FieldDefinitionHandle(field.OriginalDefinition.MetadataToken)).GetOffset();
                }
                else
                {
                    foreach (var attribute in field.GetAttributes())
                    {
                        if (attribute.AttributeClass is { } cls &&
                            TypeNames.HasMetadataName(cls, "System.Runtime.InteropServices.FieldOffsetAttribute") &&
                            attribute.ConstructorArguments.Length == 1 &&
                            attribute.ConstructorArguments[0].Value is int o)
                            offset = o;
                    }
                }

                fields.Add(new KeyValuePair<ITypeSymbol, int>(field.Type, offset));
            }

            return true;
        }
    }
}
