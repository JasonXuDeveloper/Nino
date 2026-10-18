using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nino.TypeScript
{
    /// <summary>
    /// One encoding of a member in a published layout (<c>NinoTypeLayoutAttribute.Members</c>): what the bytes are.
    /// <see cref="Kind"/> is the name before the parentheses (<c>seq</c>, <c>raw</c>, <c>type</c>...);
    /// <see cref="Arguments"/> the encodings inside them, except for <c>raw</c>, <c>type</c>, <c>grid</c> and
    /// <c>formatter</c>, whose argument is <see cref="Text"/>. A raw struct has <see cref="Size"/> and
    /// <see cref="Fields"/>.
    /// </summary>
    internal sealed class WireEncoding
    {
        public string Kind { get; private set; } = "";
        public string Text { get; private set; } = "";
        public List<WireEncoding> Arguments { get; } = new();
        public int Size { get; private set; } = -1;
        public List<RawField> Fields { get; } = new();

        /// <summary>A field of a raw struct: <c>name:type@offset:encoding</c>.</summary>
        public sealed record RawField(string Name, string Type, int Offset, WireEncoding Encoding);

        public bool IsRaw => Kind == "raw";

        public override string ToString() => Source;

        /// <summary>The encoding as it was written.</summary>
        public string Source { get; private set; } = "";

        public static WireEncoding Parse(string text)
        {
            int pos = 0;
            var encoding = Parse(text, ref pos);
            if (pos != text.Length)
                throw new FormatException($"Unexpected '{text[pos]}' at {pos} in encoding \"{text}\".");
            return encoding;
        }

        /// <summary>Parses an encoding at <paramref name="pos"/>, which ends at a top-level , ) } or ;.</summary>
        public static WireEncoding Parse(string text, ref int pos)
        {
            int start = pos;
            var result = new WireEncoding();
            int nameEnd = pos;
            while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd])))
                nameEnd++;
            result.Kind = text.Substring(pos, nameEnd - pos);
            pos = nameEnd;
            if (result.Kind.Length == 0)
                throw new FormatException($"Expected an encoding at {pos} in \"{text}\".");

            if (pos < text.Length && text[pos] == '(')
            {
                pos++;
                switch (result.Kind)
                {
                    case "raw":
                    {
                        int close = text.IndexOf(')', pos);
                        result.Size = int.Parse(text.AsSpan(pos, close - pos), CultureInfo.InvariantCulture);
                        pos = close + 1;
                        if (pos < text.Length && text[pos] == '{')
                        {
                            pos++;
                            while (text[pos] != '}')
                            {
                                int colon = text.IndexOf(':', pos);
                                string name = text.Substring(pos, colon - pos);
                                int at = TopLevel(text, colon + 1, '@');
                                string type = text.Substring(colon + 1, at - colon - 1);
                                int colon2 = text.IndexOf(':', at);
                                int offset = int.Parse(text.AsSpan(at + 1, colon2 - at - 1),
                                    CultureInfo.InvariantCulture);
                                pos = colon2 + 1;
                                result.Fields.Add(new RawField(name, type, offset, Parse(text, ref pos)));
                                if (text[pos] == ',')
                                    pos++;
                            }

                            pos++;
                        }

                        break;
                    }
                    case "type":
                    case "formatter":
                    case "grid":
                    {
                        int close = Matching(text, pos - 1);
                        result.Text = text.Substring(pos, close - pos);
                        pos = close + 1;
                        break;
                    }
                    default:
                        while (true)
                        {
                            result.Arguments.Add(Parse(text, ref pos));
                            if (text[pos] == ')')
                                break;
                            pos++; // ','
                        }

                        pos++;
                        break;
                }
            }

            result.Source = text.Substring(start, pos - start);
            return result;
        }

        /// <summary>The ) matching the ( at <paramref name="open"/>, skipping nested brackets of any kind.</summary>
        private static int Matching(string text, int open)
        {
            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '(' or '<' or '{' or '[')
                    depth++;
                else if (c is ')' or '>' or '}' or ']' && --depth == 0)
                    return i;
            }

            throw new FormatException($"Unbalanced brackets in \"{text}\".");
        }

        /// <summary>The first <paramref name="target"/> at <paramref name="pos"/> outside generic brackets.</summary>
        private static int TopLevel(string text, int pos, char target)
        {
            int depth = 0;
            for (int i = pos; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '<')
                    depth++;
                else if (c == '>')
                    depth--;
                else if (c == target && depth == 0)
                    return i;
            }

            throw new FormatException($"Expected '{target}' in \"{text}\".");
        }

        /// <summary>Splits a layout's members on the top-level <c>;</c>.</summary>
        public static List<string> SplitMembers(string members)
        {
            var list = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < members.Length; i++)
            {
                char c = members[i];
                if (c is '(' or '<' or '{' or '[')
                    depth++;
                else if (c is ')' or '>' or '}' or ']')
                    depth--;
                else if (c == ';' && depth == 0)
                {
                    list.Add(members.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (members.Length != 0)
                list.Add(members.Substring(start));
            return list;
        }
    }
}
