using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Codegen.Inspect;
using Nino.Codegen.Model;
using Nino.Core;
using Nino.Tests;

namespace Nino.Codegen.Tests
{
    /// <summary>
    /// <c>nino-gen inspect</c>: every case of the conformance corpus read with the layouts of its types (each valid one
    /// to its end and to the value the corpus records, each malicious one and each truncation to an error, never a
    /// crash), the text of representative messages against committed files (Inspect/*.txt, rewritten with
    /// <c>NINO_UPDATE_GENERATED=1</c>), custom formatters, and the command line.
    /// </summary>
    [TestClass]
    public class InspectCommandTests
    {
        private static CodegenModel _conformance;

        [ClassInitialize]
        public static void Load(TestContext _) =>
            _conformance = CodegenModel.Load(GeneratedFilesTests.Conformance, new List<string>());

        [ClassCleanup]
        public static void Unload() => _conformance?.Dispose();

        private static string Corpus(params string[] parts) =>
            RepositoryFiles.Of(new[] { "tests", "conformance", "corpus" }.Concat(parts).ToArray());

        private static InspectResult Inspect(CodegenModel model, string typeName, byte[] data)
        {
            var type = model.ResolveType(typeName);
            Assert.IsNotNull(type, typeName);
            var root = model.RootValue(type);
            Assert.IsNotNull(root, typeName);
            return InspectCommand.OnLargeStack(() => Inspector.Read(model, root, type, data, 0));
        }

        /// <summary>Deep cases nest their values as deep as their data.</summary>
        private static readonly JsonDocumentOptions Deep = new() { MaxDepth = 10000 };

        private static IEnumerable<(string Name, JsonElement Case)> Cases(string kind)
        {
            using var index = JsonDocument.Parse(File.ReadAllText(Corpus("index.json")));
            foreach (var name in index.RootElement.GetProperty(kind).EnumerateArray().Select(e => e.GetString()))
                yield return (name, JsonDocument.Parse(File.ReadAllText(Corpus(kind, name + ".json")), Deep).RootElement);
        }

        private static (int Exit, string Output) Run(params string[] args) => Run(null, args);

        private static (int Exit, string Output) Run(byte[] input, params string[] args)
        {
            var output = new StringWriter { NewLine = "\n" };
            int exit = InspectCommand.Run(args, output, () => new MemoryStream(input ?? Array.Empty<byte>()));
            return (exit, output.ToString());
        }

        [TestMethod]
        public void LongKeysAreComparedBeyondTheirDisplayPrefix()
        {
            string prefix = new('x', 300);
            string first = prefix + "a", second = prefix + "b";
            const string setType = "System.Collections.Generic.HashSet<System.String>";
            const string mapType = "System.Collections.Generic.Dictionary<System.String,System.Int32>";

            Assert.IsNull(Inspect(_conformance, setType, NinoSerializer.Serialize(new HashSet<string>
            {
                first, second
            })).Error);
            Assert.IsNull(Inspect(_conformance, mapType, NinoSerializer.Serialize(new Dictionary<string, int>
            {
                [first] = 1, [second] = 2
            })).Error);

            // An array has the same encoding as a set and can write a repeated element.
            var duplicate = Inspect(_conformance, setType, NinoSerializer.Serialize(new[] { first, first }));
            Assert.IsNotNull(duplicate.Error);
            StringAssert.Contains(duplicate.Error.Message, "repeated");
        }

        // ---------------------------------------------------------------- the corpus

        [TestMethod]
        public void EveryValidCaseReadsToItsEndAsTheValueRecorded()
        {
            int cases = 0, leaves = 0;
            foreach (var (name, json) in Cases("valid"))
            {
                var data = File.ReadAllBytes(Corpus("valid", name + ".bin"));
                var result = Inspect(_conformance, json.GetProperty("type").GetString(), data);
                Assert.IsNull(result.Error, $"{name}: {result.Error?.Offset}: {result.Error?.Message}");
                Assert.AreEqual(data.Length, result.End, name);
                Assert.AreEqual(data.Length, result.Root.Length, name);
                leaves += Compare(result.Root, json.GetProperty("value"), name);
                cases++;
            }

            Assert.IsGreaterThan(100, cases, $"{cases} cases");
            Assert.IsGreaterThan(1000, leaves, $"{leaves} values compared");
        }

