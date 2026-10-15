using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace Nino.Generator;

/// <summary>
/// Fixes for <see cref="NinoDiagnostics"/>. Each diagnostic id maps to its fixes; a fix edits the declaration the
/// diagnostic points at through a <see cref="DocumentEditor"/>, and formatting is left to the code action cleanup.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NinoCodeFixProvider)), Shared]
public sealed class NinoCodeFixProvider : CodeFixProvider
{
    private delegate void Edit(DocumentEditor editor, SyntaxNode node);

    private sealed class Fix
    {
        public Fix(string title, string key, Edit apply, Func<SyntaxNode, bool>? applies = null) =>
            (Title, Key, Apply, Applies) = (title, key, apply, applies);

        public string Title { get; }
        public string Key { get; }
        public Edit Apply { get; }
        public Func<SyntaxNode, bool>? Applies { get; }
    }

    private const string NinoMember = "NinoMember", NinoIgnore = "NinoIgnore", NinoType = "NinoType";

    private static readonly Dictionary<string, Fix[]> Fixes = new()
    {
        ["NINO002"] = new[] { new Fix("Make type public", "MakePublic", MakeAccessible) },
        ["NINO003"] = new[] { new Fix("Add [NinoType]", "AddNinoType", (e, n) => AddAttribute(e, Type(n), NinoType)) },
        ["NINO004"] = new[] { RemoveAttributeFix(NinoMember) },
        ["NINO005"] = new[] { RemoveAttributeFix(NinoIgnore) },
        ["NINO006"] = new[]
        {
            RemoveAttributeFix(NinoMember), RemoveAttributeFix(NinoIgnore),
            new Fix("Remove both attributes", "RemoveBoth", (e, n) =>
            {
                RemoveAttribute(e, n, NinoMember);
                RemoveAttribute(e, n, NinoIgnore);
            }),
        },
        ["NINO007"] = new[]
        {
            RemoveAttributeFix(NinoMember),
            new Fix("Update type to allow non-public members", "AllowNonPublic",
                (e, n) => SetTypeArgument(e, n, "containNonPublicMembers", 1, true)),
        },
        ["NINO009"] = new[] { new Fix("Use the next free index", "NextIndex", UseNextFreeIndex) },
        ["NINO011"] = new[] { AddIgnoreFix() },
        ["NINO013"] = new[] { new Fix("Make type partial", "MakePartial", MakePartial) },
        ["NINO014"] = new[] { AddIgnoreFix() },
        ["NINO016"] = new[]
        {
            // Only the struct variant: classes that cannot be tracked need a different design, not an edit.
            new Fix("Remove trackReferences", "RemoveTrackReferences",
                (e, n) => SetTypeArgument(e, n, "trackReferences", 5, false),
                n => Type(n) is StructDeclarationSyntax ||
                     Type(n) is RecordDeclarationSyntax r && r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)),
        },
    };

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = Fixes.Keys.ToImmutableArray();

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (!Fixes.TryGetValue(diagnostic.Id, out var fixes))
                continue;
            var span = diagnostic.Location.SourceSpan;
            var node = root.FindNode(span, getInnermostNodeForTie: true);
            foreach (var fix in fixes)
            {
                if (fix.Applies != null && !fix.Applies(node))
                    continue;
                context.RegisterCodeFix(CodeAction.Create(fix.Title,
                    ct => ApplyAsync(context.Document, span, fix, ct), diagnostic.Id + fix.Key), diagnostic);
            }
        }
    }

    private static async Task<Document> ApplyAsync(Document document, TextSpan span, Fix fix, CancellationToken ct)
    {
        var editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);
        fix.Apply(editor, editor.OriginalRoot.FindNode(span, getInnermostNodeForTie: true));
        return editor.GetChangedDocument();
    }

    private static Fix RemoveAttributeFix(string name) =>
        new($"Remove [{name}]", "Remove" + name, (e, n) => RemoveAttribute(e, n, name));

    private static Fix AddIgnoreFix() =>
        new("Add [NinoIgnore]", "AddNinoIgnore", (e, n) => AddAttribute(e, Member(n), NinoIgnore));

    private static MemberDeclarationSyntax Member(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().First();

    private static TypeDeclarationSyntax Type(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().First();

    private static bool IsAttribute(AttributeSyntax attribute, string name)
    {
        var text = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => "",
        };
        return text == name || text == name + "Attribute";
    }

    private static AttributeSyntax? FindAttribute(MemberDeclarationSyntax member, string name) =>
        member.AttributeLists.SelectMany(l => l.Attributes).FirstOrDefault(a => IsAttribute(a, name));

    private static void RemoveAttribute(DocumentEditor editor, SyntaxNode node, string name)
    {
        var member = Member(node);
        if (FindAttribute(member, name) is not { Parent: AttributeListSyntax list } attribute)
            return;
        if (list.Attributes.Count > 1)
        {
            editor.RemoveNode(attribute);
            return;
        }

        // The whole list goes; the declaration keeps the indentation the list carried.
        editor.ReplaceNode(member, (current, _) =>
        {
            var m = (MemberDeclarationSyntax)current;
            var currentList = m.AttributeLists.First(l => l.Attributes.Any(a => IsAttribute(a, name)));
            var leading = m.AttributeLists[0] == currentList ? currentList.GetLeadingTrivia() : default;
            var removed = m.RemoveNode(currentList, SyntaxRemoveOptions.KeepNoTrivia)!;
            return leading.Count == 0 ? removed : removed.WithLeadingTrivia(leading);
        });
    }

    private static void AddAttribute(DocumentEditor editor, MemberDeclarationSyntax member, string name)
    {
        editor.ReplaceNode(member, (current, generator) =>
            generator.AddAttributes(current, generator.Attribute(name)).WithAdditionalAnnotations(Formatter.Annotation));
        bool imported = member.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
            .SelectMany(ns => ns.Usings)
            .Concat((editor.OriginalRoot as CompilationUnitSyntax)?.Usings ?? default)
            .Any(u => u.Name.ToString() == "Nino.Core");
        if (!imported)
        {
            editor.ReplaceNode(editor.OriginalRoot, (current, generator) =>
                generator.AddNamespaceImports(current, generator.NamespaceImportDeclaration("Nino.Core")));
        }
    }

    /// <summary>The type becomes public; containing types only need to be public or internal.</summary>
    private static void MakeAccessible(DocumentEditor editor, SyntaxNode node)
    {
        var type = Type(node);
        editor.SetAccessibility(type, Accessibility.Public);
        foreach (var parent in type.Ancestors().OfType<TypeDeclarationSyntax>())
        {
            if (editor.Generator.GetAccessibility(parent) is not (Accessibility.Public or Accessibility.Internal))
                editor.SetAccessibility(parent, Accessibility.Public);
        }
    }

    /// <summary>The type and every type containing it become partial.</summary>
    private static void MakePartial(DocumentEditor editor, SyntaxNode node)
    {
        foreach (var type in node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>())
        {
            if (!type.Modifiers.Any(SyntaxKind.PartialKeyword))
                editor.SetModifiers(type, editor.Generator.GetModifiers(type).WithPartial(true));
        }
    }

    /// <summary>Sets a <c>[NinoType]</c> argument to true, or removes it (false is its default).</summary>
    private static void SetTypeArgument(DocumentEditor editor, SyntaxNode node, string name, int position, bool value)
    {
        if (FindAttribute(Type(node), NinoType) is not { } attribute)
            return;
        var arguments = attribute.ArgumentList?.Arguments ?? default;
        int index = -1;
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument.NameColon?.Name.Identifier.ValueText == name ||
                (argument.NameColon == null && argument.NameEquals == null && i == position))
                index = i;
        }

        var literal = SyntaxFactory.LiteralExpression(value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression);
        SeparatedSyntaxList<AttributeArgumentSyntax> updated;
        if (index >= 0)
        {
            // A positional argument can only be dropped when it is the last one.
            updated = !value && (arguments[index].NameColon != null || index == arguments.Count - 1)
                ? arguments.RemoveAt(index)
                : arguments.Replace(arguments[index], arguments[index].WithExpression(literal));
        }
        else if (value)
        {
            updated = arguments.Add(SyntaxFactory.AttributeArgument(literal).WithNameColon(SyntaxFactory.NameColon(name)));
        }
        else
        {
            return;
        }

        editor.ReplaceNode(attribute, attribute.WithArgumentList(
            updated.Count == 0 ? null : SyntaxFactory.AttributeArgumentList(updated)));
    }

    private static void UseNextFreeIndex(DocumentEditor editor, SyntaxNode node)
    {
        var member = Member(node);
        if (FindAttribute(member, NinoMember) is not { ArgumentList.Arguments.Count: > 0 } attribute ||
            editor.SemanticModel.GetDeclaredSymbol(Type(member)) is not { } type)
            return;
        var used = new HashSet<ushort>(type.GetMembers()
            .SelectMany(m => m.GetAttributes())
            .Where(a => a.AttributeClass?.Name == "NinoMemberAttribute" && a.ConstructorArguments.Length > 0)
            .Select(a => a.ConstructorArguments[0].Value is ushort index ? index : (ushort)0));
        ushort next = 0;
        while (used.Contains(next))
            next++;
        var expression = attribute.ArgumentList!.Arguments[0].Expression;
        editor.ReplaceNode(expression, SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(next)).WithTriviaFrom(expression));
    }
}
