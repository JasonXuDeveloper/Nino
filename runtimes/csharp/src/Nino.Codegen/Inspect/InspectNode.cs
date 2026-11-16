using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Nino.Codegen.Inspect
{
    /// <summary>
    /// One part of a message as <c>nino-gen inspect</c> read it: a value, a member, an element or a header, where its
    /// bytes lie, and what they hold.
    /// </summary>
    internal sealed class InspectNode
    {
        public InspectNode(string name, int offset)
        {
            Name = name;
            Offset = offset;
        }

        /// <summary>The member's name, an element's index (<c>[0]</c>), or what the bytes are (<c>type id</c>).</summary>
        public string Name { get; }

        /// <summary>The .NET type (as the layout writes it, or its runtime-form name), or null.</summary>
        public string? Type { get; set; }

        /// <summary>The layout's encoding (<c>seq(raw(4))</c>), or null.</summary>
        public string? Encoding { get; set; }

        public int Offset { get; set; }

        /// <summary>The bytes it took; for a value the read stopped in, those read up to there.</summary>
        public int Length { get; set; }

        /// <summary>The value as text, or null when the children hold it.</summary>
        public string? Value { get; private set; }

        /// <summary>The value as a JSON token (a number, a string, <c>true</c>, <c>null</c>, an array).</summary>
        public string? Json { get; private set; }

        /// <summary>What else to know: a key, a type id's type, a deferred body, bytes skipped.</summary>
        public string? Note { get; set; }

        public List<InspectNode> Children { get; } = new();

        public InspectNode Add(string name, int offset)
        {
            var child = new InspectNode(name, offset);
            Children.Add(child);
            return child;
        }

        private string? _fullValue;

        public void Set(string text, string json, string? fullValue = null)
        {
            Value = text;
            Json = json;
            _fullValue = fullValue;
        }

        public void SetNull() => Set("null", "null");

        /// <summary>Adds to <see cref="Note"/>.</summary>
        public void AddNote(string note) => Note = Note == null ? note : Note + "; " + note;

        /// <summary>The full value with its children's, to compare two keys of a map or set.</summary>
        public string Canonical()
        {
            if (Value != null)
                return _fullValue ?? Value;
            var sb = new StringBuilder("{");
            foreach (var child in Children)
                sb.Append(child.Name).Append('=').Append(child.Canonical()).Append(';');
            return sb.Append('}').ToString();
        }

        // ---------------------------------------------------------------- text

        /// <summary>Indentation stops growing here; a deeper line shows its depth instead.</summary>
        private const int MaxIndent = 32;

        public void WriteText(TextWriter output, int depth = 0)
        {
            var line = new StringBuilder();
            line.Append(Offset.ToString(CultureInfo.InvariantCulture).PadLeft(8)).Append(' ')
                .Append(Length.ToString(CultureInfo.InvariantCulture).PadLeft(7)).Append("  ");
            if (depth <= MaxIndent)
                line.Append(' ', depth * 2);
            else
                line.Append(' ', MaxIndent * 2).Append('(').Append(depth).Append(") ");
            line.Append(Name);
            if (Type != null)
                line.Append(": ").Append(Type);
            if (Encoding != null)
            {
                // A raw struct's encoding lists its fields, which its children show.
                int fields = Encoding.IndexOf('{');
                line.Append(" [").Append(fields < 0 ? Encoding : string.Concat(Encoding.AsSpan(0, fields), "{...}"))
                    .Append(']');
            }
            if (Value != null)
                line.Append(" = ").Append(Value);
            if (Note != null)
                line.Append("  (").Append(Note).Append(')');
            output.WriteLine(line.ToString());
            foreach (var child in Children)
                child.WriteText(output, depth + 1);
        }

        // ---------------------------------------------------------------- JSON

        public void WriteJson(Utf8JsonWriter json)
        {
            json.WriteStartObject();
            json.WriteString("name", Name);
            if (Type != null)
                json.WriteString("type", Type);
            if (Encoding != null)
                json.WriteString("encoding", Encoding);
            json.WriteNumber("offset", Offset);
            json.WriteNumber("length", Length);
            if (Json != null)
            {
                json.WritePropertyName("value");
                json.WriteRawValue(Json, skipInputValidation: true);
                json.WriteString("text", Value);
            }

            if (Note != null)
                json.WriteString("note", Note);
            if (Children.Count != 0)
            {
                json.WriteStartArray("children");
                foreach (var child in Children)
                    child.WriteJson(json);
                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        /// <summary>
        /// A string as a JSON string token, every code unit kept: quotes, backslashes, control characters and
        /// surrogates (a lone one too) escaped as <c>\uXXXX</c>.
        /// </summary>
        public static string JsonString(string text)
        {
            var sb = new StringBuilder(text.Length + 2).Append('"');
            foreach (char c in text)
            {
                if (c == '"' || c == '\\')
                    sb.Append('\\').Append(c);
                else if (char.IsControl(c) || char.IsSurrogate(c))
                    sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }

            return sb.Append('"').ToString();
        }

        /// <summary>An integer as a JSON number when a double holds it exactly, else as a string (the corpus's rule).</summary>
        public static string JsonInteger(string text, bool exact) => exact ? text : "\"" + text + "\"";

        public static bool Exact(long value) => value is >= -9007199254740992L and <= 9007199254740992L;
    }
}
