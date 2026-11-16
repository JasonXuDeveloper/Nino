using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nino.Codegen.Layouts;
using Nino.Codegen.Model;
using Nino.Generator.Common;

namespace Nino.Codegen
{
    /// <summary>
    /// <c>nino-gen layouts --assembly &lt;dll&gt; --out &lt;NinoLayouts.cs&gt; [--check] [--references &lt;file&gt;]</c>:
    /// writes the layout baseline of a built assembly (<c>[assembly: NinoLayouts]</c>, the file the analyzer's code
    /// fixes keep), or with <c>--check</c> compares the assembly with the baseline in that file and exits with 1 when a
    /// change breaks data (NINO034) or loses it silently (NINO035): the analyzer's rules (<see cref="LayoutRules"/>,
    /// the same source), for builds without an IDE (CI, Unity batch mode). See docs/codegen.md.
    /// </summary>
    internal static class LayoutsCommand
    {
        public const string Name = "layouts";

        public const string Usage =
            "Usage: nino-gen layouts --assembly <dll> --out <NinoLayouts.cs> [--check] [--references <file>]";

        public static int Run(IReadOnlyList<string> args, TextWriter output)
        {
            string? assembly = null, file = null, referencesFile = null;
            bool check = false;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (arg == "--help")
                {
                    output.WriteLine(Usage);
                    return 0;
                }

                if (arg == "--check")
                {
                    check = true;
                    continue;
                }

                if (arg is not ("--assembly" or "--out" or "--references"))
                    throw new ArgumentException($"Unexpected argument \"{arg}\".\n{Usage}");
                if (i + 1 == args.Count)
                    throw new ArgumentException($"{arg} needs a value.\n{Usage}");
                string value = args[++i];
                switch (arg)
                {
                    case "--assembly": assembly = value; break;
                    case "--out": file = value; break;
                    default: referencesFile = value; break;
                }
            }

            if (assembly == null || file == null)
                throw new ArgumentException($"--assembly and --out are required.\n{Usage}");
            var references = referencesFile == null
                ? new List<string>()
                : File.ReadAllLines(referencesFile).Select(l => l.Trim()).Where(l => l.Length != 0).ToList();

            using var reader = new LayoutReader(assembly, references);
            var layouts = Read(reader);
            if (!check)
            {
                var scope = LayoutRules.Scope(layouts.Values.Select(l => l.Entry).ToList(),
                    entry => layouts[entry.Key].Ancestors);
                var entries = layouts.Values.Select(l => l.Entry).Where(e => scope.Contains(e.Key)).ToList();
                Program.WriteIfChanged(file, File.Exists(file) && Literal(File.ReadAllText(file)) is { } found
                    ? Replace(File.ReadAllText(file), found, LayoutBaseline.Write(entries))
                    : LayoutBaseline.File(entries));
                output.WriteLine($"nino-gen: layout baseline of {entries.Count} type(s) -> {Path.GetFullPath(file)}");
                return 0;
            }

            if (!File.Exists(file))
                throw new ArgumentException($"{file} does not exist: write it first with nino-gen layouts without --check.");
            return Check(file, layouts, output);
        }

        /// <summary>A layout of the assembly as a baseline entry, with the keys of its [NinoType] ancestors.</summary>
        private sealed class Layout
        {
            public LayoutEntry Entry = null!;
            public List<string> Ancestors = new();
        }

        /// <summary>The layouts of the assembly's own types (not its references'), by key.</summary>
        private static Dictionary<string, Layout> Read(LayoutReader reader)
        {
            var own = reader.ReadAll().Values.Where(l => l.Type.Assembly == reader.Assembly).ToList();
            var layouts = new Dictionary<string, Layout>(StringComparer.Ordinal);
            foreach (var layout in own)
            {
                string key = TypeNames.Runtime(layout.Type);
                layouts[key] = new Layout
                {
                    Entry = LayoutEntry.FromLayout(key, layout.Format, layout.Header, layout.TrackReferences,
                        layout.Raw, layout.Id, layout.Deferrable, layout.MembersText),
                };
            }

            foreach (var layout in own)
            {
                var ancestors = new List<Type>();
                for (var b = layout.Type.BaseType; b != null; b = b.BaseType)
                    ancestors.Add(b);
                ancestors.AddRange(layout.Type.GetInterfaces());
                layouts[TypeNames.Runtime(layout.Type)].Ancestors.AddRange(ancestors
                    .Select(a => TypeNames.Runtime(a.IsGenericType ? a.GetGenericTypeDefinition() : a))
                    .Where(layouts.ContainsKey));
            }

            return layouts;
        }

