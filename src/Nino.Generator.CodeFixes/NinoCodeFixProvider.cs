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
using Microsoft.CodeAnalysis.FindSymbols;
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
        NinoType = NinoNames.NinoType, NinoFormerName = "NinoFormerName", NinoFactory = "NinoFactory",
        NinoUtf8 = "NinoUtf8", NinoMemberFormatter = "NinoMemberFormatter";

    private const string NoParameterNamed = "CS1739";

    /// <summary>
    /// The <c>[NinoType]</c> parameters renamed before 4.0, by their former name: <c>containNonPublicMembers</c> (v3)
    /// and <c>allowInheritance</c> (the 4.0 release candidates); the values mean the same. Declared before
    /// <see cref="Fixes"/>, which reads them: static fields initialize in order.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedTypeArguments = new()
    {
        ["containNonPublicMembers"] = "includeNonPublicMembers",
        ["allowInheritance"] = "inheritedBySubtypes",
    };

    private static readonly Dictionary<string, Fix[]> Fixes = new()
    {
        [NinoDiagnostics.InaccessibleType.Id] = new[]
        {
            // A static class stays unserializable whatever its accessibility.
            new Fix("Make type public", "MakePublic", MakeAccessible,
                n => !Type(n).Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))),
        },
        [NinoDiagnostics.NotPartial.Id] = new[] { new Fix("Make type partial", "MakePartial", MakePartial) },
        [NinoDiagnostics.SubtypeWithoutNinoType.Id] = new[] { new Fix("Add [NinoType]", "AddNinoType", (e, n) => AddAttribute(e, Type(n), NinoType)) },
        [NinoDiagnostics.ReadOnlyMember.Id] = new[] { AddIgnoreFix() },
        [NinoDiagnostics.UnserializableMember.Id] = new[] { AddIgnoreFix() },
        [NinoDiagnostics.PrivateMember.Id] = new[]
        {
            RemoveAttributeFix(NinoMember),
            new Fix("Update type to allow non-public members", "AllowNonPublic",
                (e, n) => SetTypeArgument(e, n, "includeNonPublicMembers", true)),
        },
        [NinoDiagnostics.RedundantMember.Id] = new[] { RemoveAttributeFix(NinoMember) },
        [NinoDiagnostics.NotSerializableMember.Id] = new[] { RemoveAttributeFix(NinoMember) },
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
        [NinoDiagnostics.FormerNameNotKeyed.Id] = new[] { RemoveAttributeFix(NinoFormerName) },
        // The factory is never called while the type has a [NinoConstructor]: removing it changes nothing.
        [NinoDiagnostics.FactoryIgnored.Id] = new[] { RemoveAttributeFix(NinoFactory) },
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
        [NinoDiagnostics.WeakToleranceRemoved.Id] = new[]
        {
            new Fix("Make it version tolerant", "VersionTolerant", d => d.Properties.TryGetValue(
                NinoDiagnostics.NinoTypeToAdd, out var ninoType) && ninoType != null
                ? (e, n) => AddAttribute(e, Type(n), ninoType)
                : MakeVersionTolerant),
        },
        // No effect either way: nothing changes when it goes.
        [NinoDiagnostics.Utf8WithoutEffect.Id] = new[] { RemoveAttributeFix(NinoUtf8) },
        [NinoDiagnostics.Utf8FormatterByHand.Id] = new[] { new Fix("Use [NinoUtf8]", "UseNinoUtf8", UseNinoUtf8) },
        // The compiler's "no parameter named ..." on a [NinoType] argument under its name before 4.0.
        // One fix for every former name, so fixing all renames them all at once.
        [NoParameterNamed] = new[]
        {
            new Fix("Use the 4.0 parameter name", "RenameFormerArgument", (e, n) => e.ReplaceNode(n,
                SyntaxFactory.IdentifierName(RenamedTypeArguments[FormerArgumentName(n)!]).WithTriviaFrom(n)),
                n => FormerArgumentName(n) != null),
        },
    };

    /// <summary>
    /// The name of <paramref name="node"/> when it is a <c>[NinoType]</c> argument named by a former name
    /// (<see cref="RenamedTypeArguments"/>), else null.
    /// </summary>
    private static string? FormerArgumentName(SyntaxNode node) =>
        node is IdentifierNameSyntax { Parent: NameColonSyntax
        {
            Parent: AttributeArgumentSyntax { Parent: AttributeArgumentListSyntax { Parent: AttributeSyntax attribute } },
        } } identifier && RenamedTypeArguments.ContainsKey(identifier.Identifier.ValueText) &&
        attribute.Name.ToString() is var type &&
        (type.EndsWith(NinoType, StringComparison.Ordinal) || type.EndsWith(NinoType + "Attribute", StringComparison.Ordinal))
            ? identifier.Identifier.ValueText
            : null;

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        Fixes.Keys.Append(NinoDiagnostics.InterfaceMemberAttribute.Id).ToImmutableArray();

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root == null)
            return;

        foreach (var diagnostic in context.Diagnostics)
        {
            if (diagnostic.Id == NinoDiagnostics.InterfaceMemberAttribute.Id)
            {
                await RegisterMoveToImplementationsAsync(context, root, diagnostic).ConfigureAwait(false);
                continue;
            }

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
    /// NINO033: the attribute of an interface member goes to the members implementing it implicitly in the solution's
    /// source, which the generator writes (one that has an attribute of its class already keeps its own), and leaves the
    /// interface, where it has no effect; with no such member, the fix removes it. An explicit implementation in the
    /// solution (another project than the one reported) writes the interface's member with its attributes: the
    /// attribute then stays on the interface, copied to the implicit implementations, and with none of those there is
    /// no fix. Names are written in full and reduced by the simplifier, so the attribute compiles in a file without
    /// <c>using Nino.Core;</c> or under an alias. One key, so fixing all moves every one at once.
    /// </summary>
    private static async Task RegisterMoveToImplementationsAsync(CodeFixContext context, SyntaxNode root,
        Diagnostic diagnostic)
    {
        var document = context.Document;
        var ct = context.CancellationToken;
        if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true).AncestorsAndSelf()
                .OfType<AttributeSyntax>().FirstOrDefault() is not { } syntax ||
            await document.GetSemanticModelAsync(ct).ConfigureAwait(false) is not { } model ||
            model.GetDeclaredSymbol(Member(syntax), ct) is not { } member ||
            member.GetAttributes().FirstOrDefault(a => a.ApplicationSyntaxReference is { } r &&
                                                      r.SyntaxTree == syntax.SyntaxTree && r.Span == syntax.Span)
                is not { AttributeClass: { } attributeClass } attribute)
            return;

        var implementations = new List<ISymbol>();
        bool explicitImplementation = false;
        foreach (var implementation in await SymbolFinder.FindImplementationsAsync(member, document.Project.Solution,
                     cancellationToken: ct).ConfigureAwait(false))
        {
            if (implementation is not IPropertySymbol property)
                continue;
            if (!property.ExplicitInterfaceImplementations.IsEmpty)
                explicitImplementation = true;
            else if (property.DeclaringSyntaxReferences.Length != 0 &&
                     !property.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeClass)))
                implementations.Add(property);
        }

        if (explicitImplementation && implementations.Count == 0)
            return;
        string name = attributeClass.Name.Substring(0, attributeClass.Name.Length - "Attribute".Length);
        string title = explicitImplementation ? $"Copy [{name}] to the implicit implementations"
            : implementations.Count == 0 ? $"Remove [{name}]"
            : $"Move [{name}] to the implementing members";
        context.RegisterCodeFix(CodeAction.Create(title,
                c => MoveToImplementationsAsync(document, syntax.Span, name, Copy(attribute), implementations,
                    keep: explicitImplementation, c),
                diagnostic.Id + "MoveToImplementations"),
            diagnostic);
    }

    /// <summary><paramref name="attribute"/> as an attribute with every name in full, for the simplifier to reduce.</summary>
    private static AttributeSyntax Copy(AttributeData attribute)
    {
        static TypeSyntax FullName(ITypeSymbol type) => SyntaxFactory
            .ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .WithAdditionalAnnotations(Simplifier.Annotation);

        var arguments = attribute.ConstructorArguments.Select(a => SyntaxFactory.AttributeArgument(
            a is { Kind: TypedConstantKind.Type, Value: ITypeSymbol type }
                ? SyntaxFactory.TypeOfExpression(FullName(type))
                : SyntaxFactory.ParseExpression(a.ToCSharpString()))).ToList();
        var copy = SyntaxFactory.Attribute((NameSyntax)FullName(attribute.AttributeClass!));
        return arguments.Count == 0
            ? copy
            : copy.WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SeparatedList(arguments)));
    }

    private static async Task<Solution> MoveToImplementationsAsync(Document document, TextSpan span, string name,
        AttributeSyntax attribute, List<ISymbol> implementations, bool keep, CancellationToken ct)
    {
        var solution = document.Project.Solution;
        var editor = new SolutionEditor(solution);
        if (!keep)
        {
            var interfaceEditor = await editor.GetDocumentEditorAsync(document.Id, ct).ConfigureAwait(false);
            RemoveAttribute(interfaceEditor, interfaceEditor.OriginalRoot.FindNode(span, getInnermostNodeForTie: true),
                name);
        }

        foreach (var implementation in implementations)
        {
            var reference = implementation.DeclaringSyntaxReferences[0];
            if (solution.GetDocumentId(reference.SyntaxTree) is not { } id)
                continue;
            var target = await editor.GetDocumentEditorAsync(id, ct).ConfigureAwait(false);
            var declaration = await reference.GetSyntaxAsync(ct).ConfigureAwait(false);
            // A record's positional parameter declares the property through an attribute on the property target.
            target.ReplaceNode(declaration, (current, generator) => (current is ParameterSyntax parameter
                ? parameter.AddAttributeLists(SyntaxFactory.AttributeList(
                    SyntaxFactory.AttributeTargetSpecifier(SyntaxFactory.Token(SyntaxKind.PropertyKeyword)),
                    SyntaxFactory.SingletonSeparatedList(attribute)))
                : generator.AddAttributes(current, attribute)).WithAdditionalAnnotations(Formatter.Annotation));
        }

        var changed = editor.GetChangedSolution();
        foreach (var id in changed.GetChanges(solution).GetProjectChanges().SelectMany(p => p.GetChangedDocuments()))
        {
            var original = await solution.GetDocument(id)!.GetTextAsync(ct).ConfigureAwait(false);
            changed = (await FormatAsync(changed.GetDocument(id)!, original, ct).ConfigureAwait(false)).Project.Solution;
        }

        return changed;
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

    /// <summary>
    /// <c>[NinoMemberFormatter(typeof(Utf8StringFormatter))]</c> becomes <c>[NinoUtf8]</c> in its place, named as it
    /// was (qualified or not); with a <c>[NinoUtf8]</c> there already, it goes.
    /// </summary>
    private static void UseNinoUtf8(DocumentEditor editor, SyntaxNode node)
    {
        var member = Member(node);
        if (FindAttribute(editor, member, NinoMemberFormatter) is not { } formatter)
            return;
        if (FindAttribute(editor, member, NinoUtf8) != null)
        {
            RemoveAttribute(editor, node, NinoMemberFormatter);
            return;
        }

        NameSyntax name = SyntaxFactory.IdentifierName(NinoUtf8);
        if (formatter.Name is QualifiedNameSyntax { Left: var left })
            name = SyntaxFactory.QualifiedName(left, (SimpleNameSyntax)name);
        editor.ReplaceNode(formatter, SyntaxFactory.Attribute(name).WithTriviaFrom(formatter));
    }

    /// <summary>
    /// <c>format: NinoFormat.VersionTolerant</c> on the type's <c>[NinoType]</c> (NINO032), named as briefly as the
    /// file allows (the simplifier reduces the full name: <c>NinoFormat</c> under <c>using Nino.Core;</c>, else
    /// qualified, as for a <c>[NinoType]</c> named through an alias).
    /// </summary>
    private static void MakeVersionTolerant(DocumentEditor editor, SyntaxNode node) =>
        SetTypeArgument(editor, node, "format",
            SyntaxFactory.ParseExpression("global::" + NinoNames.CoreNamespace + ".NinoFormat.VersionTolerant")
                .WithAdditionalAnnotations(Simplifier.Annotation), removable: false);

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

    /// <summary>
    /// Removes every <c>[name]</c> of the member <paramref name="node"/> is in (a member may carry several
    /// <c>[NinoFormerName]</c>s), with the lists left empty; the declaration keeps the indentation the first list
    /// carried.
    /// </summary>
    private static void RemoveAttribute(DocumentEditor editor, SyntaxNode node, string name)
    {
        var member = Member(node);
        var attributes = member.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => IsAttribute(editor.SemanticModel, a, name)).ToList();
        if (attributes.Count == 0)
            return;
        editor.ReplaceNode(member, (current, _) =>
        {
            var m = (MemberDeclarationSyntax)current;
            var removed = new List<SyntaxNode>();
            foreach (var list in m.AttributeLists)
            {
                var matched = list.Attributes.Where(a => attributes.Any(r => r.IsEquivalentTo(a))).ToList();
                if (matched.Count == list.Attributes.Count)
                    removed.Add(list);
                else
                    removed.AddRange(matched);
            }

            var leading = m.AttributeLists.Count != 0 && removed.Contains(m.AttributeLists[0])
                ? m.AttributeLists[0].GetLeadingTrivia()
                : default;
            var result = m.RemoveNodes(removed, SyntaxRemoveOptions.KeepNoTrivia)!;
            return leading.Count == 0 ? result : result.WithLeadingTrivia(leading);
        });
    }

    /// <summary>
    /// Adds <c>[<paramref name="attribute"/>]</c> (a name, or a name and its arguments) to the member, and
    /// <c>using Nino.Core;</c> where it is missing.
    /// </summary>
    private static void AddAttribute(DocumentEditor editor, MemberDeclarationSyntax member, string attribute)
    {
        editor.ReplaceNode(member, (current, generator) =>
            generator.AddAttributes(current, attribute.IndexOf('(') < 0
                    ? generator.Attribute(attribute)
                    : SyntaxFactory.ParseCompilationUnit("[" + attribute + "] class C { }")
                        .DescendantNodes().OfType<AttributeSyntax>().First())
                .WithAdditionalAnnotations(Formatter.Annotation));
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
    private static void SetTypeArgument(DocumentEditor editor, SyntaxNode node, string name, bool value) =>
        SetTypeArgument(editor, node, name,
            SyntaxFactory.LiteralExpression(value ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
            removable: !value);

    /// <summary>
    /// Sets a <c>[NinoType]</c> argument to <paramref name="value"/>, or removes it when <paramref name="removable"/>
    /// (its value is the default).
    /// </summary>
    private static void SetTypeArgument(DocumentEditor editor, SyntaxNode node, string name, ExpressionSyntax value,
        bool removable)
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

        SeparatedSyntaxList<AttributeArgumentSyntax> updated;
        if (index >= 0)
        {
            // A positional argument can only be dropped when it is the last one.
            updated = removable && (arguments[index].NameColon != null || index == arguments.Count - 1)
                ? arguments.RemoveAt(index)
                : arguments.Replace(arguments[index], arguments[index].WithExpression(value));
        }
        else if (!removable)
        {
            updated = arguments.Add(SyntaxFactory.AttributeArgument(value).WithNameColon(SyntaxFactory.NameColon(name)));
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
