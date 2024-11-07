using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator;

public static class NinoTypeHelper
{
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

    public static List<ITypeSymbol> GetNinoTypeSymbols(this ImmutableArray<CSharpSyntaxNode> syntaxes, Compilation compilation)
    {
        return syntaxes.Select(s => s.GetTypeSymbol(compilation))
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
        return typeDecl.IsReferenceType || typeDecl is { IsRecord: true, IsValueType: false } || typeDecl.TypeKind == TypeKind.Interface;
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

    public static int GetId(this List<string> typeFullNames, string typeFullName)
    {
        int index = typeFullNames.IndexOf(typeFullName);
        return index + 4;
    }

    public static (Dictionary<string, List<string>> inheritanceMap,
        Dictionary<string, List<string>> subTypeMap,
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
        if (!ts.IsNinoType())
        {
            return "Deserialize";
        }

        var assName = ts.ContainingAssembly.Name;
        var curNamespace = $"{assName}";
        if (!string.IsNullOrEmpty(curNamespace))
            curNamespace = $"{curNamespace}_";
        if (!char.IsLetter(curNamespace[0]))
            curNamespace = $"_{curNamespace}";
        //replace special characters with _
        curNamespace =
            new string(curNamespace.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        curNamespace += "Nino";

        return $"{curNamespace}.Deserializer.Deserialize";
    }

    public static string GetSerializePrefix(this ITypeSymbol ts)
    {
        if (!ts.IsNinoType())
        {
            return "Serialize";
        }

        var assName = ts.ContainingAssembly.Name;
        var curNamespace = $"{assName}";
        if (!string.IsNullOrEmpty(curNamespace))
            curNamespace = $"{curNamespace}_";
        if (!char.IsLetter(curNamespace[0]))
            curNamespace = $"_{curNamespace}";
        //replace special characters with _
        curNamespace =
            new string(curNamespace.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        curNamespace += "Nino";

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

    public static string GeneratePublicDeserializeMethodBodyForSubType(this string typeName, string topType,
        string indent = "")
    {
        var ret = $$"""
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize(ReadOnlySpan<byte> data, out {{typeName}} value)
                    {
                        var reader = new Reader(data);
                        Deserialize(out value, ref reader);
                    }

                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    public static void Deserialize(out {{typeName}} value, ref Reader reader)
                    {
                        Deserialize(out {{topType}} v, ref reader);
                        value = ({{typeName}}) v;
                    }

                    """;

        // indent
        ret = ret.Replace("\n", $"\n{indent}");
        return $"{indent}{ret}";
    }

    public static string GetTypeFullName(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }
    
    public static ITypeSymbol? GetDeclaredTypeFullName(this CSharpSyntaxNode memberDeclaration,
        Compilation compilation)
    {
        var model = compilation.GetSemanticModel(memberDeclaration.SyntaxTree);

        switch (memberDeclaration)
        {
            case PropertyDeclarationSyntax propertyDeclaration:
                var propertySymbol = model.GetDeclaredSymbol(propertyDeclaration);
                return propertySymbol?.Type;

            case FieldDeclarationSyntax fieldDeclaration:
                var variable = fieldDeclaration.Declaration.Variables.First();
                var fieldSymbol = model.GetDeclaredSymbol(variable) as IFieldSymbol;
                return fieldSymbol?.Type;

            case ParameterSyntax parameterSyntax:
                var parameterSymbol = model.GetDeclaredSymbol(parameterSyntax);
                return parameterSymbol?.Type;
        }

        return null;
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