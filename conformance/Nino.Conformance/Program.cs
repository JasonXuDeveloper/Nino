using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nino.Core;

namespace Conformance
{
    /// <summary>
    /// Writes and checks the conformance corpus. See conformance/README.md.
    /// <code>
    /// --out &lt;dir&gt;      write the corpus (valid and malicious cases, with what .NET does on each)
    /// --verify &lt;dir&gt;   read a committed corpus back with .NET and check every case
    /// --probe &lt;type&gt; &lt;file&gt;   (internal) deserialize one file in this process and print the outcome
    /// </code>
    /// </summary>
    public static class Program
    {
        /// <summary>Bumped when the corpus's file layout or JSON schema changes incompatibly.</summary>
        public const int CorpusVersion = 1;

        public static int Main(string[] args)
        {
            // Closed generic subtypes the code only creates (new Box<int>), never names as a member or call type: a
            // process that reads them without writing them first registers them (docs/v4-design.md, Polymorphism).
            NinoPolymorphic.Register<Box<int>>();
            NinoPolymorphic.Register<Box<string>>();
            NinoPolymorphic.Register<Box<List<Point>>>();
            NinoPolymorphic.Register<Box<Vec2>>();
            try
            {
                return args switch
                {
                    // The JSON of the deep cases is written recursively: a large stack, not Nino's.
                    ["--out", var dir] => OnLargeStack(() => Generate(dir)),
                    ["--verify", var dir] => OnLargeStack(() => Verify(dir)),
                    ["--probe", var type, var file] => Probe.Run(type, file),
                    ["--probe-prefixes", var type, var file] => Probe.RunPrefixes(type, file),
                    _ => Usage(),
                };
            }
            catch (VerificationException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        private static int OnLargeStack(Func<int> run)
        {
            int result = 0;
            Exception error = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    result = run();
                }
                catch (Exception e)
                {
                    error = e;
                }
            }, 512 << 20);
            thread.Start();
            thread.Join();
            if (error != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
            return result;
        }

        private static int Usage()
        {
            Console.Error.WriteLine("usage: Nino.Conformance --out <corpus dir> | --verify <corpus dir>");
            return 2;
        }

        // ------------------------------------------------------------------ writing

        private static int Generate(string dir)
        {
            dir = Path.GetFullPath(dir);
            string valid = Path.Combine(dir, "valid"), malicious = Path.Combine(dir, "malicious");
            foreach (string sub in new[] { valid, malicious })
            {
                if (Directory.Exists(sub))
                    Directory.Delete(sub, true);
                Directory.CreateDirectory(sub);
            }

            var cases = ValidCases.All();
            var names = new HashSet<string>();
            foreach (var c in cases)
            {
                if (!names.Add(c.Name))
                    throw new VerificationException($"duplicate case {c.Name}");
                byte[] bytes = c.Write();
                File.WriteAllBytes(Path.Combine(valid, c.Name + ".bin"), bytes);
                WriteJson(Path.Combine(valid, c.Name + ".json"), Describe(c, bytes));
            }

            var bad = MaliciousCases.All(cases);
            foreach (var m in bad)
            {
                if (!names.Add(m.Name))
                    throw new VerificationException($"duplicate case {m.Name}");
                File.WriteAllBytes(Path.Combine(malicious, m.Name + ".bin"), m.Bytes);
            }

            var outcomes = Probe.InChildren(bad.Select(m => (m.Type, Path.Combine(malicious, m.Name + ".bin"))).ToList());
            for (int i = 0; i < bad.Count; i++)
                WriteJson(Path.Combine(malicious, bad[i].Name + ".json"), MaliciousCases.Describe(bad[i], outcomes[i]));

            var truncations = MaliciousCases.Truncations(cases);
            var truncationJson = MaliciousCases.DescribeTruncations(truncations, cases, valid);
            WriteJson(Path.Combine(malicious, "truncations.json"), truncationJson);

            WriteJson(Path.Combine(dir, "index.json"), new JsonObject
            {
                ["corpusVersion"] = CorpusVersion,
                ["valid"] = new JsonArray(cases.Select(c => (JsonNode)c.Name).ToArray()),
                ["malicious"] = new JsonArray(bad.Select(c => (JsonNode)c.Name).ToArray()),
            });
            Console.WriteLine($"{cases.Count} valid cases, {bad.Count} malicious cases, " +
                              $"{truncationJson["prefixCount"]} truncated prefixes, in {dir}");
            return 0;
        }

        /// <summary>The .json of a valid case.</summary>
        internal static JsonObject Describe(ValidCase c, byte[] bytes)
        {
            object value = c.Read(bytes);
            var json = new ValueJson();
            var node = ValueJson.ToNode(value, json);
            byte[] rewritten = c.Rewrite(value);

            var involved = new SortedDictionary<string, Type>(StringComparer.Ordinal);
            Layouts.Involved(c.Type, involved);
            foreach (var seen in json.Seen.Values)
                Layouts.Involved(seen, involved);
            var layouts = new JsonObject();
            foreach (var (name, type) in involved)
                layouts[name] = Layouts.Of(type).ToString();
            // Closed generic types: their layout (of the definition) has no id; it is computed from the type arguments.
            var ids = new JsonObject();
            foreach (var (name, type) in json.Seen)
                if (type.IsGenericType)
                    ids[name] = $"0x{NinoTypeId.Of(type):X8}";

            var result = new JsonObject
            {
                ["case"] = c.Name,
                ["description"] = c.Description,
                ["type"] = Layouts.Name(c.Type),
                ["features"] = new JsonArray(c.Features.Select(f => (JsonNode)f).ToArray()),
            };
            if (c.Optional != null)
                result["optional"] = c.Optional;
            result["size"] = bytes.Length;
            result["roundTrip"] = rewritten.AsSpan().SequenceEqual(bytes);
            if (!rewritten.AsSpan().SequenceEqual(bytes))
                result["reencoded"] = Hex.Of(rewritten);
            result["layouts"] = layouts;
            if (ids.Count > 0)
                result["typeIds"] = ids;
            result["value"] = node;
            return result;
        }

