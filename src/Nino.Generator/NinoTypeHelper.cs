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
using Nino.Generator.Metadata;

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

    private static readonly ConcurrentDictionary<ITypeSymbol, int> TypeHierarchyLevelCache =
        new(SymbolEqualityComparer.Default);

    public enum NinoTypeKind
    {
        Boxed,
        Unmanaged,
        NinoType,
        BuiltIn,
        Invalid
    }

    public static NinoTypeKind GetKind(this ITypeSymbol type, NinoGraph ninoGraph, HashSet<ITypeSymbol> generatedTypes)
    {
        // pointer types - invalid
        if (type.TypeKind == TypeKind.Pointer)
        {
            return NinoTypeKind.Invalid;
        }

        // object types - use boxed serialization
        if (type.SpecialType == SpecialType.System_Object)
        {
            return NinoTypeKind.Boxed;
        }

        // unmanaged
        if (type.IsUnmanagedType &&
            type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T &&
            (!ninoGraph.TypeMap.TryGetValue(type.GetDisplayString(), out var nt) ||
             !nt.IsPolymorphic()))
        {
            return NinoTypeKind.Unmanaged;
        }

        // nino type
        if (ninoGraph.TypeMap.ContainsKey(type.GetDisplayString()))
        {
            return NinoTypeKind.NinoType;
        }

        // built-in type
        if (type.SpecialType == SpecialType.System_String || generatedTypes.Contains(type))
        {
            return NinoTypeKind.BuiltIn;
        }

        return NinoTypeKind.Invalid;
    }


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
        var hexString = ((uint)hash).ToString("X8");
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

        // Sanitize multi-dimensional array syntax: [*,*] -> [,], [*,*,*] -> [,,], etc.
        // This handles all cases including user-defined types and nested arrays
        // Examples: int[*,*] -> int[,], TestStruct3[*,*] -> TestStruct3[,]
        //           List<someClass[*,*]>[][*,*][*,*,*] -> List<someClass[,]>[][,][,,]
        if (ret.Contains("[*"))
        {
            var sb = new StringBuilder(ret.Length);
            for (int i = 0; i < ret.Length; i++)
            {
                if (ret[i] == '[' && i + 1 < ret.Length && ret[i + 1] == '*')
                {
                    // Found start of multi-dimensional array syntax
                    sb.Append('[');
                    i++; // Skip the '*'

                    // Skip asterisks and commas, but preserve commas
                    while (i < ret.Length && ret[i] != ']')
                    {
                        if (ret[i] == ',')
                        {
                            sb.Append(',');
                        }

                        i++;
                    }

                    if (i < ret.Length)
                    {
                        sb.Append(']');
                    }
                }
                else
                {
                    sb.Append(ret[i]);
                }
            }

            ret = sb.ToString();
        }

        ToDisplayStringCache[typeSymbol] = ret;
        return ret;
    }

    public static (bool isValid, Compilation newCompilation) IsValidCompilation(this Compilation compilation)
    {
        //make sure the compilation contains the Nino.Core assembly
        if (!compilation.ReferencedAssemblyNames.Any(static a => a.Name == "Nino.Core"))
        {
            return (false, compilation);
        }

        // Skip generation if Nino.Core namespace is not used anywhere in the compilation
        bool hasNinoCoreUsage = compilation.SyntaxTrees.Any(tree =>
            tree.GetRoot().DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Any(u =>
                {
                    var name = u.Name?.ToString();
                    return name == "Nino.Core" || name == "global::Nino.Core" || name?.StartsWith("Nino.Core.") == true;
                }));

        if (!hasNinoCoreUsage)
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
        return (true, compilation);
    }

    public static IncrementalValuesProvider<CSharpSyntaxNode> GetTypeSyntaxes(
        this IncrementalGeneratorInitializationContext context)
    {
        var ninoTypeAnnotatedTypes = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Nino.Core.NinoTypeAttribute",
            predicate: static (s, _) => s is TypeDeclarationSyntax,
            transform: static (ctx, _) => (CSharpSyntaxNode)ctx.TargetNode);

        return ninoTypeAnnotatedTypes;
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
        StringBuilder sb = new();
        foreach (var c in curNamespace.Split('.'))
        {
            if (string.IsNullOrEmpty(c)) continue;
            var part = c;
            if (!char.IsLetter(part[0]) && part[0] != '_')
                sb.Append('_');
            for (int i = 0; i < part.Length; i++)
            {
                var ch = part[i];
                if (char.IsLetterOrDigit(ch) || ch == '_')
                {
                    sb.Append(ch);
                }
                else
                {
                    sb.Append('_');
                }
            }

            sb.Append('.');
        }

        sb.Append("NinoGen");
        return sb.ToString();
    }

    public static bool IsAccessible(this ISymbol symbol)
    {
        // Check the symbol itself and its containing types
        var s = symbol;
        while (s != null)
        {
            if (s.DeclaredAccessibility != Accessibility.Public &&
                s.DeclaredAccessibility != Accessibility.NotApplicable)
            {
                return false;
            }

            s = s.ContainingType;
        }

        // For generic types, also check all type arguments
        if (symbol is INamedTypeSymbol namedType && namedType.IsGenericType)
        {
            foreach (var typeArg in namedType.TypeArguments)
            {
                if (!typeArg.IsAccessible())
                {
                    return false;
                }
            }
        }

        // For array types, check the element type
        if (symbol is IArrayTypeSymbol arrayType)
        {
            if (!arrayType.ElementType.IsAccessible())
            {
                return false;
            }
        }

        return true;
    }

    public static bool CheckGenericValidity(this ITypeSymbol containingType)
    {
        var toValidate = new Stack<ITypeSymbol>();
        var visited = new HashSet<ITypeSymbol>(TupleSanitizedEqualityComparer.Default);

        var current = containingType;
        while (current != null)
        {
            toValidate.Push(current);
            current = current.ContainingType;
        }

        // Validate all types
        while (toValidate.Count > 0)
        {
            var type = toValidate.Pop();

            // Skip if already visited to prevent infinite loops
            if (!visited.Add(type))
                continue;

            switch (type)
            {
                case ITypeParameterSymbol:
                    return false;

                case IArrayTypeSymbol arrayTypeSymbol:
                    toValidate.Push(arrayTypeSymbol.ElementType);
                    break;

                case INamedTypeSymbol { IsGenericType: true } namedTypeSymbol:
                    // Validate generic type
                    if (namedTypeSymbol.TypeArguments.Length != namedTypeSymbol.TypeParameters.Length)
                        return false;

                    foreach (var typeArg in namedTypeSymbol.TypeArguments)
                    {
                        if (typeArg.TypeKind == TypeKind.TypeParameter)
                            return false;
                    }

                    // Push type arguments to stack for validation
                    for (int i = namedTypeSymbol.TypeArguments.Length - 1; i >= 0; i--)
                    {
                        toValidate.Push(namedTypeSymbol.TypeArguments[i]);
                    }

                    break;

                case INamedTypeSymbol:
                    break;

                default:
                    if (type.IsUnmanagedType)
                        break;

                    return false;
            }
        }

        return true;
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

    public static bool IsSealedOrStruct(this ITypeSymbol? typeSymbol)
    {
        if (typeSymbol == null)
            return false;

        if (typeSymbol.IsValueType)
            return true;

        if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
            return namedTypeSymbol.IsSealed;

        return false;
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
    internal static int GetLegacyNonRandomizedHashCode(this string str)
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
    /// Gets the hierarchy level of a type based on its composition depth.
    /// Base types (int, string, etc.) are level 1.
    /// Arrays add their rank + 1 to their element type's level.
    /// For example: int[] is level 2, int[,] is level 3, int[,,] is level 4.
    /// For jagged arrays: int[][] is level 3, int[][][] is level 4.
    /// For multi-dim arrays of arrays: int[,][] is level 4, int[,,,][] is level 6.
    /// Generic types are 1 + max(type argument levels).
    /// For example: KeyValuePair&lt;int[][], int[,,,][]&gt; is 1 + max(3, 6) = 7 - 1 = 6
    /// Uses memoization to avoid redundant computation.
    /// </summary>
    public static int GetTypeHierarchyLevel(this ITypeSymbol type)
    {
        if (TypeHierarchyLevelCache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var level = type switch
        {
            // Arrays: rank + element level
            // For 1D arrays (rank=1): 1 + element level
            // For 2D arrays (rank=2): 2 + element level
            // For 3D arrays (rank=3): 3 + element level, etc.
            IArrayTypeSymbol arrayType => arrayType.Rank + GetTypeHierarchyLevel(arrayType.ElementType),

            // Generics: 1 + max level of type arguments
            INamedTypeSymbol { IsGenericType: true } namedType =>
                1 + namedType.TypeArguments.Max(GetTypeHierarchyLevel),

            // Base types (including non-generic named types)
            _ => 1
        };

        TypeHierarchyLevelCache[type] = level;
        return level;
    }
}

/// <summary>
/// Custom equality comparer that treats tuple types with the same type arguments as equal,
/// regardless of field names, using Roslyn's TupleUnderlyingType.
/// </summary>
public class TupleSanitizedEqualityComparer : IEqualityComparer<ITypeSymbol>
{
    private TupleSanitizedEqualityComparer()
    {
    }

    public static TupleSanitizedEqualityComparer Default { get; } = new();

    public bool Equals(ITypeSymbol? x, ITypeSymbol? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;

        // Normalize both types to handle tuple field names
        var normalizedX = x.GetNormalizedTypeSymbol();
        var normalizedY = y.GetNormalizedTypeSymbol();

        // Use default symbol equality on normalized types
        return SymbolEqualityComparer.Default.Equals(normalizedX, normalizedY);
    }

    public int GetHashCode(ITypeSymbol? obj)
    {
        if (obj is null) return 0;

        // Use hash code of normalized type
        var normalized = obj.GetNormalizedTypeSymbol();
        return SymbolEqualityComparer.Default.GetHashCode(normalized);
    }
}