        /// <summary>
        /// Every malicious input fails with the offset and the reason, as the reader of every runtime must (the
        /// untrusted ones too: inspect makes the untrusted mode's checks); deep inline nesting outside regions, which
        /// .NET reads as deferred bodies, may read. Bytes after the value are reported, not read.
        /// </summary>
        [TestMethod]
        public void EveryMaliciousCaseFails()
        {
            int cases = 0;
            foreach (var (name, json) in Cases("malicious"))
            {
                var data = File.ReadAllBytes(Corpus("malicious", name + ".bin"));
                var result = Inspect(_conformance, json.GetProperty("type").GetString(), data);
                cases++;
                if (json.GetProperty("expected").GetString() == "error-or-value" && result.Error == null)
                    continue;
                if (json.GetProperty("category").GetString() == "trailing")
                {
                    Assert.IsNull(result.Error, name);
                    Assert.IsLessThan(data.Length, result.End, name);
                    continue;
                }

                Assert.IsNotNull(result.Error, $"{name} ({json.GetProperty("exercises").GetString()}) read");
                Assert.IsTrue(result.Error.Offset >= 0 && result.Error.Offset <= data.Length, name);
            }

            Assert.IsGreaterThan(100, cases, $"{cases} cases");
        }

        [TestMethod]
        public void EveryTruncationFails()
        {
            using var truncations = JsonDocument.Parse(File.ReadAllText(Corpus("malicious", "truncations.json")));
            int prefixes = 0;
            foreach (var entry in truncations.RootElement.GetProperty("cases").EnumerateArray())
            {
                string name = entry.GetProperty("case").GetString();
                var data = File.ReadAllBytes(Corpus("valid", name + ".bin"));
                var type = entry.GetProperty("type").GetString();
                for (int length = 0; length < data.Length; length++)
                {
                    var result = Inspect(_conformance, type, data.AsSpan(0, length).ToArray());
                    Assert.IsNotNull(result.Error, $"{name}, {length} of {data.Length} bytes, read");
                    prefixes++;
                }
            }

            Assert.AreEqual(truncations.RootElement.GetProperty("prefixCount").GetInt32(), prefixes);
        }

        /// <summary>
        /// Valid cases with bytes changed at random (a fixed seed): every read ends, in a value or in an
        /// <see cref="InspectException"/>; nothing else is thrown.
        /// </summary>
        [TestMethod]
        public void MutatedInputsFailCleanly()
        {
            var random = new Random(4_000_010);
            int reads = 0, failed = 0;
            foreach (var (name, json) in Cases("valid"))
            {
                var data = File.ReadAllBytes(Corpus("valid", name + ".bin"));
                if (data.Length == 0 || data.Length > 4096)
                    continue;
                string type = json.GetProperty("type").GetString();
                for (int round = 0; round < 40; round++)
                {
                    var mutated = (byte[])data.Clone();
                    for (int n = random.Next(1, 4); n > 0; n--)
                        mutated[random.Next(mutated.Length)] = random.Next(3) switch
                        {
                            0 => 0xFF,
                            1 => (byte)random.Next(256),
                            _ => (byte)(mutated[random.Next(mutated.Length)] ^ (1 << random.Next(8))),
                        };
                    var result = Inspect(_conformance, type, mutated);
                    reads++;
                    if (result.Error != null)
                        failed++;
                }
            }

            Assert.IsTrue(reads > 4000 && failed > reads / 4, $"{failed} of {reads} mutated inputs failed");
        }

        /// <summary>
        /// Compares what was read with the corpus's value (README.md, "The value as JSON"): every value inspect shows
        /// as JSON that the corpus records in the same form (floats it records as their bits are skipped), objects
        /// member by member, collections element by element. Returns the number of values compared.
        /// </summary>
        private static int Compare(InspectNode node, JsonElement expected, string path)
        {
            // A member older data lacks: the reader keeps its default, which the tree does not make up.
            if (node.Note?.EndsWith("default value", StringComparison.Ordinal) == true)
                return 0;
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.AreEqual("null", node.Json, path);
                return 1;
            }

