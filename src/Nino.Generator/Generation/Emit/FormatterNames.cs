using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// File-local <c>using</c> aliases for long member formatter names. They are repeated (serialize, deserialize)
/// and long, so aliases keep generated files small and fast to parse. Only names actually used get an alias.
/// </summary>
internal sealed class FormatterNames
{
    /// <summary>
    /// Formatter names longer than this get an alias; shorter ones cost little more than the alias declaration.
    /// </summary>
    private const int MinAliasedLength = 48;

    private readonly Dictionary<FormatterRef, string> _aliases = new();
    private readonly HashSet<FormatterRef> _used = new();
    private string? _selfAlias, _self;
    private bool _inline;

    public FormatterNames(TypeModel m)
    {
        int index = 0;
        // Formatters naming the type's own type parameters (FormatterRef.Open) cannot be aliased outside the type.
        foreach (var (formatter, _) in m.Members.Select(x => x.Formatter)
                     .Concat(m.ShallowBodies ? m.Members.Select(x => x.ShallowFormatter) : Enumerable.Empty<FormatterRef>())
                     .Where(f => !f.Open)
                     .Distinct()
                     .Select(f => (f, Text: f.ToString()))
                     .Where(f => f.Text.Length > MinAliasedLength)
                     .OrderByDescending(f => f.Text.Length))
            _aliases[formatter] = "__NinoF" + index++;
    }

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

    /// <summary>The name to write for <paramref name="formatter"/>: its alias when it has one.</summary>
    public string Use(FormatterRef formatter)
    {
        if (!_aliases.TryGetValue(formatter, out var alias))
            return formatter.ToString();
        _used.Add(formatter);
        return alias;
    }

    /// <summary>Declares <paramref name="alias"/> for the formatted type <paramref name="type"/> itself.</summary>
    public void Alias(string alias, string type) => (_selfAlias, _self) = (alias, type);

    /// <summary>
    /// The alias declarations for the names used, the inlining attribute aliases when used, and the type's own
    /// (<see cref="Alias"/>).
    /// </summary>
    public void WriteAliases(StringBuilder sb)
    {
        if (_inline)
        {
            foreach (var alias in GeneratedCode.InlineAliases)
                sb.Append("    ").Append(alias).Append('\n');
        }

        if (_selfAlias != null)
            sb.Append($"    using {_selfAlias} = {_self};\n");

        foreach (var pair in _aliases.Where(p => _used.Contains(p.Key)).OrderBy(p => p.Value.Length).ThenBy(p => p.Value))
            sb.Append($"    using {pair.Value} = {pair.Key};\n");
        sb.Append('\n');
    }
}
