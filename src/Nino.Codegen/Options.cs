using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nino.Codegen
{
    /// <summary>The languages nino-gen writes.</summary>
    public enum CodegenLanguage
    {
        TypeScript,
        Go,
        Rust,
    }

    /// <summary>
    /// One option of nino-gen: its name on the command line (<c>--name</c>), the metadata of a <c>&lt;NinoCodegen&gt;</c>
    /// item that sets it in a build (build/Nino.Codegen.targets passes it as that option), the language it belongs to,
    /// and the values it takes. <see cref="CodegenOptions.All"/> is the one list of them, read by the command line, its
    /// usage text and the test that checks the targets.
    /// </summary>
    public sealed class CodegenOption
    {
        internal CodegenOption(string name, string? metadata, CodegenLanguage? language, string usage,
            string? fallback, string[]? values = null, bool flag = false)
        {
            Name = name;
            Metadata = metadata;
            Language = language;
            Usage = usage;
            Default = fallback;
            Values = values ?? Array.Empty<string>();
            Flag = flag;
        }

        /// <summary>The name on the command line, without the leading <c>--</c>.</summary>
        public string Name { get; }

        /// <summary>
        /// The metadata of a <c>&lt;NinoCodegen&gt;</c> item that sets it; null for the options the build sets itself
        /// (the assembly and its references). The language is the item's <c>Include</c>.
        /// </summary>
        public string? Metadata { get; }

        /// <summary>The language the option belongs to; null for the options every language takes.</summary>
        public CodegenLanguage? Language { get; }

        /// <summary>How the usage text shows its value.</summary>
        public string Usage { get; }

        /// <summary>The value when it is not given; null when there is none, or it depends on the assembly.</summary>
        public string? Default { get; }

        /// <summary>The values it takes; empty for any.</summary>
        public IReadOnlyList<string> Values { get; }

        /// <summary>A switch: given alone (<c>--serde</c>) it means true; it may be followed by true or false.</summary>
        public bool Flag { get; }
    }

    /// <summary>The options of nino-gen, and the command line parsed against them.</summary>
    public static class CodegenOptions
    {
        /// <summary>Every option, in the order of the usage text.</summary>
        public static IReadOnlyList<CodegenOption> All { get; } = new[]
        {
            new CodegenOption("assembly", null, null, "<dll>", null),
            new CodegenOption("lang", null, null, "ts|go|rust", null),
            new CodegenOption("out", "Out", null, "<dir>", null),
            new CodegenOption("references", null, null, "<file>", null),
            new CodegenOption("name", "Name", null, "<file name>", null),
            new CodegenOption("names", "Names", CodegenLanguage.TypeScript, "camel|keep", "camel",
                new[] { "camel", "keep" }),
            new CodegenOption("import-extension", "ImportExtension", CodegenLanguage.TypeScript,
                "<.js|.ts|.mjs|.mts>", "", new[] { "", ".js", ".ts", ".mjs", ".mts" }),
            new CodegenOption("runtime", "Runtime", CodegenLanguage.TypeScript, "<module>", ""),
            new CodegenOption("numeric-arrays", "NumericArrays", CodegenLanguage.TypeScript, "array|typed", "typed",
                new[] { "array", "typed" }),
            new CodegenOption("package", "Package", CodegenLanguage.Go, "<name>", null),
            new CodegenOption("tags", "Tags", CodegenLanguage.Go, "json,yaml", "json,yaml"),
            new CodegenOption("tag-names", "TagNames", CodegenLanguage.Go, "camel|snake|original", "camel",
                new[] { "camel", "snake", "original" }),
            new CodegenOption("serde", "Serde", CodegenLanguage.Rust, "", "false", new[] { "true", "false" },
                flag: true),
            new CodegenOption("serde-names", "SerdeNames", CodegenLanguage.Rust, "camel|snake|original", "camel",
                new[] { "camel", "snake", "original" }),
            new CodegenOption("strings", "Strings", CodegenLanguage.Rust, "string|wide", "string",
                new[] { "string", "wide" }),
            new CodegenOption("maps", "Maps", CodegenLanguage.Rust, "indexmap|std", "indexmap",
                new[] { "indexmap", "std" }),
        };

        /// <summary>The usage text, from <see cref="All"/>.</summary>
        public static string Usage
        {
            get
            {
                static string Show(CodegenOption o) => o.Flag ? $"[--{o.Name}]" : $"[--{o.Name} {o.Usage}]";
                var lines = new List<string>
                {
                    "Usage: nino-gen --assembly <dll> --lang ts|go|rust --out <dir> " +
                    string.Join(" ", All.Where(o => o.Language == null && o.Name is not ("assembly" or "lang" or "out"))
                        .Select(Show)),
                };
                foreach (var (language, label) in new[]
                         {
                             (CodegenLanguage.TypeScript, "ts:  "), (CodegenLanguage.Go, "go:  "),
                             (CodegenLanguage.Rust, "rust:"),
                         })
                    lines.Add($"  {label} " + string.Join(" ", All.Where(o => o.Language == language).Select(Show)));
                return string.Join("\n", lines);
            }
        }

        /// <summary>The language a <c>--lang</c> value names: <c>ts</c> or <c>typescript</c>, <c>go</c>, <c>rust</c>.</summary>
        public static CodegenLanguage Language(string name) => name switch
        {
            "ts" or "typescript" => CodegenLanguage.TypeScript,
            "go" => CodegenLanguage.Go,
            "rust" => CodegenLanguage.Rust,
            _ => throw new ArgumentException($"--lang must be ts, go or rust, not \"{name}\"."),
        };

        /// <summary>
        /// Parses a command line: every option once, known, of the language given, with one of its values. Throws an
        /// <see cref="ArgumentException"/> naming what is wrong. <c>--help</c> alone returns null.
        /// </summary>
        public static CodegenArguments? Parse(IReadOnlyList<string> args)
        {
            var given = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg == "--help")
                    return null;
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unexpected argument \"{arg}\".\n{Usage}");
                string name = arg.Substring(2);
                var option = All.FirstOrDefault(o => o.Name == name) ??
                             throw new ArgumentException($"Unknown option {arg}.\n{Usage}");
                if (given.ContainsKey(name))
                    throw new ArgumentException($"{arg} is given twice.");
                if (option.Flag)
                {
                    // A switch, or a switch and its value (true or false), as a build passes it.
                    given[name] = i + 1 < args.Count && args[i + 1] is "true" or "false" ? args[++i] : "true";
                    continue;
                }

                if (i + 1 == args.Count)
                    throw new ArgumentException($"{arg} needs a value.\n{Usage}");
                given[name] = args[++i];
            }

            if (!given.TryGetValue("assembly", out var assembly) || !given.TryGetValue("out", out var output) ||
                !given.TryGetValue("lang", out var lang))
                throw new ArgumentException($"--assembly, --lang and --out are required.\n{Usage}");
            var language = Language(lang);
            foreach (var (name, value) in given)
            {
                var option = All.First(o => o.Name == name);
                if (option.Language != null && option.Language != language)
                    throw new ArgumentException(
                        $"--{name} is an option of {Label(option.Language.Value)}, not of {Label(language)}.");
                if (option.Values.Count != 0 && !option.Values.Contains(value))
                    throw new ArgumentException(
                        $"--{name} must be {string.Join(", ", option.Values.Select(v => v.Length == 0 ? "empty" : v))}, not \"{value}\".");
            }

            var references = given.TryGetValue("references", out var list)
                ? File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length != 0).ToList()
                : new List<string>();
            return new CodegenArguments(assembly, language, output, references, given);
        }

        private static string Label(CodegenLanguage language) => language switch
        {
            CodegenLanguage.TypeScript => "ts",
            CodegenLanguage.Go => "go",
            _ => "rust",
        };
    }

    /// <summary>A parsed command line.</summary>
    public sealed class CodegenArguments
    {
        private readonly Dictionary<string, string> _given;

        internal CodegenArguments(string assembly, CodegenLanguage language, string output, List<string> references,
            Dictionary<string, string> given)
        {
            Assembly = assembly;
            Language = language;
            Output = output;
            References = references;
            _given = given;
        }

        public string Assembly { get; }
        public CodegenLanguage Language { get; }
        public string Output { get; }
        public IReadOnlyList<string> References { get; }

        /// <summary>The value of an option: as given, else its default, else <paramref name="fallback"/>.</summary>
        public string Get(string name, string? fallback = null)
        {
            if (_given.TryGetValue(name, out var value))
                return value;
            return CodegenOptions.All.First(o => o.Name == name).Default ?? fallback ??
                throw new InvalidOperationException($"--{name} has no default.");
        }

        /// <summary>Whether the option was given.</summary>
        public bool Has(string name) => _given.ContainsKey(name);
    }
}
