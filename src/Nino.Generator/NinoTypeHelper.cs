using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// ReSharper disable HeuristicUnreachableCode
#pragma warning disable CS0162 // Unreachable code detected

namespace Nino.Generator;

public static class NinoTypeHelper
{
    public const string WeakVersionToleranceSymbol = "WEAK_VERSION_TOLERANCE";

    public static List<ITypeSymbol> GetPotentialCollectionTypes(this ImmutableArray<TypeSyntax> types,
        Compilation compilation, bool forDeserialization = false)
    {
        var typeSymbols = types.Select(t =>
            {
                var model = compilation.GetSemanticModel(t.SyntaxTree);
                var typeInfo = model.GetTypeInfo(t);
                if (typeInfo.Type != null) return typeInfo.Type;

                return null;
            })
            .Concat(GetAllNinoRequiredTypes(compilation))
            .Where(s => s != null)
            .Select(s => s!)
            .Distinct(SymbolEqualityComparer.Default)
            .Select(s => (ITypeSymbol)s!)
            .Where(symbol => symbol.IsSerializableType())
            .ToList();
        //for typeSymbols implements ICollection<KeyValuePair<T1, T2>>, add type KeyValuePair<T1, T2> to typeSymbols
        var kvps = typeSymbols.Select(ts =>
        {
            var i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                namedTypeSymbol.Name == "ICollection" && namedTypeSymbol.TypeArguments.Length == 1);
            var cond = forDeserialization
                ? i != null //when deserializing, we want ICollection and dont care other things
                : i != null &&
                  !i.IsUnmanagedType && // when serializing, we want ICollection of what we want, i.e. not unmanaged, otherwise we have a default fallback
                  i.TypeArguments.All(t => t.IsSerializableType());
            if (cond)
            {
                return i!.TypeArguments[0].IsSerializableType() ? i.TypeArguments[0] : null;
            }

            return null;
        }).Where(ts => ts != null).Select(ts => ts!).ToList();
        typeSymbols.AddRange(kvps);
        typeSymbols = typeSymbols
            .Where(ts =>
            {
                //we dont want unmanaged
                if (ts.IsUnmanagedType) return false;
                //we dont want nino type
                if (ts.IsNinoType()) return false;
                //we dont want string
                if (ts.SpecialType == SpecialType.System_String) return false;

                //we dont want any of the type arguments to be a type parameter
                if (ts is INamedTypeSymbol s)
                {
                    bool IsTypeParameter(ITypeSymbol typeSymbol)
                    {
                        if (typeSymbol.TypeKind == TypeKind.TypeParameter) return true;
                        if (typeSymbol is INamedTypeSymbol namedTypeSymbol)
                        {
                            return namedTypeSymbol.TypeArguments.Any(IsTypeParameter);
                        }

                        return false;
                    }

                    if (s.TypeArguments.Any(IsTypeParameter)) return false;
                }

                //we dont want IList/ICollection of unmanaged
                var i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                    namedTypeSymbol.Name == (forDeserialization ? "IList" : "ICollection") &&
                    namedTypeSymbol.TypeArguments.Length == 1);
                if (i != null)
                {
                    if (i.TypeArguments[0].IsUnmanagedType) return false;
                }

                //we dont want Dictionary that has no getter/setter in its indexer
                var iDict = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                    namedTypeSymbol.Name == "IDictionary" && namedTypeSymbol.TypeArguments.Length == 2);
                if (iDict != null)
                {
                    var kType = iDict.TypeArguments[0];
                    var vType = iDict.TypeArguments[1];

                    //use indexer to set/get value, TODO alternatively, use attributes to specify relevant methods
                    var indexers = ts
                        .GetMembers()
                        .OfType<IPropertySymbol>()
                        .Where(p => p.IsIndexer)
                        .ToList();

                    //ensure there exists one public indexer that returns vType and takes only kType
                    var validIndexers = indexers.Where(p =>
                            p.Type.Equals(vType, SymbolEqualityComparer.Default) && p.Parameters.Length == 1 &&
                            p.Parameters[0].Type.Equals(kType, SymbolEqualityComparer.Default))
                        .ToList();
                    if (!validIndexers.Any()) return false;

                    //ensure the valid indexer has public getter and setter
                    if (validIndexers.Any(p => p.GetMethod?.DeclaredAccessibility == Accessibility.Public &&
                                               p.SetMethod?.DeclaredAccessibility == Accessibility.Public))
                    {
                        return true;
                    }

                    return false;
                }

