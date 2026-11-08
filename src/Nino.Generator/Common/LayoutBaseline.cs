using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Nino.Generator.Common;

// Compiled into the analyzer, the code fixes and nino-gen (src/Nino.Codegen), which write and read the same baseline.

/// <summary>
/// The layout of one <c>[NinoType]</c> as a layout baseline holds it: what <c>NinoTypeLayoutAttribute</c> says (wire
/// format spec §8), split into fields. Built from a layout (<see cref="FromLayout"/>) or read from a baseline
/// (<see cref="LayoutBaseline.Parse"/>).
/// </summary>
internal sealed class LayoutEntry
{
    public static readonly string[] FormatNames = { "Positional", "VersionTolerant", "Keyed" };
    public static readonly string[] HeaderNames = { "None", "Presence", "TypeId" };

    /// <summary>The type's runtime-form name (<c>Ns.Outer+Inner</c>, <c>Ns.Box&lt;T&gt;</c>): its table in the baseline.</summary>
    public string Key = "";

    /// <summary>One of <see cref="FormatNames"/>.</summary>
    public string Format = "";

    /// <summary>One of <see cref="HeaderNames"/>.</summary>
    public string Header = "";

    public bool TrackReferences;

    /// <summary>The raw encoding of a type written as its raw memory; null for any other.</summary>
    public string? Raw;

    /// <summary>The type id (0 for a generic type).</summary>
    public int Id;

    /// <summary>Whether a body nested too deep is deferred; null when the layout does not say (an older generator).</summary>
    public bool? Deferrable;

    /// <summary>The encoding of a <c>[NinoType]</c> collection's elements; null for any other type.</summary>
    public string? Elements;

    /// <summary>The members in the order they are written.</summary>
    public List<LayoutMember> Members = new();

    /// <summary>The line of the table header in the baseline (1-based); 0 for a layout of the code.</summary>
    public int Line;

    public bool IsKeyed => Format == FormatNames[2];
    public bool IsVersionTolerant => Format == FormatNames[1];

    /// <summary>
    /// The entry of a published layout: the arguments of a <c>NinoTypeLayoutAttribute</c>, its members text as §8.1
    /// describes it.
    /// </summary>
    public static LayoutEntry FromLayout(string key, int format, int header, bool trackReferences, bool raw, int id,
        bool? deferrable, string members)
    {
        var entry = new LayoutEntry
        {
            Key = key,
            Format = Name(FormatNames, format),
            Header = Name(HeaderNames, header),
            TrackReferences = trackReferences,
            Id = id,
            Deferrable = deferrable,
        };
        if (raw)
        {
            entry.Raw = members;
            return entry;
        }

        foreach (string part in Split(members, ';'))
        {
            if (part.StartsWith(ElementsPrefix, StringComparison.Ordinal))
            {
                entry.Elements = part.Substring(ElementsPrefix.Length);
                continue;
            }

            // name:type:encoding[:key=0x...][:former=0x...,0x...], split at the top-level ':'.
            var fields = Split(part, ':');
            if (fields.Count < 3)
                throw new FormatException($"Expected name:type:encoding in the layout member \"{part}\".");
            var member = new LayoutMember { Name = fields[0], Type = fields[1], Encoding = fields[2] };
            for (int i = 3; i < fields.Count; i++)
            {
                if (fields[i].StartsWith("key=", StringComparison.Ordinal))
                    member.Key = Hex(fields[i].Substring(4));
                else if (fields[i].StartsWith("former=", StringComparison.Ordinal))
                    member.Former.AddRange(fields[i].Substring(7).Split(',').Select(Hex));
            }

            entry.Members.Add(member);
        }

        return entry;
    }

    private const string ElementsPrefix = "[elements]::";

    /// <summary>
    /// Whether <see cref="FromLayout"/> of these arguments (with this entry's key) makes an entry equal to this one,
    /// found by reading <paramref name="members"/> against the fields without splitting it: what a type whose layout
    /// did not change costs the analyzer. False when unsure (another spelling of the same layout); compare then.
    /// </summary>
    public bool Matches(int format, int header, bool trackReferences, bool raw, int id, bool? deferrable,
        string members)
    {
        if (Format != Name(FormatNames, format) || Header != Name(HeaderNames, header) ||
            TrackReferences != trackReferences || Id != id || Deferrable != deferrable)
            return false;
        if (raw)
            return Raw == members && Elements == null && Members.Count == 0;
        if (Raw != null)
            return false;
        int at = 0;
        if (Elements != null && !(Next(members, ref at, ElementsPrefix) && Next(members, ref at, Elements)))
            return false;
        foreach (var member in Members)
        {
            if (at != 0 && !Next(members, ref at, ";"))
                return false;
            if (!Next(members, ref at, member.Name) || !Next(members, ref at, ":") ||
                !Next(members, ref at, member.Type) || !Next(members, ref at, ":") ||
                !Next(members, ref at, member.Encoding))
                return false;
            if (member.Key is { } key && !Next(members, ref at, ":key=" + LayoutBaseline.Hex(key)))
                return false;
            if (member.Former.Count != 0 &&
                !Next(members, ref at, ":former=" + string.Join(",", member.Former.Select(LayoutBaseline.Hex))))
                return false;
        }

        return at == members.Length;
    }

