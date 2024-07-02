using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
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
            .Any(static a => a.Name.ToString() == "NinoType");

    public static bool IsNinoType(this ITypeSymbol typeSymbol)
    {
        return typeSymbol.GetAttributes().Any(static a => a.AttributeClass?.Name == "NinoTypeAttribute");
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
                        var ret = bufferWriter.WrittenMemory.ToArray();
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
                    private static void Deserialize(out {{typeName}} value, ref Reader reader)
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

    public static ITypeSymbol? GetDeclaredTypeFullName(this MemberDeclarationSyntax memberDeclaration,
        Compilation compilation)
    {
        var model = compilation.GetSemanticModel(memberDeclaration.SyntaxTree);

        switch (memberDeclaration)
        {
            case PropertyDeclarationSyntax propertyDeclaration:
                var propertySymbol = model.GetDeclaredSymbol(propertyDeclaration) as IPropertySymbol;
                return propertySymbol?.Type;

            case FieldDeclarationSyntax fieldDeclaration:
                var variable = fieldDeclaration.Declaration.Variables.First();
                var fieldSymbol = model.GetDeclaredSymbol(variable) as IFieldSymbol;
                return fieldSymbol?.Type;
        }

        return null;
    }

    public static string? GetMemberName(this MemberDeclarationSyntax member)
    {
        return member switch
        {
            FieldDeclarationSyntax field => field.Declaration.Variables.First().Identifier.Text,
            PropertyDeclarationSyntax property => property.Identifier.Text,
            _ => null
        };
    }

    public static List<MemberDeclarationSyntax> GetNinoTypeMembers(this TypeDeclarationSyntax typeDeclarationSyntax,
        List<TypeDeclarationSyntax>? parentNinoTypes)
    {
        //ensure type has attribute NinoType
        if (!IsNinoType(typeDeclarationSyntax))
        {
            return new List<MemberDeclarationSyntax>();
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
        var ret = ninoTypes.SelectMany(static t => t.Members)
            .Where(static m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax { AccessorList: not null })
            .Where(static m => m != null)
            .Where(m =>
            {
                //has to be public
                if (!m.Modifiers.Any(static m => m.Text == "public"))
                {
                    return false;
                }
                
                //if has ninoignore attribute, ignore this member
                if (m.AttributeLists.SelectMany(static al => al.Attributes)
                    .Any(static a => a.Name.ToString() == "NinoIgnore"))
                {
                    return false;
                }
                
                var memberName = m.GetMemberName();
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
                var arg = m.AttributeLists.SelectMany(static al => al.Attributes)
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