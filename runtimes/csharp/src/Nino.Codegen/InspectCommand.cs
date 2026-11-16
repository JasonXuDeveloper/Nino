using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Nino.Codegen.Inspect;
using Nino.Codegen.Model;

namespace Nino.Codegen
{
    /// <summary>
    /// <c>nino-gen inspect --assembly &lt;dll&gt; --type &lt;name&gt; [--references &lt;file&gt;] [--hex] [--offset &lt;n&gt;]
    /// [--json] [&lt;file&gt;]</c>: reads one message (a file, or standard input) with the layouts the assembly publishes
    /// (<see cref="CodegenModel"/>, the IR the targets write code from) and prints what each byte is: a tree of the
    /// values, members, elements, headers, keyed entries, type ids, references and deferred bodies, with their offsets
    /// and lengths. Malformed data prints what was read, then the offset and the check that failed (exit code 2). See
    /// docs/codegen.md.
    /// </summary>
    internal static class InspectCommand
    {
        public const string Name = "inspect";

        public const string Usage =
            "Usage: nino-gen inspect --assembly <dll> --type <name> [--references <file>] [--hex] [--offset <n>] " +
            "[--json] [<file>|-]";

        /// <summary>The exit code for data that is not what Nino writes.</summary>
        public const int Malformed = 2;

        /// <summary>The stack the read runs on: deep data nests its tree as deep (bounded by the depth limits).</summary>
        private const int StackSize = 256 * 1024 * 1024;

        public static int Run(IReadOnlyList<string> args, TextWriter output, Func<Stream> standardInput)
        {
            string? assembly = null, typeName = null, referencesFile = null, file = null;
            bool hex = false, json = false;
            int offset = 0;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--help":
                        output.WriteLine(Usage);
                        return 0;
                    case "--hex":
                        hex = true;
                        continue;
                    case "--json":
                        json = true;
                        continue;
                    case "--assembly" or "--type" or "--references" or "--offset":
                        if (i + 1 == args.Count)
                            throw new ArgumentException($"{arg} needs a value.\n{Usage}");
                        string value = args[++i];
                        switch (arg)
                        {
                            case "--assembly": assembly = value; break;
                            case "--type": typeName = value; break;
                            case "--references": referencesFile = value; break;
                            default:
                                if (!int.TryParse(value, out offset) || offset < 0)
                                    throw new ArgumentException($"--offset must be a byte offset, not \"{value}\".");
                                break;
                        }

                        continue;
                }

                if (arg.StartsWith("--", StringComparison.Ordinal) || file != null)
                    throw new ArgumentException($"Unexpected argument \"{arg}\".\n{Usage}");
                file = arg;
            }

            if (assembly == null || typeName == null)
                throw new ArgumentException($"--assembly and --type are required.\n{Usage}");
            byte[] data = ReadInput(file, hex, standardInput);
            if (offset > data.Length)
                throw new ArgumentException($"--offset {offset} is past the end of the {data.Length} byte(s) given.");
            var references = referencesFile == null
                ? new List<string>()
                : File.ReadAllLines(referencesFile).Select(l => l.Trim()).Where(l => l.Length != 0).ToList();

            using var model = CodegenModel.Load(assembly, references);
            var type = model.ResolveType(typeName) ?? throw new ArgumentException(
                $"--type {typeName}: no such type in {Path.GetFileName(assembly)} or the assemblies it references " +
                "(write its runtime-form name: Namespace.Outer+Inner, Namespace.Box<System.Int32>).");
            var root = model.RootValue(type) ?? throw new ArgumentException(
                $"--type {typeName}: Nino has no formatter nino-gen knows for it.");

            var (text, failed) = OnLargeStack(() =>
            {
                var result = Inspector.Read(model, root, type, data, offset);
                return (json ? Json(result, typeName, data.Length, offset) : Text(result, typeName, data.Length, offset),
                    result.Error != null);
            });
            output.Write(text);
            return failed ? Malformed : 0;
        }

        /// <summary>Runs <paramref name="run"/> on a thread with a stack of <see cref="StackSize"/>.</summary>
        internal static T OnLargeStack<T>(Func<T> run)
        {
            T value = default!;
            ExceptionDispatchInfo? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    value = run();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
            }, StackSize);
            thread.Start();
            thread.Join();
            failure?.Throw();
            return value;
        }

        /// <summary>The input: a file, or standard input when none is named or it is <c>-</c>; hex text with <c>--hex</c>.</summary>
        private static byte[] ReadInput(string? file, bool hex, Func<Stream> standardInput)
        {
            byte[] bytes;
            if (file == null || file == "-")
            {
                using var input = standardInput();
                using var copy = new MemoryStream();
                input.CopyTo(copy);
                bytes = copy.ToArray();
            }
            else
                bytes = File.ReadAllBytes(file);

            return hex ? FromHex(Encoding.UTF8.GetString(bytes)) : bytes;
        }

        /// <summary>Hex digits, in pairs; whitespace, <c>,</c> <c>:</c> <c>-</c> and <c>0x</c> prefixes between them are ignored.</summary>
        internal static byte[] FromHex(string text)
        {
            var digits = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '0' && i + 1 < text.Length && text[i + 1] is 'x' or 'X')
                    i++;
                else if (Uri.IsHexDigit(c))
                    digits.Append(c);
                else if (!char.IsWhiteSpace(c) && c is not (',' or ':' or '-'))
                    throw new ArgumentException($"--hex: '{c}' at {i} is not a hex digit.");
            }

            if (digits.Length % 2 != 0)
                throw new ArgumentException($"--hex: an odd number of hex digits ({digits.Length}).");
            return Convert.FromHexString(digits.ToString());
        }

        private static string Text(InspectResult result, string type, int size, int offset)
        {
            var output = new StringWriter { NewLine = "\n" };
            output.WriteLine($"nino-gen inspect: {type}, {size - offset} byte(s) from offset {offset}");
            output.WriteLine("  offset  length  value");
            result.Root.WriteText(output);
            if (Footer(result, size) is { } footer)
                output.WriteLine("nino-gen: " + footer);
            return output.ToString();
        }

        /// <summary>The outcome: the error, or the bytes after the value; null when the value took every byte.</summary>
        private static string? Footer(InspectResult result, int size) =>
            result.Error != null ? $"malformed data at offset {result.Error.Offset}: {result.Error.Message}"
            : result.End < size ? $"{size - result.End} byte(s) after the value, from offset {result.End}, not read " +
                                  "(a reader in untrusted mode rejects them)"
            : null;

        private static string Json(InspectResult result, string type, int size, int offset)
        {
            using var stream = new MemoryStream();
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
                   {
                       Indented = true, SkipValidation = true, MaxDepth = int.MaxValue,
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
            {
                json.WriteStartObject();
                json.WriteString("type", type);
                json.WriteNumber("offset", offset);
                json.WriteNumber("size", size - offset);
                json.WriteNumber("end", result.End);
                json.WriteNumber("trailing", result.Error == null ? size - result.End : 0);
                if (result.Error == null)
                    json.WriteNull("error");
                else
                {
                    json.WriteStartObject("error");
                    json.WriteNumber("offset", result.Error.Offset);
                    json.WriteString("message", result.Error.Message);
                    json.WriteEndObject();
                }

                json.WritePropertyName("value");
                result.Root.WriteJson(json);
                json.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        }
    }
}