    /// <summary>Whether <paramref name="text"/> continues with <paramref name="expected"/> at <paramref name="at"/>; moves past it.</summary>
    private static bool Next(string text, ref int at, string expected)
    {
        if (text.Length - at < expected.Length ||
            string.CompareOrdinal(text, at, expected, 0, expected.Length) != 0)
            return false;
        at += expected.Length;
        return true;
    }

    private static string Name(string[] names, int value) =>
        value >= 0 && value < names.Length ? names[value] : value.ToString(CultureInfo.InvariantCulture);

    private static int Hex(string text) => TryHex(text, out int value)
        ? value
        : throw new FormatException($"Expected 0x and up to eight hexadecimal digits, not \"{text}\".");

    /// <summary><c>0x</c> and one to eight hexadecimal digits, as an int (the uint they spell).</summary>
    internal static bool TryHex(string text, out int value)
    {
        value = 0;
        if (text.Length < 3 || text.Length > 10 || !text.StartsWith("0x", StringComparison.Ordinal))
            return false;
        uint result = 0;
        for (int i = 2; i < text.Length; i++)
        {
            int digit = text[i] switch
            {
                >= '0' and <= '9' => text[i] - '0',
                >= 'A' and <= 'F' => text[i] - 'A' + 10,
                >= 'a' and <= 'f' => text[i] - 'a' + 10,
                _ => -1,
            };
            if (digit < 0)
                return false;
            result = (result << 4) | (uint)digit;
        }

        value = unchecked((int)result);
        return true;
    }

    /// <summary><paramref name="text"/> split at each <paramref name="separator"/> outside any brackets.</summary>
    internal static List<string> Split(string text, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '<' or '{' or '[')
                depth++;
            else if (c is ')' or '>' or '}' or ']')
                depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(text.Substring(start, i - start));
                start = i + 1;
            }
        }

        if (text.Length != 0)
            parts.Add(text.Substring(start));
        return parts;
    }
}

/// <summary>A member of a <see cref="LayoutEntry"/>.</summary>
internal sealed class LayoutMember
{
    /// <summary>The C# name.</summary>
    public string Name = "";

    /// <summary>The C# type, fully qualified (spec §8.2).</summary>
    public string Type = "";

    /// <summary>What it is written as (spec §8.3).</summary>
    public string Encoding = "";

    /// <summary>Its key in a keyed type; null in any other.</summary>
    public int? Key;

    /// <summary>The keys of its former names (a keyed type's <c>[NinoFormerName]</c>s), read too.</summary>
    public List<int> Former = new();

    /// <summary>Its line in the baseline (1-based); 0 for a layout of the code.</summary>
    public int Line;
}

/// <summary>A baseline that cannot be read: the reason and its line (1-based).</summary>
internal sealed class LayoutBaselineException : Exception
{
    public LayoutBaselineException(string message, int line) : base(message) => Line = line;

    public int Line { get; }
}

/// <summary>
/// The layout baseline: the TOML document in <c>[assembly: NinoLayouts(@"...")]</c>, one table per type
/// (<c>['Ns.Type']</c>) holding a <see cref="LayoutEntry"/>, its members an array of inline tables, one a line. Only
/// the subset Nino writes is read (literal strings in single quotes, hexadecimal and decimal integers, booleans,
/// arrays, inline tables, comments): no TOML library, and nothing in it needs escaping in a C# verbatim string.
/// </summary>
internal static class LayoutBaseline
{
    /// <summary>
    /// A C# file (<c>NinoLayouts.cs</c>) holding the baseline of <paramref name="entries"/>, with
    /// <paramref name="newLine"/> as its line break.
    /// </summary>
    public static string File(IEnumerable<LayoutEntry> entries, string newLine = "\n") => (
        "// The layout baseline of this assembly: the wire layouts that data it wrote depends on (its version-tolerant\n" +
        "// and keyed [NinoType]s and every type they reach). Nino's analyzer reports a change that breaks them\n" +
        "// (NINO034-NINO036); accept an intended change with its code fix or nino-gen layouts, which rewrite this file.\n" +
        "[assembly: Nino.Core.NinoLayouts(@\"" + Write(entries) + "\")]\n").Replace("\n", newLine);

