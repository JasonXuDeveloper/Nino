using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Nino.Generator.Common;
using Nino.Generator.Generation;

namespace Nino.Generator.Analysis;

/// <summary>
/// The layout baseline of a compilation (<c>[assembly: NinoLayouts]</c>) as the analyzer checks it: parsed once per
/// text, looked up per compilation, and the types of the code its sections are about (<see cref="LayoutRules"/> decides
/// what a change does). Null for a compilation without one: the check then costs one attribute lookup.
/// </summary>
internal sealed class LayoutCheck
{
    private static readonly ConditionalWeakTable<Compilation, StrongBox<LayoutCheck?>> Shared = new();

    /// <summary>The last baseline text parsed, and what came of it: the same text in the next compilation (an edit
    /// anywhere else) is not parsed again.</summary>
    private static Tuple<string, List<LayoutEntry>?, LayoutBaselineException?>? s_parsed;

    private readonly Compilation _compilation;
    private readonly TypeFacts _facts;
    private readonly Dictionary<string, LayoutEntry> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LayoutEntry>> _byDotted = new(StringComparer.Ordinal);
    private List<LayoutEntry>? _missing;
    private Dictionary<string, LayoutEntry>? _pairs;

    /// <summary>The <c>[NinoLayouts]</c> application.</summary>
    public AttributeData Attribute { get; }

    /// <summary>Why the baseline cannot be read; null when it can.</summary>
    public LayoutBaselineException? Error { get; }

    public IReadOnlyCollection<LayoutEntry> Entries => _byKey.Values;

    private LayoutCheck(Compilation compilation, TypeFacts facts, AttributeData attribute, string text)
    {
        _compilation = compilation;
        _facts = facts;
        Attribute = attribute;
        var parsed = s_parsed;
        if (parsed == null || parsed.Item1 != text)
        {
            List<LayoutEntry>? entries = null;
            LayoutBaselineException? error = null;
            try
            {
                entries = LayoutBaseline.Parse(text);
            }
            catch (LayoutBaselineException e)
            {
                error = e;
            }

            s_parsed = parsed = Tuple.Create(text, entries, error);
        }

        Error = parsed.Item3;
        foreach (var entry in parsed.Item2 ?? Enumerable.Empty<LayoutEntry>())
        {
            _byKey[entry.Key] = entry;
            string dotted = LayoutRules.Dotted(entry.Key);
            if (!_byDotted.TryGetValue(dotted, out var list))
                _byDotted.Add(dotted, list = new List<LayoutEntry>());
            list.Add(entry);
        }
    }

