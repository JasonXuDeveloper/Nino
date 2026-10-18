using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Nino.TypeScript
{
    /// <summary>
    /// <c>nino-ts --assembly &lt;dll&gt; --out &lt;dir&gt; [--references &lt;file&gt;] [--import-extension &lt;.js|.ts&gt;]
    /// [--names camel|keep]</c>: writes <c>nino.ts</c> and <c>&lt;Assembly&gt;.ts</c> into the output directory, each
    /// only when its text changes. Run after each build by <c>build/Nino.TypeScript.targets</c>.
    /// </summary>
    internal static class Program
    {
        /// <summary>The files the last run wrote, so a file it no longer writes (a renamed assembly) is deleted.</summary>
        private const string Manifest = ".nino-ts";

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception e) when (e is ArgumentException or IOException or BadImageFormatException
                                          or FormatException)
            {
                Console.Error.WriteLine($"nino-ts : error NINOTS001: {e.Message}");
                return 1;
            }
        }

        internal static int Run(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length)
                    throw new ArgumentException($"Unexpected argument \"{args[i]}\". " +
                                                "Usage: nino-ts --assembly <dll> --out <dir> [--references <file>] " +
                                                "[--import-extension <.js|.ts>] [--names camel|keep]");
                options[args[i].Substring(2)] = args[++i];
            }

            if (!options.TryGetValue("assembly", out var assembly) || !options.TryGetValue("out", out var output))
                throw new ArgumentException("--assembly and --out are required.");
            string extension = options.TryGetValue("import-extension", out var ext) ? ext : "";
            if (extension is not ("" or ".js" or ".ts" or ".mjs" or ".mts"))
                throw new ArgumentException($"--import-extension must be empty, .js, .ts, .mjs or .mts, not \"{extension}\".");
            string names = options.TryGetValue("names", out var n) ? n : "camel";
            if (names is not ("camel" or "keep"))
                throw new ArgumentException($"--names must be camel or keep, not \"{names}\".");
            // One reference path per line (MSBuild's @(ReferencePath)).
            var references = options.TryGetValue("references", out var list)
                ? File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length != 0).ToList()
                : new List<string>();

            using var reader = new LayoutReader(assembly, references);
            string name = reader.Assembly.GetName().Name!;
            var emitter = new TypeScriptEmitter(reader.ReadAll(),
                new TypeScriptOptions { ImportExtension = extension, CamelCase = names == "camel" });
            var files = new Dictionary<string, string>
            {
                ["nino.ts"] = Runtime(),
                [name + ".ts"] = emitter.Emit(name),
            };
            foreach (var warning in emitter.Warnings)
                Console.WriteLine($"nino-ts : warning NINOTS002: {warning}");

            Directory.CreateDirectory(output);
            foreach (var (file, text) in files)
                WriteIfChanged(Path.Combine(output, file), text);
            string manifest = Path.Combine(output, Manifest);
            if (File.Exists(manifest))
            {
                foreach (var stale in File.ReadAllLines(manifest).Where(f => f.Length != 0 && !files.ContainsKey(f)))
                    File.Delete(Path.Combine(output, Path.GetFileName(stale)));
            }

            WriteIfChanged(manifest, string.Join("\n", files.Keys.OrderBy(f => f, StringComparer.Ordinal)) + "\n");
            Console.WriteLine($"nino-ts: {name}.ts and nino.ts -> {Path.GetFullPath(output)}");
            return 0;
        }

        /// <summary>The runtime, stamped with this tool's version.</summary>
        private static string Runtime()
        {
            using var stream = typeof(Program).Assembly.GetManifestResourceStream("nino.ts")!;
            using var text = new StreamReader(stream, Encoding.UTF8);
            string version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "";
            int plus = version.IndexOf('+');
            return text.ReadToEnd().Replace("__NINO_VERSION__", plus < 0 ? version : version.Substring(0, plus));
        }

        /// <summary>Leaves an unchanged file alone, so watchers and incremental TypeScript builds see no change.</summary>
        private static void WriteIfChanged(string path, string text)
        {
            text = text.Replace("\r\n", "\n");
            if (File.Exists(path) && File.ReadAllText(path) == text)
                return;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
