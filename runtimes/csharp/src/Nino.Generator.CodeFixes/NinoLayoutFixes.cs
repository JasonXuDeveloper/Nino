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
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using Nino.Generator.Analysis;
using Nino.Generator.Common;
using Nino.Generator.Generation;

namespace Nino.Generator;

/// <summary>
/// Fixes for the layout baseline's findings (NINO034-NINO036): accept the change (rewrite the type's section of the
/// baseline, every finding's at once with Fix All), or undo it the way the finding names (<see cref="LayoutFix"/>:
/// add <c>[NinoFormerName]</c>, restore or remove <c>sealed</c>, move an inserted member to the end).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(NinoLayoutFixProvider)), Shared]
public sealed class NinoLayoutFixProvider : CodeFixProvider
{
    private const string AcceptKey = "NinoLayoutAccept";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(
        NinoDiagnostics.LayoutBreakingChange.Id, NinoDiagnostics.LayoutSilentLoss.Id, NinoDiagnostics.LayoutChangeNote.Id);

    public override FixAllProvider GetFixAllProvider() => FixAll.Instance;

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(NinoDiagnostics.LayoutKey, out _) ||
                diagnostic.AdditionalLocations.Count == 0)
                continue;
            var document = context.Document;
            var span = diagnostic.Location.SourceSpan;
            string? argument = diagnostic.Properties.TryGetValue(NinoDiagnostics.LayoutArgument, out var a) ? a : null;
            var fix = diagnostic.Properties.TryGetValue(NinoDiagnostics.LayoutFixKind, out var kind) &&
                      Enum.TryParse(kind, out LayoutFix parsed)
                ? parsed
                : LayoutFix.Accept;
            NinoCodeFixProvider.Edit? edit = fix switch
            {
                LayoutFix.FormerName when argument != null => (e, n) =>
                    NinoCodeFixProvider.AddAttribute(e, Declaration(n), $"NinoFormerName(\"{argument}\")"),
                LayoutFix.AddSealed => (e, n) => e.SetModifiers(NinoCodeFixProvider.Type(n),
                    e.Generator.GetModifiers(NinoCodeFixProvider.Type(n)).WithIsSealed(true)),
                LayoutFix.RemoveSealed => (e, n) => e.SetModifiers(NinoCodeFixProvider.Type(n),
                    e.Generator.GetModifiers(NinoCodeFixProvider.Type(n)).WithIsSealed(false)),
                LayoutFix.MoveToEnd => MoveToEnd,
                _ => null,
            };
            string? title = fix switch
            {
                LayoutFix.FormerName => $"Add [NinoFormerName(\"{argument}\")]",
                LayoutFix.AddSealed => "Make the class sealed again",
                LayoutFix.RemoveSealed => "Remove sealed",
                LayoutFix.MoveToEnd => "Move the member to the end of the type",
                _ => null,
            };
            if (edit != null && title != null)
                context.RegisterCodeFix(CodeAction.Create(title,
                    ct => NinoCodeFixProvider.ApplyAsync(document, span, edit, ct), "NinoLayout" + fix), diagnostic);
            context.RegisterCodeFix(CodeAction.Create("Accept the change into the layout baseline",
                ct => AcceptAsync(document.Project.Solution, new[] { diagnostic }, ct), AcceptKey), diagnostic);
        }

        return Task.CompletedTask;
    }

    /// <summary>The member a finding names, or the type when it names none.</summary>
    private static MemberDeclarationSyntax Declaration(SyntaxNode node) =>
        node.AncestorsAndSelf().FirstOrDefault(n => n is VariableDeclaratorSyntax or PropertyDeclarationSyntax
            or ParameterSyntax or BaseTypeDeclarationSyntax) switch
        {
            VariableDeclaratorSyntax or PropertyDeclarationSyntax => NinoCodeFixProvider.Member(node),
            _ => NinoCodeFixProvider.Type(node),
        };

    /// <summary>
    /// Moves the member's declaration after the last field or property its type declares, where a version-tolerant
    /// type appends it. Offered by the analyzer only for a member the type declares itself.
    /// </summary>
    private static void MoveToEnd(DocumentEditor editor, SyntaxNode node)
    {
        var member = NinoCodeFixProvider.Member(node);
        if (member.Parent is not TypeDeclarationSyntax type)
            return;
        var last = type.Members.LastOrDefault(m => m is FieldDeclarationSyntax or PropertyDeclarationSyntax);
        if (last == null || last == member)
            return;
        editor.RemoveNode(member, SyntaxRemoveOptions.KeepNoTrivia);
        editor.InsertAfter(last, member.WithLeadingTrivia(last.GetLeadingTrivia()));
    }

    /// <summary>
    /// The solution with the baseline of each diagnostic (its additional location) rewritten: each type's section
    /// replaced by the one the diagnostic carries (none: removed), a section about the same type under its old name
    /// removed.
    /// </summary>
    private static async Task<Solution> AcceptAsync(Solution solution, IEnumerable<Diagnostic> diagnostics,
        CancellationToken ct)
    {
        foreach (var group in diagnostics.Where(d => d.AdditionalLocations.Count != 0)
                     .GroupBy(d => d.AdditionalLocations[0].SourceTree))
        {
            if (group.Key == null || solution.GetDocument(group.Key) is not { } document ||
                await document.GetSyntaxRootAsync(ct).ConfigureAwait(false) is not { } root ||
                LayoutBaselineSyntax.Literal(root, group.First().AdditionalLocations[0].SourceSpan) is not { } literal)
                continue;
            var changes = new List<KeyValuePair<string, string?>>();
            foreach (var diagnostic in group)
            {
                var properties = diagnostic.Properties;
                if (properties.TryGetValue(NinoDiagnostics.LayoutOldKey, out var oldKey) && oldKey != null)
                    changes.Add(new KeyValuePair<string, string?>(oldKey, null));
                if (properties.TryGetValue(NinoDiagnostics.LayoutKey, out var key) && key != null)
                    changes.Add(new KeyValuePair<string, string?>(key,
                        properties.TryGetValue(NinoDiagnostics.LayoutSection, out var section) ? section : null));
            }

            string text = LayoutBaseline.Update(literal.Token.ValueText, changes);
            solution = solution.WithDocumentSyntaxRoot(document.Id,
                LayoutBaselineSyntax.WithText(root, literal, text));
        }

        return solution;
    }

    /// <summary>Fix All: accepting rewrites each baseline once with every finding's section; other fixes in batches.</summary>
    private sealed class FixAll : FixAllProvider
    {
        public static readonly FixAll Instance = new();

        public override async Task<CodeAction?> GetFixAsync(FixAllContext context)
        {
            if (context.CodeActionEquivalenceKey != AcceptKey)
                return await WellKnownFixAllProviders.BatchFixer.GetFixAsync(context).ConfigureAwait(false);
            var diagnostics = new List<Diagnostic>();
            var documents = context.Scope switch
            {
                FixAllScope.Document when context.Document != null => new[] { context.Document },
                FixAllScope.Solution => context.Solution.Projects.SelectMany(p => p.Documents),
                _ => context.Project.Documents,
            };
            foreach (var document in documents)
                diagnostics.AddRange(await context.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false));
            if (context.Scope != FixAllScope.Document)
                diagnostics.AddRange(await context.GetProjectDiagnosticsAsync(context.Project).ConfigureAwait(false));
            return CodeAction.Create("Accept every change into the layout baseline",
                ct => AcceptAsync(context.Solution, diagnostics, ct), AcceptKey);
        }
    }
}