    /// <summary>The baseline of <paramref name="compilation"/>, or null when it has none.</summary>
    public static LayoutCheck? For(Compilation compilation, TypeFacts facts)
    {
        if (Shared.TryGetValue(compilation, out var box))
            return box.Value;
        return Shared.GetValue(compilation, c =>
        {
            var symbol = facts.Symbols.Layouts;
            var attribute = symbol == null
                ? null
                : c.Assembly.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, symbol));
            return new StrongBox<LayoutCheck?>(attribute is { ConstructorArguments.Length: 1 } &&
                                               attribute.ConstructorArguments[0].Value is string text
                ? new LayoutCheck(c, facts, attribute, text)
                : null);
        }).Value;
    }

    /// <summary>The key of a type: its runtime-form name (<c>Ns.Outer+Inner</c>, <c>Ns.Box&lt;T&gt;</c>).</summary>
    public static string Key(INamedTypeSymbol type) => TypeNames.RuntimeName(type.OriginalDefinition);

    /// <summary>Whether the type id of <paramref name="type"/> is written: a <c>TypeId</c> header, or a subtype.</summary>
    public static bool IdWritten(INamedTypeSymbol type, LayoutEntry layout) =>
        layout.Header == LayoutEntry.HeaderNames[2] || type.NinoAncestors().Any();

    /// <summary>
    /// The section about <paramref name="type"/>: its own, else the section of a type the code no longer has that it
    /// was renamed or moved from (<see cref="LayoutRules.Pair"/>).
    /// </summary>
    public LayoutEntry? SectionOf(INamedTypeSymbol type, string key, CancellationToken ct)
    {
        if (_byKey.TryGetValue(key, out var entry))
            return entry;
        if (Missing(ct).Count == 0)
            return null;
        // The pairing needs only the types that could pair with these sections: this one and those of its name.
        var added = new List<KeyValuePair<string, int>> { new(key, Id(type.OriginalDefinition)) };
        string simple = LayoutRules.SimpleName(key);
        foreach (var other in Named(simple.Substring(0, simple.IndexOf('`')), ct))
        {
            string otherKey = Key(other);
            if (otherKey != key && !_byKey.ContainsKey(otherKey))
                added.Add(new(otherKey, Id(other)));
        }

        return LayoutRules.Pair(Missing(ct), added).TryGetValue(key, out var old) ? old : null;
    }

    /// <summary>The sections whose type is no longer a [NinoType] of this assembly, and not renamed or moved.</summary>
    public IEnumerable<LayoutEntry> Removed(CancellationToken ct)
    {
        var missing = Missing(ct);
        if (missing.Count == 0)
            return missing;
        if (_pairs == null)
        {
            var added = new List<KeyValuePair<string, int>>();
            foreach (var type in AssemblyTypes.Of(_compilation.Assembly, ct))
            {
                if (type.GetNinoTypeOptions() == null)
                    continue;
                string key = Key(type);
                if (!_byKey.ContainsKey(key))
                    added.Add(new(key, Id(type)));
            }

            _pairs = LayoutRules.Pair(missing, added);
        }

        var paired = new HashSet<LayoutEntry>(_pairs.Values);
        return missing.Where(m => !paired.Contains(m));
    }

    /// <summary>
    /// Whether <paramref name="old"/> (a type name in an encoding of the baseline) and <paramref name="now"/> (one in
    /// the code's) name the same type: <paramref name="now"/>'s section is <paramref name="old"/>'s.
    /// </summary>
    public bool SameType(string old, string now, CancellationToken ct)
    {
        if (!_byDotted.TryGetValue(old, out var sections) || sections.Count != 1)
            return false;
        int dot = now.LastIndexOf('.');
        foreach (var type in Named(now.Substring(dot + 1), ct))
        {
            string key = Key(type);
            if (LayoutRules.Dotted(key) == now)
                return SectionOf(type, key, ct) == sections[0];
        }

        return false;
    }

    /// <summary>Whether a type name (dotted) has a section of its own for its raw memory.</summary>
    public bool RawSection(string name) =>
        _byDotted.TryGetValue(name, out var sections) && sections.Any(s => s.Raw != null);

    /// <summary>The type id a layout of <paramref name="type"/> publishes: 0 for a generic type (made at run time).</summary>
    private int Id(INamedTypeSymbol type) => TypeNames.AllTypeParameters(type).Count != 0 ? 0 : _facts.Ids(type).Id;

    /// <summary>The [NinoType]s this assembly declares with the simple name <paramref name="name"/>.</summary>
    private IEnumerable<INamedTypeSymbol> Named(string name, CancellationToken ct) =>
        _compilation.GetSymbolsWithName(name, SymbolFilter.Type, ct).OfType<INamedTypeSymbol>()
            .Where(t => t.GetNinoTypeOptions() != null);

    /// <summary>The sections whose key names no [NinoType] of this assembly.</summary>
    private List<LayoutEntry> Missing(CancellationToken ct)
    {
        if (_missing != null)
            return _missing;
        var missing = new List<LayoutEntry>();
        foreach (var entry in _byKey.Values)
        {
            ct.ThrowIfCancellationRequested();
            if (Resolve(entry.Key) == null)
                missing.Add(entry);
        }

        return _missing = missing;
    }

    /// <summary>
    /// The [NinoType] of this assembly whose key is <paramref name="key"/>: its metadata name is the key's with an
    /// arity on each generic type (<c>Ns.Outer`1+Inner`1</c>), and a key does not say how a nested generic type's
    /// arguments split between it and the types containing it, so each split is tried.
    /// </summary>
    private INamedTypeSymbol? Resolve(string key)
    {
        int open = key.IndexOf('<');
        string name = open < 0 ? key : key.Substring(0, open);
        int arity = open < 0 ? 0 : LayoutEntry.Split(key.Substring(open + 1, key.Length - open - 2), ',').Count;
        var parts = name.Split('+');
        foreach (string metadataName in MetadataNames(parts, 0, arity))
        {
            if (_compilation.Assembly.GetTypeByMetadataName(metadataName) is { } type &&
                type.GetNinoTypeOptions() != null && Key(type) == key)
                return type;
        }

        return null;
    }

    private static IEnumerable<string> MetadataNames(string[] parts, int index, int arity)
    {
        if (index == parts.Length - 1)
        {
            yield return arity == 0 ? parts[index] : parts[index] + "`" + arity;
            yield break;
        }

        for (int own = 0; own <= arity; own++)
        {
            string part = own == 0 ? parts[index] : parts[index] + "`" + own;
            foreach (string rest in MetadataNames(parts, index + 1, arity - own))
                yield return part + "+" + rest;
        }
    }

    /// <summary>
    /// The location of line <paramref name="line"/> of the baseline: that line in a verbatim string written in one
    /// piece, as the code fixes and nino-gen write it; else the attribute.
    /// </summary>
    public Location LocationOf(int line, CancellationToken ct)
    {
        var whole = BaselineLocation(ct);
        if (Literal(ct) is not { } literal || line < 1)
            return whole;
        var token = literal.Token;
        string value = token.ValueText;
        // Only a verbatim string without doubled quotes maps its value onto its source one to one.
        if (!token.Text.StartsWith("@\"", StringComparison.Ordinal) || token.Text.Length != value.Length + 3)
            return whole;
        int start = 0;
        for (int i = 1; i < line; i++)
        {
            start = value.IndexOf('\n', start) + 1;
            if (start == 0)
                return whole;
        }

        int end = value.IndexOf('\n', start);
        string text = value.Substring(start, (end < 0 ? value.Length : end) - start).TrimEnd('\r');
        int indent = text.Length - text.TrimStart().Length;
        return Location.Create(token.SyntaxTree!,
            new TextSpan(token.SpanStart + 2 + start + indent, text.Trim().Length));
    }

    /// <summary>The <c>[NinoLayouts]</c> attribute in source: where the code fixes find the baseline to rewrite.</summary>
    public Location BaselineLocation(CancellationToken ct) =>
        Attribute.ApplicationSyntaxReference is { } syntax
            ? Location.Create(syntax.SyntaxTree, syntax.Span)
            : Location.None;

    private LiteralExpressionSyntax? Literal(CancellationToken ct) =>
        Attribute.ApplicationSyntaxReference?.GetSyntax(ct) is AttributeSyntax { ArgumentList: { Arguments.Count: 1 } arguments } &&
        arguments.Arguments[0].Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal
            : null;

    /// <summary>The properties every finding carries for its fixes.</summary>
    public static ImmutableDictionary<string, string?> Properties(string key, string? section, string? oldKey,
        LayoutFix fix, string? argument)
    {
        var properties = ImmutableDictionary<string, string?>.Empty.Add(NinoDiagnostics.LayoutKey, key)
            .Add(NinoDiagnostics.LayoutFixKind, fix.ToString());
        if (section != null)
            properties = properties.Add(NinoDiagnostics.LayoutSection, section);
        if (oldKey != null && oldKey != key)
            properties = properties.Add(NinoDiagnostics.LayoutOldKey, oldKey);
        if (argument != null)
            properties = properties.Add(NinoDiagnostics.LayoutArgument, argument);
        return properties;
    }
}
