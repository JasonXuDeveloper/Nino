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

// ReSharper disable HeuristicUnreachableCode
#pragma warning disable CS0162 // Unreachable code detected

namespace Nino.Generator;

public static class NinoTypeHelper
{
    public const string WeakVersionToleranceSymbol = "WEAK_VERSION_TOLERANCE";

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
                           symbol.TypeArguments.All(t => t is INamedTypeSymbol)))))
            .ToList();
    }

    public static IEnumerable<ITypeSymbol> GetAllNinoRequiredTypes(Compilation compilation)
    {
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
            .ToList();
        foreach (var typeSymbol in ret.ToList())
        {
            AddElementRecursively(typeSymbol, ret);
        }

        return ret.Distinct(SymbolEqualityComparer.Default)
            .Where(s => s != null)
            .Select(s => (ITypeSymbol)s!)
            .ToList();
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

    public static IEnumerable<INamedTypeSymbol> GetAllTypes(Compilation compilation)
    {
        var allTypes = new List<INamedTypeSymbol>();

        // Add all types from the current assembly (compilation)
        allTypes.AddRange(GetTypesInNamespace(compilation.GlobalNamespace));

        // Add all types from each referenced assembly
        foreach (var referencedAssembly in compilation.References)
        {
            var assemblySymbol = compilation.GetAssemblyOrModuleSymbol(referencedAssembly) as IAssemblySymbol;
            if (assemblySymbol != null)
            {
                allTypes.AddRange(GetTypesInNamespace(assemblySymbol.GlobalNamespace));
            }
        }


        //distinct
        return allTypes
            .Distinct(SymbolEqualityComparer.Default)
            .Where(s => s != null)
            .Select(s => (INamedTypeSymbol)s!)
            .ToList();
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

    public static ITypeSymbol ReplaceTypeParameters(this ITypeSymbol declaredType, ITypeSymbol symbol)
    {
        //if declaredType is type parameter, get the type from the symbol
        if (declaredType.TypeKind == TypeKind.TypeParameter)
        {
            INamedTypeSymbol namedTypeSymbol = (INamedTypeSymbol)symbol;
            int typeParamIndex = namedTypeSymbol.TypeParameters.ToList()
                .FindIndex(p => p.Name == declaredType.Name);
            if (typeParamIndex == -1)
                throw new Exception("typeParamIndex is -1");
            declaredType = namedTypeSymbol.TypeArguments[typeParamIndex];
            declaredType = declaredType.ReplaceTypeParameters(symbol);
        }
        //if declaredType is a generic type, that uses a type parameter, substitute it with the actual type argument
        else if (declaredType is INamedTypeSymbol namedTypeSymbol)
        {
            // If declaredType is a generic type, replace its type arguments recursively
            if (namedTypeSymbol.IsGenericType)
            {
                // Replace type parameters within each generic argument
                var newArguments = namedTypeSymbol.TypeArguments.Select(arg =>
                        arg.ReplaceTypeParameters(symbol) // Recursive call for nested replacements
                ).ToArray();

                // Only construct if replacements were made
                if (!newArguments.SequenceEqual(namedTypeSymbol.TypeArguments, SymbolEqualityComparer.Default))
                {
                    return namedTypeSymbol.ConstructedFrom.Construct(newArguments);
                }
            }
        }

        return declaredType;
    }


    public static bool IsNinoType(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.GetAttributes().Any(static a => a.AttributeClass?.Name == "NinoTypeAttribute");
    }

    public static bool IsReferenceType(this ITypeSymbol typeDecl)
    {
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
                    public static void Deserialize{{typeParam}}(Span<byte> data, out {{typeName}} value) {{genericConstraint}}
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

    public static ITypeSymbol? GetDeclaredTypeFullName(this CSharpSyntaxNode memberDeclaration,
        Compilation compilation, ITypeSymbol declaringType)
    {
        var name = memberDeclaration.GetMemberName();
        ITypeSymbol? declaredType = null;
        var s = declaringType.GetMembers().FirstOrDefault(s => s.Name == name);
        while (s == null && declaringType.BaseType != null)
        {
            declaringType = declaringType.BaseType;
            s = declaringType.GetMembers().FirstOrDefault(symbol => symbol.Name == name);
        }

        switch (s)
        {
            case IFieldSymbol fs:
                declaredType = fs.Type;
                break;
            case IPropertySymbol ps:
                declaredType = ps.Type;
                break;
            case IParameterSymbol ps:
                declaredType = ps.Type;
                break;
        }

        return declaredType;
    }

    public static string? GetMemberName(this CSharpSyntaxNode member)
    {
        return member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            ParameterSyntax parameter => parameter.Identifier.Text,
            _ => null
        };
    }

    public static List<CSharpSyntaxNode> GetNinoTypeMembers(this ITypeSymbol typeSymbol,
        List<ITypeSymbol>? parentNinoTypes)
    {
        //ensure type has attribute NinoType
        if (!IsNinoType(typeSymbol))
        {
            return new List<CSharpSyntaxNode>();
        }

        //get NinoType attribute first argument value from typeSymbol
        var autoCollectValue = typeSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass != null &&
            a.AttributeClass.ToDisplayString().EndsWith("NinoTypeAttribute"));
        bool autoCollect = autoCollectValue == null || (bool)(autoCollectValue.ConstructorArguments[0].Value ?? false);

        //true = auto collect, false = manual collect with NinoMemberAttribute
        Dictionary<string, int> memberIndex = new Dictionary<string, int>();
        List<ITypeSymbol> ninoTypes = new List<ITypeSymbol>();
        ninoTypes.Add(typeSymbol);
        if (parentNinoTypes != null)
            ninoTypes.AddRange(parentNinoTypes);
        //get all fields and properties with getter and setter
        var ret =
            //consider record (init only) properties
            //i.e. public record Record(int A, string B);, we want to get A and B
            ninoTypes.Where(static t => t.IsRecord)
                //get record's primary constructor members
                .Select(static t => t.DeclaringSyntaxReferences.First().GetSyntax())
                .OfType<RecordDeclarationSyntax>()
                .Where(static r => r != null && r.ParameterList != null)
                //now extract the init only properties (A and B) from the record declaration
                .SelectMany(static r => r!.ParameterList!.Parameters)
                .Where(static p => p.Type != null)
                .Concat(
                    ninoTypes
                        .SelectMany(static t => t.DeclaringSyntaxReferences
                            .Select(static r => r.GetSyntax())
                            .OfType<TypeDeclarationSyntax>())
                        .SelectMany(static t => t.Members)
                        .Where(static m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax
                        {
                            AccessorList: not null
                        })
                        .Select(static m => m as CSharpSyntaxNode)
                        .Where(static m => m != null))
                .Where(node =>
                {
                    MemberDeclarationSyntax? m = node as MemberDeclarationSyntax;
                    //has to be public
                    if (m != null)
                    {
                        if (!m.Modifiers.Any(static m => m.Text == "public"))
                        {
                            return false;
                        }
                    }

                    var attrList = m?.AttributeLists ?? ((ParameterSyntax)node).AttributeLists;

                    //if has ninoignore attribute, ignore this member
                    if (attrList.SelectMany(static al => al.Attributes)
                        .Any(static a => a.Name.ToString() == "NinoIgnore"))
                    {
                        return false;
                    }

                    var memberName = node.GetMemberName();
                    if (memberName == null)
                    {
                        return false;
                    }

                    if (autoCollect)
                    {
                        memberIndex[memberName] = memberIndex.Count;
                        return true;
                    }


                    //get nino member attribute's first argument on this member
                    var arg = attrList.SelectMany(static al => al.Attributes)
                        .Where(static a => a.Name.ToString() == "NinoMember")
                        .Select(static a => a.ArgumentList?.Arguments.FirstOrDefault())
                        .Select(static a => a?.Expression)
                        .OfType<LiteralExpressionSyntax>()
                        .FirstOrDefault();

                    if (arg == null)
                    {
                        return false;
                    }

                    //get index value from NinoMemberAttribute
                    var indexValue = arg.Token.Value as int?;
                    if (indexValue == null)
                    {
                        return false;
                    }

                    memberIndex[memberName] = indexValue.Value;
                    return true;
                })
                .ToList();

        //sort by name
        ret.Sort((a, b) =>
        {
            var aName = a.GetMemberName();
            var bName = b.GetMemberName();
            return string.Compare(aName, bName, StringComparison.Ordinal);
        });
        //sort by index
        ret.Sort((a, b) =>
        {
            var aName = a.GetMemberName();
            var bName = b.GetMemberName();
            return memberIndex[aName!].CompareTo(memberIndex[bName!]);
        });
        return ret;
    }
}