            if (node.Json != null)
            {
                if (expected.ValueKind == JsonValueKind.Object && !expected.TryGetProperty("$ref", out _) &&
                    !expected.TryGetProperty("ticks", out _) && !expected.TryGetProperty("utcTicks", out _))
                    return 0;
                using var actual = JsonDocument.Parse(node.Json);
                return Same(actual.RootElement, expected, path);
            }

            int compared = 0;
            if (expected.ValueKind == JsonValueKind.Object)
            {
                var members = Members(node).ToList();
                foreach (var property in expected.EnumerateObject())
                {
                    // The type a type id names (elsewhere the declared type, which the tree does not repeat).
                    if (property.Name == "$type" && node.Children.Any(c => c.Name == "type id"))
                    {
                        string type = property.Value.GetString();
                        Assert.IsTrue(node.Type == type || node.Note?.Contains(type) == true,
                            $"{path}: {type}, not {node.Type} ({node.Note})");
                        continue;
                    }

                    if (property.Name == "$elements")
                    {
                        compared += Compare(members.First(m => m.Name == "[elements]"), property.Value, path + ".$elements");
                        continue;
                    }

                    if (property.Name.StartsWith('$'))
                        continue;
                    var member = members.FirstOrDefault(m => m.Name == property.Name);
                    if (member != null)
                        compared += Compare(member, property.Value, path + "." + property.Name);
                }
            }
            else if (expected.ValueKind == JsonValueKind.Array)
            {
                var elements = node.Children.Where(c => c.Name.StartsWith('[') && c.Name != "[elements]").ToList();
                if (elements.Count != expected.GetArrayLength())
                    return 0; // a tuple or a pair: its items, not elements
                int i = 0;
                foreach (var item in expected.EnumerateArray())
                {
                    var element = elements[i++];
                    if (item.ValueKind == JsonValueKind.Array && element.Children.Any(c => c.Name == "key"))
                    {
                        compared += Compare(element.Children.First(c => c.Name == "key"), item[0], path + "[key]");
                        compared += Compare(element.Children.First(c => c.Name == "value"), item[1], path + "[value]");
                    }
                    else
                        compared += Compare(element, item, $"{path}[{i - 1}]");
                }
            }

            return compared;
        }

        /// <summary>A body's members, wherever it was read (inline, or deferred).</summary>
        private static IEnumerable<InspectNode> Members(InspectNode node) =>
            node.Children.SelectMany(c => c.Name == "(body)" ? Members(c) : new[] { c });

        private static int Same(JsonElement actual, JsonElement expected, string path)
        {
            // Floats are recorded as their bits ("0x3ff0000000000000"), inspect shows their value.
            if (expected.ValueKind == JsonValueKind.String && expected.GetString().StartsWith("0x", StringComparison.Ordinal))
                return 0;
            // A string holding a surrogate is recorded as its code units.
            if (expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("utf16", out var units))
            {
                Assert.AreEqual(new string(units.EnumerateArray().Select(u => (char)u.GetInt32()).ToArray()),
                    actual.GetString(), path);
                return 1;
            }

            Assert.AreEqual(expected.ValueKind == JsonValueKind.Array ? JsonValueKind.Array : expected.ValueKind,
                actual.ValueKind, path);
            switch (expected.ValueKind)
            {
                case JsonValueKind.Array:
                {
                    Assert.AreEqual(expected.GetArrayLength(), actual.GetArrayLength(), path);
                    int compared = 0;
                    for (int i = 0; i < expected.GetArrayLength(); i++)
                        compared += Same(actual[i], expected[i], $"{path}[{i}]");
                    return compared;
                }
                case JsonValueKind.Object:
                {
                    int compared = 0;
                    foreach (var property in expected.EnumerateObject())
                        compared += Same(actual.GetProperty(property.Name), property.Value, path + "." + property.Name);
                    return compared;
                }
                case JsonValueKind.String:
                    Assert.AreEqual(expected.GetString(), actual.GetString(), path);
                    return 1;
                default:
                    Assert.AreEqual(expected.GetRawText(), actual.GetRawText(), path);
                    return 1;
            }
        }

        // ---------------------------------------------------------------- the text