    /// <summary>A whole baseline of <paramref name="entries"/>, sorted by type: a newline, then the sections.</summary>
    public static string Write(IEnumerable<LayoutEntry> entries) =>
        "\n" + string.Join("\n", entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(Section));

    /// <summary>The section of <paramref name="entry"/>: its table, ending with a newline.</summary>
    public static string Section(LayoutEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(Literal(entry.Key)).Append("]\n");
        sb.Append("format = ").Append(Literal(entry.Format)).Append('\n');
        sb.Append("header = ").Append(Literal(entry.Header)).Append('\n');
        if (entry.TrackReferences)
            sb.Append("trackReferences = true\n");
        if (entry.Raw != null)
            sb.Append("raw = ").Append(Literal(entry.Raw)).Append('\n');
        sb.Append("id = ").Append(Hex(entry.Id)).Append('\n');
        if (entry.Deferrable is { } deferrable)
            sb.Append("deferrable = ").Append(deferrable ? "true" : "false").Append('\n');
        if (entry.Elements != null)
            sb.Append("elements = ").Append(Literal(entry.Elements)).Append('\n');
        if (entry.Raw != null)
            return sb.ToString();
        if (entry.Members.Count == 0)
            return sb.Append("members = []\n").ToString();
        sb.Append("members = [\n");
        foreach (var member in entry.Members)
        {
            sb.Append("  { name = ").Append(Literal(member.Name))
                .Append(", type = ").Append(Literal(member.Type))
                .Append(", encoding = ").Append(Literal(member.Encoding));
            if (member.Key is { } key)
                sb.Append(", key = ").Append(Hex(key));
            if (member.Former.Count != 0)
                sb.Append(", former = [").Append(string.Join(", ", member.Former.Select(Hex))).Append(']');
            sb.Append(" },\n");
        }

        return sb.Append("]\n").ToString();
    }

