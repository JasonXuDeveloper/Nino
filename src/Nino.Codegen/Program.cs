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
    /// Each file is written only when its text changes, and a manifest in the output directory (<c>.nino-gen</c>)
    /// lists them, so a file the next run no longer writes is deleted. The options are <see cref="CodegenOptions"/>;
    /// Nino.Codegen's build targets run this same entry point. See docs/codegen.md.
    /// </summary>
    public static class Program
    {
        /// <summary>The files the last run wrote into its output directory.</summary>
        internal const string Manifest = ".nino-gen";

        /// <summary>The manifest Nino.TypeScript's nino-ts wrote: read once, so its files are cleaned up too.</summary>
        private const string LegacyManifest = ".nino-ts";

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
            var options = CodegenOptions.Parse(args);
            if (options == null)
            {
                output.WriteLine(CodegenOptions.Usage);
                return 0;
            }

            using var model = CodegenModel.Load(options.Assembly, options.References.ToList());
            string source = model.AssemblyName;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> warnings;
            switch (options.Language)
            {
                case CodegenLanguage.TypeScript:
                {
                    string runtime = options.Get("runtime");
                    var target = new TypeScriptTarget(model, new TypeScriptOptions
                    {
                        ImportExtension = options.Get("import-extension"),
                        CamelCase = options.Get("names") == "camel",
                        RuntimeModule = runtime.Length == 0 ? null : runtime,
                    });
                    files[options.Get("name", source + ".ts")] = target.Render(source);
                    // The runtime is written next to the code unless the code imports it from a package.
                    if (runtime.Length == 0)
                        files["nino.ts"] = TypeScriptTarget.Runtime();
                    warnings = target.Warnings;
                    break;
                }
                case CodegenLanguage.Go:
                {
                    var target = new GoTarget(model, new GoOptions
                    {
                        Package = options.Get("package", GoPackage(source)),
                        Tags = options.Get("tags").Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                              StringSplitOptions.TrimEntries).ToList(),
                        TagNaming = Naming(options.Get("tag-names")),
                    });
                    files[options.Get("name", "nino_types.go")] = target.Render(source);
                    warnings = target.Warnings;
                    break;
                }
                default:
                {
                    var target = new RustTarget(model, new RustOptions
                    {
                        Serde = options.Get("serde") == "true",
                        SerdeNaming = Naming(options.Get("serde-names")),
                        WideStrings = options.Get("strings") == "wide",
                        StdMaps = options.Get("maps") == "std",
                    });
                    files[options.Get("name", "nino_types.rs")] = target.Render(source);
                    warnings = target.Warnings;
                    break;
                }
            }

            foreach (var warning in warnings)
                output.WriteLine($"nino-gen : warning NINOGEN002: {warning}");
            Write(options.Output, files);
            output.WriteLine($"nino-gen: {string.Join(", ", files.Keys)} -> {Path.GetFullPath(options.Output)}");
            return 0;
        }

        /// <summary>
        /// Writes the files that changed, deletes the ones the manifest lists that this run no longer writes (a renamed
        /// assembly or <c>--name</c>, <c>--runtime</c> given), then writes the manifest.
        /// </summary>
        private static void Write(string directory, Dictionary<string, string> files)
        {
            Directory.CreateDirectory(directory);
            foreach (var (file, text) in files)
                WriteIfChanged(Path.Combine(directory, file), text);
            var stale = new List<string>();
            foreach (string name in new[] { Manifest, LegacyManifest })
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                    stale.AddRange(File.ReadAllLines(path).Select(line => Path.GetFileName(line.Trim()))
                        .Where(f => f.Length != 0 && f != Manifest && !files.ContainsKey(f)));
            }

            foreach (string file in stale)
                File.Delete(Path.Combine(directory, file));
            File.Delete(Path.Combine(directory, LegacyManifest));
            WriteIfChanged(Path.Combine(directory, Manifest),
                string.Join("\n", files.Keys.OrderBy(f => f, StringComparer.Ordinal)) + "\n");
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
