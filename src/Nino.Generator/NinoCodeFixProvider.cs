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
        "NINO002", "NINO003", "NINO004", "NINO005", "NINO006", "NINO007", "NINO008", "NINO009"
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

            case "NINO008": // Nested type with non-public members
                RegisterNino008Fix(context, root, node);
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
        TypeDeclarationSyntax newTypeDeclaration;

        // Check if type has any accessibility modifier
        var hasAccessibilityModifier = typeDeclaration.Modifiers.Any(m =>
            m.IsKind(SyntaxKind.PublicKeyword) ||
            m.IsKind(SyntaxKind.PrivateKeyword) ||
            m.IsKind(SyntaxKind.ProtectedKeyword) ||
            m.IsKind(SyntaxKind.InternalKeyword));

        if (hasAccessibilityModifier)
        {
            // Replace existing accessibility modifier with public
            var oldModifiers = typeDeclaration.Modifiers;
            var newModifiers = SyntaxFactory.TokenList();

            foreach (var modifier in oldModifiers)
            {
                if (modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                    modifier.IsKind(SyntaxKind.ProtectedKeyword) ||
                    modifier.IsKind(SyntaxKind.InternalKeyword))
                {
                    newModifiers = newModifiers.Add(SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                        .WithTriviaFrom(modifier));
                }
                else
                {
                    newModifiers = newModifiers.Add(modifier);
                }
            }

            newTypeDeclaration = typeDeclaration.WithModifiers(newModifiers);
        }
        else
        {
            // Add public modifier
            var publicToken = SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space);
            newTypeDeclaration = typeDeclaration.WithModifiers(
                typeDeclaration.Modifiers.Insert(0, publicToken));
        }

        // Handle nested types - make parent types public too
        var currentParent = typeDeclaration.Parent as TypeDeclarationSyntax;
        var nodesToReplace = new Dictionary<SyntaxNode, SyntaxNode> { { typeDeclaration, newTypeDeclaration } };

        while (currentParent != null)
        {
            var parentModifiers = currentParent.Modifiers;
            if (!parentModifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                var newParentModifiers = SyntaxFactory.TokenList();
                bool replaced = false;

                foreach (var modifier in parentModifiers)
                {
                    if (modifier.IsKind(SyntaxKind.PrivateKeyword) ||
                        modifier.IsKind(SyntaxKind.ProtectedKeyword) ||
                        modifier.IsKind(SyntaxKind.InternalKeyword))
                    {
                        newParentModifiers = newParentModifiers.Add(
                            SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTriviaFrom(modifier));
                        replaced = true;
                    }
                    else
                    {
                        newParentModifiers = newParentModifiers.Add(modifier);
                    }
                }

                if (!replaced)
                {
                    var publicToken = SyntaxFactory.Token(SyntaxKind.PublicKeyword)
                        .WithTrailingTrivia(SyntaxFactory.Space);
                    newParentModifiers = parentModifiers.Insert(0, publicToken);
                }

                nodesToReplace[currentParent] = currentParent.WithModifiers(newParentModifiers);
            }

            currentParent = currentParent.Parent as TypeDeclarationSyntax;
        }

        var newRoot = root.ReplaceNodes(nodesToReplace.Keys, (original, _) => nodesToReplace[original]);
        return Task.FromResult(document.WithSyntaxRoot(newRoot));
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

    #region NINO008 - Fix Nested Type with Non-Public Members

    private void RegisterNino008Fix(CodeFixContext context, SyntaxNode root, SyntaxNode node)
    {
        var typeDeclaration = node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (typeDeclaration == null) return;

        context.RegisterCodeFix(
            CodeAction.Create(
                title: "Set containNonPublicMembers to false",
                createChangedDocument: _ => UpdateContainNonPublicMembersToFalseAsync(
                    context.Document, root, typeDeclaration),
                equivalenceKey: ChangeParameterEquivalenceKey),
            context.Diagnostics);
    }

    private Task<Document> UpdateContainNonPublicMembersToFalseAsync(Document document, SyntaxNode root,
        TypeDeclarationSyntax typeDeclaration)
    {
        var ninoTypeAttr = typeDeclaration.AttributeLists
            .SelectMany(list => list.Attributes)
            .FirstOrDefault(attr =>
            {
                var name = attr.Name.ToString();
                return name == "NinoType" || name == "NinoTypeAttribute";
            });

        if (ninoTypeAttr == null || ninoTypeAttr.ArgumentList == null)
            return Task.FromResult(document);

        var args = ninoTypeAttr.ArgumentList.Arguments;

        // Find the containNonPublicMembers argument (either named or positional)
        var containNonPublicMembersArgIndex = -1;
        for (int i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.NameColon != null && arg.NameColon.Name.ToString() == "containNonPublicMembers")
            {
                containNonPublicMembersArgIndex = i;
                break;
            }
            else if (arg.NameColon == null && i == 1) // Second positional argument
            {
                containNonPublicMembersArgIndex = i;
                break;
            }
        }

        if (containNonPublicMembersArgIndex == -1) return Task.FromResult(document);

        // Create new argument with false value
        var oldArg = args[containNonPublicMembersArgIndex];
        var newArg = SyntaxFactory.AttributeArgument(
                SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))
            .WithNameColon(oldArg.NameColon); // Preserve the name if it exists

        // Replace the argument at the found index
        var newArgs = args.Replace(args[containNonPublicMembersArgIndex], newArg);

        var newAttr = ninoTypeAttr.WithArgumentList(
            SyntaxFactory.AttributeArgumentList(newArgs));

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
