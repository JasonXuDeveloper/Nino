using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
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
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using Nino.Generator.Common;
using Nino.Generator.Analysis;

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
        public Fix(string title, string key, Edit apply, Func<SyntaxNode, bool>? applies = null)
            : this(title, key, _ => apply, applies)
        {
        }

        /// <summary>A fix whose edit depends on the diagnostic (its properties).</summary>
        public Fix(string title, string key, Func<Diagnostic, Edit> apply, Func<SyntaxNode, bool>? applies = null) =>
            (Title, Key, Apply, Applies) = (title, key, apply, applies);

        public string Title { get; }
        public string Key { get; }
        public Func<Diagnostic, Edit> Apply { get; }
        public Func<SyntaxNode, bool>? Applies { get; }
    }

    private const string NinoMember = NinoNames.NinoMember, NinoIgnore = NinoNames.NinoIgnore,
        NinoType = NinoNames.NinoType;

    private static readonly Dictionary<string, Fix[]> Fixes = new()
    {
        [NinoDiagnostics.InaccessibleType.Id] = new[]
        {
            // A static class stays unserializable whatever its accessibility.
            new Fix("Make type public", "MakePublic", MakeAccessible,
                n => !Type(n).Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))),
        },
        [NinoDiagnostics.NotPartial.Id] = new[] { new Fix("Make type partial", "MakePartial", MakePartial) },
        [NinoDiagnostics.SubTypeWithoutNinoType.Id] = new[] { new Fix("Add [NinoType]", "AddNinoType", (e, n) => AddAttribute(e, Type(n), NinoType)) },
        [NinoDiagnostics.ReadOnlyMember.Id] = new[] { AddIgnoreFix() },
        [NinoDiagnostics.UnserializableMember.Id] = new[] { AddIgnoreFix() },
        [NinoDiagnostics.PrivateMember.Id] = new[]
        {
            RemoveAttributeFix(NinoMember),
            new Fix("Update type to allow non-public members", "AllowNonPublic",
                (e, n) => SetTypeArgument(e, n, "containNonPublicMembers", true)),
        },
        [NinoDiagnostics.RedundantMember.Id] = new[] { RemoveAttributeFix(NinoMember) },
        [NinoDiagnostics.RedundantIgnore.Id] = new[] { RemoveAttributeFix(NinoIgnore) },
        [NinoDiagnostics.AmbiguousMember.Id] = new[]
        {
            RemoveAttributeFix(NinoMember), RemoveAttributeFix(NinoIgnore),
            new Fix("Remove both attributes", "RemoveBoth", (e, n) =>
            {
                RemoveAttribute(e, n, NinoMember);
                RemoveAttribute(e, n, NinoIgnore);
            }),
        },
        [NinoDiagnostics.DuplicateIndex.Id] = new[] { new Fix("Use the next free index", "NextIndex", NextFreeIndex) },
        [NinoDiagnostics.TrackReferencesOnStruct.Id] = new[]
        {
            new Fix("Remove trackReferences", "RemoveTrackReferences",
                (e, n) => SetTypeArgument(e, n, "trackReferences", false)),
        },
        [NinoDiagnostics.InvalidPooledType.Id] = new[]
        {
            new Fix("Make class sealed", "MakeSealed",
                (e, n) => e.SetModifiers(Type(n), e.Generator.GetModifiers(Type(n)).WithIsSealed(true)),
                n => Type(n) is ClassDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.RawKind: 0 } &&
                     !Type(n).Modifiers.Any(m => m.IsKind(SyntaxKind.AbstractKeyword) || m.IsKind(SyntaxKind.StaticKeyword) ||
                                                 m.IsKind(SyntaxKind.SealedKeyword))),
        },
        [NinoDiagnostics.PoolHooksNotImplemented.Id] = new[] { new Fix("Implement pool hooks", "ImplementPoolHooks", ImplementPoolHooks) },
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
                    ct => ApplyAsync(context.Document, span, fix.Apply(diagnostic), ct), diagnostic.Id + fix.Key),
                    diagnostic);
            }
        }
    }

    private static async Task<Document> ApplyAsync(Document document, TextSpan span, Edit apply, CancellationToken ct)
    {
        var editor = await DocumentEditor.CreateAsync(document, ct).ConfigureAwait(false);
        apply(editor, editor.OriginalRoot.FindNode(span, getInnermostNodeForTie: true));
        return await FormatAsync(editor.GetChangedDocument(), await document.GetTextAsync(ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Formats what the fix inserted with the line breaks of <paramref name="original"/>, then drops the formatting
    /// annotations and makes the elastic trivia the syntax generator adds (around a using directive or an attribute)
    /// concrete: left to the code action's cleanup, they would take the machine's line break (Environment.NewLine),
    /// and a fix on Windows would add CRLF lines to an LF file.
    /// </summary>
    private static async Task<Document> FormatAsync(Document document, SourceText original, CancellationToken ct)
    {
        if (NewLine(original) is not { } newLine)
            return document;
        var options = (await document.GetOptionsAsync(ct).ConfigureAwait(false))
            .WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, newLine);
        document = await Formatter.FormatAsync(document, Formatter.Annotation, options, ct).ConfigureAwait(false);
        document = await Formatter.FormatAsync(document, SyntaxAnnotation.ElasticAnnotation, options, ct)
            .ConfigureAwait(false);
        var root = (await document.GetSyntaxRootAsync(ct).ConfigureAwait(false))!;
        root = root.ReplaceNodes(root.GetAnnotatedNodes(Formatter.Annotation),
            static (_, node) => node.WithoutAnnotations(Formatter.Annotation));
        root = root.ReplaceTrivia(root.DescendantTrivia(descendIntoTrivia: true).Where(static t => t.HasAnnotation(SyntaxAnnotation.ElasticAnnotation)),
            (trivia, _) => trivia.IsKind(SyntaxKind.EndOfLineTrivia)
                ? SyntaxFactory.EndOfLine(newLine)
                : trivia.IsKind(SyntaxKind.WhitespaceTrivia)
                    ? SyntaxFactory.Whitespace(trivia.ToString())
                    : trivia.WithoutAnnotations(SyntaxAnnotation.ElasticAnnotation));
        return document.WithSyntaxRoot(root);
    }

    /// <summary>The line break of the first line of <paramref name="text"/>, or null for a single line.</summary>
    private static string? NewLine(SourceText text) => text.Lines.Count < 2
        ? null
        : text.ToString(TextSpan.FromBounds(text.Lines[0].End, text.Lines[0].EndIncludingLineBreak));

    private static Fix RemoveAttributeFix(string name) =>
        new($"Remove [{name}]", "Remove" + name, (e, n) => RemoveAttribute(e, n, name));

    private static Fix AddIgnoreFix() =>
        new("Add [NinoIgnore]", "AddNinoIgnore", (e, n) => AddAttribute(e, Member(n), NinoIgnore));

    private static MemberDeclarationSyntax Member(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().First();

    private static TypeDeclarationSyntax Type(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().First();

    /// <summary>
    /// Whether <paramref name="attribute"/> is Nino.Core's <c>[name]</c>: resolved through the semantic model, so
    /// aliases (<c>using NM = Nino.Core.NinoMemberAttribute;</c>) and qualified names match too.
    /// </summary>
    private static bool IsAttribute(SemanticModel model, AttributeSyntax attribute, string name) =>
        model.GetSymbolInfo(attribute).Symbol is IMethodSymbol { ContainingType: { } type } &&
        SymbolEqualityComparer.Default.Equals(type, AttributeType(model, name));

    /// <summary>Nino.Core's <c>[name]</c> attribute type in the document's compilation.</summary>
    private static INamedTypeSymbol? AttributeType(SemanticModel model, string name) =>
        model.Compilation.GetTypeByMetadataName(NinoNames.Core(name + "Attribute"));

    private static AttributeSyntax? FindAttribute(DocumentEditor editor, MemberDeclarationSyntax member, string name) =>
        member.AttributeLists.SelectMany(l => l.Attributes)
            .FirstOrDefault(a => IsAttribute(editor.SemanticModel, a, name));

    private static void RemoveAttribute(DocumentEditor editor, SyntaxNode node, string name)
    {
        var member = Member(node);
        if (FindAttribute(editor, member, name) is not { Parent: AttributeListSyntax list } attribute)
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
            var currentList = m.AttributeLists.First(l => l.Attributes.Any(a => a.IsEquivalentTo(attribute)));
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
            .Any(u => u.Name.ToString() == NinoNames.CoreNamespace);
        if (!imported)
        {
            editor.ReplaceNode(editor.OriginalRoot, (current, generator) =>
                generator.AddNamespaceImports(current, generator.NamespaceImportDeclaration(NinoNames.CoreNamespace)));
        }
    }

    /// <summary>Adds empty bodies for the pool hooks the type does not implement yet.</summary>
    private static void ImplementPoolHooks(DocumentEditor editor, SyntaxNode node)
    {
        var type = Type(node);
        if (editor.SemanticModel.GetDeclaredSymbol(type) is not { } symbol)
            return;
        string self = type.Identifier.ValueText + TypeArguments(type);
        string newLine = NewLine(editor.OriginalRoot.GetText()) ?? "\n";
        foreach (var name in PoolHooks.Missing(symbol, CancellationToken.None))
        {
            string declaration = name == PoolHooks.Warmup
                ? $"private static partial System.Collections.Generic.IEnumerable<{self}> {name}()\n{{\n    return System.Array.Empty<{self}>();\n}}\n"
                : $"private partial void {name}()\n{{\n}}\n";
            if (SyntaxFactory.ParseMemberDeclaration(declaration.Replace("\n", newLine)) is { } member)
                editor.AddMember(type, member.WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation));
        }
    }

    private static string TypeArguments(TypeDeclarationSyntax type) => type.TypeParameterList is { } list
        ? "<" + string.Join(", ", list.Parameters.Select(p => p.Identifier.ValueText)) + ">"
        : "";

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

    /// <summary>
    /// Sets a <c>[NinoType]</c> argument to true, or removes it (false is its default). A positional argument is
    /// matched by the position of the constructor parameter of that name.
    /// </summary>
    private static void SetTypeArgument(DocumentEditor editor, SyntaxNode node, string name, bool value)
    {
        if (FindAttribute(editor, Type(node), NinoType) is not { } attribute ||
            editor.SemanticModel.GetSymbolInfo(attribute).Symbol is not IMethodSymbol constructor)
            return;
        int position = -1;
        for (int i = 0; i < constructor.Parameters.Length; i++)
        {
            if (constructor.Parameters[i].Name == name)
                position = i;
        }

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

    /// <summary>
    /// Gives the member a free index: the n-th free one for the n-th duplicate the analyzer reported in its type
    /// (<see cref="NinoDiagnostics.DuplicateRank"/>), so fixing them all at once (each fix sees the original document)
    /// assigns distinct indices.
    /// </summary>
    private static Edit NextFreeIndex(Diagnostic diagnostic)
    {
        int rank = diagnostic.Properties.TryGetValue(NinoDiagnostics.DuplicateRank, out var value) &&
                   int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int r)
            ? r
            : 0;
        return (editor, node) => UseFreeIndex(editor, node, rank);
    }

    private static void UseFreeIndex(DocumentEditor editor, SyntaxNode node, int rank)
    {
        var member = Member(node);
        if (FindAttribute(editor, member, NinoMember) is not { ArgumentList.Arguments.Count: > 0 } attribute ||
            editor.SemanticModel.GetDeclaredSymbol(Type(member)) is not { } type)
            return;

        var ninoMember = AttributeType(editor.SemanticModel, NinoMember);
        var used = new HashSet<ushort>();
        foreach (var m in type.GetMembers())
        {
            var index = m.GetAttributes()
                .Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, ninoMember) &&
                            a.ConstructorArguments.Length > 0)
                .Select(a => a.ConstructorArguments[0].Value as ushort?)
                .FirstOrDefault();
            if (index is { } i)
                used.Add(i);
        }

        ushort next = 0;
        for (int free = -1; ; next++)
        {
            if (!used.Contains(next) && ++free == rank)
                break;
        }

        var expression = attribute.ArgumentList!.Arguments[0].Expression;
        editor.ReplaceNode(expression, SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
            SyntaxFactory.Literal(next)).WithTriviaFrom(expression));
    }
}
