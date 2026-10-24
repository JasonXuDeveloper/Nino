using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nino.Codegen.Model;
using Nino.Codegen.Targets;

namespace Nino.Codegen
{
    /// <summary>
    /// <c>nino-gen --assembly &lt;dll&gt; --lang ts|go|rust --out &lt;dir&gt; [options]</c>: writes the types and codecs of
    /// the assembly's <c>[NinoType]</c>s in another language, from the layouts the Nino generator published in it.
    /// Each file is written only when its text changes. See docs/codegen.md.
    /// </summary>
    public static class Program
    {
        private const string Usage =
            "Usage: nino-gen --assembly <dll> --lang ts|go|rust --out <dir> [--references <file>] [--name <file name>]\n" +
            "  ts:   [--names camel|keep] [--import-extension <.js|.ts|.mjs|.mts>] [--runtime <module>]\n" +
            "  go:   [--package <name>] [--tags json,yaml] [--tag-names camel|snake|original]\n" +
            "  rust: [--serde] [--serde-names camel|snake|original] [--strings string|wide]";

        public static int Main(string[] args)
        {
            try
            {
                return Run(args, Console.Out);
            }
            catch (Exception e) when (e is ArgumentException or IOException or BadImageFormatException
                                          or FormatException)
            {
                Console.Error.WriteLine($"nino-gen : error NINOGEN001: {e.Message}");
                return 1;
            }
        }

        /// <summary>Runs the tool; warnings and the summary go to <paramref name="output"/>.</summary>
        public static int Run(string[] args, TextWriter output)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unexpected argument \"{args[i]}\".\n{Usage}");
                string name = args[i].Substring(2);
                // Flags without a value.
                if (name is "serde" or "help")
                {
                    options[name] = "true";
                    continue;
                }

                if (i + 1 == args.Length)
                    throw new ArgumentException($"{args[i]} needs a value.\n{Usage}");
                options[name] = args[++i];
            }

            if (options.ContainsKey("help"))
            {
                output.WriteLine(Usage);
                return 0;
            }

            if (!options.TryGetValue("assembly", out var assembly) || !options.TryGetValue("out", out var outDir) ||
                !options.TryGetValue("lang", out var lang))
                throw new ArgumentException($"--assembly, --lang and --out are required.\n{Usage}");
            var references = options.TryGetValue("references", out var list)
                ? File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length != 0).ToList()
                : new List<string>();
            string Option(string key, string fallback, params string[] allowed)
            {
                string value = options.TryGetValue(key, out var v) ? v : fallback;
                if (allowed.Length != 0 && !allowed.Contains(value))
                    throw new ArgumentException($"--{key} must be {string.Join(", ", allowed)}, not \"{value}\".");
                return value;
            }

            using var model = CodegenModel.Load(assembly, references);
            string source = model.AssemblyName;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> warnings;
            switch (lang)
            {
                case "ts":
                case "typescript":
                {
                    string runtime = Option("runtime", "");
                    var target = new TypeScriptTarget(model, new TypeScriptOptions
                    {
                        ImportExtension = Option("import-extension", "", "", ".js", ".ts", ".mjs", ".mts"),
                        CamelCase = Option("names", "camel", "camel", "keep") == "camel",
                        RuntimeModule = runtime.Length == 0 ? null : runtime,
                        Generator = "nino-gen",
                    });
                    files[Option("name", source + ".ts")] = target.Render(source);
                    // The runtime is written next to the code unless the code imports it from a package.
                    if (runtime.Length == 0)
                        files["nino.ts"] = TypeScriptTarget.Runtime();
                    warnings = target.Warnings;
                    break;
                }
                case "go":
                {
                    string package = Option("package", GoPackage(source));
                    var target = new GoTarget(model, new GoOptions
                    {
                        Package = package,
                        Tags = Option("tags", "json,yaml").Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                                         StringSplitOptions.TrimEntries).ToList(),
                        TagNaming = Naming(Option("tag-names", "camel", "camel", "snake", "original")),
                    });
                    files[Option("name", "nino_types.go")] = target.Render(source);
                    warnings = target.Warnings;
                    break;
                }
                case "rust":
                {
                    var target = new RustTarget(model, new RustOptions
                    {
                        Serde = options.ContainsKey("serde"),
                        SerdeNaming = Naming(Option("serde-names", "camel", "camel", "snake", "original")),
                        WideStrings = Option("strings", "string", "string", "wide") == "wide",
                    });
                    files[Option("name", "nino_types.rs")] = target.Render(source);
                    warnings = target.Warnings;
                    break;
                }
                default:
                    throw new ArgumentException($"--lang must be ts, go or rust, not \"{lang}\".");
            }

            foreach (var warning in warnings)
                output.WriteLine($"nino-gen : warning NINOGEN002: {warning}");
            Directory.CreateDirectory(outDir);
            foreach (var (file, text) in files)
                WriteIfChanged(Path.Combine(outDir, file), text);
            output.WriteLine($"nino-gen: {string.Join(", ", files.Keys)} -> {Path.GetFullPath(outDir)}");
            return 0;
        }

        private static TagNaming Naming(string name) => name switch
        {
            "snake" => TagNaming.Snake,
            "original" => TagNaming.Original,
            _ => TagNaming.Camel,
        };

        /// <summary>A Go package name from an assembly name: its letters and digits, lower-cased.</summary>
        private static string GoPackage(string assembly)
        {
            string name = new string(assembly.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            return name.Length == 0 || char.IsDigit(name[0]) ? "nino" + name : name;
        }

        /// <summary>Leaves an unchanged file alone, so watchers and incremental builds see no change.</summary>
        public static void WriteIfChanged(string path, string text)
        {
            text = text.Replace("\r\n", "\n");
            if (File.Exists(path) && File.ReadAllText(path) == text)
                return;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
