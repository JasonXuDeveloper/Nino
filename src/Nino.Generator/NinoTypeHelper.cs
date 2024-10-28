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
    public static IncrementalValuesProvider<TypeDeclarationSyntax> GetNinoTypeModels(
        this IncrementalGeneratorInitializationContext context)
    {
        return context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (s, _) => IsNinoType(s),
                transform: static (ctx, _) => GetSemanticTarget(ctx))
            .Where(static m => m is not null);
    }

    private static bool IsNinoType(SyntaxNode node) =>
        node is TypeDeclarationSyntax typeDeclarationSyntax &&
        typeDeclarationSyntax.AttributeLists.SelectMany(static al => al.Attributes)
            .Any(static a => a.Name.ToString().EndsWith("NinoType"));

    public static bool IsNinoType(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.GetAttributes().Any(static a => a.AttributeClass?.Name == "NinoTypeAttribute");
    }

    public static bool IsReferenceType(this TypeDeclarationSyntax typeDecl)
    {
        return typeDecl is ClassDeclarationSyntax or RecordDeclarationSyntax or InterfaceDeclarationSyntax;
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
        ImmutableArray<string> topNinoTypes) GetInheritanceMap(this Compilation compilation,
            ImmutableArray<TypeDeclarationSyntax> models)
    {
        var ninoTypeModels = models.Select(m => m.GetTypeFullName()).ToImmutableArray();
        Dictionary<string, List<string>> inheritanceMap = new(); // type -> all base types
        Dictionary<string, List<string>> subTypeMap = new(); //top type -> all subtypes
        //get top nino types (i.e. types that are not inherited by other nino types)
        var topNinoTypes = ninoTypeModels.Where(ninoTypeFullName =>
        {
            List<string> inheritedTypes = new();
            inheritanceMap.Add(ninoTypeFullName, inheritedTypes);
            INamedTypeSymbol? subTypeSymbol = compilation.GetTypeByMetadataName(ninoTypeFullName);
            if (subTypeSymbol == null)
            {
                //check if is a nested type
                TypeDeclarationSyntax? typeDeclarationSyntax = models.FirstOrDefault(m =>
                    string.Equals(m.GetTypeFullName(), ninoTypeFullName, StringComparison.Ordinal));

                if (typeDeclarationSyntax == null)
                    return false;

                var ninoTypeFullName2 = typeDeclarationSyntax.GetTypeFullName("+");
                subTypeSymbol = compilation.GetTypeByMetadataName(ninoTypeFullName2);
                if (subTypeSymbol == null)
                    return false;
            }

            //get toppest ninotype base type
            INamedTypeSymbol? baseType = subTypeSymbol;
            List<string> interfaces = new();
            interfaces.AddRange(baseType.Interfaces.Select(i => i.ToString()));
            while (baseType.BaseType != null)
            {
                baseType = baseType.BaseType;
                string baseTypeFullName = baseType.ToString();
                if (ninoTypeModels.Contains(baseTypeFullName))
                {
                    interfaces.AddRange(baseType.Interfaces.Select(i => i.ToString()));
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

        return (inheritanceMap, subTypeMap, topNinoTypes);
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

    public static INamedTypeSymbol GetTypeSymbol(this Compilation compilation, string typeFullName,
        ImmutableArray<TypeDeclarationSyntax> models)
    {
        var typeSymbol = compilation.GetTypeByMetadataName(typeFullName);
        if (typeSymbol == null)
        {
            //check if is a nested type
            TypeDeclarationSyntax? typeDeclarationSyntax = models.FirstOrDefault(m =>
                string.Equals(m.GetTypeFullName(), typeFullName, StringComparison.Ordinal));
            if (typeDeclarationSyntax == null)
                throw new Exception("typeDeclarationSyntax is null");

            var typeFullName2 = typeDeclarationSyntax.GetTypeFullName("+");
            typeSymbol = compilation.GetTypeByMetadataName(typeFullName2);
            if (typeSymbol == null)
                throw new Exception("structSymbol is null");
        }

        return typeSymbol;
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

    private static TypeDeclarationSyntax GetSemanticTarget(GeneratorSyntaxContext context)
    {
        return (TypeDeclarationSyntax)context.Node;
    }

    public static string GetTypeFullName(this TypeDeclarationSyntax typeDeclarationSyntax, string seperator = ".")
    {
        var namespaceName = GetNamespace(typeDeclarationSyntax);
        var typeName = GetFullTypeName(typeDeclarationSyntax, seperator);

        return string.IsNullOrEmpty(namespaceName) ? $"{typeName}" : $"{namespaceName}.{typeName}";
    }

    private static string GetNamespace(SyntaxNode node)
    {
        //namespace XXX { ... }
        var namespaceDeclaration = node.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        //file-scoped namespace i.e. namespace XXX;
        if (namespaceDeclaration == null)
        {
            var fileScopedNamespace = node.Ancestors().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
            return fileScopedNamespace?.Name.ToString() ?? string.Empty;
        }

        return namespaceDeclaration.Name.ToString();
    }

    private static string GetFullTypeName(TypeDeclarationSyntax typeDeclaration, string separator = ".")
    {
        var typeNames = new Stack<string>();
        var currentTypeDeclaration = typeDeclaration;

        while (currentTypeDeclaration != null)
        {
            typeNames.Push(currentTypeDeclaration.Identifier.Text);
            currentTypeDeclaration = currentTypeDeclaration.Parent as TypeDeclarationSyntax;
        }

        return string.Join(separator, typeNames);
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

    public static List<CSharpSyntaxNode> GetNinoTypeMembers(this TypeDeclarationSyntax typeDeclarationSyntax,
        List<TypeDeclarationSyntax>? parentNinoTypes)
    {
        //ensure type has attribute NinoType
        if (!IsNinoType(typeDeclarationSyntax))
        {
            return new List<CSharpSyntaxNode>();
        }

        //model is TypeDeclarationSyntax, get its NinoType attribute's first argument value
        var autoCollectValue = typeDeclarationSyntax.AttributeLists
            .SelectMany(static al => al.Attributes)
            .Where(static a => a.Name.ToString() == "NinoType")
            .Select(static a => a.ArgumentList?.Arguments.FirstOrDefault())
            .Select(static a => a?.Expression)
            .OfType<LiteralExpressionSyntax>()
            .FirstOrDefault();
        bool autoCollect = autoCollectValue?.Token.Value as bool? ?? true;

        //true = auto collect, false = manual collect with NinoMemberAttribute
        Dictionary<string, int> memberIndex = new Dictionary<string, int>();
        List<TypeDeclarationSyntax> ninoTypes = new List<TypeDeclarationSyntax>();
        ninoTypes.Add(typeDeclarationSyntax);
        if (parentNinoTypes != null)
            ninoTypes.AddRange(parentNinoTypes);
        //get all fields and properties with getter and setter
        var ret =
            //consider record (init only) properties
            //i.e. public record Record(int A, string B);, we want to get A and B
            ninoTypes.Where(static t => t is RecordDeclarationSyntax)
                .Select(static t => t as RecordDeclarationSyntax)
                .Where(static r => r != null && r.ParameterList != null)
                //now extract the init only properties (A and B) from the record declaration
                .SelectMany(static r => r!.ParameterList!.Parameters)
                .Where(static p => p.Type != null)
                .Concat(
                    ninoTypes
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