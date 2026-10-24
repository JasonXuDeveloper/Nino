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
/// <para>
/// Projects compile against reference assemblies, where a struct's private fields are replaced by placeholders
/// (<c>int _dummyPrimitive</c>, <c>object _dummy</c>): the BCL structs whose fields are private take their layout from
/// <see cref="Leaf"/> and <see cref="Known"/> instead, and any other struct with a placeholder has no known layout.
/// </para>
/// </summary>
internal static class RawLayout
{
    public static bool IsPortable(ITypeSymbol type)
    {
        var wide = new List<NinoLayoutCore.Leaf>();
        var narrow = new List<NinoLayoutCore.Leaf>();
        var view = new SymbolView(type);
        return NinoLayoutCore.Measure(view, type, 8, 8, 8, 0, wide, out int wideSize, out _) &&
               NinoLayoutCore.Measure(view, type, 4, 4, 4, 0, narrow, out int narrowSize, out _) &&
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
        if (!NinoLayoutCore.Measure(new SymbolView(type), type, 8, 8, 8, 0, new List<NinoLayoutCore.Leaf>(),
                out int size, out _, offsets))
            return false;
        sb.Append("raw(").Append(size.ToString(CultureInfo.InvariantCulture)).Append(')');
        if (Leaf(type) != NinoLeaf.None)
            return true;

        var fields = Fields(type)!;
        sb.Append('{');
        for (int i = 0; i < fields.Count; i++)
        {
            if (i != 0)
                sb.Append(',');
            var field = fields[i];
            sb.Append(field.Name).Append(':')
                .Append(TypeNames.Plain(TypeNames.Global(field.Type))).Append('@')
                .Append(offsets[i].ToString(CultureInfo.InvariantCulture)).Append(':');
            if (!Describe(field.Type, sb))
                return false;
        }

        sb.Append('}');
        return true;
    }