    /// <summary><c>0x</c> and eight upper-case digits: the int as a uint, as layouts show ids and keys.</summary>
    public static string Hex(int value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    private static readonly char[] Unwritable = { '\'', '\n', '\r', '"' };

    private static string Literal(string value)
    {
        if (value.IndexOfAny(Unwritable) >= 0)
            throw new ArgumentException($"A baseline cannot hold \"{value}\": it has a quote or a line break.");
        return "'" + value + "'";
    }

    /// <summary>
    /// <paramref name="text"/> with the sections of <paramref name="changes"/>' keys replaced by their texts (a
    /// <see cref="Section"/>), removed (null) or, for a key without a section, inserted in key order. Everything else
    /// (comments, other sections) is kept as written.
    /// </summary>
    public static string Update(string text, IEnumerable<KeyValuePair<string, string?>> changes)
    {
        text = text.Replace("\r\n", "\n");
        var (prefix, sections) = Sections(text);
        foreach (var change in changes)
        {
            int index = sections.FindIndex(s => s.Key == change.Key);
            if (change.Value == null)
            {
                if (index >= 0)
                    sections.RemoveAt(index);
                continue;
            }

            if (index >= 0)
                sections[index] = new KeyValuePair<string, string>(change.Key, change.Value);
            else
            {
                int at = sections.FindIndex(s => string.CompareOrdinal(s.Key, change.Key) > 0);
                sections.Insert(at < 0 ? sections.Count : at, new KeyValuePair<string, string>(change.Key, change.Value));
            }
        }

        if (prefix.Length == 0 || prefix[prefix.Length - 1] != '\n')
            prefix += "\n";
        return prefix + string.Join("\n", sections.Select(s => s.Value.TrimEnd('\n', ' ', '\t') + "\n"));
    }

    /// <summary>The text before the first table, and each table's key and text up to the next.</summary>
    private static (string Prefix, List<KeyValuePair<string, string>> Sections) Sections(string text)
    {
        var sections = new List<KeyValuePair<string, string>>();
        string prefix = text;
        int start = -1;
        string key = "";
        int pos = 0;
        while (pos <= text.Length)
        {
            int end = text.IndexOf('\n', pos);
            if (end < 0)
                end = text.Length;
            string line = text.Substring(pos, end - pos).Trim();
            if (line.StartsWith("['", StringComparison.Ordinal) && line.EndsWith("']", StringComparison.Ordinal))
            {
                if (start < 0)
                    prefix = text.Substring(0, pos);
                else
                    sections.Add(new KeyValuePair<string, string>(key, text.Substring(start, pos - start)));
                start = pos;
                key = line.Substring(2, line.Length - 4);
            }

            pos = end + 1;
        }

        if (start >= 0)
            sections.Add(new KeyValuePair<string, string>(key, text.Substring(start)));
        return (prefix.TrimEnd(' ', '\t'), sections);
    }

    /// <summary>
    /// The entries of a baseline, in the order written. Throws a <see cref="LayoutBaselineException"/> naming the
    /// line for anything else than what <see cref="Write"/> writes (with comments and other whitespace).
    /// </summary>
    public static List<LayoutEntry> Parse(string text) => new Parser(text).Read();

    private sealed class Parser
    {
        private readonly string _text;
        private int _pos, _line = 1;

        public Parser(string text) => _text = text;

        private LayoutBaselineException Error(string message) => new(message, _line);

        public List<LayoutEntry> Read()
        {
            var entries = new List<LayoutEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            LayoutEntry? entry = null;
            HashSet<string>? seen = null;
            while (true)
            {
                SkipBlank(newLines: true);
                if (_pos == _text.Length)
                    break;
                if (_text[_pos] == '[')
                {
                    if (entry != null)
                        Complete(entry, seen!);
                    _pos++;
                    SkipBlank(newLines: false);
                    entry = new LayoutEntry { Line = _line, Key = ReadString() };
                    SkipBlank(newLines: false);
                    Expect(']');
                    if (!keys.Add(entry.Key))
                        throw Error($"type '{entry.Key}' has a second table");
                    seen = new HashSet<string>(StringComparer.Ordinal);
                    entries.Add(entry);
                    EndLine();
                    continue;
                }

                if (entry == null)
                    throw Error("expected a table (['Namespace.Type']) first");
                int line = _line;
                string name = ReadBareKey();
                if (!seen!.Add(name))
                    throw Error($"'{name}' is given twice");
                SkipBlank(newLines: false);
                Expect('=');
                SkipBlank(newLines: false);
                object value = ReadValue();
                Set(entry, name, value, line);
                EndLine();
            }

            if (entry != null)
                Complete(entry, seen!);
            return entries;
        }

        private static void Complete(LayoutEntry entry, HashSet<string> seen)
        {
            foreach (string required in new[] { "format", "header", "id" })
            {
                if (!seen.Contains(required))
                    throw new LayoutBaselineException($"type '{entry.Key}' has no {required}", entry.Line);
            }
        }

        private static void Set(LayoutEntry entry, string name, object value, int line)
        {
            switch (name)
            {
                case "format":
                    entry.Format = OneOf(String(name, value, line), LayoutEntry.FormatNames, name, line);
                    break;
                case "header":
                    entry.Header = OneOf(String(name, value, line), LayoutEntry.HeaderNames, name, line);
                    break;
                case "trackReferences":
                    entry.TrackReferences = Bool(name, value, line);
                    break;
                case "raw":
                    entry.Raw = String(name, value, line);
                    break;
                case "id":
                    entry.Id = Int(name, value, line);
                    break;
                case "deferrable":
                    entry.Deferrable = Bool(name, value, line);
                    break;
                case "elements":
                    entry.Elements = String(name, value, line);
                    break;
                case "members":
                    if (value is not List<object> items)
                        throw new LayoutBaselineException("members must be an array of inline tables", line);
                    foreach (object item in items)
                        entry.Members.Add(Member(item, line));
                    break;
                default:
                    throw new LayoutBaselineException($"unknown key '{name}'", line);
            }
        }

        private static LayoutMember Member(object item, int line)
        {
            if (item is not KeyValuePair<int, Dictionary<string, object>> table)
                throw new LayoutBaselineException("members must be an array of inline tables", line);
            line = table.Key;
            var member = new LayoutMember { Line = line };
            foreach (var field in table.Value)
            {
                switch (field.Key)
                {
                    case "name":
                        member.Name = String(field.Key, field.Value, line);
                        break;
                    case "type":
                        member.Type = String(field.Key, field.Value, line);
                        break;
                    case "encoding":
                        member.Encoding = String(field.Key, field.Value, line);
                        break;
                    case "key":
                        member.Key = Int(field.Key, field.Value, line);
                        break;
                    case "former":
                        if (field.Value is not List<object> keys)
                            throw new LayoutBaselineException("former must be an array of keys", line);
                        member.Former.AddRange(keys.Select(k => Int(field.Key, k, line)));
                        break;
                    default:
                        throw new LayoutBaselineException($"unknown member key '{field.Key}'", line);
                }
            }

            if (member.Name.Length == 0 || member.Encoding.Length == 0)
                throw new LayoutBaselineException("a member needs a name and an encoding", line);
            return member;
        }

        private static string OneOf(string value, string[] names, string name, int line) =>
            Array.IndexOf(names, value) >= 0
                ? value
                : throw new LayoutBaselineException($"{name} must be {string.Join(", ", names)}, not '{value}'", line);

        private static string String(string name, object value, int line) =>
            value as string ?? throw new LayoutBaselineException($"{name} must be a string in single quotes", line);

        private static bool Bool(string name, object value, int line) =>
            value as bool? ?? throw new LayoutBaselineException($"{name} must be true or false", line);

        private static int Int(string name, object value, int line) =>
            value as int? ?? throw new LayoutBaselineException($"{name} must be an integer (0x...)", line);

        /// <summary>A string, an int, a bool, a list of values, or an inline table (its line and fields).</summary>
        private object ReadValue()
        {
            if (_pos == _text.Length)
                throw Error("expected a value");
            char c = _text[_pos];
            if (c == '\'')
                return ReadString();
            if (c == '[')
            {
                _pos++;
                var items = new List<object>();
                while (true)
                {
                    SkipBlank(newLines: true);
                    if (Peek(']'))
                        break;
                    items.Add(ReadValue());
                    SkipBlank(newLines: true);
                    if (Peek(','))
                        _pos++;
                    else if (!Peek(']'))
                        throw Error("expected ',' or ']' in an array");
                }

                _pos++;
                return items;
            }

            if (c == '{')
            {
                int line = _line;
                _pos++;
                var fields = new Dictionary<string, object>(StringComparer.Ordinal);
                while (true)
                {
                    SkipBlank(newLines: false);
                    if (Peek('}'))
                        break;
                    string name = ReadBareKey();
                    SkipBlank(newLines: false);
                    Expect('=');
                    SkipBlank(newLines: false);
                    if (fields.ContainsKey(name))
                        throw Error($"'{name}' is given twice");
                    fields[name] = ReadValue();
                    SkipBlank(newLines: false);
                    if (Peek(','))
                        _pos++;
                    else if (!Peek('}'))
                        throw Error("expected ',' or '}' in an inline table");
                }

                _pos++;
                return new KeyValuePair<int, Dictionary<string, object>>(line, fields);
            }

            int start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] is '_' or '-' or '+'))
                _pos++;
            string token = _text.Substring(start, _pos - start);
            if (token == "true")
                return true;
            if (token == "false")
                return false;
            string digits = token.Replace("_", "");
            if (LayoutEntry.TryHex(digits, out int hex))
                return hex;
            if (int.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int number))
                return number;
            throw Error(token.Length == 0
                ? $"unexpected '{c}' (strings are in single quotes)"
                : $"'{token}' is not a value (strings are in single quotes)");
        }

        private string ReadString()
        {
            if (!Peek('\''))
                throw Error("expected a string in single quotes");
            int end = _text.IndexOf('\'', _pos + 1);
            int newline = _text.IndexOf('\n', _pos + 1);
            if (end < 0 || newline >= 0 && newline < end)
                throw Error("a string in single quotes ends on its line");
            string value = _text.Substring(_pos + 1, end - _pos - 1);
            _pos = end + 1;
            return value;
        }

        private string ReadBareKey()
        {
            int start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] is '_' or '-'))
                _pos++;
            if (_pos == start)
                throw Error(_pos == _text.Length ? "expected a key" : $"expected a key, not '{_text[_pos]}'");
            return _text.Substring(start, _pos - start);
        }

        private bool Peek(char c) => _pos < _text.Length && _text[_pos] == c;

        private void Expect(char c)
        {
            if (!Peek(c))
                throw Error(_pos == _text.Length ? $"expected '{c}'" : $"expected '{c}', not '{_text[_pos]}'");
            _pos++;
        }

        /// <summary>Spaces, tabs and comments, and line breaks when <paramref name="newLines"/>.</summary>
        private void SkipBlank(bool newLines)
        {
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (c is ' ' or '\t' or '\r')
                    _pos++;
                else if (c == '#')
                {
                    while (_pos < _text.Length && _text[_pos] != '\n')
                        _pos++;
                }
                else if (c == '\n' && newLines)
                {
                    _pos++;
                    _line++;
                }
                else
                    break;
            }
        }

        private void EndLine()
        {
            SkipBlank(newLines: false);
            if (_pos == _text.Length)
                return;
            if (_text[_pos] != '\n')
                throw Error($"unexpected '{_text[_pos]}' after the value");
        }
    }
}