/// <summary>
/// Offers to create the layout baseline of the project (<c>NinoLayouts.cs</c>) on a <c>[NinoType]</c> declaration, or
/// to write the one it has again: from the layouts the generator published (<c>[assembly: NinoTypeLayout]</c>), of
/// the types <see cref="LayoutRules.Scope"/> keeps.
/// </summary>
[ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(NinoLayoutsRefactoringProvider)), Shared]
public sealed class NinoLayoutsRefactoringProvider : CodeRefactoringProvider
{
    public const string FileName = "NinoLayouts.cs";

    public override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
    {
        var document = context.Document;
        var ct = context.CancellationToken;
        if (await document.GetSyntaxRootAsync(ct).ConfigureAwait(false) is not { } root ||
            root.FindNode(context.Span).AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() is not { } type ||
            // On the declaration itself (attributes, modifiers, name), not anywhere in its body.
            context.Span.Start > type.Identifier.Span.End ||
            !type.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name.ToString().Contains(NinoNames.NinoType)) ||
            await document.Project.GetCompilationAsync(ct).ConfigureAwait(false) is not { } compilation)
            return;
        var entries = Layouts(compilation);
        if (entries.Count == 0)
            return;
        var existing = compilation.Assembly.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name == NinoNames.NinoLayoutsAttribute &&
            a.AttributeClass.ContainingNamespace.ToDisplayString() == NinoNames.CoreNamespace);
        if (existing?.ApplicationSyntaxReference is { } syntax)
        {
            context.RegisterRefactoring(CodeAction.Create("Write the layout baseline again (accept every change)",
                c => RewriteAsync(document.Project.Solution, syntax, entries, c), "NinoLayoutsRewrite"));
            return;
        }

        if (existing != null)
            return;
        context.RegisterRefactoring(CodeAction.Create($"Create the layout baseline ({FileName})",
            _ => Task.FromResult(Create(document.Project, entries)), "NinoLayoutsCreate"));
    }

    /// <summary>
    /// The entries of the baseline of <paramref name="compilation"/>: its published layouts (with the generator's
    /// output, as the IDE's compilation has it) that <see cref="LayoutRules.Scope"/> keeps.
    /// </summary>
    internal static List<LayoutEntry> Layouts(Compilation compilation)
    {
        var layouts = new Dictionary<string, (LayoutEntry Entry, INamedTypeSymbol Type)>(StringComparer.Ordinal);
        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.Name != NinoNames.NinoTypeLayoutAttribute ||
                attribute.ConstructorArguments.Length != 7 ||
                attribute.ConstructorArguments[0].Value is not INamedTypeSymbol type)
                continue;
            var args = attribute.ConstructorArguments;
            bool? deferrable = attribute.NamedArguments.FirstOrDefault(n => n.Key == "Deferrable").Value.Value as bool?;
            type = type.OriginalDefinition;
            string key = TypeNames.RuntimeName(type);
            layouts[key] = (LayoutEntry.FromLayout(key, (int)args[1].Value!, (int)args[2].Value!, (bool)args[3].Value!,
                (bool)args[4].Value!, (int)args[5].Value!, deferrable, (string)args[6].Value!), type);
        }

        var scope = LayoutRules.Scope(layouts.Values.Select(l => l.Entry).ToList(), entry =>
        {
            var type = layouts[entry.Key].Type;
            var ancestors = new List<INamedTypeSymbol>();
            for (var b = type.BaseType; b != null; b = b.BaseType)
                ancestors.Add(b);
            ancestors.AddRange(type.AllInterfaces);
            return ancestors.Select(a => TypeNames.RuntimeName(a.OriginalDefinition)).Where(layouts.ContainsKey);
        });
        return layouts.Values.Select(l => l.Entry).Where(e => scope.Contains(e.Key)).ToList();
    }

    private static Solution Create(Project project, List<LayoutEntry> entries)
    {
        string newLine = project.Documents.FirstOrDefault()?.TryGetText(out var text) == true &&
                         text!.Lines.Count > 1 && text.ToString(TextSpan.FromBounds(text.Lines[0].End,
                             text.Lines[0].EndIncludingLineBreak)) is { Length: > 0 } found
            ? found
            : "\n";
        string? directory = project.FilePath == null ? null : System.IO.Path.GetDirectoryName(project.FilePath);
        return project.AddDocument(FileName, SourceText.From(LayoutBaseline.File(entries, newLine)),
            filePath: directory == null ? null : System.IO.Path.Combine(directory, FileName)).Project.Solution;
    }

    private static async Task<Solution> RewriteAsync(Solution solution, SyntaxReference syntax,
        List<LayoutEntry> entries, CancellationToken ct)
    {
        if (solution.GetDocument(syntax.SyntaxTree) is not { } document ||
            await document.GetSyntaxRootAsync(ct).ConfigureAwait(false) is not { } root ||
            LayoutBaselineSyntax.Literal(root, syntax.Span) is not { } literal)
            return solution;
        return solution.WithDocumentSyntaxRoot(document.Id,
            LayoutBaselineSyntax.WithText(root, literal, LayoutBaseline.Write(entries)));
    }
}

