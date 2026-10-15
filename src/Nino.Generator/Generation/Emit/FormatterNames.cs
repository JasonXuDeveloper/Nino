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
    public const string Inline = "[__NinoInline(__NinoInlineOptions.AggressiveInlining)]";

    private readonly Dictionary<string, string> _aliases = new();
    private readonly HashSet<string> _used = new();

    public FormatterNames(TypeModel m)
    {
        var typeParameters = m.TypeParameters.Length == 0
            ? new string[0]
            : m.TypeParameters.Trim('<', '>').Split(',').Select(p => p.Trim()).ToArray();
        int index = 0;
        // Formatters mentioning the type's own type parameters cannot be aliased outside the type.
        foreach (var formatter in m.Members.Select(x => x.Formatter)
                     .Where(f => f.Length > 48 && !MentionsAny(f, typeParameters))
                     .Distinct()
                     .OrderByDescending(f => f.Length))
            _aliases[formatter] = "__NinoF" + index++;
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
    public void WriteAliases(StringBuilder sb, bool inline)
    {
        if (inline)
        {
            sb.Append("    using __NinoInline = global::System.Runtime.CompilerServices.MethodImplAttribute;\n");
            sb.Append("    using __NinoInlineOptions = global::System.Runtime.CompilerServices.MethodImplOptions;\n");
        }

        foreach (var pair in _aliases.Where(p => _used.Contains(p.Key)).OrderBy(p => p.Value.Length).ThenBy(p => p.Value))
            sb.Append($"    using {pair.Value} = {pair.Key};\n");
        sb.Append('\n');
    }

    private static bool MentionsAny(string text, string[] identifiers)
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