        /// <summary>Messages of each kind, as the text shows them (Inspect/&lt;case&gt;.txt).</summary>
        [TestMethod]
        [DataRow("keyed-v2-to-v1", DisplayName = "keyed, newer data: unknown keys skipped, missing members")]
        [DataRow("tolerant-new-to-old", DisplayName = "version tolerant, newer data: bytes skipped")]
        [DataRow("polymorphism", DisplayName = "type ids, closed generic types")]
        [DataRow("generic-slots", DisplayName = "generic types behind type ids")]
        [DataRow("references-shared", DisplayName = "reference tracking")]
        [DataRow("nino-collections", DisplayName = "[NinoType] collections")]
        [DataRow("bcl-raw-values", DisplayName = "BCL values in raw structs")]
        [DataRow("deep-tolerant-chain-129", DisplayName = "a section of deferred bodies")]
        [DataRow("presence-bool-02", DisplayName = "malformed: an invalid bool")]
        [DataRow("keyed-member-too-short", DisplayName = "malformed: a keyed entry too short")]
        public void PrintsTheTree(string name)
        {
            string kind = File.Exists(Corpus("valid", name + ".bin")) ? "valid" : "malicious";
            using var json = JsonDocument.Parse(File.ReadAllText(Corpus(kind, name + ".json")), Deep);
            var (exit, output) = Run("--assembly", GeneratedFilesTests.Conformance, "--type",
                json.RootElement.GetProperty("type").GetString(), Corpus(kind, name + ".bin"));
            Assert.AreEqual(kind == "valid" ? 0 : InspectCommand.Malformed, exit, output);
            Golden(name, output.Replace(Corpus(kind, name + ".bin"), name + ".bin"));
        }

        private static void Golden(string name, string text)
        {
            string path = RepositoryFiles.Of("tests", "Nino.Codegen.Tests", "Inspect", name + ".txt");
            if (Environment.GetEnvironmentVariable("NINO_UPDATE_GENERATED") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Program.WriteIfChanged(path, text);
                return;
            }

            Assert.IsTrue(File.Exists(path), $"{path} is missing: run with NINO_UPDATE_GENERATED=1");
            Assert.AreEqual(File.ReadAllText(path).Replace("\r\n", "\n"), text,
                $"{name}: not what nino-gen inspect prints: run the tests with NINO_UPDATE_GENERATED=1 and review the diff");
        }

        [TestMethod]
        public void MalformedDataShowsWhatWasReadThenWhereAndWhy()
        {
            // integers-mixed.bin cut to 20 bytes, from standard input: the members up to there, then the failing check.
            var data = File.ReadAllBytes(Corpus("valid", "integers-mixed.bin"));
            var (exit, output) = Run(data[..20], "--assembly", GeneratedFilesTests.Conformance, "--type",
                "Conformance.Integers", "-");
            Assert.AreEqual(InspectCommand.Malformed, exit, output);
            StringAssert.Contains(output, "      11       4    U32: uint [raw(4)] = ");
            StringAssert.Contains(output, "      15       0    I64: long [raw(8)]\n");
            StringAssert.EndsWith(output, "nino-gen: malformed data at offset 15: unexpected end of data: 8 byte(s) " +
                                          "needed, 5 left\n");
        }

        // ---------------------------------------------------------------- custom formatters

