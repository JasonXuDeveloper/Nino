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

    public CodeWriter(int capacity = 4096, int indent = 0)
    {
        _sb = new StringBuilder(capacity);
        _indent = indent;
    }

    /// <summary>Writes one line at the current indentation; an empty string writes a blank line.</summary>
    public CodeWriter Line(string text = "")
    {
        if (text.Length == 0)
        {
            _blank = !_opened;
            return this;
        }

        string code = text.Trim();
        if (code.Length == 0)
            return Line();
        if (_blank && code[0] != '}')
            _sb.Append('\n');
        _blank = false;
        _opened = code[code.Length - 1] == '{';
        _sb.Append(' ', _indent * 4).Append(text).Append('\n');
        return this;
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
