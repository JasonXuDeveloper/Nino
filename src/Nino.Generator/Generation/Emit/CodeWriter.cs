using System;
using System.Text;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Builds C# source line by line, tracking indentation (four spaces per level). Blank lines separate what emitters
/// write in turn, so one is written only between two lines of the same block: never after an opening brace, before a
/// closing one or next to another blank line, whichever emitter asked for it.
/// </summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _sb;
    private int _indent;

    /// <summary>A blank line was asked for and is written before the next line, unless that line ends a block.</summary>
    private bool _blank;

    /// <summary>The last line written opened a block.</summary>
    private bool _opened;

    public CodeWriter(int capacity = 4096) => _sb = new StringBuilder(capacity);

    public CodeWriter(StringBuilder buffer) => _sb = buffer;

    /// <summary>Writes one line at the current indentation; an empty string writes a blank line.</summary>
    public CodeWriter Line(string text = "")
    {
        if (text.Length == 0)
        {
            _blank = !_opened;
            return this;
        }

        ReadOnlySpan<char> code = text.AsSpan().Trim();
        if (code.Length == 0)
            return Line();
        StartLine(code[0], code[code.Length - 1]);
        _sb.Append(text).Append('\n');
        return this;
    }

    /// <summary>Writes a nonblank registration line, compacting only the supplied code fragment.</summary>
    public void AliasedLine(string code, GeneratedCode.NamespaceAliases aliases, string prefix = "", string suffix = "")
    {
        StartLine(prefix.Length == 0 ? code[0] : prefix[0],
            suffix.Length == 0 ? code[code.Length - 1] : suffix[suffix.Length - 1]);
        _sb.Append(prefix);
        aliases.Append(_sb, code);
        _sb.Append(suffix).Append('\n');
    }

    /// <summary>Writes an attribute and its optional comment without assembling a temporary line.</summary>
    public void AttributeLine(string name, string arguments, string comment)
    {
        StartLine('[', ']');
        _sb.Append('[').Append(name).Append('(').Append(arguments).Append(")]");
        if (comment.Length != 0)
            _sb.Append(" // ").Append(comment);
        _sb.Append('\n');
    }

    /// <summary>Writes a numbered static forwarder without materializing its signature fragments.</summary>
    public void NumberedForwarder(string signature, int number, string target, string call)
    {
        int parameters = signature.IndexOf('(');
        StartLine('p', ';');
        _sb.Append("public static ").Append(signature, 0, parameters).Append(number)
            .Append(signature, parameters, signature.Length - parameters).Append(" => ").Append(target)
            .Append(call).Append(";\n");
    }

    private void StartLine(char first, char last)
    {
        if (_blank && first != '}')
            _sb.Append('\n');
        _blank = false;
        _opened = last == '{';
        _sb.Append(' ', _indent * 4);
    }

    /// <summary>
    /// Writes each line of <paramref name="text"/> (a raw string literal: its lines indented relative to each other)
    /// at the current indentation, an empty one as a blank line.
    /// </summary>
    public CodeWriter Lines(string text)
    {
        foreach (var line in text.Split('\n'))
            Line(line.TrimEnd('\r'));
        return this;
    }

    /// <summary>Writes <paramref name="header"/> (when given) and an opening brace, then indents.</summary>
    public CodeWriter Open(string? header = null)
    {
        if (header != null)
            Line(header);
        Line("{");
        _indent++;
        return this;
    }

    /// <summary>Unindents and writes a closing brace followed by <paramref name="suffix"/>.</summary>
    public CodeWriter Close(string suffix = "")
    {
        _indent--;
        return Line("}" + suffix);
    }

    /// <summary>Writes a single line one indentation level deeper.</summary>
    public void IndentedLine(string text)
    {
        _indent++;
        Line(text);
        _indent--;
    }

    /// <summary>Indents the lines written by <paramref name="body"/> by one level.</summary>
    public CodeWriter Indented(Action body)
    {
        _indent++;
        body();
        _indent--;
        return this;
    }

    /// <summary><paramref name="header"/> { <paramref name="body"/> }.</summary>
    public CodeWriter Block(string header, Action body)
    {
        Open(header);
        body();
        return Close();
    }

    /// <summary>The source written, ending with the blank line asked for last (one that separates it from what follows).</summary>
    public override string ToString() => _blank ? _sb.ToString() + "\n" : _sb.ToString();
}
