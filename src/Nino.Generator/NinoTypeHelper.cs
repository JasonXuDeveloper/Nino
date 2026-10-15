using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nino.Generator;

internal static class NinoTypeHelper
{
    public const string WeakVersionToleranceSymbol = "WEAK_VERSION_TOLERANCE";

    public static ImmutableArray<AttributeData> GetAttributesCache<T>(this T symbol) where T : ISymbol
    {
        return symbol.GetAttributes();
    }

    public static string GetDisplayString(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString();
    }

    /// <summary>Whether the symbol and all of its containing types are public.</summary>
    public static bool IsAccessible(this ISymbol symbol)
    {
        for (var s = symbol; s != null; s = s.ContainingType)
        {
            if (s.DeclaredAccessibility != Accessibility.Public &&
                s.DeclaredAccessibility != Accessibility.NotApplicable)
                return false;
        }

        return true;
    }

    public static ITypeSymbol? GetTypeSymbol(this TypeDeclarationSyntax syntax, SyntaxNodeAnalysisContext context)
    {
        return context.SemanticModel.GetDeclaredSymbol(syntax) as ITypeSymbol;
    }

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

    public static T GetConstructorArgumentByName<T>(AttributeData? attributeData, string parameterName,
        T defaultValue = default!)
    {
        if (attributeData?.AttributeConstructor == null)
            return defaultValue;

        var parameters = attributeData.AttributeConstructor.Parameters;
        var arguments = attributeData.ConstructorArguments;
        for (int i = 0; i < parameters.Length && i < arguments.Length; i++)
        {
            if (parameters[i].Name == parameterName)
                return arguments[i].Value is T typed ? typed : defaultValue;
        }

        return defaultValue;
    }
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

    public static NinoTypeOptions From(AttributeData attribute) => new()
    {
        AutoCollect = NinoTypeHelper.GetConstructorArgumentByName(attribute, "autoCollect", true),
        ContainNonPublicMembers = NinoTypeHelper.GetConstructorArgumentByName(attribute, "containNonPublicMembers", false),
        AllowInheritance = NinoTypeHelper.GetConstructorArgumentByName(attribute, "allowInheritance", true),
        VersionTolerant = NinoTypeHelper.GetConstructorArgumentByName(attribute, "versionTolerant", false),
        Keyed = NinoTypeHelper.GetConstructorArgumentByName(attribute, "keyed", false),
        TrackReferences = NinoTypeHelper.GetConstructorArgumentByName(attribute, "trackReferences", false),
    };
}
