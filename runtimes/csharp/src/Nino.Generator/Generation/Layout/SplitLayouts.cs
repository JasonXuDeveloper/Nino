using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// The generic structs (<c>T?</c> included) whose fields IL2CPP places in two ways: computed at run time for reflection
/// and shared generic code, and by the C++ compiler for the code compiled for the type. They differ where the struct's
/// <c>Pack</c> is below its fields' alignment, or where it holds a struct whose <c>Pack</c> moves a struct field
/// (Nino.Core's <c>NinoReflection.IsSplitGeneric</c>, the same rule over symbols; unity.yml runs 44102730242 and
/// 44108765619). The registry registers each one's compiled offsets (<c>NinoLayoutOffsets</c>), in compiled code, so
/// Nino.Core lays out the values its generated formatters copy as that code does; and a top-level call of a raw
/// value holding one registers its formatter, so the formatter is compiled code too.
/// </summary>
internal static class SplitLayouts
{
    // Bounded for recursive generic structs (S<T> holding an S<S<T>>...); real nesting is far shallower.
    private const int MaxDepth = 8;

    /// <summary>
    /// Adds the statements registering the offsets of the split structs <paramref name="type"/> names (itself, type
    /// arguments, elements, fields) to <paramref name="into"/>, for those generated code can name with every field.
    /// </summary>
    public static void Collect(ITypeSymbol type, TypeFacts facts, ICollection<string> into)
    {
        HashSet<ITypeSymbol>? visited = null;
        Walk(type, facts, into, ref visited, 0);
    }

    /// <summary>Whether <paramref name="type"/> is or holds a split struct (<see cref="Collect"/> finds one).</summary>
    public static bool Holds(ITypeSymbol type, TypeFacts facts)
    {
        var found = new List<string>();
        Collect(type, facts, found);
        return found.Count != 0;
    }

    private static void Walk(ITypeSymbol type, TypeFacts facts, ICollection<string> into,
        ref HashSet<ITypeSymbol>? visited, int depth)
    {
        if (depth > MaxDepth)
            return;
        while (type is IArrayTypeSymbol array)
            type = array.ElementType;
        if (type is not INamedTypeSymbol named || TypeNames.ContainsTypeParameter(named))
            return;
        if (!(visited ??= new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default)).Add(named))
            return;
        if (named.IsGenericType)
        {
            foreach (var argument in TypeNames.AllTypeArguments(named))
                Walk(argument, facts, into, ref visited, depth + 1);
        }

        if (named.TypeKind != TypeKind.Struct)
            return;
        if (IsSplitGeneric(named) && Registration(named, facts) is { } registration)
            into.Add(registration);
        foreach (var field in InstanceFields(named))
            Walk(field.Type, facts, into, ref visited, depth + 1);
    }

    /// <summary>The statement registering <paramref name="type"/>'s compiled offsets, or null where it cannot be named.</summary>
    private static string? Registration(INamedTypeSymbol type, TypeFacts facts)
    {
        if (!facts.Visibility.CanName(type))
            return null;
        const string offsets = "global::Nino.Core.CompilerServices.NinoLayoutOffsets";
        if (IsNullable(type))
            return $"{offsets}.RegisterNullable<{TypeNames.Global(type.TypeArguments[0])}>();";
        var fields = InstanceFields(type).ToList();
        if (fields.Count == 0 || fields.Any(f => f.IsFixedSizeBuffer || f.IsImplicitlyDeclared ||
                                                 !facts.Visibility.IsVisible(f)))
            return null;
        string name = TypeNames.Global(type);
        var of = string.Join(", ", fields.Select(f => $"{offsets}.Of(ref v, ref v.@{f.Name})"));
        return $"{{ var v = default({name}); {offsets}.Register<{name}>({of}); }}";
    }

    /// <summary>
    /// Whether <paramref name="type"/> is a closed generic struct whose <c>Pack</c> is below its fields' alignment or
    /// that holds a struct whose <c>Pack</c> moves a struct field.
    /// </summary>
    internal static bool IsSplitGeneric(INamedTypeSymbol type) =>
        type is { TypeKind: TypeKind.Struct, IsGenericType: true } && !TypeNames.ContainsTypeParameter(type) &&
        (UnderPacked(type) || HoldsUnderPacked(type, 0));

    private static bool HoldsUnderPacked(INamedTypeSymbol type, int depth)
    {
        if (depth > 32)
            return false;
        foreach (var fieldType in FieldTypes(type))
        {
            if (fieldType is INamedTypeSymbol named && IsPlainStruct(named) &&
                (UnderPacked(named) || HoldsUnderPacked(named, depth + 1)))
                return true;
        }

        return false;
    }

    private static bool UnderPacked(INamedTypeSymbol type)
    {
        if (RawLayout.KindOf(type) != LayoutKind.Sequential || RawLayout.PackOf(type) is not (> 0 and var pack))
            return false;
        if (type.IsGenericType && pack < AlignOf(type, 0))
            return true;
        foreach (var field in InstanceFields(type))
        {
            if (!field.IsFixedSizeBuffer && field.Type is INamedTypeSymbol named && IsPlainStruct(named) &&
                pack < AlignOf(named, 0))
                return true;
        }

        return false;
    }

    /// <summary>A struct other than a primitive or an enum (fixed buffers are left out by the callers).</summary>
    private static bool IsPlainStruct(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Struct && Primitive(type) == 0;

    /// <summary>The largest primitive a value of <paramref name="type"/> holds (its natural alignment, at most 8).</summary>
    private static int AlignOf(ITypeSymbol type, int depth)
    {
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
            type = underlying;
        if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
            return 8;
        if (Primitive(type) is > 0 and var size)
            return size;
        if (type is not INamedTypeSymbol { TypeKind: TypeKind.Struct } named || depth > 32)
            return 8;
        switch (RawLayout.Leaf(named))
        {
            case NinoLeaf.Size1:
                return 1;
            case NinoLeaf.Size2:
                return 2;
            case NinoLeaf.Size4:
            case NinoLeaf.Guid:
                return 4;
            case NinoLeaf.None:
                break;
            default:
                return 8;
        }

        int align = 1;
        bool any = false;
        foreach (var fieldType in FieldTypes(named))
        {
            any = true;
            align = System.Math.Max(align, AlignOf(fieldType, depth + 1));
        }

        // A struct read from metadata without its private fields (DateTimeOffset): as aligned as a long.
        return any ? align : 8;
    }

    /// <summary>The size of a primitive (its alignment), 0 for any other type.</summary>
    private static int Primitive(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte => 1,
        SpecialType.System_Char or SpecialType.System_Int16 or SpecialType.System_UInt16 => 2,
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Single => 4,
        SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Double => 8,
        SpecialType.System_IntPtr or SpecialType.System_UIntPtr => 8,
        _ => 0,
    };

    /// <summary>
    /// The types of <paramref name="type"/>'s instance fields; for a <c>T?</c> and a struct read from metadata
    /// without its private fields, its type arguments (what those fields hold).
    /// </summary>
    private static IEnumerable<ITypeSymbol> FieldTypes(INamedTypeSymbol type)
    {
        if (IsNullable(type))
            return type.TypeArguments;
        var fields = InstanceFields(type).Where(f => !f.IsFixedSizeBuffer).Select(f => f.Type).ToList();
        return fields.Count == 0 && type.IsGenericType ? TypeNames.AllTypeArguments(type) : fields;
    }

    private static IEnumerable<IFieldSymbol> InstanceFields(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IFieldSymbol>().Where(static f => !f.IsStatic && !f.IsConst);

    private static bool IsNullable(INamedTypeSymbol type) =>
        type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
}
