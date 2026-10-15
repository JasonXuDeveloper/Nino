using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NinoCodeFixProvider)), Shared]
public class NinoCodeFixProvider : CodeFixProvider
{
    private const string RemoveAttributeEquivalenceKey = "RemoveAttribute";
    private const string AddAttributeEquivalenceKey = "AddAttribute";
    private const string ChangeAccessibilityEquivalenceKey = "ChangeAccessibility";
    private const string ChangeParameterEquivalenceKey = "ChangeParameter";
    private const string ChangeIndexEquivalenceKey = "ChangeIndex";

    public override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(
        "NINO002", "NINO003", "NINO004", "NINO005", "NINO006", "NINO007", "NINO009"
    );

    public override FixAllProvider GetFixAllProvider()
    {
        return WellKnownFixAllProviders.BatchFixer;
    }

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null) return;

        var diagnostic = context.Diagnostics.First();
        var diagnosticSpan = diagnostic.Location.SourceSpan;
        var node = root.FindNode(diagnosticSpan);

        switch (diagnostic.Id)
        {
            case "NINO002": // Type must be public
                RegisterNino002Fix(context, root, node);
                break;

            case "NINO003": // Add [NinoType] to subtype
                RegisterNino003Fix(context, root, node);
                break;

            case "NINO004": // Remove redundant [NinoMember]
                RegisterNino004Fix(context, root, node);
                break;

            case "NINO005": // Remove redundant [NinoIgnore]
                RegisterNino005Fix(context, root, node);
                break;

            case "NINO006": // Ambiguous member (both attributes)
                RegisterNino006Fix(context, root, node);
                break;

            case "NINO007": // Private member issue
                await RegisterNino007FixAsync(context, root, node).ConfigureAwait(false);
                break;

            case "NINO009": // Duplicate member index
                await RegisterNino009FixAsync(context, root, node).ConfigureAwait(false);
                break;
        }
    }

    #region NINO002 - Make Type Public

    private void RegisterNino002Fix(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        var typeDeclaration = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration == null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Make type public",
                createChangedDocument: _ => MakeTypePublicAsync(context.Document, root, typeDeclaration),
                equivalenceKey: ChangeAccessibilityEquivalenceKey),
            context.Diagnostics);
    }

    private Task<Document> MakeTypePublicAsync(Document document, SyntaxNode root,
        TypeDeclarationSyntax typeDeclaration)
    {
        // The type itself becomes public; containing types only need to be public or internal.
        var targets = new List<TypeDeclarationSyntax> { typeDeclaration };
        for (var parent = typeDeclaration.Parent as TypeDeclarationSyntax; parent != null;
             parent = parent.Parent as TypeDeclarationSyntax)
        {
            if (!IsPublicOrInternal(parent.Modifiers))
                targets.Add(parent);
        }

        // The replacement is built from the rewritten node, so a containing type keeps the change to its member.
        var newRoot = root.ReplaceNodes(targets, (_, rewritten) => MakePublic(rewritten));
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    private static bool IsPublicOrInternal(SyntaxTokenList modifiers) =>
        modifiers.Any(SyntaxKind.PublicKeyword) ||
        (modifiers.Any(SyntaxKind.InternalKeyword) && !modifiers.Any(SyntaxKind.PrivateKeyword));

    private static bool IsAccessibilityModifier(SyntaxToken token) =>
        token.IsKind(SyntaxKind.PublicKeyword) || token.IsKind(SyntaxKind.PrivateKeyword) ||
        token.IsKind(SyntaxKind.ProtectedKeyword) || token.IsKind(SyntaxKind.InternalKeyword);

    /// <summary>Replaces the accessibility modifiers (e.g. <c>private protected</c>) with <c>public</c>.</summary>
    private static TypeDeclarationSyntax MakePublic(TypeDeclarationSyntax declaration)
    {
        var modifiers = declaration.Modifiers;
        int first = -1;
        for (int i = modifiers.Count - 1; i >= 0; i--)
        {
            if (!IsAccessibilityModifier(modifiers[i]))
                continue;
            first = i;
            if (i + 1 < modifiers.Count && IsAccessibilityModifier(modifiers[i + 1]))
                modifiers = modifiers.RemoveAt(i + 1);
        }

        var publicToken = SyntaxFactory.Token(SyntaxKind.PublicKeyword);
        if (first >= 0)
            return declaration.WithModifiers(modifiers.Replace(modifiers[first], publicToken.WithTriviaFrom(modifiers[first])));

        // No modifier: move the leading trivia (e.g. indentation) of the first token in front of 'public'.
        if (modifiers.Count > 0)
            return declaration.WithModifiers(modifiers
                .Replace(modifiers[0], modifiers[0].WithLeadingTrivia())
                .Insert(0, publicToken.WithLeadingTrivia(modifiers[0].LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space)));
        var keyword = declaration.Keyword;
        return declaration
            .WithKeyword(keyword.WithLeadingTrivia())
            .WithModifiers(SyntaxFactory.TokenList(
                publicToken.WithLeadingTrivia(keyword.LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space)));
    }

    #endregion

    #region NINO003 - Add [NinoType] Attribute

    private void RegisterNino003Fix(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        var typeDeclaration = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration == null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Add [NinoType] attribute",
                createChangedDocument: _ => AddNinoTypeAttributeAsync(context.Document, root, typeDeclaration),
                equivalenceKey: AddAttributeEquivalenceKey),
            context.Diagnostics);
    }

    private Task<Document> AddNinoTypeAttributeAsync(Document document, SyntaxNode root,
        TypeDeclarationSyntax typeDeclaration)
    {
        // Create [NinoType] attribute
        var ninoTypeAttribute = SyntaxFactory.Attribute(
            SyntaxFactory.IdentifierName("NinoType"));

        var attributeList = SyntaxFactory.AttributeList(
            SyntaxFactory.SingletonSeparatedList(ninoTypeAttribute))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        var newTypeDeclaration = typeDeclaration.AddAttributeLists(attributeList);
        var newRoot = root.ReplaceNode(typeDeclaration, newTypeDeclaration);

        // Check if using directive exists
        var compilationUnit = newRoot as CompilationUnitSyntax;
        if (compilationUnit != null)
        {
            var hasNinoCoreUsing = compilationUnit.Usings.Any(u =>
                u.Name.ToString() == "Nino.Core");

            if (!hasNinoCoreUsing)
            {
                var usingDirective = SyntaxFactory.UsingDirective(
                    SyntaxFactory.IdentifierName("Nino.Core"))
                    .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                newRoot = compilationUnit.AddUsings(usingDirective);
            }
        }

        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    #endregion

    #region NINO004 - Remove Redundant [NinoMember]

    private void RegisterNino004Fix(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove redundant [NinoMember] attribute",
                createChangedDocument: _ => RemoveAttributeAsync(context.Document, root, node, "NinoMemberAttribute"),
                equivalenceKey: RemoveAttributeEquivalenceKey),
            context.Diagnostics);
    }

    #endregion

    #region NINO005 - Remove Redundant [NinoIgnore]

    private void RegisterNino005Fix(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove redundant [NinoIgnore] attribute",
                createChangedDocument: _ => RemoveAttributeAsync(context.Document, root, node, "NinoIgnoreAttribute"),
                equivalenceKey: RemoveAttributeEquivalenceKey),
            context.Diagnostics);
    }

    #endregion

    #region NINO006 - Fix Ambiguous Annotations

    private void RegisterNino006Fix(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        // Offer three options
        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove [NinoMember] attribute",
                createChangedDocument: _ => RemoveAttributeAsync(context.Document, root, node, "NinoMemberAttribute"),
                equivalenceKey: RemoveAttributeEquivalenceKey + "_Member"),
            context.Diagnostics);

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove [NinoIgnore] attribute",
                createChangedDocument: _ => RemoveAttributeAsync(context.Document, root, node, "NinoIgnoreAttribute"),
                equivalenceKey: RemoveAttributeEquivalenceKey + "_Ignore"),
            context.Diagnostics);

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove both attributes",
                createChangedDocument: _ => RemoveBothAttributesAsync(context.Document, root, node),
                equivalenceKey: RemoveAttributeEquivalenceKey + "_Both"),
            context.Diagnostics);
    }

    private Task<Document> RemoveBothAttributesAsync(Document document, SyntaxNode root, SyntaxNode node)
    {
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member == null) return Task.FromResult(document);

        var attributeListsToRemove = new List<AttributeListSyntax>();
        var attributeListsToUpdate = new Dictionary<AttributeListSyntax, AttributeListSyntax>();

        foreach (var attrList in member.AttributeLists)
        {
            var attributesToRemove = attrList.Attributes.Where(attr =>
            {
                var name = attr.Name.ToString();
                return name.EndsWith("NinoMember") || name.EndsWith("NinoMemberAttribute") ||
                       name == "NinoIgnore" || name == "NinoIgnoreAttribute";
            }).ToList();

            if (attributesToRemove.Count > 0)
            {
                if (attributesToRemove.Count == attrList.Attributes.Count)
                {
                    // Remove entire attribute list
                    attributeListsToRemove.Add(attrList);
                }
                else
                {
                    // Remove just specific attributes
                    var newAttrList = attrList;
                    foreach (var attr in attributesToRemove)
                    {
                        newAttrList = newAttrList.RemoveNode(
                            newAttrList.Attributes.First(a => a.IsEquivalentTo(attr)),
                            SyntaxRemoveOptions.KeepNoTrivia)!;
                    }
                    attributeListsToUpdate[attrList] = newAttrList;
                }
            }
        }

        var newMember = member;

        // Apply updates
        if (attributeListsToUpdate.Count > 0)
        {
            newMember = newMember.ReplaceNodes(attributeListsToUpdate.Keys,
                (original, _) => attributeListsToUpdate[original]);
        }

        // Remove attribute lists
        if (attributeListsToRemove.Count > 0)
        {
            // Check if we're removing all attribute lists
            bool removingAllAttributeLists =
                (attributeListsToRemove.Count == member.AttributeLists.Count) &&
                (attributeListsToUpdate.Count == 0);

            // Capture original leading trivia (indentation)
            var originalLeadingTrivia = member.GetLeadingTrivia();

            newMember = newMember.RemoveNodes(
                newMember.AttributeLists.Where(al => attributeListsToRemove.Any(r => r.IsEquivalentTo(al))),
                SyntaxRemoveOptions.KeepNoTrivia)!;

            // Only restore indentation if we're removing all attribute lists
            if (removingAllAttributeLists)
            {
                newMember = newMember.WithLeadingTrivia(originalLeadingTrivia);
            }
        }

        var newRoot = root.ReplaceNode(member, newMember);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    #endregion

    #region NINO007 - Fix Private Member Issue

    private Task RegisterNino007FixAsync(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        // Option 1: Remove [NinoMember]
        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Remove [NinoMember] attribute",
                createChangedDocument: _ => RemoveAttributeAsync(context.Document, root, node, "NinoMemberAttribute"),
                equivalenceKey: RemoveAttributeEquivalenceKey),
            context.Diagnostics);

        // Option 2: Update type to containNonPublicMembers: true
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member != null)
        {
            var typeDeclaration = member.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (typeDeclaration != null)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Update type to allow non-public members",
                        createChangedDocument: _ => UpdateTypeToAllowNonPublicMembersAsync(
                            context.Document, root, typeDeclaration),
                        equivalenceKey: ChangeParameterEquivalenceKey),
                    context.Diagnostics);
            }
        }

        return Task.CompletedTask;
    }

    private Task<Document> UpdateTypeToAllowNonPublicMembersAsync(Document document, SyntaxNode root,
        TypeDeclarationSyntax typeDeclaration)
    {
        var ninoTypeAttr = typeDeclaration.AttributeLists
            .SelectMany(list => list.Attributes)
            .FirstOrDefault(attr =>
            {
                var name = attr.Name.ToString();
                return name == "NinoType" || name == "NinoTypeAttribute";
            });

        if (ninoTypeAttr == null) return Task.FromResult(document);

        AttributeSyntax newAttr;
        if (ninoTypeAttr.ArgumentList == null || ninoTypeAttr.ArgumentList.Arguments.Count == 0)
        {
            // [NinoType] -> [NinoType(containNonPublicMembers: true)]
            newAttr = ninoTypeAttr.WithArgumentList(
                SyntaxFactory.AttributeArgumentList(
                    SyntaxFactory.SeparatedList(new[]
                    {
                        SyntaxFactory.AttributeArgument(
                                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))
                            .WithNameColon(SyntaxFactory.NameColon("containNonPublicMembers"))
                    })));
        }
        else
        {
            var args = ninoTypeAttr.ArgumentList.Arguments;

            // Check if containNonPublicMembers already exists
            var containNonPublicMembersIndex = -1;
            for (int i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (arg.NameColon != null && arg.NameColon.Name.ToString() == "containNonPublicMembers")
                {
                    containNonPublicMembersIndex = i;
                    break;
                }
                else if (arg.NameColon == null && i == 1) // Second positional argument
                {
                    containNonPublicMembersIndex = i;
                    break;
                }
            }

            if (containNonPublicMembersIndex >= 0)
            {
                // Update existing argument
                var oldArg = args[containNonPublicMembersIndex];
                var newArg = SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))
                    .WithNameColon(oldArg.NameColon);
                var newArgs = args.Replace(oldArg, newArg);
                newAttr = ninoTypeAttr.WithArgumentList(SyntaxFactory.AttributeArgumentList(newArgs));
            }
            else
            {
                // Add new argument
                var newArg = SyntaxFactory.AttributeArgument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))
                    .WithNameColon(SyntaxFactory.NameColon("containNonPublicMembers"));
                newAttr = ninoTypeAttr.WithArgumentList(
                    SyntaxFactory.AttributeArgumentList(args.Add(newArg)));
            }
        }

        var newRoot = root.ReplaceNode(ninoTypeAttr, newAttr);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    #endregion

    #region NINO009 - Fix Duplicate Member Index

    private async Task RegisterNino009FixAsync(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (semanticModel == null) return;

        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member == null) return;

        var typeDeclaration = member.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration == null) return;

        // Calculate next available index
        var usedIndices = new HashSet<ushort>();
        var typeSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
        if (typeSymbol == null) return;

        foreach (var m in typeSymbol.GetMembers())
        {
            var ninoMemberAttr = m.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name.EndsWith("NinoMemberAttribute") == true);

            if (ninoMemberAttr != null && ninoMemberAttr.ConstructorArguments.Length > 0)
            {
                var indexArg = ninoMemberAttr.ConstructorArguments[0];
                if (indexArg.Value != null)
                {
                    usedIndices.Add((ushort)indexArg.Value);
                }
            }
        }

        // Find next available index
        ushort nextIndex = 0;
        while (usedIndices.Contains(nextIndex))
        {
            nextIndex++;
        }

        context.RegisterCodeFix(
            CodeAction.Create(
                title: $"Change index to {nextIndex}",
                createChangedDocument: _ => UpdateMemberIndexAsync(context.Document, root, member, nextIndex),
                equivalenceKey: ChangeIndexEquivalenceKey),
            context.Diagnostics);
    }

    private Task<Document> UpdateMemberIndexAsync(Document document, SyntaxNode root,
        MemberDeclarationSyntax member, ushort newIndex)
    {
        var ninoMemberAttr = member.AttributeLists
            .SelectMany(list => list.Attributes)
            .FirstOrDefault(attr =>
            {
                var name = attr.Name.ToString();
                return name.EndsWith("NinoMember") || name.EndsWith("NinoMemberAttribute");
            });

        if (ninoMemberAttr == null || ninoMemberAttr.ArgumentList == null)
            return Task.FromResult(document);

        var newArgument = SyntaxFactory.AttributeArgument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SyntaxFactory.Literal(newIndex)));

        var newArgumentList = SyntaxFactory.AttributeArgumentList(
            SyntaxFactory.SingletonSeparatedList(newArgument));

        var newAttr = ninoMemberAttr.WithArgumentList(newArgumentList);
        var newRoot = root.ReplaceNode(ninoMemberAttr, newAttr);

        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    #endregion

    #region Helper Methods

    private Task<Document> RemoveAttributeAsync(Document document, SyntaxNode root, SyntaxNode node,
        string attributeName)
    {
        var member = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault();
        if (member == null) return Task.FromResult(document);

        // Normalize attribute name (remove "Attribute" suffix if present)
        var targetName1 = attributeName.Replace("Attribute", "");
        var targetName2 = attributeName;

        var attributeToRemove = member.AttributeLists
            .SelectMany(list => list.Attributes)
            .FirstOrDefault(attr =>
            {
                var name = attr.Name.ToString();
                // Match exact names or names that end with the target
                // This handles both "NinoMember" and "NinoMemberAttribute" variations
                return name == targetName1 || name == targetName2 ||
                       name.EndsWith("." + targetName1) || name.EndsWith("." + targetName2);
            });

        if (attributeToRemove == null) return Task.FromResult(document);

        var attributeList = attributeToRemove.Parent as AttributeListSyntax;
        if (attributeList == null) return Task.FromResult(document);

        MemberDeclarationSyntax newMember;
        if (attributeList.Attributes.Count == 1)
        {
            // Check if this is the only attribute list on the member
            bool isOnlyAttributeList = member.AttributeLists.Count == 1;

            // Capture original leading trivia (indentation)
            var originalLeadingTrivia = member.GetLeadingTrivia();

            // Remove entire attribute list
            newMember = member.RemoveNode(attributeList, SyntaxRemoveOptions.KeepNoTrivia)!;

            // Only restore indentation if we're removing the last attribute list
            if (isOnlyAttributeList)
            {
                newMember = newMember.WithLeadingTrivia(originalLeadingTrivia);
            }
        }
        else
        {
            // Remove just the attribute from the list
            var newAttributeList = attributeList.RemoveNode(attributeToRemove, SyntaxRemoveOptions.KeepNoTrivia);
            newMember = member.ReplaceNode(attributeList, newAttributeList!);
        }

        var newRoot = root.ReplaceNode(member, newMember);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
    }

    #endregion
}
