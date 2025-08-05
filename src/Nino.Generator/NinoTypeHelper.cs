using System;
using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<ITypeSymbol, bool> IsNinoTypeCache =
        new(SymbolEqualityComparer.Default);

    private static readonly ConcurrentDictionary<ITypeSymbol, string> ToDisplayStringCache =
        new(SymbolEqualityComparer.Default);

    private static readonly ConcurrentDictionary<(Compilation, SyntaxTree), SemanticModel> SemanticModelCache = new();
    private static readonly ConcurrentDictionary<(SyntaxTree, CSharpSyntaxNode), ITypeSymbol?> TypeSymbolCache = new();

    private static readonly ConcurrentDictionary<ISymbol, ImmutableArray<AttributeData>> AttributeCache =
        new(SymbolEqualityComparer.Default);

    public static bool IsRefStruct(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsRefLikeType && typeSymbol.IsValueType;
    }

    public static string GetTypeInstanceName(this ITypeSymbol typeSymbol)
    {
        var ret = typeSymbol.GetDisplayString()
            .Replace("global::", "")
            .ToLower()
            .Select(c => char.IsLetterOrDigit(c) ? c : '_')
            .Aggregate("", (current, c) => current + c);

        return $"@{ret}";
    }

    /// <summary>
    /// Generates a unique, deterministic variable name for cached serializer/deserializer instances.
    /// Uses the type's display name hash to create a short, collision-resistant identifier.
    /// </summary>
    public static string GetCachedVariableName(this ITypeSymbol typeSymbol, string prefix)
    {
        var typeDisplayName = typeSymbol.GetDisplayString();
        var hash = typeDisplayName.GetLegacyNonRandomizedHashCode();
        var hexString = Math.Abs(hash).ToString("x8");
        return $"{prefix}_{hexString}";
    }

    public static ImmutableArray<AttributeData> GetAttributesCache<T>(this T typeSymbol) where T : ISymbol
    {
        if (AttributeCache.TryGetValue(typeSymbol, out var ret))
        {
            return ret;
        }

        ret = typeSymbol.GetAttributes();
        AttributeCache[typeSymbol] = ret;
        return ret;
    }

    public static string GetDisplayString(this ITypeSymbol typeSymbol)
    {
        if (ToDisplayStringCache.TryGetValue(typeSymbol, out var ret))
        {
            return ret;
        }

        ret = typeSymbol.ToDisplayString();
        ToDisplayStringCache[typeSymbol] = ret;
        return ret;
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
        // Use "Nino.NinoTypeAttribute" - this is the actual working metadata name
        // Even though the attribute is defined in namespace Nino.Core, the metadata name
        // that works with ForAttributeWithMetadataName is "Nino.NinoTypeAttribute"
        // Using "Nino.Core.NinoTypeAttribute" causes the provider to fail silently
        var ninoTypeAnnotatedTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Nino.Core.NinoTypeAttribute",
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

    public static bool IsPolyMorphicType(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsReferenceType || typeSymbol is { IsRecord: true, IsValueType: false } ||
               typeSymbol.TypeKind == TypeKind.Interface;
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

        foreach (var syntax in GetAllNinoRequiredTypes(syntaxes.ToImmutableArray(), compilation))
        {
            if (syntax != null && syntax.IsNinoType()
                               && syntax is not ITypeParameterSymbol
                               && CheckGenericValidity(syntax))
                typeSymbols.Add(syntax.GetPureType());
        }

        return typeSymbols.ToList();
    }

    public static List<ITypeSymbol> GetAllNinoRequiredTypes(this ImmutableArray<CSharpSyntaxNode> syntaxes,
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
            if (ret.Add(typeSymbol.GetPureType()))
            {
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
        if (ret.Add(symbol.ElementType.GetPureType()))
            AddElementRecursively(symbol.ElementType.GetPureType(), ret);
    }

    private static void AddTypeArguments(INamedTypeSymbol symbol, HashSet<ITypeSymbol> ret)
    {
        foreach (var typeArgument in symbol.TypeArguments)
        {
            if (ret.Add(typeArgument.GetPureType()))
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SemanticModel GetCachedSemanticModel(Compilation compilation, SyntaxTree syntaxTree)
    {
        var key = (compilation, syntaxTree);
        if (SemanticModelCache.TryGetValue(key, out var cachedModel))
        {
            return cachedModel;
        }

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        SemanticModelCache[key] = semanticModel;
        return semanticModel;
    }

    public static ITypeSymbol? GetTypeSymbol(this CSharpSyntaxNode syntax, Compilation compilation)
    {
        // Check direct cache first for maximum performance
        var cacheKey = (syntax.SyntaxTree, syntax);
        if (TypeSymbolCache.TryGetValue(cacheKey, out var cachedResult))
        {
            return cachedResult;
        }

        // Cache miss - compute the result using optimized path
        var result = GetTypeSymbolUncached(syntax, compilation);

        // Cache the result for future lookups
        TypeSymbolCache[cacheKey] = result;
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ITypeSymbol? GetTypeSymbolUncached(CSharpSyntaxNode syntax, Compilation compilation)
    {
        // Use cached semantic model to avoid repeated expensive calls
        var semanticModel = GetCachedSemanticModel(compilation, syntax.SyntaxTree);

        switch (syntax)
        {
            // Use if-else instead of switch for potentially better performance with type checks
            case TupleTypeSyntax tupleTypeSyntax:
                return semanticModel.GetTypeInfo(tupleTypeSyntax).Type;
            case TypeDeclarationSyntax typeDeclaration:
                return semanticModel.GetDeclaredSymbol(typeDeclaration);
            case TypeSyntax typeSyntax:
            {
                // Try GetTypeInfo first as it's more direct for type syntax
                var typeInfo = semanticModel.GetTypeInfo(typeSyntax);
                return typeInfo.Type ?? semanticModel.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol;
            }
            default:
                return null;
        }
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
        if (typeSymbol.IsUnmanagedType)
        {
            return true;
        }

        if (IsNinoTypeCache.TryGetValue(typeSymbol, out var isNinoType))
        {
            return isNinoType;
        }

        foreach (var attribute in typeSymbol.GetAttributesCache())
        {
            if (!string.Equals(attribute.AttributeClass?.Name, "NinoTypeAttribute", StringComparison.Ordinal)) continue;
            IsNinoTypeCache[typeSymbol] = true;
            return true;
        }

        IsNinoTypeCache[typeSymbol] = false;
        return false;
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

        return methodSymbol.GetAttributesCache()
            .FirstOrDefault(static a => a.AttributeClass?.Name == "NinoConstructorAttribute");
    }

    public static int GetId(this ITypeSymbol typeSymbol)
    {
        var formerName = typeSymbol.GetAttributesCache()
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
                        var bufferWriter = NinoSerializer.GetBufferWriter();
                        Serialize(value, bufferWriter);
                        var ret = bufferWriter.WrittenSpan.ToArray();
                        NinoSerializer.ReturnBufferWriter(bufferWriter);
                        return ret;
                    }

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Serialize{{typeParam}}(this {{typeFullName}} value, INinoBufferWriter bufferWriter) {{genericConstraint}}
                    {
                        Writer writer = new Writer(bufferWriter);
                        Serialize(value, ref writer);
                    }
                    """;

        ret = ret.Replace("\n", $"\n{indent}");
        sb.AppendLine();
        sb.AppendLine($"{indent}{ret}");
        sb.AppendLine();
    }

    public static void GenerateClassDeserializeMethods(this StringBuilder sb, string typeFullName,
        string typeParam = "",
        string genericConstraint = "")
    {
        sb.AppendLine($$"""
                                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                                public static void Deserialize{{typeParam}}(ReadOnlySpan<byte> data, out {{typeFullName}} value) {{genericConstraint}}
                                {
                                    var reader = new Reader(data);
                                    Deserialize(out value, ref reader);
                                }
                        """);
        sb.AppendLine();
    }

    public static string GetTypeFullName(this ITypeSymbol typeSymbol)
    {
        if (typeSymbol.SpecialType == SpecialType.System_Object)
        {
            return typeof(object).FullName!;
        }

        return typeSymbol.GetDisplayString();
    }

    /// <summary>
    /// Gets the normalized type symbol for tuple types, using TupleUnderlyingType to ignore field names.
    /// This ensures that (int a, int b) and (int aa, int bb) are treated as the same type.
    /// Also recursively normalizes type arguments (e.g., List&lt;(int a, int b)&gt; becomes List&lt;ValueTuple&lt;int, int&gt;&gt;).
    /// </summary>
    public static ITypeSymbol GetNormalizedTypeSymbol(this ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol namedType)
        {
            // For tuple types, use the underlying type which ignores field names
            if (typeSymbol.IsTupleType)
            {
                return namedType.TupleUnderlyingType ?? typeSymbol;
            }

            // For generic types, recursively normalize type arguments
            if (namedType.TypeArguments.Length > 0)
            {
                var normalizedArgs = namedType.TypeArguments.Select(GetNormalizedTypeSymbol).ToArray();

                // Check if any type arguments were actually normalized
                bool hasChanges = false;
                for (int i = 0; i < namedType.TypeArguments.Length; i++)
                {
                    if (!SymbolEqualityComparer.Default.Equals(namedType.TypeArguments[i], normalizedArgs[i]))
                    {
                        hasChanges = true;
                        break;
                    }
                }

                // If type arguments were normalized, construct a new generic type with normalized arguments
                if (hasChanges)
                {
                    return namedType.ConstructedFrom.Construct(normalizedArgs);
                }
            }
        }

        // For array types, normalize the element type
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var normalizedElement = GetNormalizedTypeSymbol(arrayType.ElementType);
            if (!SymbolEqualityComparer.Default.Equals(arrayType.ElementType, normalizedElement))
            {
                // Create a new array type with the normalized element type
                // Note: This is more complex - for now, let's just return the original for arrays
                // Array types with tuple elements are less common in the typical use case
                return typeSymbol;
            }
        }

        return typeSymbol;
    }

    /// <summary>
    /// Gets the display string for comparison, using normalized tuple types.
    /// </summary>
    public static string GetSanitizedDisplayString(this ITypeSymbol typeSymbol)
    {
        return GetNormalizedTypeSymbol(typeSymbol).GetDisplayString();
    }

    /// <summary>
    /// Custom equality comparer that treats tuple types with the same type arguments as equal,
    /// regardless of field names, using Roslyn's TupleUnderlyingType.
    /// </summary>
    public class TupleSanitizedEqualityComparer : IEqualityComparer<ITypeSymbol>
    {
#nullable disable
        public bool Equals(ITypeSymbol x, ITypeSymbol y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            // Normalize both types to handle tuple field names
            var normalizedX = x.GetNormalizedTypeSymbol();
            var normalizedY = y.GetNormalizedTypeSymbol();

            // Use default symbol equality on normalized types
            return SymbolEqualityComparer.Default.Equals(normalizedX, normalizedY);
        }

        public int GetHashCode(ITypeSymbol obj)
        {
            if (obj is null) return 0;

            // Use hash code of normalized type
            var normalized = obj.GetNormalizedTypeSymbol();
            return SymbolEqualityComparer.Default.GetHashCode(normalized);
        }
#nullable restore
    }
}