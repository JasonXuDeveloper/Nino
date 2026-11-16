using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

public sealed partial class NinoGenerator
{
    /// <summary>
    /// Whether <paramref name="invocation"/> may call a <c>NinoSerializer</c> / <c>NinoDeserializer</c> method that
    /// names a type (<see cref="NamesType"/>), from syntax alone: the method is named so, and is called on a type named
    /// <c>NinoSerializer</c> or <c>NinoDeserializer</c> (qualified or not), on an identifier (which may be a
    /// <c>using</c> alias of one, declared in any file), or without a receiver (<c>using static</c>).
    /// </summary>
    private static bool MayCallNino(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax { Name: var name, Expression: var receiver } =>
            NamesType(name, invocation.ArgumentList) && (receiver is IdentifierNameSyntax || IsNinoReceiver(receiver)) &&
            !NamesOpenMethodParameter(name, invocation),
        SimpleNameSyntax name => NamesType(name, invocation.ArgumentList) && !NamesOpenMethodParameter(name, invocation),
        _ => false,
    };

    /// <summary>
    /// An explicit type argument naming a lexical method type parameter cannot register a closed type or be
    /// named by a file-level interceptor. Qualified identifiers are still bound by Roslyn.
    /// </summary>
    private static bool NamesOpenMethodParameter(SimpleNameSyntax name, InvocationExpressionSyntax invocation)
    {
        if (name is not GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic)
            return false;
        var argument = generic.TypeArgumentList.Arguments[0];
        if (argument.ContainsDiagnostics)
            return false;
        for (var node = invocation.Parent; node != null && node is not TypeDeclarationSyntax; node = node.Parent)
        {
            var parameters = node switch
            {
                MethodDeclarationSyntax method when method.Body?.Span.Contains(invocation.Span) == true ||
                                                    method.ExpressionBody?.Span.Contains(invocation.Span) == true =>
                    method.TypeParameterList,
                LocalFunctionStatementSyntax local when local.Body?.Span.Contains(invocation.Span) == true ||
                                                        local.ExpressionBody?.Span.Contains(invocation.Span) == true =>
                    local.TypeParameterList,
                _ => null,
            };
            if (parameters != null && NamesParameter(argument, parameters.Parameters))
                return true;
        }

        return false;
    }

    private static bool NamesParameter(TypeSyntax type, SeparatedSyntaxList<TypeParameterSyntax> parameters,
        bool qualified = false)
    {
        switch (type)
        {
            case IdentifierNameSyntax identifier when !qualified &&
                 SyntaxFacts.GetContextualKeywordKind(identifier.Identifier.Text) == SyntaxKind.None &&
                 identifier.Identifier.Text is not ("dynamic" or "nint" or "nuint" or "var"):
                foreach (var parameter in parameters)
                    if (parameter.Identifier.ValueText == identifier.Identifier.ValueText)
                        return true;
                break;
            case ArrayTypeSyntax array:
                return NamesParameter(array.ElementType, parameters);
            case NullableTypeSyntax nullable:
                return NamesParameter(nullable.ElementType, parameters);
            case GenericNameSyntax generic:
                foreach (var argument in generic.TypeArgumentList.Arguments)
                    if (NamesParameter(argument, parameters))
                        return true;
                break;
            case QualifiedNameSyntax name:
                return NamesParameter(name.Left, parameters, qualified: true) ||
                       NamesParameter(name.Right, parameters, qualified: true);
            case AliasQualifiedNameSyntax alias:
                return NamesParameter(alias.Name, parameters, qualified: true);
            case TupleTypeSyntax tuple:
                foreach (var element in tuple.Elements)
                    if (NamesParameter(element.Type, parameters))
                        return true;
                break;
        }

        return false;
    }

    /// <summary>
    /// Whether a call of <paramref name="name"/> names, from syntax, the type it needs a formatter for: by type
    /// argument (<c>Serialize</c>, <c>Deserialize</c> and their <c>...Async</c> forms, inferred or not, and
    /// <c>GetFixedSize&lt;T&gt;</c>), or by a <c>typeof</c> argument (<c>SerializeBoxed</c>, <c>DeserializeBoxed</c>,
    /// their <c>...Async</c> forms and <c>IsSupported</c>; one given a
    /// <see cref="Type"/> computed at run time names nothing, and is not bound).
    /// </summary>
    private static bool NamesType(SimpleNameSyntax name, ArgumentListSyntax arguments) => name.Identifier.ValueText switch
    {
        "Serialize" or "Deserialize" or "SerializeAsync" or "DeserializeAsync" => true,
        "GetFixedSize" => name is GenericNameSyntax,
        "SerializeBoxed" or "DeserializeBoxed" or "SerializeBoxedAsync" or "DeserializeBoxedAsync" or "IsSupported" =>
            NinoSymbols.TypeOfArgument(arguments) != null,
        _ => false,
    };

    private static bool IsNinoReceiver(ExpressionSyntax receiver)
    {
        var name = receiver switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            TypeSyntax t => InheritedCandidates.SimpleName(t),
            _ => null,
        };
        return name is NinoNames.Serializer or NinoNames.Deserializer;
    }

    /// <summary>
    /// Whether the declaration has an attribute named like <c>[NinoType]</c>. A syntax-only hint, which keeps
    /// marked types from being bound as inheritance candidates: it misses aliased attributes, whose types then
    /// become candidates and are skipped once bound, and whose names reach <see cref="InheritedCandidates.Find"/>
    /// through the bound attribute instead (the names of the marked types' stream).
    /// </summary>
    private static bool HasNinoTypeSyntax(TypeDeclarationSyntax syntax)
    {
        foreach (var list in syntax.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                if (InheritedCandidates.SimpleName(attribute.Name) is { } name &&
                    (name.EndsWith(NinoNames.NinoType, StringComparison.Ordinal) ||
                     name.EndsWith(NinoNames.NinoTypeAttribute, StringComparison.Ordinal)))
                    return true;
            }
        }

        return false;
    }
}
