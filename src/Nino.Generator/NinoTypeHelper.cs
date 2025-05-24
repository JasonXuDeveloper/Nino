using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Nino.Generator;

public static class NinoTypeHelper
{
    public const string WeakVersionToleranceSymbol = "WEAK_VERSION_TOLERANCE";

    public static string GetTypeInstanceName(this ITypeSymbol typeSymbol)
    {
        var ret = typeSymbol.ToDisplayString()
            .Replace("global::", "")
            .ToLower()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .Aggregate("", (current, c) => current + c);

        return $"@{ret}";
    }

    public static bool IsAccessibleFromCurrentAssembly(this ITypeSymbol type, Compilation compilation)
    {
        var asm = compilation.Assembly;
        switch (type.DeclaredAccessibility)
        {
            case Accessibility.Public:
                return true;
            case Accessibility.Internal:
            case Accessibility.ProtectedOrInternal:
                return SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, asm);
            default:
                return false;
        }
    }

    public static List<ITypeSymbol> MergeTypes(this List<ITypeSymbol?> types, List<ITypeSymbol?> otherTypes)
    {
        HashSet<ITypeSymbol?> typeSymbols = new(SymbolEqualityComparer.Default);
        typeSymbols.UnionWith(types);
        typeSymbols.UnionWith(otherTypes);
        typeSymbols.RemoveWhere(ts => ts == null);

        foreach (var typeSymbol in typeSymbols.ToList())
        {
            AddElementRecursively(typeSymbol!, typeSymbols!);
        }

        return typeSymbols.ToList()!;
    }

    public static (bool isValid, Compilation newCompilation) IsValidCompilation(this Compilation compilation)
    {
        //make sure the compilation contains the Nino.Core assembly
        if (!compilation.ReferencedAssemblyNames.Any(static a => a.Name == "Nino.Core"))
        {
            return (false, compilation);
        }

        //disable nullable reference types
        Compilation newCompilation = compilation;
        // Cast to CSharpCompilation to access the C#-specific options
        if (compilation is CSharpCompilation csharpCompilation)
        {
            // Check if nullable reference types is enabled
            if (csharpCompilation.Options.NullableContextOptions != NullableContextOptions.Disable)
            {
                // Create a new CSharpCompilationOptions with nullable warnings disabled
                var newOptions = csharpCompilation.Options.WithNullableContextOptions(NullableContextOptions.Disable);

                // Return a new compilation with the updated options
                newCompilation = csharpCompilation.WithOptions(newOptions);
            }
        }

        compilation = newCompilation;

        //make sure the compilation indeed uses Nino.Core
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var usingDirectives = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(usingDirective => usingDirective.Name.ToString().Contains("Nino.Core"));

            if (usingDirectives.Any())
            {
                return (true, newCompilation); // Namespace is used in a using directive
            }
        }

        //or if any member has NinoTypeAttribute/NinoMemberAttribute/NinoIgnoreAttribute/NinoConstructorAttribute/NinoUtf8Attribute
        return (compilation.SyntaxTrees
            .SelectMany(static s => s.GetRoot().DescendantNodes())
            .Any(static s => s is AttributeSyntax attributeSyntax &&
                             (attributeSyntax.Name.ToString().EndsWith("NinoType") ||
                              attributeSyntax.Name.ToString().EndsWith("NinoMember") ||
                              attributeSyntax.Name.ToString().EndsWith("NinoIgnore") ||
                              attributeSyntax.Name.ToString().EndsWith("NinoConstructor") ||
                              attributeSyntax.Name.ToString().EndsWith("NinoFormerName") ||
                              attributeSyntax.Name.ToString().EndsWith("NinoUtf8"))), newCompilation);
    }

    public static IncrementalValuesProvider<CSharpSyntaxNode> GetTypeSyntaxes(
        this IncrementalGeneratorInitializationContext context)
    {
        var ninoTypeAnnotatedTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Nino.NinoTypeAttribute",
            predicate: static (s, _) => s is TypeDeclarationSyntax,
            transform: static (ctx, _) => (CSharpSyntaxNode)ctx.TargetNode);

        var declaredGenericTypes = context.SyntaxProvider.CreateSyntaxProvider(
            predicate: static (s, _) => s is GenericNameSyntax,
            transform: static (ctx, _) => (TypeSyntax)ctx.Node);

        var collectedAnnotatedTypes = ninoTypeAnnotatedTypes.Collect();
        var collectedGenericTypes = declaredGenericTypes.Collect();

        return collectedAnnotatedTypes.Combine(collectedGenericTypes)
            .SelectMany((pair, _) => pair.Left.Concat(pair.Right));
    }

    public static string GetTypeConstName(this string typeFullName)
    {
        return typeFullName.ToCharArray()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .Aggregate("", (a, b) => a + b);
    }

    public static string GetNamespace(this string assemblyName)
    {
        var curNamespace = assemblyName;
        if (!string.IsNullOrEmpty(curNamespace))
            curNamespace = $"{curNamespace}.";
        if (curNamespace.Length > 0 && !char.IsLetter(curNamespace[0]))
            curNamespace = $"_{curNamespace}";
        //replace special characters with _
        curNamespace = new string(curNamespace.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
        curNamespace += "NinoGen";
        return curNamespace;
    }

    private static bool CheckGenericValidity(ITypeSymbol containingType)
    {
        //containing type can not be an uninstantiated generic type
        bool IsContainingTypeValid(ITypeSymbol type)
        {
            return type switch
            {
                ITypeParameterSymbol => false,
                IArrayTypeSymbol arrayTypeSymbol => IsContainingTypeValid(arrayTypeSymbol.ElementType),
                INamedTypeSymbol { IsGenericType: true } namedTypeSymbol =>
                    namedTypeSymbol.TypeArguments.Length == namedTypeSymbol.TypeParameters.Length &&
                    namedTypeSymbol.TypeArguments.All(t => t.TypeKind != TypeKind.TypeParameter) &&
                    namedTypeSymbol.TypeArguments.All(IsContainingTypeValid),
                INamedTypeSymbol => true,
                _ => false
            };
        }

        while (containingType != null)
        {
            if (IsContainingTypeValid(containingType))
            {
                containingType = containingType.ContainingType;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    public static List<ITypeSymbol> GetNinoTypeSymbols(this ImmutableArray<CSharpSyntaxNode> syntaxes,
        Compilation compilation)
    {
        var visited = new HashSet<string>();
        var typeSymbols = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var syntax in syntaxes)
        {
            if (!visited.Add(syntax.ToFullString())) continue;
            var typeSymbol = syntax.GetTypeSymbol(compilation);
            if (typeSymbol != null && typeSymbol.IsNinoType()
                                   && typeSymbol is not ITypeParameterSymbol
                                   && CheckGenericValidity(typeSymbol))
                typeSymbols.Add(typeSymbol.GetPureType());
        }

        foreach (var syntax in GetAllNinoRequiredTypes(syntaxes.Select(s => (TypeSyntax)s).ToImmutableArray()
                     , compilation))
        {
            if (syntax != null && syntax.IsNinoType()
                               && syntax is not ITypeParameterSymbol
                               && CheckGenericValidity(syntax))
                typeSymbols.Add(syntax.GetPureType());
        }

        return typeSymbols.ToList();
    }

    public static List<ITypeSymbol> GetAllNinoRequiredTypes(this ImmutableArray<TypeSyntax> syntaxes,
        Compilation compilation)
    {
        HashSet<INamedTypeSymbol> validTypes = new(SymbolEqualityComparer.Default);
        foreach (var type in GetAllTypes(compilation))
        {
            if (type.IsNinoType())
            {
                //if it is a generic type, check if it is valid
                if (type.IsGenericType && !CheckGenericValidity(type))
                    continue;
                //add to valid types
                validTypes.Add(type);
            }
        }

        foreach (var syntax in syntaxes)
        {
            if (syntax == null) continue;
            var typeSymbol = syntax.GetTypeSymbol(compilation);
            if (typeSymbol != null && typeSymbol is INamedTypeSymbol namedTypeSymbol && typeSymbol.IsNinoType()
                && typeSymbol is not ITypeParameterSymbol
                && CheckGenericValidity(typeSymbol))
                validTypes.Add((INamedTypeSymbol)namedTypeSymbol.GetPureType());
        }

        HashSet<ITypeSymbol> ret = new(SymbolEqualityComparer.Default);
        foreach (var typeSymbol in validTypes)
        {
            ret.Add(typeSymbol.GetPureType());
            var members = typeSymbol.GetMembers();
            foreach (var member in members)
            {
                switch (member)
                {
                    case IFieldSymbol fieldSymbol:
                        ret.Add(fieldSymbol.Type.GetPureType());
                        break;
                    case IPropertySymbol propertySymbol:
                        ret.Add(propertySymbol.Type.GetPureType());
                        break;
                    case IParameterSymbol parameterSymbol:
                        ret.Add(parameterSymbol.Type.GetPureType());
                        break;
                }
            }
        }

        foreach (var typeSymbol in ret.ToList())
        {
            AddElementRecursively(typeSymbol, ret);
        }

        return ret.ToList();
    }

    private static void AddElementRecursively(ITypeSymbol symbol, HashSet<ITypeSymbol> ret)
    {
        if (symbol is INamedTypeSymbol namedTypeSymbol)
        {
            AddTypeArguments(namedTypeSymbol, ret);
        }
        else if (symbol is IArrayTypeSymbol arrayTypeSymbol)
        {
            AddArrayElementType(arrayTypeSymbol, ret);
        }
    }

    private static void AddArrayElementType(IArrayTypeSymbol symbol, HashSet<ITypeSymbol> ret)
    {
        ret.Add(symbol.ElementType.GetPureType());
        AddElementRecursively(symbol.ElementType.GetPureType(), ret);
    }

    private static void AddTypeArguments(INamedTypeSymbol symbol, HashSet<ITypeSymbol> ret)
    {
        foreach (var typeArgument in symbol.TypeArguments)
        {
            ret.Add(typeArgument.GetPureType());
            AddElementRecursively(typeArgument, ret);
        }
    }

    private static INamedTypeSymbol[] GetTypesInAssembly(IAssemblySymbol assembly)
    {
        return GetTypesInNamespace(assembly.GlobalNamespace).ToArray();
    }

    public static HashSet<INamedTypeSymbol> GetAllTypes(Compilation compilation)
    {
        var allTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        // Add all types from the current assembly (compilation)
        foreach (var type in GetTypesInNamespace(compilation.GlobalNamespace))
        {
            allTypes.Add(type);
        }

        // Add all types from each referenced assembly
        foreach (var referencedAssembly in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(referencedAssembly) is IAssemblySymbol assemblySymbol)
            {
                foreach (var type in GetTypesInAssembly(assemblySymbol))
                {
                    allTypes.Add(type);
                }
            }
        }

        // remove all types that are not public
        allTypes.RemoveWhere(s => s.DeclaredAccessibility != Accessibility.Public);
        return allTypes;
    }

    private static IEnumerable<INamedTypeSymbol> GetTypesInNamespace(INamespaceSymbol namespaceSymbol)
    {
        // Collect all types in the current namespace
        foreach (var typeSymbol in namespaceSymbol.GetTypeMembers())
        {
            foreach (var nestedType in GetNestedTypes(typeSymbol))
            {
                yield return nestedType;
            }
        }

        // Recursively get types from nested namespaces
        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var nestedType in GetTypesInNamespace(nestedNamespace))
            {
                foreach (var nestedNestedType in GetNestedTypes(nestedType))
                {
                    yield return nestedNestedType;
                }
            }
        }
    }

    public static IEnumerable<INamedTypeSymbol> GetNestedTypes(this INamedTypeSymbol typeSymbol)
    {
        // check public accessibility
        if (!typeSymbol.DeclaredAccessibility.HasFlag(Accessibility.Public))
        {
            yield break;
        }

        // yiled itself
        yield return typeSymbol;

        //recursively get nested types
        foreach (var nestedType in typeSymbol.GetTypeMembers())
        {
            //recursive call
            foreach (var nestedNestedType in GetNestedTypes(nestedType))
            {
                yield return nestedNestedType;
            }
        }
    }

    public static ITypeSymbol? GetTypeSymbol(this CSharpSyntaxNode syntax, Compilation compilation)
    {
        switch (syntax)
        {
            case TupleTypeSyntax tupleTypeSyntax:
                return compilation.GetSemanticModel(tupleTypeSyntax.SyntaxTree).GetTypeInfo(tupleTypeSyntax).Type;
            case TypeDeclarationSyntax typeDeclaration:
                return compilation.GetSemanticModel(typeDeclaration.SyntaxTree).GetDeclaredSymbol(typeDeclaration);
            case TypeSyntax typeSyntax:
                var semanticModel = compilation.GetSemanticModel(typeSyntax.SyntaxTree);
                var typeInfo = semanticModel.GetTypeInfo(typeSyntax);
                ITypeSymbol? typeSymbol = typeInfo.Type;
                if (typeSymbol is null)
                {
                    return semanticModel.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol;
                }

                return typeSymbol;
        }

        return null;
    }

    public static ITypeSymbol? GetTypeSymbol(this TypeDeclarationSyntax syntax, SyntaxNodeAnalysisContext context)
    {
        return context.SemanticModel.GetDeclaredSymbol(syntax);
    }

    public static ITypeSymbol GetPureType(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
    }

    public static bool IsNinoType(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsUnmanagedType ||
               typeSymbol.GetAttributes().Any(static a => a.AttributeClass?.Name == "NinoTypeAttribute");
    }

    public static string GetTypeModifiers(this ITypeSymbol typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol)
        {
            return string.Empty; // Not a named type.
        }

        // Determine the base modifier.
        string typeKind = namedTypeSymbol.TypeKind switch
        {
            TypeKind.Class => "class",
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => string.Empty
        };

        // Check if it's a record.
        if (namedTypeSymbol.IsRecord)
        {
            typeKind = typeKind switch
            {
                "class" => "record", // Record class.
                "struct" => "record struct", // Record struct.
                _ => typeKind
            };
        }

        // Check for ref struct.
        if (typeKind == "struct" && namedTypeSymbol.IsRefLikeType)
        {
            typeKind = "ref struct";
        }

        return typeKind;
    }

    public static bool IsInstanceType(this ITypeSymbol typeSymbol)
    {
        //can't be interface or abstract class
        return typeSymbol.TypeKind switch
        {
            TypeKind.Interface => false,
            TypeKind.Class => !typeSymbol.IsAbstract,
            TypeKind.Struct => true,
            TypeKind.Array => true,
            _ => false
        };
    }

    public static AttributeData? GetNinoConstructorAttribute(this IMethodSymbol? methodSymbol)
    {
        if (methodSymbol == null)
        {
            return null;
        }

        return methodSymbol.GetAttributes()
            .FirstOrDefault(static a => a.AttributeClass?.Name == "NinoConstructorAttribute");
    }

    public static int GetId(this ITypeSymbol typeSymbol)
    {
        var formerName = typeSymbol.GetAttributes()
            .FirstOrDefault(static a => a.AttributeClass?.Name == "NinoFormerNameAttribute");

        if (formerName == null)
            return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .GetLegacyNonRandomizedHashCode();

        //if not generic
        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol || !namedTypeSymbol.IsGenericType)
        {
            return formerName.ConstructorArguments[0].Value?.ToString().GetLegacyNonRandomizedHashCode() ??
                   typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                       .GetLegacyNonRandomizedHashCode();
        }

        //for generic, we only replace the non-generic part of the name
        var typeFullName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var genericIndex = typeFullName.IndexOf('<');
        var newName = $"{formerName.ConstructorArguments[0].Value}{typeFullName.Substring(genericIndex)}";
        return newName.GetLegacyNonRandomizedHashCode();
    }

    // Use this if and only if you need the hashcode to not change across app domains (e.g. you have an app domain agile
    // hash table).
    /// <summary>
    /// Reference: https://github.com/microsoft/referencesource/blob/51cf7850defa8a17d815b4700b67116e3fa283c2/mscorlib/system/string.cs#L894C9-L949C10
    /// </summary>
    /// <returns></returns>
    private static int GetLegacyNonRandomizedHashCode(this string str)
    {
        ReadOnlySpan<char> span = str.AsSpan();
        int hash1 = 5381;
        int hash2 = hash1;

        int c;
        ref char s = ref MemoryMarshal.GetReference(span);
        while ((c = s) != 0)
        {
            hash1 = ((hash1 << 5) + hash1) ^ c;
            c = Unsafe.Add(ref s, 1);
            if (c == 0)
                break;
            hash2 = ((hash2 << 5) + hash2) ^ c;
            s = ref Unsafe.Add(ref s, 2);
        }

        return hash1 + hash2 * 1566083941;
    }

    public static void GenerateClassSerializeMethods(this StringBuilder sb, string typeFullName, string typeParam = "",
        string genericConstraint = "")
    {
        var indent = "        ";
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static byte[] Serialize{{typeParam}}(this {{typeFullName}} value) {{genericConstraint}}
                    {
                        var bufferWriter = GetBufferWriter();
                        Serialize(value, bufferWriter);
                        var ret = bufferWriter.WrittenSpan.ToArray();
                        ReturnBufferWriter(bufferWriter);
                        return ret;
                    }

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Serialize{{typeParam}}(this {{typeFullName}} value, IBufferWriter<byte> bufferWriter) {{genericConstraint}}
                    {
                        Writer writer = new Writer(bufferWriter);
                        value.Serialize(ref writer);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");

        sb.AppendLine();
        sb.AppendLine($"{indent}{ret}");
        sb.AppendLine();
    }

    public static void GenerateClassDeserializeMethods(this StringBuilder sb, string typeFullName,
        string typeParam = "",
        string genericConstraint = "")
    {
        var indent = "        ";
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize{{typeParam}}(ReadOnlySpan<byte> data, out {{typeFullName}} value) {{genericConstraint}}
                    {
                        var reader = new Reader(data);
                        Deserialize(out value, ref reader);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");

        sb.AppendLine();
        sb.AppendLine($"{indent}{ret}");
        sb.AppendLine();
    }

    public static string GetTypeFullName(this ITypeSymbol typeSymbol)
    {
        if (typeSymbol.SpecialType == SpecialType.System_Object)
        {
            return typeof(object).FullName!;
        }

        return typeSymbol.ToDisplayString();
    }
}