    /// <summary>
    /// How a raw value of <paramref name="type"/> is laid out when it is not a struct of its own fields: primitives,
    /// enums (as their underlying type), <c>decimal</c>, <c>DateTime</c>, <c>TimeSpan</c>, <c>Guid</c> and the BCL
    /// structs of one private primitive field (<c>Half</c>, <c>Rune</c>, <c>DateOnly</c>, <c>TimeOnly</c>,
    /// <c>Index</c>) are leaves, pointer-sized and reference types are not supported. The one table of the primitive
    /// sizes the generator uses (<see cref="SizeOf"/>, <c>LayoutTypes</c>), as <c>NinoLayout</c>'s is the runtime's.
    /// </summary>
    public static NinoLeaf Leaf(ITypeSymbol type)
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
        if (definition.Arity != 0)
            return NinoLeaf.None;
        // Their one field is private: reference assemblies show a placeholder in its place.
        return definition.MetadataName switch
        {
            "Half" when TypeNames.HasMetadataName(definition, "System.Half") => NinoLeaf.Size2, // ushort _value
            "DateOnly" when TypeNames.HasMetadataName(definition, "System.DateOnly") => NinoLeaf.Size4, // uint
            "Index" when TypeNames.HasMetadataName(definition, "System.Index") => NinoLeaf.Size4, // int _value
            "Rune" when TypeNames.HasMetadataName(definition, "System.Text.Rune") => NinoLeaf.Size4, // uint _value
            "TimeOnly" when TypeNames.HasMetadataName(definition, "System.TimeOnly") => NinoLeaf.Size8, // ulong
            "TimeSpan" when TypeNames.HasMetadataName(definition, "System.TimeSpan") => NinoLeaf.Size8,
            "Guid" when TypeNames.HasMetadataName(definition, "System.Guid") => NinoLeaf.Guid,
            _ => NinoLeaf.None,
        };
    }

    /// <summary>
    /// Size of a raw unmanaged type: a constant for the leaves (<see cref="Leaf"/>: primitives, enums and the BCL
    /// structs whose size and layout are the same on every runtime), symbolic for other structs. A constant saves
    /// interpreted code (HybridCLR, Mono's interpreter) a <c>SizeOf</c> call per member.
    /// </summary>
    public static RawSize SizeOf(ITypeSymbol type)
    {
        int bytes = Leaf(type) switch
        {
            NinoLeaf.Size1 => 1,
            NinoLeaf.Size2 => 2,
            NinoLeaf.Size4 => 4,
            NinoLeaf.Size8 => 8,
            NinoLeaf.Decimal or NinoLeaf.Guid => 16,
            _ => 0,
        };
        return bytes != 0
            ? RawSize.Constant(bytes)
            : new RawSize(0, $"{TypeNames.CoreGenerated}NinoUnsafe.SizeOf<{TypeNames.Global(type)}>()");
    }

    /// <summary>A struct's field as laid out: its name (its property's for a backing field), type and symbol.</summary>
    private readonly record struct Field(string Name, ITypeSymbol Type, IFieldSymbol? Symbol);

    /// <summary>
    /// The instance fields of a struct, in declaration (metadata) order, or null when they are not known: a struct
    /// read from a reference assembly, whose private fields are placeholders, unless <see cref="Known"/> lists them.
    /// </summary>
    private static List<Field>? Fields(ITypeSymbol type)
    {
        if (Known(type) is { } known)
            return known;
        var fields = new List<Field>();
        foreach (var member in type.GetMembers())
        {
            if (member is not IFieldSymbol { IsStatic: false, IsConst: false } field)
                continue;
            if (field.Name is "_dummyPrimitive" or "_dummy" && field.DeclaredAccessibility == Accessibility.Private &&
                !field.Locations.Any(static l => l.IsInSource))
                return null;
            fields.Add(new Field((field.AssociatedMember() ?? field).Name, field.Type, field));
        }

        return fields;
    }

    /// <summary>
    /// The fields of the BCL structs of several private fields, as the runtimes declare them (reference assemblies
    /// show placeholders): <c>Nullable&lt;T&gt;</c>, <c>Complex</c>, <c>Range</c>, <c>Int128</c> and <c>UInt128</c>
    /// (little endian). Null for any other type.
    /// </summary>
    private static List<Field>? Known(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named)
            return null;
        var definition = named.OriginalDefinition;
        if (definition.SpecialType == SpecialType.System_Nullable_T)
            return Pair(PropertyType(named, "HasValue"), "hasValue", named.TypeArguments[0], "value");
        if (TypeNames.HasMetadataName(definition, "System.Numerics.Complex") &&
            PropertyType(named, "Real") is { } real)
            return Pair(real, "m_real", real, "m_imaginary");
        if (TypeNames.HasMetadataName(definition, "System.Range") && PropertyType(named, "Start") is { } index)
            return Pair(index, "Start", index, "End");
        if ((TypeNames.HasMetadataName(definition, "System.Int128") ||
             TypeNames.HasMetadataName(definition, "System.UInt128")) &&
            definition.BaseType?.ContainingAssembly.GetTypeByMetadataName("System.UInt64") is { } ulongType)
            return Pair(ulongType, "_lower", ulongType, "_upper");
        return null;

        static List<Field>? Pair(ITypeSymbol? first, string firstName, ITypeSymbol second, string secondName) =>
            first == null
                ? null
                : new List<Field> { new(firstName, first, null), new(secondName, second, null) };
    }

    private static ITypeSymbol? PropertyType(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault()?.Type;

    /// <summary>
    /// <c>Int128</c> and <c>UInt128</c>: 16-byte aligned on .NET 8 and later (64-bit), 8-byte aligned before, so their
    /// offset in another struct differs by runtime; on their own they are 16 bytes everywhere.
    /// </summary>
    private static bool IsWide(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct, Arity: 0 } named &&
        (TypeNames.HasMetadataName(named, "System.Int128") || TypeNames.HasMetadataName(named, "System.UInt128"));

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

    /// <summary>
    /// The generator's view of a type: pointer-sized fields are never portable, and neither is a struct with fields of
    /// unknown layout (<see cref="Fields"/>) or with an <c>Int128</c> in it (<see cref="IsWide"/>; one on its own,
    /// <paramref name="top"/>, is).
    /// </summary>
    private readonly struct SymbolView(ITypeSymbol top) : INinoLayoutView<ITypeSymbol>
    {
        public NinoLeaf Leaf(ITypeSymbol type) => RawLayout.Leaf(type);

        public bool Struct(ITypeSymbol type, List<KeyValuePair<ITypeSymbol, int>> fields, out LayoutKind kind,
            out int pack, out int minSize)
        {
            // A known layout is sequential, without Pack or Size.
            var metadata = ReadLayout(type, out kind, out pack, out minSize);
            if (Known(type) != null)
            {
                metadata = null;
                kind = LayoutKind.Sequential;
                pack = minSize = 0;
            }

            if ((IsWide(type) && !SymbolEqualityComparer.Default.Equals(type, top)) || Fields(type) is not { } own)
                return false;
            foreach (var field in own)
            {
                fields.Add(new KeyValuePair<ITypeSymbol, int>(field.Type,
                    field.Symbol == null ? -1 : Offset(field.Symbol, metadata)));
            }

            return true;
        }

        /// <summary>A field's explicit offset (<c>[FieldOffset]</c>, or the field layout table), or -1.</summary>
        private static int Offset(IFieldSymbol field, MetadataReader? metadata)
        {
            if (metadata != null)
            {
                return metadata.GetFieldDefinition(
                    MetadataTokens.FieldDefinitionHandle(field.OriginalDefinition.MetadataToken)).GetOffset();
            }

            int offset = -1;
            foreach (var attribute in field.GetAttributes())
            {
                if (attribute.AttributeClass is { } cls &&
                    TypeNames.HasMetadataName(cls, "System.Runtime.InteropServices.FieldOffsetAttribute") &&
                    attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is int o)
                    offset = o;
            }

            return offset;
        }
    }
}
