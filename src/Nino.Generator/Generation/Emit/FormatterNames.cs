using System;
using System.Collections.Generic;
using System.Linq;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// The names a formatter file declares aliases for: the inlining attributes once used, and the member formatters,
/// which <see cref="GeneratedCode.WithAliases"/> aliases where that makes the file shorter.
/// </summary>
internal sealed class FormatterNames
{
    private bool _inline;
    private readonly bool _shadowsMethodFlags;

    public FormatterNames(TypeModel m, bool compactSyntax)
    {
        CompactSyntax = compactSyntax;
        _shadowsMethodFlags = m.IsGeneric && m.TypeParameters.Contains(GeneratedCode.MethodFlags);
        // Generic ones only: a formatter without type arguments is one type's name, short once its namespace is (the
        // generated ones in NinoGen), and the name to read; and one naming the type's own type parameters
        // (FormatterRef.Open) cannot be aliased outside the type.
        List<string>? formatters = null;
        AddFormatters(m, false, ref formatters);
        if (m.ShallowBodies)
            AddFormatters(m, true, ref formatters);
        Formatters = formatters is null ? Array.Empty<string>() : formatters;
    }

    private static void AddFormatters(TypeModel m, bool shallow, ref List<string>? formatters)
    {
        FormatterRef? previous = null;
        for (int i = 0; i < m.Members.Length; i++)
        {
            var member = m.Members[i];
            var formatter = shallow ? member.ShallowFormatter : member.Formatter;
            if (formatter.Args.Length == 0 || formatter.Open || formatter == previous)
                continue;
            (formatters ??= new List<string>()).Add(formatter.ToString());
            previous = formatter;
        }
    }

    /// <summary>The generic member formatters, as the body writes them, in member order (their aliases').</summary>
    public IReadOnlyList<string> Formatters { get; }

    /// <summary>Whether the compilation supports file-scoped namespaces and target-typed construction.</summary>
    public bool CompactSyntax { get; }

    /// <summary>The aggressive inlining attribute, through its aliases (declared once used).</summary>
    public string Inline
    {
        get
        {
            if (_shadowsMethodFlags)
                return TypeNames.InlineAttribute;
            _inline = true;
            return GeneratedCode.Inline;
        }
    }

    /// <summary>The no-inlining attribute, through the same aliases.</summary>
    public string NoInline
    {
        get
        {
            if (_shadowsMethodFlags)
                return TypeNames.NoInlineAttribute;
            _inline = true;
            return GeneratedCode.NoInline;
        }
    }

    /// <summary>
    /// The alias declarations: the inlining attribute aliases when used, then <paramref name="others"/> (those
    /// <see cref="GeneratedCode.WithAliases"/> declared).
    /// </summary>
    public IEnumerable<string> Declarations(IEnumerable<string> others) =>
        _inline ? GeneratedCode.InlineAliases.Concat(others) : others;
}
