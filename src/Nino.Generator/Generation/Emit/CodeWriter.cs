using System;
using System.Text;

namespace Nino.Generator.Generation.Emit;

/// <summary>Builds C# source line by line, tracking indentation (four spaces per level).</summary>
internal sealed class CodeWriter
{
    private readonly StringBuilder _sb;
    private int _indent;

    public CodeWriter(int capacity = 4096, int indent = 0)
    {
        _sb = new StringBuilder(capacity);
        _indent = indent;
    }

    /// <summary>Writes one line at the current indentation; an empty string writes a blank line.</summary>
    public CodeWriter Line(string text = "")
    {
        if (text.Length != 0)
            _sb.Append(' ', _indent * 4).Append(text);
        _sb.Append('\n');
        return this;
    }

    /// <summary>Writes <paramref name="text"/> as is (it carries its own line breaks and indentation).</summary>
    public CodeWriter Raw(string text)
    {
        _sb.Append(text);
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

    public override string ToString() => _sb.ToString();
}