/// <summary>The string literal of a <c>[NinoLayouts]</c> attribute, read and written.</summary>
internal static class LayoutBaselineSyntax
{
    /// <summary>The attribute's string at <paramref name="span"/> (the attribute's), or null.</summary>
    public static LiteralExpressionSyntax? Literal(SyntaxNode root, TextSpan span) =>
        root.FindNode(span, getInnermostNodeForTie: true).AncestorsAndSelf().OfType<AttributeSyntax>().FirstOrDefault()
            is { ArgumentList: { Arguments.Count: 1 } list } &&
        list.Arguments[0].Expression is LiteralExpressionSyntax literal &&
        literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal
            : null;

    /// <summary><paramref name="root"/> with <paramref name="literal"/> holding <paramref name="text"/>, as a verbatim string.</summary>
    public static SyntaxNode WithText(SyntaxNode root, LiteralExpressionSyntax literal, string text)
    {
        string newLine = NinoCodeFixProvider.NewLine(root.GetText()) ?? "\n";
        string value = text.Replace("\r\n", "\n").Replace("\n", newLine);
        var token = SyntaxFactory.Literal("@\"" + value.Replace("\"", "\"\"") + "\"", value);
        return root.ReplaceNode(literal, literal.WithToken(token.WithTriviaFrom(literal.Token)));
    }
}