        /// <summary>Prints every finding of the baseline in <paramref name="file"/> against the layouts; 1 when one breaks data.</summary>
        private static int Check(string file, Dictionary<string, Layout> layouts, TextWriter output)
        {
            string source = File.ReadAllText(file);
            var literal = Literal(source) ??
                          throw new ArgumentException($"{file} has no [assembly: NinoLayouts(@\"...\")].");
            int firstLine = source.Take(literal.Start).Count(c => c == '\n') + 1;
            List<LayoutEntry> baseline;
            try
            {
                baseline = LayoutBaseline.Parse(literal.Text);
            }
            catch (LayoutBaselineException e)
            {
                output.WriteLine($"{file}({firstLine + e.Line - 1}): error NINO037: The layout baseline cannot be read: {e.Message}");
                return 1;
            }

            var byKey = baseline.ToDictionary(e => e.Key, StringComparer.Ordinal);
            var missing = baseline.Where(e => !layouts.ContainsKey(e.Key)).ToList();
            var pairs = LayoutRules.Pair(missing, layouts.Values.Where(l => !byKey.ContainsKey(l.Entry.Key))
                .Select(l => new KeyValuePair<string, int>(l.Entry.Key, l.Entry.Id)));
            var current = layouts.Values.ToDictionary(l => l.Entry.Key, l => l.Entry.Key, StringComparer.Ordinal);
            foreach (var pair in pairs)
                current[pair.Value.Key] = pair.Key;

            bool SameType(string old, string now)
            {
                var olds = baseline.Where(e => LayoutRules.Dotted(e.Key) == old).ToList();
                var nows = layouts.Keys.Where(k => LayoutRules.Dotted(k) == now).ToList();
                return olds.Count == 1 && nows.Count == 1 && current.TryGetValue(olds[0].Key, out var key) &&
                       key == nows[0];
            }

            int breaking = 0, total = 0;
            foreach (var old in baseline)
            {
                IEnumerable<LayoutFinding> findings;
                string subject = old.Key;
                if (!current.TryGetValue(old.Key, out var key) || !layouts.TryGetValue(key, out var layout))
                    findings = new[] { LayoutRules.Removed(old) };
                else
                {
                    var now = layout.Entry;
                    subject = now.Key;
                    findings = LayoutRules.Compare(old, now,
                        now.Header == LayoutEntry.HeaderNames[2] || layout.Ancestors.Count != 0, SameType,
                        name => baseline.Any(e => e.Raw != null && LayoutRules.Dotted(e.Key) == name));
                }

                foreach (var finding in findings)
                {
                    total++;
                    if (finding.Severity != LayoutSeverity.Info)
                        breaking++;
                    int line = old.Members.FirstOrDefault(m => m.Name == finding.Member)?.Line ?? old.Line;
                    string id = finding.Severity switch
                    {
                        LayoutSeverity.Breaking => "warning NINO034",
                        LayoutSeverity.SilentLoss => "warning NINO035",
                        _ => "info NINO036",
                    };
                    string what = finding.Member == null ? subject : subject + "." + finding.Member;
                    output.WriteLine($"{file}({firstLine + line - 1}): {id}: '{what}' {finding.Change}; {finding.Fix}");
                }
            }

            output.WriteLine(total == 0
                ? $"nino-gen: the layouts match the baseline in {file}"
                : $"nino-gen: {total} change(s) from the baseline in {file}, {breaking} that break or lose data");
            return breaking == 0 ? 0 : 1;
        }

        /// <summary>The value of the verbatim string in <c>NinoLayouts(@"...")</c>, and where its source lies.</summary>
        private sealed record BaselineLiteral(int Start, int End, string Text);

        private static BaselineLiteral? Literal(string source)
        {
            int at = source.IndexOf("NinoLayouts(", StringComparison.Ordinal);
            if (at < 0)
                return null;
            int pos = at + "NinoLayouts(".Length;
            while (pos < source.Length && char.IsWhiteSpace(source[pos]))
                pos++;
            if (pos + 1 >= source.Length || source[pos] != '@' || source[pos + 1] != '"')
                return null;
            int start = pos + 2;
            var text = new StringBuilder();
            for (int i = start; i < source.Length; i++)
            {
                if (source[i] != '"')
                {
                    text.Append(source[i]);
                    continue;
                }

                if (i + 1 < source.Length && source[i + 1] == '"')
                {
                    text.Append('"');
                    i++;
                    continue;
                }

                return new BaselineLiteral(start, i, text.ToString());
            }

            return null;
        }

        private static string Replace(string source, BaselineLiteral literal, string text) =>
            string.Concat(source.AsSpan(0, literal.Start), text.Replace("\"", "\"\""), source.AsSpan(literal.End));
    }
}
