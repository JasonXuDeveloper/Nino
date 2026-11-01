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
/// The memory of raw structs, measured with the runtime's own rules (<see cref="NinoLayoutCore"/>, shared source).
/// <see cref="IsPortable"/>: a struct's field offsets and size are the same under the 8-byte model (64-bit, ARM,
/// wasm) and the 4-byte model (32-bit x86, Mono/IL2CPP's <c>decimal</c>), so its memory is copied as it lies on every
/// runtime. <see cref="Describe"/>: the canonical layout (64-bit CoreCLR's, auto-layout structs such as value tuples
/// placed as it places them), which every runtime converts to where its own differs and the published layout states.
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
        var view = new SymbolView(type, canonical: false);
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
        // Auto-layout structs (value tuples, DateTimeOffset) placed as 64-bit CoreCLR places them: the canonical layout.
        var view = new SymbolView(type, canonical: true);
        if (!NinoLayoutCore.Measure(view, type, 8, 8, 8, 0, new List<NinoLayoutCore.Leaf>(),
                out int size, out _, offsets, autoPointerSize: 8))
            return false;
        sb.Append("raw(").Append(size.ToString(CultureInfo.InvariantCulture)).Append(')');
        // Int128 is a leaf of its own alignment (16) but still published as its two halves, as it always was.
        bool int128 = IsInt128(type);
        if (view.Leaf(type) != NinoLeaf.None && !int128)
            return true;

        var fields = Fields(type)!;
        if (int128)
            offsets = new List<int> { 0, 8 };
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
        if (IsVector(definition))
            return NinoLeaf.Vector;
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
        var size = bytes != 0
            ? RawSize.Constant(bytes)
            : new RawSize(0, $"{TypeNames.CoreCompilerServices}NinoUnsafe.SizeOf<{TypeNames.Global(type)}>()");
        return size with { Read = ReadOf(type, size) };
    }

    /// <summary>How a raw member of <paramref name="type"/>, of <paramref name="size"/>, is read (<see cref="RawRead"/>).</summary>
    private static RawRead ReadOf(ITypeSymbol type, RawSize size)
    {
        if (type.SpecialType == SpecialType.System_Boolean)
            return RawRead.Bool;
        if (!size.IsConstant || type.SpecialType is SpecialType.System_DateTime or SpecialType.System_Decimal)
            return RawRead.Checked;
        return type is INamedTypeSymbol named &&
               (TypeNames.HasMetadataName(named.OriginalDefinition, "System.DateOnly") ||
                TypeNames.HasMetadataName(named.OriginalDefinition, "System.TimeOnly"))
            ? RawRead.Checked
            : RawRead.Plain;
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
    /// show placeholders): <c>Nullable&lt;T&gt;</c>, <c>KeyValuePair&lt;TKey, TValue&gt;</c>, <c>Complex</c>,
    /// <c>Range</c>, <c>Int128</c> and <c>UInt128</c> (little endian), and <c>DateTimeOffset</c>. Null for any other
    /// type.
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
        if (TypeNames.HasMetadataName(definition, "System.Collections.Generic.KeyValuePair`2"))
            return Pair(named.TypeArguments[0], "key", named.TypeArguments[1], "value");
        if (TypeNames.HasMetadataName(definition, "System.Range") && PropertyType(named, "Start") is { } index)
            return Pair(index, "Start", index, "End");
        if ((TypeNames.HasMetadataName(definition, "System.Int128") ||
             TypeNames.HasMetadataName(definition, "System.UInt128")) &&
            definition.BaseType?.ContainingAssembly.GetTypeByMetadataName("System.UInt64") is { } ulongType)
            return Pair(ulongType, "_lower", ulongType, "_upper");
        // Auto layout (SymbolView.Struct), the minutes an int as .NET 10 declares them: the canonical layout.
        if (IsDateTimeOffset(definition) && definition.ContainingAssembly.GetTypeByMetadataName("System.Int32") is { } i32 &&
            definition.ContainingAssembly.GetTypeByMetadataName("System.DateTime") is { } dateTime)
            return Pair(dateTime, "dateTime", i32, "offsetMinutes");
        return null;

        static List<Field>? Pair(ITypeSymbol? first, string firstName, ITypeSymbol second, string secondName) =>
            first == null
                ? null
                : new List<Field> { new(firstName, first, null), new(secondName, second, null) };
    }

    /// <summary>
    /// <c>Vector64&lt;T&gt;</c> to <c>Vector512&lt;T&gt;</c> (<see cref="NinoLeaf.Vector"/>; the runtime's is
    /// <c>NinoLayout.VectorIndex</c>). <c>Vector&lt;T&gt;</c> never gets here: no type holding one is raw
    /// (<see cref="RawRules.HostSizedVectorIn"/>).
    /// </summary>
    private static bool IsVector(INamedTypeSymbol definition) =>
        definition.Arity == 1 && definition.MetadataName is "Vector64`1" or "Vector128`1" or "Vector256`1" or "Vector512`1" &&
        TypeNames.HasMetadataName(definition, "System.Runtime.Intrinsics." + definition.MetadataName);

    private static bool IsDateTimeOffset(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct, Arity: 0 } named &&
        TypeNames.HasMetadataName(named, "System.DateTimeOffset");

    /// <summary><c>System.ValueTuple</c> of any arity: <c>[StructLayout(LayoutKind.Auto)]</c> in every runtime.</summary>
    private static bool IsValueTuple(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Struct } named &&
        named.OriginalDefinition.ContainingNamespace?.ToDisplayString() == "System" &&
        named.OriginalDefinition.MetadataName.StartsWith("ValueTuple", System.StringComparison.Ordinal);

    private static ITypeSymbol? PropertyType(INamedTypeSymbol type, string name) =>
        type.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault()?.Type;

    /// <summary>
    /// <c>Int128</c> and <c>UInt128</c>: 16-byte aligned on .NET 8 and later (64-bit), 8-byte aligned before, so their
    /// offset in another struct differs by runtime; on their own they are 16 bytes everywhere.
    /// </summary>
    private static bool IsInt128(ITypeSymbol type) =>
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
    /// The generator's view of a type. For portability (<see cref="IsPortable"/>): pointer-sized fields are never
    /// portable, and neither is a struct with fields of unknown layout (<see cref="Fields"/>) or with an
    /// <c>Int128</c> in it (<see cref="IsInt128"/>; one on its own, <paramref name="top"/>, is). For the canonical
    /// layout (<paramref name="canonical"/>, <see cref="Describe"/>): <c>nint</c>, <c>nuint</c> and pointers are 8
    /// bytes, <c>Int128</c> and <c>UInt128</c> 16 aligned to 16, as on 64-bit .NET 8 and later (spec §5.2).
    /// </summary>
    private readonly struct SymbolView(ITypeSymbol top, bool canonical) : INinoLayoutView<ITypeSymbol>
    {
        public NinoLeaf Leaf(ITypeSymbol type)
        {
            if (canonical)
            {
                if (type.SpecialType == SpecialType.System_IntPtr)
                    return NinoLeaf.SignedPointer;
                if (type.SpecialType == SpecialType.System_UIntPtr || type is IPointerTypeSymbol)
                    return NinoLeaf.Pointer;
                if (IsInt128(type))
                    return NinoLeaf.Wide;
            }

            return RawLayout.Leaf(type);
        }

        // For the canonical layout Describe publishes (a struct with several fields of an auto layout is never portable).
        // SIMD vectors are aligned per runtime (Vector256 at 16 on Arm64): never portable, and their canonical layout is
        // Nino.Core's (NinoLayout), which nino-gen does not express.
        public bool Vector(ITypeSymbol type, out int size, out int align)
        {
            size = align = 0;
            return false;
        }

        public bool Primitive(ITypeSymbol type) =>
            type.TypeKind is TypeKind.Enum or TypeKind.Pointer ||
            type.SpecialType is >= SpecialType.System_Boolean and <= SpecialType.System_UInt64 or
                SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_IntPtr or
                SpecialType.System_UIntPtr;

        public bool Struct(ITypeSymbol type, List<KeyValuePair<ITypeSymbol, int>> fields, out LayoutKind kind,
            out int pack, out int minSize)
        {
            // A known layout is sequential, without Pack or Size; the value tuples are auto-layout, which reference
            // assemblies do not show (they declare them sequential).
            var metadata = ReadLayout(type, out kind, out pack, out minSize);
            if (Known(type) != null || IsValueTuple(type))
            {
                metadata = null;
                kind = IsValueTuple(type) || IsDateTimeOffset(type) ? LayoutKind.Auto : LayoutKind.Sequential;
                pack = minSize = 0;
            }

            if ((IsInt128(type) && !SymbolEqualityComparer.Default.Equals(type, top)) || Fields(type) is not { } own)
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
