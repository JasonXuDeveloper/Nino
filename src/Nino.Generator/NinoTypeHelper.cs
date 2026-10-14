using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nino.Generator;

public static class NinoTypeHelper
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
        return GetNinoTypeAttribute(typeSymbol) != null;
    }

    /// <summary>
    /// The NinoType attribute that applies to the type: its own, then the nearest base class's (if it allows
    /// inheritance), then an interface's (if it allows inheritance).
    /// </summary>
    public static NinoTypeAttribute? GetNinoTypeAttribute(this ITypeSymbol typeSymbol)
    {
        var attr = GetDirectNinoTypeAttribute(typeSymbol);
        if (attr != null)
            return NinoTypeAttribute.GetAttribute(attr);

        for (var baseType = typeSymbol.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            attr = GetDirectNinoTypeAttribute(baseType);
            if (attr == null)
                continue;
            var ninoTypeAttr = NinoTypeAttribute.GetAttribute(attr);
            if (ninoTypeAttr.allowInheritance)
                return ninoTypeAttr;
            break;
        }

        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            attr = GetDirectNinoTypeAttribute(interfaceType);
            if (attr == null)
                continue;
            var ninoTypeAttr = NinoTypeAttribute.GetAttribute(attr);
            if (ninoTypeAttr.allowInheritance)
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

[System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "InconsistentNaming")]
public class NinoTypeAttribute
{
    public bool autoCollect = true;
    public bool containNonPublicMembers;
    public bool allowInheritance = true;
    public bool versionTolerant;

    public static NinoTypeAttribute GetAttribute(AttributeData attributeData)
    {
        return new NinoTypeAttribute
        {
            autoCollect = NinoTypeHelper.GetConstructorArgumentByName(attributeData, nameof(autoCollect), true),
            containNonPublicMembers =
                NinoTypeHelper.GetConstructorArgumentByName(attributeData, nameof(containNonPublicMembers), false),
            allowInheritance =
                NinoTypeHelper.GetConstructorArgumentByName(attributeData, nameof(allowInheritance), true),
            versionTolerant = NinoTypeHelper.GetConstructorArgumentByName(attributeData, nameof(versionTolerant), false),
        };
    }
}
