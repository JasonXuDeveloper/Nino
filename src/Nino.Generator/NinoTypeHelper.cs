using Microsoft.CodeAnalysis;

namespace Nino.Generator;

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

    /// <summary>
    /// The NinoType attribute that applies to the type: its own, then the nearest base class's (if it allows
    /// inheritance), then an interface's (if it allows inheritance).
    /// </summary>
    public static NinoTypeOptions? GetNinoTypeOptions(this ITypeSymbol typeSymbol)
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
            if (attribute.AttributeClass?.Name == "NinoTypeAttribute" &&
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

    /// <summary>Whether <paramref name="ns"/> is <c>Nino.Core</c> (without allocating its display string).</summary>
    public static bool IsNinoCoreNamespace(INamespaceSymbol? ns) =>
        ns is { Name: "Core", ContainingNamespace: { Name: "Nino", ContainingNamespace.IsGlobalNamespace: true } };
}

/// <summary>The arguments of a <c>[NinoType]</c> attribute (by parameter name, so any argument order works).</summary>
internal sealed class NinoTypeOptions
{
    public bool AutoCollect { get; private set; } = true;
    public bool ContainNonPublicMembers { get; private set; }
    public bool AllowInheritance { get; private set; } = true;
    public bool VersionTolerant { get; private set; }
    public bool Keyed { get; private set; }
    public bool TrackReferences { get; private set; }

    public static NinoTypeOptions From(AttributeData attribute)
    {
        var options = new NinoTypeOptions();
        if (attribute.AttributeConstructor is not { } constructor)
            return options;
        var parameters = constructor.Parameters;
        var arguments = attribute.ConstructorArguments;
        for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
        {
            if (arguments[i].Value is not bool value)
                continue;
            switch (parameters[i].Name)
            {
                case "autoCollect": options.AutoCollect = value; break;
                case "containNonPublicMembers": options.ContainNonPublicMembers = value; break;
                case "allowInheritance": options.AllowInheritance = value; break;
                case "versionTolerant": options.VersionTolerant = value; break;
                case "keyed": options.Keyed = value; break;
                case "trackReferences": options.TrackReferences = value; break;
            }
        }

        return options;
    }
}