                //we dont want array of unmanaged
                if (ts is IArrayTypeSymbol arrayTypeSymbol)
                {
                    if (arrayTypeSymbol.ElementType.TypeKind == TypeKind.TypeParameter) return false;
                    if (arrayTypeSymbol.ElementType.IsUnmanagedType) return false;
                }

                //we dont want nullable of unmanaged
                if (ts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                {
                    //get type parameter
                    // Get the type argument of Nullable<T>
                    if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                    {
                        if (namedTypeSymbol.TypeArguments[0].IsUnmanagedType) return false;
                        //we also dont want nullable of reference type, as it already has a null check
                        if (namedTypeSymbol.TypeArguments[0].IsReferenceType) return false;
                    }
                }

                //we dont want span of unmanaged
                if (ts.OriginalDefinition.ToDisplayString() == "System.Span<T>")
                {
                    if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
                    {
                        if (namedTypeSymbol.TypeArguments[0].IsUnmanagedType) return false;
                    }
                }

                //we dont want IDictionary of unmanaged
                i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
                    namedTypeSymbol.Name == "IDictionary" && namedTypeSymbol.TypeArguments.Length == 2);
                if (i != null)
                {
                    if (i.TypeArguments[0].IsUnmanagedType && i.TypeArguments[1].IsUnmanagedType) return false;
                }