        [TestMethod]
        public void AFormatterMemberOfAKeyedBodyIsShownAsItsBytes()
        {
            var bytes = NinoSerializer.Serialize(new InspectSamples.KeyedCustom { Before = 1, Delta = -300, After = "z" });
            var (exit, output) = Run(bytes, "--assembly", GeneratedFilesTests.Shapes, "--type",
                "InspectSamples.KeyedCustom");
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "Delta: long [formatter(InspectSamples.VarLong)] = d7 04  (key 0x");
            StringAssert.Contains(output, "opaque: written by the custom formatter InspectSamples.VarLong");
            StringAssert.Contains(output, "After: string [string] = \"z\"");
        }

        [TestMethod]
        public void AFormatterMemberOfAPositionalBodyStopsTheRead()
        {
            var bytes = NinoSerializer.Serialize(new InspectSamples.PositionalCustom { Before = 1, Delta = -300, After = "z" });
            var (exit, output) = Run(bytes, "--assembly", GeneratedFilesTests.Shapes, "--type",
                "InspectSamples.PositionalCustom");
            Assert.AreEqual(InspectCommand.Malformed, exit, output);
            StringAssert.Contains(output, "Before: int [raw(4)] = 1");
            StringAssert.Contains(output, "nino-gen: malformed data at offset 5: cannot read Delta: written by the " +
                                          "custom formatter InspectSamples.VarLong, and its length is not written");
        }

        // ---------------------------------------------------------------- the command line

        [TestMethod]
        public void ReadsHexFromStandardInputAtAnOffset()
        {
            // Two bytes of a frame header, then Conformance.Point {X = 1, Y = 2}, then a byte after it.
            var hex = Encoding.ASCII.GetBytes("AA BB\n01 0x01000000 02-00-00-00 FF");
            var (exit, output) = Run(hex, "--assembly", GeneratedFilesTests.Conformance, "--type", "Conformance.Point",
                "--hex", "--offset", "2");
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "       2       9  value: Conformance.Point");
            StringAssert.Contains(output, "       7       4    Y: int [raw(4)] = 2");
            StringAssert.Contains(output, "nino-gen: 1 byte(s) after the value, from offset 11, not read");
        }

        [TestMethod]
        public void WritesJsonForTools()
        {
            var (exit, output) = Run("--assembly", GeneratedFilesTests.Conformance, "--type", "Conformance.Zoo",
                "--json", Corpus("valid", "polymorphism.bin"));
            Assert.AreEqual(0, exit, output);
            using var json = JsonDocument.Parse(output);
            var root = json.RootElement;
            Assert.AreEqual(JsonValueKind.Null, root.GetProperty("error").ValueKind);
            Assert.AreEqual(0, root.GetProperty("trailing").GetInt32());
            var value = root.GetProperty("value");
            Assert.AreEqual("Conformance.Zoo", value.GetProperty("type").GetString());
            var star = value.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == "Star");
            var id = star.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == "type id");
            Assert.AreEqual(4, id.GetProperty("length").GetInt32());
            StringAssert.StartsWith(id.GetProperty("note").GetString(), "Conformance.");
        }

        [TestMethod]
        public void JsonCarriesTheError()
        {
            var (exit, output) = Run("--assembly", GeneratedFilesTests.Conformance, "--type", "Conformance.Targets.AnimalSlot",
                "--json", Corpus("malicious", "type-id-unknown.bin"));
            Assert.AreEqual(InspectCommand.Malformed, exit, output);
            using var json = JsonDocument.Parse(output);
            StringAssert.StartsWith(json.RootElement.GetProperty("error").GetProperty("message").GetString(),
                "unknown type id");
        }

        [TestMethod]
        public void NamesClosedGenericAndBuiltInTypes()
        {
            Assert.IsNotNull(_conformance.ResolveType("Conformance.Box<System.Int32>"));
            Assert.IsNotNull(_conformance.ResolveType("System.Collections.Generic.List<Conformance.Point>"));
            Assert.IsNotNull(_conformance.ResolveType("System.Int32[]"));
            Assert.IsNull(_conformance.ResolveType("Conformance.Box"));
            Assert.IsNull(_conformance.ResolveType("Conformance.Missing"));
            var result = Inspect(_conformance, "System.Collections.Generic.List<System.String>",
                Convert.FromHexString("0200000003610100"));
            Assert.IsNull(result.Error);
            Assert.AreEqual("[\"a\",\"\"]", "[" + string.Join(",", result.Root.Children.Select(c => c.Json)) + "]");
        }

        [TestMethod]
        public void RefusesWhatItCannotRead()
        {
            Assert.ThrowsExactly<ArgumentException>(() => Run("--assembly", GeneratedFilesTests.Conformance, "--type",
                "Conformance.Missing", Corpus("valid", "keyed.bin")));
            Assert.ThrowsExactly<ArgumentException>(() => Run("--assembly", GeneratedFilesTests.Conformance,
                Corpus("valid", "keyed.bin")));
            Assert.ThrowsExactly<ArgumentException>(() => Run(Encoding.ASCII.GetBytes("0g"), "--assembly",
                GeneratedFilesTests.Conformance, "--type", "Conformance.Point", "--hex"));
            Assert.ThrowsExactly<ArgumentException>(() => Run(new byte[2], "--assembly",
                GeneratedFilesTests.Conformance, "--type", "Conformance.Point", "--offset", "3"));
        }

        [TestMethod]
        public void UsageListsInspect() => StringAssert.Contains(CodegenOptions.Usage, InspectCommand.Usage);
    }
}
