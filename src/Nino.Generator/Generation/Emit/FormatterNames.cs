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

    private readonly Dictionary<string, string> _aliases = new();
    private readonly HashSet<string> _used = new();
    private bool _inline;

    public FormatterNames(TypeModel m)
    {
        int index = 0;
        // Formatters mentioning the type's own type parameters cannot be aliased outside the type.
        foreach (var formatter in m.Members.Select(x => x.Formatter)
                     .Concat(m.ShallowBodies ? m.Members.Select(x => x.ShallowFormatter) : Enumerable.Empty<string>())
                     .Where(f => f.Length > MinAliasedLength && !MentionsAny(f, m.TypeParameters))
                     .Distinct()
                     .OrderByDescending(f => f.Length))
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
    public string Use(string formatter)
    {
        if (!_aliases.TryGetValue(formatter, out var alias))
            return formatter;
        _used.Add(formatter);
        return alias;
    }

    /// <summary>The alias declarations for the names used, and the inlining attribute aliases when used.</summary>
    public void WriteAliases(StringBuilder sb)
    {
        if (_inline)
        {
            foreach (var alias in GeneratedCode.InlineAliases)
                sb.Append("    ").Append(alias).Append('\n');
        }

        foreach (var pair in _aliases.Where(p => _used.Contains(p.Key)).OrderBy(p => p.Value.Length).ThenBy(p => p.Value))
            sb.Append($"    using {pair.Value} = {pair.Key};\n");
        sb.Append('\n');
    }

    private static bool MentionsAny(string text, IEnumerable<string> identifiers)
    {
        foreach (var id in identifiers)
        {
            int i = 0;
            while ((i = text.IndexOf(id, i, System.StringComparison.Ordinal)) >= 0)
            {
                bool start = i == 0 || !IsIdentifierChar(text[i - 1]);
                bool end = i + id.Length == text.Length || !IsIdentifierChar(text[i + id.Length]);
                if (start && end)
                    return true;
                i += id.Length;
            }
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';
}