                return true;
            }).ToList();
        typeSymbols.Sort((t1, t2) =>
            String.Compare(t1.ToDisplayString(), t2.ToDisplayString(), StringComparison.Ordinal));


        return typeSymbols;
    }

    public static bool IsSerializableType(this ITypeSymbol ts)
    {
        ts = ts.GetPureType();
        //we dont want void
        if (ts.SpecialType == SpecialType.System_Void) return false;
        //we accept string
        if (ts.SpecialType == SpecialType.System_String) return true;
        //we want nino type
        if (ts.IsNinoType()) return true;

        //we also want unmanaged type
        if (ts.IsUnmanagedType) return true;

        //we also want array of what we want
        if (ts is IArrayTypeSymbol arrayTypeSymbol)
        {
            return IsSerializableType(arrayTypeSymbol.ElementType);
        }

        //we also want KeyValuePair
        if (ts.OriginalDefinition.ToDisplayString() ==
            "System.Collections.Generic.KeyValuePair<TKey, TValue>")
        {
            if (ts is INamedTypeSymbol { TypeArguments.Length: 2 } namedTypeSymbol)
            {
                return IsSerializableType(namedTypeSymbol.TypeArguments[0]) &&
                       IsSerializableType(namedTypeSymbol.TypeArguments[1]);
            }
        }

        //if ts implements IList and type parameter is what we want
        var i = ts.AllInterfaces.FirstOrDefault(namedTypeSymbol =>
            namedTypeSymbol.Name == "ICollection" && namedTypeSymbol.TypeArguments.Length == 1);
        if (i != null)
            return IsSerializableType(i.TypeArguments[0]);

        //if ts is Span of what we want
        if (ts.OriginalDefinition.ToDisplayString() == "System.Span<T>")
        {
            if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
            {
                return IsSerializableType(namedTypeSymbol.TypeArguments[0]);
            }
        }

        //if ts is nullable of what we want
        if (ts.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            //get type parameter
            // Get the type argument of Nullable<T>
            if (ts is INamedTypeSymbol { TypeArguments.Length: 1 } namedTypeSymbol)
            {
                //we dont want nullable reference type
                return namedTypeSymbol.TypeArguments[0].IsValueType &&
                       IsSerializableType(namedTypeSymbol.TypeArguments[0]);
            }
        }

        //otherwise, we dont want it
        return false;
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
            // Create a new CSharpCompilationOptions with nullable warnings disabled
            var newOptions = csharpCompilation.Options.WithNullableContextOptions(NullableContextOptions.Disable);

            // Return a new compilation with the updated options
            newCompilation = csharpCompilation.WithOptions(newOptions);
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
                              attributeSyntax.Name.ToString().EndsWith("NinoUtf8"))), newCompilation);
    }

    public static IncrementalValuesProvider<CSharpSyntaxNode> GetTypeSyntaxes(
        this IncrementalGeneratorInitializationContext context)
    {
        return context.SyntaxProvider
            .CreateSyntaxProvider(
                //class decl, struct decl, interface decl, record decl, record struct decl
                predicate: static (s, _) => s is TypeDeclarationSyntax || s is TypeSyntax,
                transform: static (ctx, _) => ctx.Node as CSharpSyntaxNode)
            .Where(static m => m != null)!;
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
        if (!char.IsLetter(curNamespace[0]))
            curNamespace = $"_{curNamespace}";
        //replace special characters with _
        curNamespace = new string(curNamespace.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_').ToArray());
        curNamespace += "NinoGen";
        return curNamespace;
    }

    public static List<ITypeSymbol> GetNinoTypeSymbols(this ImmutableArray<CSharpSyntaxNode> syntaxes,
        Compilation compilation)
    {
        return syntaxes.Select(s => s.GetTypeSymbol(compilation))
            .Concat(GetAllNinoRequiredTypes(compilation))
            .Where(s => s != null)
            .Distinct(SymbolEqualityComparer.Default)
            .Select(s => (ITypeSymbol)s!)
            .Where(s => s.IsNinoType())
            .Where(s => s is not ITypeParameterSymbol)
            .Where(s => s is not INamedTypeSymbol ||
                        (s is INamedTypeSymbol symbol &&
                         (!symbol.IsGenericType ||
                          (symbol.TypeArguments.Length ==
                           symbol.TypeParameters.Length &&
                           symbol.TypeArguments.All(
                               t => t is INamedTypeSymbol n && n.TypeKind != TypeKind.TypeParameter)))))
            .Select(s => s.GetPureType())
            .Select(s =>
            {
                var containingType = s;

                //containing type can not be an uninstantiated generic type
                bool IsContainingTypeValid(ITypeSymbol type)
                {
                    if (type is INamedTypeSymbol namedTypeSymbol)
                    {
                        if (namedTypeSymbol.IsGenericType)
                        {
                            return namedTypeSymbol.TypeArguments.Length ==
                                   namedTypeSymbol.TypeParameters.Length &&
                                   namedTypeSymbol.TypeArguments.All(t =>
                                       t is INamedTypeSymbol n && n.TypeKind != TypeKind.TypeParameter) &&
                                   namedTypeSymbol.TypeArguments.All(IsContainingTypeValid);
                        }

                        return true;
                    }

                    return false;
                }

                while (containingType != null)
                {
                    if (IsContainingTypeValid(containingType))
                    {
                        containingType = containingType.ContainingType;
                    }
                    else
                    {
                        return null;
                    }
                }

                return s;
            })
            .Where(s => s != null)
            .Distinct(SymbolEqualityComparer.Default)
            .Where(s => s != null)
            .Select(s => (ITypeSymbol)s!)
            .ToList();
    }

    private static readonly ConcurrentDictionary<int, List<ITypeSymbol>> AllNinoRequiredTypesCache = new();

    public static IEnumerable<ITypeSymbol> GetAllNinoRequiredTypes(Compilation compilation)
    {
        if (AllNinoRequiredTypesCache.TryGetValue(compilation.GetHashCode(), out var types))
            //return a copy
            return types.ToArray();

        var lst = GetAllTypes(compilation)
            .Where(s => s != null)
            .Distinct(SymbolEqualityComparer.Default)
            .Select(s => (ITypeSymbol)s!)
            .Where(s => s.IsNinoType())
            .Where(s => s is not ITypeParameterSymbol)
            .Where(s => s is not INamedTypeSymbol ||
                        (s is INamedTypeSymbol symbol &&
                         (!symbol.IsGenericType ||
                          (symbol.TypeArguments.Length ==
                           symbol.TypeParameters.Length &&
                           symbol.TypeArguments.All(t => t is INamedTypeSymbol)))))
            .ToList();
        var ret = lst.Concat(lst.SelectMany(type =>
            {
                var members = type.GetMembers();
                var allTypes = new List<ITypeSymbol>();
                foreach (var member in members)
                {
                    switch (member)
                    {
                        case IFieldSymbol fieldSymbol:
                            allTypes.Add(fieldSymbol.Type);
                            break;
                        case IPropertySymbol propertySymbol:
                            allTypes.Add(propertySymbol.Type);
                            break;
                        case IParameterSymbol parameterSymbol:
                            allTypes.Add(parameterSymbol.Type);
                            break;
                    }
                }

                return allTypes;
            }))
            .Distinct(SymbolEqualityComparer.Default)
            .Where(s => s != null)
            .Select(s => (ITypeSymbol)s!)
            .Select(s => s.GetPureType())
            .ToList();
        foreach (var typeSymbol in ret.ToList())
        {
            AddElementRecursively(typeSymbol, ret);
        }

        AllNinoRequiredTypesCache[compilation.GetHashCode()] = ret.Distinct(SymbolEqualityComparer.Default)
            .Where(s => s != null)
            .Select(s => (ITypeSymbol)s!)
            .ToList();

        //return a copy
        return AllNinoRequiredTypesCache[compilation.GetHashCode()].ToArray();
    }

    private static void AddElementRecursively(ITypeSymbol symbol, List<ITypeSymbol> ret)
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

    private static void AddArrayElementType(IArrayTypeSymbol symbol, List<ITypeSymbol> ret)
    {
        ret.Add(symbol.ElementType);
        AddElementRecursively(symbol.ElementType, ret);
    }

    private static void AddTypeArguments(INamedTypeSymbol symbol, List<ITypeSymbol> ret)
    {
        foreach (var typeArgument in symbol.TypeArguments)
        {
            ret.Add(typeArgument);
            AddElementRecursively(typeArgument, ret);
        }
    }


    private static readonly ConcurrentDictionary<int, List<INamedTypeSymbol>> AllTypesCache = new();
    private static readonly ConcurrentDictionary<int, List<INamedTypeSymbol>> AllAssembliesTypesCache = new();

    [SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1024:Symbols should be compared for equality")]
    private static INamedTypeSymbol[] GetTypesInAssembly(IAssemblySymbol assembly)
    {
        var hash = string.IsNullOrEmpty(assembly.Name)
            ? assembly.Name.GetLegacyNonRandomizedHashCode()
            : assembly.GetHashCode();
        if (AllAssembliesTypesCache.TryGetValue(hash, out var types))
            //return a copy
            return types.ToArray();
        
        var allTypes = GetTypesInNamespace(assembly.GlobalNamespace).ToList();
        AllAssembliesTypesCache[hash] = allTypes;
        //return a copy
        return allTypes.ToArray();
    }

    public static IEnumerable<INamedTypeSymbol> GetAllTypes(Compilation compilation)
    {
        if (AllTypesCache.TryGetValue(compilation.GetHashCode(), out var types))
            //return a copy
            return types.ToArray();

        var allTypes = new List<INamedTypeSymbol>();

        // Add all types from the current assembly (compilation)
        allTypes.AddRange(GetTypesInNamespace(compilation.GlobalNamespace));

        // Add all types from each referenced assembly
        foreach (var referencedAssembly in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(referencedAssembly) is IAssemblySymbol assemblySymbol)
            {
                allTypes.AddRange(GetTypesInAssembly(assemblySymbol));
            }
        }

        //distinct
        AllTypesCache[compilation.GetHashCode()] = allTypes
            .Distinct(SymbolEqualityComparer.Default)
            .Where(s => s != null)
            .Select(s => (INamedTypeSymbol)s!)
            .ToList();

        //return a copy
        return AllTypesCache[compilation.GetHashCode()].ToArray();
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
            case TypeDeclarationSyntax typeDeclaration:
                return compilation.GetSemanticModel(typeDeclaration.SyntaxTree).GetDeclaredSymbol(typeDeclaration);
            case TypeSyntax typeSyntax:
                return compilation.GetSemanticModel(typeSyntax.SyntaxTree).GetTypeInfo(typeSyntax).Type;
        }

        return null;
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

    public static bool IsPolymorphicType(this ITypeSymbol typeDecl)
    {
        var baseType = typeDecl.BaseType;
        while (baseType != null)
        {
            if (baseType.IsNinoType())
                return true;
            baseType = baseType.BaseType;
        }

        var interfaces = typeDecl.Interfaces;
        if (interfaces.Any(i => i.IsNinoType()))
            return true;

        return typeDecl.IsReferenceType || typeDecl is { IsRecord: true, IsValueType: false } ||
               typeDecl.TypeKind == TypeKind.Interface;
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
        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .GetLegacyNonRandomizedHashCode();
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

    public static (Dictionary<string, List<string>> inheritanceMap,
        Dictionary<string, List<string>> subTypeMap,
        // ReSharper disable once UnusedTupleComponentInReturnValue
        ImmutableArray<string> topNinoTypes) GetInheritanceMap(this List<ITypeSymbol> ninoSymbols)
    {
        var ninoTypeModels = ninoSymbols.Select(m => m.GetTypeFullName()).ToImmutableArray();
        Dictionary<string, List<string>> inheritanceMap = new(); // type -> all base types
        Dictionary<string, List<string>> subTypeMap = new(); //top type -> all subtypes
        //get top nino types (i.e. types that are not inherited by other nino types)
        var topNinoTypes = ninoSymbols.Where(typeSymbol =>
        {
            List<string> inheritedTypes = new();
            string ninoTypeFullName = typeSymbol.GetTypeFullName();
            inheritanceMap.Add(ninoTypeFullName, inheritedTypes);

            //get toppest ninotype base type
            ITypeSymbol? baseType = typeSymbol;
            List<string> interfaces = new();
            interfaces.AddRange(baseType.Interfaces.Select(i => i.GetTypeFullName()));
            while (baseType.BaseType != null)
            {
                baseType = baseType.BaseType;
                string baseTypeFullName = baseType.GetTypeFullName();
                if (ninoTypeModels.Contains(baseTypeFullName))
                {
                    interfaces.AddRange(baseType.Interfaces.Select(i => i.GetTypeFullName()));
                    if (subTypeMap.ContainsKey(baseTypeFullName))
                    {
                        subTypeMap[baseTypeFullName].Add(ninoTypeFullName);
                    }
                    else
                    {
                        subTypeMap.Add(baseTypeFullName, [ninoTypeFullName]);
                    }

                    inheritedTypes.Add(baseTypeFullName);
                }
                else
                {
                    break;
                }
            }

            //it may implement interfaces that are nino types
            foreach (var @interface in interfaces)
            {
                if (ninoTypeModels.Contains(@interface))
                {
                    if (subTypeMap.ContainsKey(@interface))
                    {
                        subTypeMap[@interface].Add(ninoTypeFullName);
                    }
                    else
                    {
                        subTypeMap.Add(@interface, [ninoTypeFullName]);
                    }

                    inheritedTypes.Add(@interface);
                }
            }

            return inheritedTypes.Count == 0;
        }).ToImmutableArray();

        return (inheritanceMap, subTypeMap, topNinoTypes.Select(t => t.GetTypeFullName()).ToImmutableArray());
    }

    public static string GetDeserializePrefix(this ITypeSymbol ts)
    {
        return "Deserialize";

        // v2 legacy code - not used
        if (!ts.IsNinoType())
        {
            return "Deserialize";
        }

        var assName = ts.ContainingAssembly.Name;
        var curNamespace = assName.GetNamespace();

        return $"{curNamespace}.Deserializer.Deserialize";
    }

    public static string GetSerializePrefix(this ITypeSymbol ts)
    {
        return "Serialize";

        // v2 legacy code - not used
        if (!ts.IsNinoType())
        {
            return "Serialize";
        }

        var assName = ts.ContainingAssembly.Name;
        var curNamespace = assName.GetNamespace();

        return $"{curNamespace}.Serializer.Serialize";
    }

    public static void GenerateClassSerializeMethods(this StringBuilder sb, string typeFullName, string typeParam = "",
        string genericConstraint = "")
    {
        sb.AppendLine($$"""
                        {{typeFullName.GeneratePublicSerializeMethodBody("        ", typeParam, genericConstraint)}}

                        """);
    }

    public static void GenerateClassDeserializeMethods(this StringBuilder sb, string typeFullName,
        string typeParam = "",
        string genericConstraint = "")
    {
        sb.AppendLine($$"""
                        {{typeFullName.GeneratePublicDeserializeMethodBody("        ", typeParam, genericConstraint)}}

                        """);
    }

    public static string GeneratePublicSerializeMethodBody(this string typeName, string indent = "",
        string typeParam = "",
        string genericConstraint = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static byte[] Serialize{{typeParam}}(this {{typeName}} value) {{genericConstraint}}
                    {
                        var bufferWriter = GetBufferWriter();
                        Serialize(value, bufferWriter);
                        var ret = bufferWriter.WrittenSpan.ToArray();
                        ReturnBufferWriter(bufferWriter);
                        return ret;
                    }

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Serialize{{typeParam}}(this {{typeName}} value, IBufferWriter<byte> bufferWriter) {{genericConstraint}}
                    {
                        Writer writer = new Writer(bufferWriter);
                        value.Serialize(ref writer);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    public static string GeneratePublicDeserializeMethodBody(this string typeName, string indent = "",
        string typeParam = "",
        string genericConstraint = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize{{typeParam}}(ReadOnlySpan<byte> data, out {{typeName}} value) {{genericConstraint}}
                    {
                        var reader = new Reader(data);
                        Deserialize(out value, ref reader);
                    }
                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    public static string GetTypeFullName(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.ToDisplayString();
    }

    public record NinoMember(
        string Name,
        ITypeSymbol Type,
        AttributeData[] Attrs,
        bool IsCtorParam,
        bool IsPrivate,
        bool IsProperty)
    {
        public readonly string Name = Name;
        public readonly ITypeSymbol Type = Type;
        public readonly AttributeData[] Attrs = Attrs;
        public readonly bool IsCtorParam = IsCtorParam;
        public readonly bool IsPrivate = IsPrivate;
        public readonly bool IsProperty = IsProperty;

        public virtual bool Equals(NinoMember? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Name == other.Name && Type.Equals(other.Type, SymbolEqualityComparer.Default) &&
                   Attrs.SequenceEqual(other.Attrs) &&
                   IsCtorParam == other.IsCtorParam;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Name.GetHashCode();
                hashCode = (hashCode * 397) ^ Type.Name.GetHashCode();
                hashCode = (hashCode * 397) ^ Attrs.GetHashCode();
                hashCode = (hashCode * 397) ^ IsCtorParam.GetHashCode();
                return hashCode;
            }
        }
    };

    public static List<NinoMember> GetNinoTypeMembers(
        this ITypeSymbol typeSymbol,
        List<ITypeSymbol>? parentNinoTypes)
    {
        //ensure type has attribute NinoType
        if (!IsNinoType(typeSymbol))
        {
            return new List<NinoMember>();
        }

        //get NinoType attribute first argument value from typeSymbol
        var attr = typeSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass != null &&
            a.AttributeClass.ToDisplayString().EndsWith("NinoTypeAttribute"));
        bool autoCollect = attr == null || (bool)(attr.ConstructorArguments[0].Value ?? false);
        bool containNonPublic = attr != null && (bool)(attr.ConstructorArguments[1].Value ?? false);

        //true = auto collect, false = manual collect with NinoMemberAttribute
        Dictionary<string, int> memberIndex = new Dictionary<string, int>();
        List<ITypeSymbol> ninoTypes = new List<ITypeSymbol>();
        ninoTypes.Add(typeSymbol);
        if (parentNinoTypes != null)
            ninoTypes.AddRange(parentNinoTypes);
        List<NinoMember> ret = new();
        var members = ninoTypes
            .SelectMany(t => t.GetMembers())
            .Where(m =>
            {
                if (m.IsImplicitlyDeclared)
                    return false;

                if (m is IFieldSymbol fieldSymbol)
                {
                    //has to be not static
                    return (containNonPublic || fieldSymbol.DeclaredAccessibility == Accessibility.Public) &&
                           !fieldSymbol.IsStatic;
                }

                if (m is IPropertySymbol propertySymbol)
                {
                    //has getter and setter and not static
                    return
                        propertySymbol.GetMethod != null &&
                        propertySymbol.SetMethod != null &&
                        (containNonPublic || propertySymbol.DeclaredAccessibility == Accessibility.Public) &&
                        !propertySymbol.IsStatic;
                }

                return false;
            })
            .ToList();
        var primaryConstructorParams = new List<IParameterSymbol>();
        if (typeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsRecord)
        {
            // Retrieve all public instance constructors
            var publicConstructors = namedTypeSymbol.InstanceConstructors
                .Where(c =>
                    c.DeclaredAccessibility == Accessibility.Public
                    && !c.IsImplicitlyDeclared
                    && !c.IsStatic
                )
                .ToList();

            foreach (var constructor in publicConstructors)
            {
                // Check that each parameter in the constructor has a matching readonly property with the same name
                foreach (var parameter in constructor.Parameters)
                {
                    var matchingProperty = namedTypeSymbol.GetMembers()
                        .OfType<IPropertySymbol>()
                        .FirstOrDefault(p =>
                            p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)
                            && !p.IsStatic);

                    // If any parameter does not have a matching readonly property, it’s likely not a primary constructor
                    if (matchingProperty == null || !matchingProperty.IsReadOnly)
                    {
                        break;
                    }
                }

                primaryConstructorParams.AddRange(constructor.Parameters);
                break;
            }
        }

        List<ISymbol> symbols = new();
        symbols.AddRange(members);
        symbols.AddRange(primaryConstructorParams);
        symbols = symbols.Distinct(SymbolEqualityComparer.Default).ToList();
        //for each symbol we get the attribute list
        foreach (var symbol in symbols)
        {
            var attrList = symbol.GetAttributes();
            //if has ninoignore attribute, ignore this member
            if (attrList.Any(a => a.AttributeClass?.Name.EndsWith("NinoIgnoreAttribute") ?? false))
            {
                continue;
            }

            var memberName = symbol.Name;
            if (memberIndex.ContainsKey(memberName))
            {
                continue;
            }

            bool isPrivate = symbol.DeclaredAccessibility != Accessibility.Public;
            //we dont count primary constructor params as private
            if (primaryConstructorParams.Contains(symbol, SymbolEqualityComparer.Default))
            {
                isPrivate = false;
            }

            var memberType = symbol switch
            {
                IFieldSymbol fieldSymbol => fieldSymbol.Type,
                IPropertySymbol propertySymbol => propertySymbol.Type,
                IParameterSymbol parameterSymbol => parameterSymbol.Type,
                _ => null
            };

            if (memberType == null)
            {
                continue;
            }

            //nullability check
            memberType = memberType.GetPureType();

            if (autoCollect)
            {
                memberIndex[memberName] = memberIndex.Count;
                ret.Add(new(memberName, memberType, attrList.ToArray(),
                    symbol is IParameterSymbol, isPrivate,
                    symbol is IPropertySymbol));
                continue;
            }

            //get nino member attribute's first argument on this member
            var arg = attrList.FirstOrDefault(a
                    => a.AttributeClass?.Name.EndsWith("NinoMemberAttribute") ?? false)?
                .ConstructorArguments.FirstOrDefault();
            if (arg == null)
            {
                continue;
            }

            //get index value from NinoMemberAttribute
            var indexValue = arg.Value.Value;
            if (indexValue == null)
            {
                continue;
            }

            memberIndex[memberName] = (ushort)indexValue;
            ret.Add(new(memberName, memberType, attrList.ToArray(),
                symbol is IParameterSymbol, isPrivate,
                symbol is IPropertySymbol));
        }

        //sort by name
        ret.Sort((a, b) =>
        {
            var aName = a.Name;
            var bName = b.Name;
            return string.Compare(aName, bName, StringComparison.Ordinal);
        });
        //sort by index
        ret.Sort((a, b) =>
        {
            var aName = a.Name;
            var bName = b.Name;
            return memberIndex[aName].CompareTo(memberIndex[bName]);
        });
        return ret;
    }
}