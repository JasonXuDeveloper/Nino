using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Common;

/// <summary>
/// Which <c>[NinoType]</c> applies to a type. Context-free (no compilation needed), since it also runs over the
/// types of referenced assemblies.
/// </summary>
internal static class NinoTypeHelper
{
    public static bool IsNinoType(this ITypeSymbol typeSymbol)
    {
        return GetNinoTypeOptions(typeSymbol) != null;
    }

    // The options of a type depend on its symbol alone, and every model build, raw check and member collection asks
    // for those of the same types (each walks the bases and interfaces): memoized per symbol instance.
    private sealed class Options
    {
        public NinoTypeOptions? Value;
    }

    private static readonly ConditionalWeakTable<ITypeSymbol, Options> Memo = new();

    /// <summary>
    /// The NinoType attribute that applies to the type: its own, then the nearest base class's (if it allows
    /// inheritance), then an interface's (if it allows inheritance).
    /// </summary>
    public static NinoTypeOptions? GetNinoTypeOptions(this ITypeSymbol typeSymbol) =>
        Memo.GetValue(typeSymbol, static t => new Options { Value = ComputeOptions(t) }).Value;

    /// <summary>The <c>[NinoType]</c> base classes of the type (nearest first), then its <c>[NinoType]</c> interfaces.</summary>
    public static IEnumerable<INamedTypeSymbol> NinoAncestors(this ITypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.IsNinoType())
                yield return b;
        }

        foreach (var i in type.AllInterfaces)
        {
            if (i.IsNinoType())
                yield return i;
        }
    }

    private static NinoTypeOptions? ComputeOptions(ITypeSymbol typeSymbol)
    {
        // BCL types are never NinoTypes; skip walking their (substituted) interfaces.
        if (IsSystemNamespace(typeSymbol.ContainingNamespace) || typeSymbol.TypeKind is TypeKind.Array or
                TypeKind.Pointer or TypeKind.TypeParameter or TypeKind.Enum or TypeKind.Delegate or TypeKind.Error)
            return null;

        var attr = GetDirectNinoTypeAttribute(typeSymbol);
        if (attr != null)
            return NinoTypeOptions.From(attr);

        for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            attr = GetDirectNinoTypeAttribute(baseType);
            if (attr == null)
                continue;
            var ninoTypeAttr = NinoTypeOptions.From(attr);
            if (ninoTypeAttr.AllowInheritance)
                return ninoTypeAttr;
            break;
        }

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            attr = GetDirectNinoTypeAttribute(interfaceType);
            if (attr == null)
                continue;
            var ninoTypeAttr = NinoTypeOptions.From(attr);
            if (ninoTypeAttr.AllowInheritance)
                return ninoTypeAttr;
        }

        return null;
    }

    public static AttributeData? GetDirectNinoTypeAttribute(this ITypeSymbol typeSymbol)
    {
        foreach (var attribute in typeSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.Name == NinoNames.NinoTypeAttribute &&
                IsNinoCoreNamespace(attribute.AttributeClass.ContainingNamespace))
                return attribute;
        }

        return null;
    }

    private static bool IsSystemNamespace(INamespaceSymbol? ns)
    {
        if (ns == null || ns.IsGlobalNamespace)
            return false;
        while (!ns.ContainingNamespace.IsGlobalNamespace)
            ns = ns.ContainingNamespace;
        return ns.Name == "System";
    }

    /// <summary>
    /// The property or event a compiler-generated field backs, or null. Roslyn links metadata fields only to
    /// events, so the backing field of an auto-property in a referenced assembly (<c>&lt;Name&gt;k__BackingField</c>)
    /// is matched by name: a type then reads the same whether its assembly is referenced as source or as metadata.
    /// </summary>
    public static ISymbol? AssociatedMember(this IFieldSymbol field)
    {
        if (field.AssociatedSymbol != null)
            return field.AssociatedSymbol;
        const string suffix = ">k__BackingField";
        var name = field.Name;
        if (name.Length <= suffix.Length + 1 || name[0] != '<' || !name.EndsWith(suffix, System.StringComparison.Ordinal))
            return null;
        foreach (var member in field.ContainingType.GetMembers(name.Substring(1, name.Length - suffix.Length - 1)))
        {
            if (member is IPropertySymbol property && property.IsStatic == field.IsStatic)
                return property;
        }

        return null;
    }

    /// <summary>Whether <paramref name="ns"/> is <c>Nino.Core</c> (without allocating its display string).</summary>
    public static bool IsNinoCoreNamespace(INamespaceSymbol? ns) =>
        ns is { Name: "Core", ContainingNamespace: { Name: "Nino", ContainingNamespace.IsGlobalNamespace: true } };
}

/// <summary>The arguments of a <c>[NinoType]</c> attribute (by parameter name, so any argument order works).</summary>
internal sealed class NinoTypeOptions
{
    public bool AutoCollect { get; private set; } = true;
    public bool IncludeNonPublicMembers { get; private set; }
    public bool AllowInheritance { get; private set; } = true;
    public NinoFormat Format { get; private set; }
    public bool TrackReferences { get; private set; }

    /// <summary><see cref="NinoFormat.VersionTolerant"/> (not counting <c>WEAK_VERSION_TOLERANCE</c>).</summary>
    public bool VersionTolerant => Format == NinoFormat.VersionTolerant;

    /// <summary><see cref="NinoFormat.Keyed"/>.</summary>
    public bool Keyed => Format == NinoFormat.Keyed;

    public static NinoTypeOptions From(AttributeData attribute)
    {
        var options = new NinoTypeOptions();
        if (attribute.AttributeConstructor is not { } constructor)
            return options;
        var parameters = constructor.Parameters;
        var arguments = attribute.ConstructorArguments;
        for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
        {
            if (parameters[i].Name == "format")
            {
                // An enum argument's value is its underlying int.
                if (arguments[i].Value is int format)
                    options.Format = (NinoFormat)format;
                continue;
            }

            if (arguments[i].Value is not bool value)
                continue;
            switch (parameters[i].Name)
            {
                case "autoCollect": options.AutoCollect = value; break;
                case "includeNonPublicMembers": options.IncludeNonPublicMembers = value; break;
                case "allowInheritance": options.AllowInheritance = value; break;
                case "trackReferences": options.TrackReferences = value; break;
            }
        }

        return options;
    }
}
