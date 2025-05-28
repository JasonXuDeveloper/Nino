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
    internal const string NinoTypeAttributeFullName = "Nino.NinoTypeAttribute";
    internal const string NinoFormerNameAttributeFullName = "Nino.NinoFormerNameAttribute";
    internal const string NinoConstructorAttributeFullName = "Nino.NinoConstructorAttribute";
    
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

    public static List<ITypeSymbol> MergeTypes(this List<ITypeSymbol?> types, List<ITypeSymbol?> otherTypes, Compilation compilation, INamedTypeSymbol? ninoTypeAttributeSymbol)
    {
        HashSet<ITypeSymbol> finalCollectedTypes = new(SymbolEqualityComparer.Default);
        Queue<ITypeSymbol> workQueue = new();

        void TryAddAndEnqueue(ITypeSymbol? typeSymbol)
        {
            if (typeSymbol == null) return;
            var pureType = typeSymbol.GetPureType();
            // Add to set and if it's a new type, add to queue for further processing
            if (finalCollectedTypes.Add(pureType))
            {
                workQueue.Enqueue(pureType);
            }
        }

        foreach (var typeSymbol in types)
        {
            TryAddAndEnqueue(typeSymbol);
        }

        foreach (var typeSymbol in otherTypes)
        {
            TryAddAndEnqueue(typeSymbol);
        }

        while (workQueue.Count > 0)
        {
            var currentSymbol = workQueue.Dequeue();
            // AddElementRecursively will add to finalCollectedTypes and workQueue if new types are found
            AddElementRecursively(currentSymbol, finalCollectedTypes, workQueue, compilation, ninoTypeAttributeSymbol);
        }

        return finalCollectedTypes.ToList();
    }

    public static (bool isValid, Compilation newCompilation) IsValidCompilation(this Compilation compilation)
    {
        //make sure the compilation contains the Nino.Core assembly
        if (!compilation.ReferencedAssemblyNames.Any(static a => a.Name == "Nino.Core"))
        {
            return (false, compilation);
        }

        // It is generally not recommended for a source generator to modify the input Compilation's options,
        // as it can interfere with Roslyn's caching and incremental compilation.
        // Nullable context should ideally be controlled by the user's project settings.
        // If specific generated code requires a disabled nullable context, pragmas should be used in that generated code.
        // For this reason, the modification of compilation options is removed here.
        // The original compilation object is returned.
        Compilation newCompilation = compilation; // Use the original compilation

        //make sure the compilation indeed uses Nino.Core
        // Note: Iterating all syntax trees can be expensive.
        // This check is okay for a bail-out, but generators usually rely on more specific triggers
        // like attributes found via ForAttributeWithMetadataName.
        foreach (var syntaxTree in newCompilation.SyntaxTrees) // Iterate on newCompilation which is same as compilation
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

    private static bool CheckGenericValidity(ITypeSymbol containingType, Compilation compilation, INamedTypeSymbol? ninoTypeAttributeSymbol)
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
                    namedTypeSymbol.TypeArguments.All(IsContainingTypeValid), // Recursive call, ninoTypeAttributeSymbol is implicitly captured if needed by IsNinoType here
                INamedTypeSymbol => true, // Potentially needs IsNinoType check if that's part of validity for non-generics
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
        var ninoTypeAttributeSymbol = compilation.GetTypeByMetadataName(NinoTypeAttributeFullName);

        foreach (var syntax in syntaxes)
        {
            if (!visited.Add(syntax.ToFullString())) continue;
            var typeSymbol = syntax.GetTypeSymbol(compilation);
            if (typeSymbol != null && typeSymbol.IsNinoType(ninoTypeAttributeSymbol)
                                   && typeSymbol is not ITypeParameterSymbol
                                   && CheckGenericValidity(typeSymbol, compilation, ninoTypeAttributeSymbol))
                typeSymbols.Add(typeSymbol.GetPureType());
        }

        // GetAllNinoRequiredTypes will also need ninoTypeAttributeSymbol
        foreach (var typeSymbolFromRequired in GetAllNinoRequiredTypes(syntaxes.Select(s => (TypeSyntax)s).ToImmutableArray(), compilation, ninoTypeAttributeSymbol))
        {
            // IsNinoType check might be redundant if GetAllNinoRequiredTypes already filters by it.
            // However, CheckGenericValidity still needs to be performed.
            if (typeSymbolFromRequired != null && typeSymbolFromRequired.IsNinoType(ninoTypeAttributeSymbol) // Potentially redundant if GetAllNinoRequiredTypes guarantees this
                               && typeSymbolFromRequired is not ITypeParameterSymbol
                               && CheckGenericValidity(typeSymbolFromRequired, compilation, ninoTypeAttributeSymbol))
                typeSymbols.Add(typeSymbolFromRequired.GetPureType());
        }

        return typeSymbols.ToList();
    }

    // Overload or modify existing to accept ninoTypeAttributeSymbol
    public static List<ITypeSymbol> GetAllNinoRequiredTypes(this ImmutableArray<TypeSyntax> syntaxes,
        Compilation compilation, INamedTypeSymbol? ninoTypeAttributeSymbol)
    {
        HashSet<ITypeSymbol> collectedTypes = new(SymbolEqualityComparer.Default);
        Queue<ITypeSymbol> workQueue = new();

        // Initial population from syntaxes
        foreach (var syntax in syntaxes)
        {
            if (syntax == null) continue;
            var typeSymbol = syntax.GetTypeSymbol(compilation);
            if (typeSymbol != null && typeSymbol.IsNinoType(ninoTypeAttributeSymbol) // Ensure it's a NinoType first
                                   && typeSymbol is not ITypeParameterSymbol
                                   && CheckGenericValidity(typeSymbol, compilation, ninoTypeAttributeSymbol))
            {
                var pureType = typeSymbol.GetPureType();
                if (collectedTypes.Add(pureType))
                {
                    workQueue.Enqueue(pureType);
                }
            }
        }

        while (workQueue.Count > 0)
        {
            var currentSymbol = workQueue.Dequeue();

            // Add members' types
            if (currentSymbol is INamedTypeSymbol currentNamedSymbol) 
            {
                var members = currentNamedSymbol.GetMembers();
                foreach (var member in members)
                {
                    ITypeSymbol? memberType = null;
                    switch (member)
                    {
                        case IFieldSymbol fieldSymbol:
                            memberType = fieldSymbol.Type;
                            break;
                        case IPropertySymbol propertySymbol:
                            memberType = propertySymbol.Type;
                            break;
                    }

                    if (memberType != null)
                    {
                        var pureMemberType = memberType.GetPureType();
                        if (pureMemberType is not ITypeParameterSymbol && 
                            (pureMemberType.IsNinoType(ninoTypeAttributeSymbol) || pureMemberType.TypeKind == TypeKind.Array || (pureMemberType is INamedTypeSymbol nts && nts.IsGenericType)) && 
                            collectedTypes.Add(pureMemberType))
                        {
                            workQueue.Enqueue(pureMemberType);
                        }
                    }
                }
            }
            AddElementRecursively(currentSymbol, collectedTypes, workQueue, compilation, ninoTypeAttributeSymbol);
        }
        return collectedTypes.ToList();
    }

    private static void AddElementRecursively(ITypeSymbol symbol, HashSet<ITypeSymbol> collectedTypes, Queue<ITypeSymbol> workQueue, Compilation compilation, INamedTypeSymbol? ninoTypeAttributeSymbol)
    {
        if (symbol is ITypeParameterSymbol) return; 
        var pureSymbol = symbol.GetPureType();

        if (pureSymbol is INamedTypeSymbol namedTypeSymbol)
        {
            AddTypeArguments(namedTypeSymbol, collectedTypes, workQueue, compilation, ninoTypeAttributeSymbol);
        }
        else if (pureSymbol is IArrayTypeSymbol arrayTypeSymbol)
        {
            AddArrayElementType(arrayTypeSymbol, collectedTypes, workQueue, compilation, ninoTypeAttributeSymbol);
        }
    }

    private static void AddArrayElementType(IArrayTypeSymbol symbol, HashSet<ITypeSymbol> collectedTypes, Queue<ITypeSymbol> workQueue, Compilation compilation, INamedTypeSymbol? ninoTypeAttributeSymbol)
    {
        var elementType = symbol.ElementType.GetPureType();
        if (elementType is not ITypeParameterSymbol && 
            (elementType.IsNinoType(ninoTypeAttributeSymbol) || elementType.TypeKind == TypeKind.Array || (elementType is INamedTypeSymbol nts && nts.IsGenericType)) &&
            collectedTypes.Add(elementType))
        {
            workQueue.Enqueue(elementType);
        }
        AddElementRecursively(elementType, collectedTypes, workQueue, compilation, ninoTypeAttributeSymbol);
    }

    private static void AddTypeArguments(INamedTypeSymbol symbol, HashSet<ITypeSymbol> collectedTypes, Queue<ITypeSymbol> workQueue, Compilation compilation, INamedTypeSymbol? ninoTypeAttributeSymbol)
    {
        if (!symbol.IsGenericType) return;

        foreach (var typeArgument in symbol.TypeArguments)
        {
            var pureTypeArgument = typeArgument.GetPureType();
            if (pureTypeArgument is not ITypeParameterSymbol &&
                (pureTypeArgument.IsNinoType(ninoTypeAttributeSymbol) || pureTypeArgument.TypeKind == TypeKind.Array || (pureTypeArgument is INamedTypeSymbol nts && nts.IsGenericType)) &&
                collectedTypes.Add(pureTypeArgument))
            {
                workQueue.Enqueue(pureTypeArgument);
            }
            AddElementRecursively(pureTypeArgument, collectedTypes, workQueue, compilation, ninoTypeAttributeSymbol);
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

    // Public version for external compatibility or when compilation is not readily available
    public static bool IsNinoType(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.IsUnmanagedType ||
               typeSymbol.GetAttributes().Any(static a => a.AttributeClass?.Name == NinoTypeAttributeFullName || a.AttributeClass?.ToDisplayString() == NinoTypeAttributeFullName);
    }
    
    // Internal optimized version
    internal static bool IsNinoType(this ITypeSymbol typeSymbol, INamedTypeSymbol? ninoTypeAttributeSymbol)
    {
        if (typeSymbol.IsUnmanagedType) return true;
        if (ninoTypeAttributeSymbol == null) // Fallback or if attribute doesn't exist in compilation
        {
            return typeSymbol.GetAttributes().Any(static a => a.AttributeClass?.Name == NinoTypeAttributeFullName || a.AttributeClass?.ToDisplayString() == NinoTypeAttributeFullName);
        }
        return typeSymbol.GetAttributes().Any(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, ninoTypeAttributeSymbol));
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

    // Public version
    public static AttributeData? GetNinoConstructorAttribute(this IMethodSymbol? methodSymbol)
    {
        if (methodSymbol == null) return null;
        return methodSymbol.GetAttributes()
            .FirstOrDefault(static a => a.AttributeClass?.Name == NinoConstructorAttributeFullName || a.AttributeClass?.ToDisplayString() == NinoConstructorAttributeFullName);
    }

    // Internal optimized version
    internal static AttributeData? GetNinoConstructorAttribute(this IMethodSymbol? methodSymbol, INamedTypeSymbol? ninoConstructorAttributeSymbol)
    {
        if (methodSymbol == null) return null;
        if (ninoConstructorAttributeSymbol == null) // Fallback
        {
            return methodSymbol.GetAttributes()
                .FirstOrDefault(static a => a.AttributeClass?.Name == NinoConstructorAttributeFullName || a.AttributeClass?.ToDisplayString() == NinoConstructorAttributeFullName);
        }
        return methodSymbol.GetAttributes()
            .FirstOrDefault(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, ninoConstructorAttributeSymbol));
    }
    
    // Internal helper for NinoFormerNameAttribute
    internal static AttributeData? GetNinoFormerNameAttribute(this ITypeSymbol typeSymbol, INamedTypeSymbol? ninoFormerNameAttributeSymbol)
    {
        if (ninoFormerNameAttributeSymbol == null) // Fallback
        {
            return typeSymbol.GetAttributes()
                .FirstOrDefault(static a => a.AttributeClass?.Name == NinoFormerNameAttributeFullName || a.AttributeClass?.ToDisplayString() == NinoFormerNameAttributeFullName);
        }
        return typeSymbol.GetAttributes()
            .FirstOrDefault(ad => SymbolEqualityComparer.Default.Equals(ad.AttributeClass?.ConstructedFrom, ninoFormerNameAttributeSymbol));
    }

    // Modified GetId to potentially use the resolved symbol
    public static int GetId(this ITypeSymbol typeSymbol, INamedTypeSymbol? ninoFormerNameAttributeSymbol = null, Compilation? compilation = null)
    {
        AttributeData? formerName = null;
        if (ninoFormerNameAttributeSymbol != null)
        {
            formerName = typeSymbol.GetNinoFormerNameAttribute(ninoFormerNameAttributeSymbol);
        }
        else if (compilation != null) // Attempt to resolve if compilation is provided
        {
            var resolvedSymbol = compilation.GetTypeByMetadataName(NinoFormerNameAttributeFullName);
            formerName = typeSymbol.GetNinoFormerNameAttribute(resolvedSymbol);
        }
        else // Fallback to string comparison
        {
            formerName = typeSymbol.GetAttributes()
                .FirstOrDefault(static a => a.AttributeClass?.Name == NinoFormerNameAttributeFullName || a.AttributeClass?.ToDisplayString() == NinoFormerNameAttributeFullName);
        }

        if (formerName == null)
            return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) // This ToDisplayString is for ID generation, acceptable.
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
        // Split the template into lines to append with indent individually
        var template = $$"""
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
        
        var lines = template.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        sb.AppendLine(); // Keep the initial blank line if intended
        foreach (var line in lines)
        {
            // Append indent only if the line is not empty, otherwise just append a newline for blank lines
            if (line.Length > 0 || (line.Length == 0 && lines.Length > 1)) // Check if it's not an empty string from a split or if it's a deliberate blank line
            {
                 sb.Append(indent).AppendLine(line);
            }
            else if (lines.Length == 1 && line.Length == 0) // Single empty line template? (Unlikely for these methods)
            {
                 //sb.Append(indent).AppendLine(line); // or just sb.AppendLine();
            }
            else
            {
                // This case handles if Split results in empty strings that were not intended as blank lines.
                // For these specific templates, all lines have content or are intentional structure.
                // If a line from split is empty, and it's not the only line, it might mean an extra newline at end of template.
                // The current templates don't end with newlines that would cause extra empty strings from split.
            }
        }
        // The final sb.AppendLine() can add an extra blank line after the block.
        // The original logic had one before and one effectively after (due to AppendLine of indented block).
        // If the template itself ends with a newline, AppendLine(line) already handles it.
        // Let's ensure one blank line after the generated block.
        // If the template does not end with a newline, the last AppendLine(line) adds one.
        // If it does, then we might get two. The original `sb.AppendLine($"{indent}{ret}"); sb.AppendLine();`
        // implies two newlines after the content of ret.
        // Let's stick to one extra newline after the content.
        // If the last line of template is empty, AppendLine(line) handles it. If not, it adds one.
        // So, an additional sb.AppendLine() might not be needed if the template includes its own trailing newlines.
        // The provided templates are self-contained blocks.
        // The original code added an empty line, then the indented block (which itself ends with a newline via AppendLine), then another empty line.
        // This means: Blank line -> Content -> Blank line.
        // The loop with Append(indent).AppendLine(line) will ensure content ends with a newline.
        // So, only one additional sb.AppendLine() is needed if we want a blank line AFTER the content block.
        sb.AppendLine(); 
    }

    public static void GenerateClassDeserializeMethods(this StringBuilder sb, string typeFullName,
        string typeParam = "",
        string genericConstraint = "")
    {
        var indent = "        ";
        var template = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize{{typeParam}}(ReadOnlySpan<byte> data, out {{typeFullName}} value) {{genericConstraint}}
                    {
                        var reader = new Reader(data);
                        Deserialize(out value, ref reader);
                    }
                    """;
        var lines = template.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        sb.AppendLine(); 
        foreach (var line in lines)
        {
            if (line.Length > 0 || (line.Length == 0 && lines.Length > 1))
            {
                 sb.Append(indent).AppendLine(line);
            }
            else if (lines.Length == 1 && line.Length == 0)
            {
                 //sb.Append(indent).AppendLine(line);
            }
        }
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