        internal static void WriteJson(string path, JsonNode node)
        {
            // Deep values are written compact: indenting them would take megabytes of spaces.
            bool deep = Depth(node) > 40;
            var options = new JsonSerializerOptions
            {
                WriteIndented = !deep, MaxDepth = 100_000, Encoder = ValueJson.WriterOptions.Encoder,
            };
            File.WriteAllText(path, node.ToJsonString(options) + "\n");
        }

        private static int Depth(JsonNode node)
        {
            int depth = 0;
            var stack = new Stack<(JsonNode, int)>();
            stack.Push((node, 1));
            while (stack.Count > 0)
            {
                var (n, d) = stack.Pop();
                depth = Math.Max(depth, d);
                if (n is JsonObject o)
                    foreach (var p in o)
                        if (p.Value != null)
                            stack.Push((p.Value, d + 1));
                if (n is JsonArray a)
                    foreach (var item in a)
                        if (item != null)
                            stack.Push((item, d + 1));
            }

            return depth;
        }

        internal static JsonNode ReadJson(string path) =>
            JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { MaxDepth = 100_000 });

        // ------------------------------------------------------------------ checking

        private static int Verify(string dir)
        {
            dir = Path.GetFullPath(dir);
            var failures = new List<string>();
            var cases = ValidCases.All().ToDictionary(c => c.Name);
            var index = ReadJson(Path.Combine(dir, "index.json"));
            if ((int)index["corpusVersion"] != CorpusVersion)
                failures.Add($"index.json: corpusVersion {index["corpusVersion"]}, expected {CorpusVersion}");

            // Valid: .NET reads the committed bytes to the committed value and writes them back.
            int validCount = 0;
            foreach (string path in Directory.GetFiles(Path.Combine(dir, "valid"), "*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                validCount++;
                var expected = ReadJson(path);
                string name = (string)expected["case"];
                if (!cases.TryGetValue(name, out var c))
                {
                    failures.Add($"valid/{name}: no such case in Nino.Conformance (regenerate the corpus)");
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(Path.ChangeExtension(path, ".bin"));
                try
                {
                    var actual = Describe(c, bytes);
                    foreach (string key in new[] { "type", "layouts", "typeIds", "value", "roundTrip", "reencoded", "size" })
                        if (!JsonNode.DeepEquals(expected[key], actual[key]))
                            failures.Add($"valid/{name}: {key} differs: .NET now gives {Short(actual[key])}");
                    byte[] written = c.Write();
                    if (!written.AsSpan().SequenceEqual(bytes))
                        failures.Add($"valid/{name}: .NET now writes {Short(Hex.Of(written))}");
                }
                catch (Exception e)
                {
                    failures.Add($"valid/{name}: .NET fails to read it: {e.GetType().Name}: {e.Message}");
                }
            }

            // Malicious: .NET still does what the corpus records (an error, or the failure it records).
            string malicious = Path.Combine(dir, "malicious");
            var files = Directory.GetFiles(malicious, "*.json").Where(p => Path.GetFileName(p) != "truncations.json")
                .OrderBy(p => p, StringComparer.Ordinal).ToList();
            var expectedBad = files.Select(ReadJson).ToList();
            var outcomes = Probe.InChildren(expectedBad
                .Select((e, i) => (Layouts.ByName[(string)e["type"]], Path.ChangeExtension(files[i], ".bin"))).ToList());
            for (int i = 0; i < files.Count; i++)
            {
                var recorded = expectedBad[i]["dotnet"];
                var now = outcomes[i].ToJson();
                if (!JsonNode.DeepEquals(recorded, now))
                    failures.Add($"malicious/{expectedBad[i]["case"]}: .NET recorded {Short(recorded)}, now {Short(now)}");
            }

            // Truncations: every prefix of the listed cases is an error in .NET.
            var truncations = ReadJson(Path.Combine(malicious, "truncations.json"));
            foreach (var entry in truncations["cases"]!.AsArray())
            {
                string name = (string)entry["case"];
                var c = cases[name];
                var outcome = Probe.Prefixes(c.Type, Path.Combine(dir, "valid", name + ".bin"));
                if (!JsonNode.DeepEquals(entry["dotnet"], outcome))
                    failures.Add($"truncations/{name}: .NET recorded {Short(entry["dotnet"])}, now {Short(outcome)}");
            }

            foreach (string failure in failures)
                Console.Error.WriteLine(failure);
            Console.WriteLine($"{validCount} valid cases, {files.Count} malicious cases, " +
                              $"{truncations["cases"]!.AsArray().Count} truncated cases checked: {failures.Count} failures");
            return failures.Count == 0 ? 0 : 1;
        }

        private static string Short(object node)
        {
            string text = node is JsonNode n ? n.ToJsonString() : node?.ToString() ?? "null";
            return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }
    }

    internal sealed class VerificationException : Exception
    {
        public VerificationException(string message) : base(message)
        {
        }
    }
}
