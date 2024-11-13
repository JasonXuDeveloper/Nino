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

    public static bool IsValidCompilation(this Compilation compilation)
    {
        //make sure the compilation contains the Nino.Core assembly
        if (!compilation.ReferencedAssemblyNames.Any(static a => a.Name == "Nino.Core"))
        {
            return false;
        }

        //make sure the compilation indeed uses Nino.Core
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var root = syntaxTree.GetRoot();
            var usingDirectives = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(usingDirective => usingDirective.Name.ToString() == "Nino.Core");

            if (usingDirectives.Any())
            {
                return true; // Namespace is used in a using directive
            }
        }

        //or if any member has NinoTypeAttribute/NinoMemberAttribute/NinoIgnoreAttribute/NinoConstructorAttribute/NinoUtf8Attribute
        return compilation.SyntaxTrees
            .SelectMany(static s => s.GetRoot().DescendantNodes())
            .Any(static s => s is AttributeSyntax attributeSyntax &&
                             (attributeSyntax.Name.ToString() == "NinoType" ||
                              attributeSyntax.Name.ToString() == "NinoMember" ||
                              attributeSyntax.Name.ToString() == "NinoIgnore" ||
                              attributeSyntax.Name.ToString() == "NinoConstructor" ||
                              attributeSyntax.Name.ToString() == "NinoUtf8"));
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

    public record NinoMember(string Name, ITypeSymbol Type, AttributeData[] Attrs, bool IsCtorParam)
    {
        public readonly string Name = Name;
        public readonly ITypeSymbol Type = Type;
        public readonly AttributeData[] Attrs = Attrs;
        public readonly bool IsCtorParam = IsCtorParam;

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
        List<NinoMember> ret = new();
        var members = ninoTypes
            .SelectMany(t => t.GetMembers())
            .Where(m =>
            {
                if (m.IsImplicitlyDeclared)
                    return false;

                if (m is IFieldSymbol fieldSymbol)
                {
                    //has to be public
                    return fieldSymbol.DeclaredAccessibility == Accessibility.Public;
                }

                if (m is IPropertySymbol propertySymbol)
                {
                    //has to be public and has getter and setter
                    return propertySymbol.DeclaredAccessibility == Accessibility.Public &&
                           propertySymbol.GetMethod != null &&
                           propertySymbol.SetMethod != null;
                }

                return false;
            })
            .ToList();
        var primaryConstructorParams = new List<IParameterSymbol>();
        if (typeSymbol is INamedTypeSymbol namedTypeSymbol && namedTypeSymbol.IsRecord)
        {
            // Retrieve all public instance constructors
            var publicConstructors = namedTypeSymbol.InstanceConstructors
                .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared)
                .ToList();

            foreach (var constructor in publicConstructors)
            {
                // Check that each parameter in the constructor has a matching readonly property with the same name
                foreach (var parameter in constructor.Parameters)
                {
                    var matchingProperty = namedTypeSymbol.GetMembers()
                        .OfType<IPropertySymbol>()
                        .FirstOrDefault(p => p.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));

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
            if (attrList.Any(a => a.AttributeClass?.Name == "NinoIgnoreAttribute"))
            {
                continue;
            }

            var memberName = symbol.Name;
            if (memberIndex.ContainsKey(memberName))
            {
                continue;
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

            if (autoCollect)
            {
                memberIndex[memberName] = memberIndex.Count;
                ret.Add(new(memberName, memberType, attrList.ToArray(), symbol is IParameterSymbol));
                continue;
            }

            //get nino member attribute's first argument on this member
            var arg = attrList.FirstOrDefault(a => a.AttributeClass?.Name == "NinoMemberAttribute")?
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
            ret.Add(new(memberName, memberType, attrList.ToArray(), symbol is IParameterSymbol));
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