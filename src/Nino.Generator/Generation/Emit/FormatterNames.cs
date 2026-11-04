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

    public FormatterNames(TypeModel m)
    {
        // Generic ones only: a formatter without type arguments is one type's name, short once its namespace is (the
        // generated ones in NinoGen), and the name to read; and one naming the type's own type parameters
        // (FormatterRef.Open) cannot be aliased outside the type.
        Formatters = m.Members.Select(x => x.Formatter)
            .Concat(m.ShallowBodies ? m.Members.Select(x => x.ShallowFormatter) : Enumerable.Empty<FormatterRef>())
            .Where(f => f.Args.Length != 0 && !f.Open)
            .Select(f => f.ToString())
            .ToList();
    }

    /// <summary>The generic member formatters, as the body writes them.</summary>
    public IReadOnlyList<string> Formatters { get; }

    /// <summary>The aggressive inlining attribute, through its aliases (declared once used).</summary>
    public string Inline
    {
        get
        {
            _inline = true;
            return GeneratedCode.Inline;
        }
    }

    /// <summary>The no-inlining attribute, through the same aliases.</summary>
    public string NoInline
    {
        get
        {
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
