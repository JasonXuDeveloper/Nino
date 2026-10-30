using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Samples;

namespace Nino.Codegen.Integration.Tests
{
    /// <summary>
    /// Generates the TypeScript of <see cref="Samples"/>, then has Node read the bytes .NET wrote and write them back:
    /// the bytes must be the same, and .NET must read a value made in TypeScript. The round trips run twice: over the
    /// default code and over the code <c>--numeric-arrays typed</c> generates (typed arrays).
    /// </summary>
    [TestClass]
    public class TypeScriptTests
    {
        private static string _directory;
        private static string _typed;
        private static string _output;

        /// <summary>The two directories the code is generated into: the default code, and typed arrays.</summary>
        private static string[] Directories => new[] { _directory, _typed };

        [ClassInitialize]
        public static void Generate(TestContext context)
        {
            _directory = GenerateInto(out _output);
            _typed = GenerateInto(out _, "--numeric-arrays", "typed");
        }

        private static string GenerateInto(out string output, params string[] options)
        {
            string directory = Path.Combine(Path.GetTempPath(), "nino-ts-" + Guid.NewGuid().ToString("N"));
            using var log = new StringWriter();
            Assert.AreEqual(0, Nino.Codegen.Program.Run(new[]
            {
                "--assembly", typeof(Player).Assembly.Location, "--lang", "ts", "--out", directory,
                "--import-extension", ".ts",
            }.Concat(options).ToArray(), log));
            output = log.ToString();
            File.Copy(Path.Combine(AppContext.BaseDirectory, "harness.ts"), Path.Combine(directory, "harness.ts"));
            return directory;
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            foreach (string directory in Directories)
                if (directory != null && Directory.Exists(directory))
                    Directory.Delete(directory, true);
        }

        private static Player Full() => new()
        {
            Id = new PlayerId(1234567890123),
            Name = "Zoë",
            Motto = "😀 never give up",
            Rank = Rank.Silver,
            Access = Access.Read | Access.Write,
            Big = Big.Huge,
            Online = true,
            Initial = 'Z',
            Score = 98.5,
            Balance = -1234.5600m,
            Session = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
            Joined = new DateTime(2026, 10, 18, 12, 30, 45, 123, DateTimeKind.Utc),
            Played = TimeSpan.FromMinutes(90.5),
            Path = new List<Vec2> { new() { X = 1.5f, Y = -2 }, new() { X = 3, Y = 4.25f } },
            Levels = new[] { 1, -2, int.MaxValue },
            Avatar = new byte[] { 0, 1, 254, 255 },
            Inventory = new Dictionary<string, int[]> { ["名字"] = new[] { 1, 2 }, ["empty"] = Array.Empty<int>(), ["none"] = null },
            Friends = new HashSet<int> { 3, 1, 2 },
            Maybe = 7,
            Gaps = new long?[] { 1, null, long.MinValue },
            Pair = (5, ""),
            Boxed = Tuple.Create(9),
            Spawn = new KeyValuePair<string, Vec2>("home", new Vec2 { X = 7, Y = 8 }),
            Tag = new Tag { Name = "tag", Weight = 3 },
            Pet = new Puppy { Name = "Rex", Bones = 2, Sleepy = true },
            Zoo = new List<Animal> { new Dog { Name = "Fido", Bones = 1 }, null, new Cat { Name = "Tom", Lives = 9 } },
            Shape = new Square { Area = 4, Side = 2 },
            Anything = new Vec2 { X = 1, Y = 2 },
            Saved = new Saved { Health = 100, Note = "keyed" },
            Tolerant = new Tolerant { A = 1, B = "tolerant" },
        };

        private static Node Cycle()
        {
            var first = new Node();
            first.Next = new Node { Next = first };
            return first;
        }

        private static Extras FullExtras() => new()
        {
            Padded = new Padded { A = 7, B = -8 },
            PaddedArray = new[] { new Padded { A = 1, B = 2 }, new Padded { A = 255, B = int.MinValue } },
            RawPair = (9, long.MaxValue),
            Maybes = new int?[] { 1, null, -3 },
            NullablePair = (null, "pair"),
            Spots = new Dictionary<int, Vec2> { [1] = new() { X = 1, Y = 2 }, [-5] = new() { X = 0.5f, Y = -0.25f } },
            Names = new Dictionary<int, string> { [1] = "one", [2] = null, [3] = "трёх" },
            Ids = new HashSet<long> { 1, -1, long.MinValue },
            Words = new List<string> { "a", null, "", "Grüße", "a longer ASCII text, longer than the short ones" },
            Ranks = new[] { Rank.Gold, Rank.None },
            // Latin-1 with chars in 0x80-0x9F (which a browser's "latin1" decoder reads as windows-1252).
            Text = "\u0080\u0081\u009f text past the length read in JavaScript: ÿ é ñ ü and so on, to the end",
            Ascii = "plain ASCII, as many bytes as chars",
            Guids = new[] { Guid.Empty, new Guid("0f8fad5b-d9cb-469f-a165-70867728950e") },
            Decimals = new[] { 0m, -0.0001m, 79228162514264337593543950335m, -1234567890.123456789m, 0.5m },
        };

        private static Numbers FullNumbers() => new()
        {
            Odd = 1,
            SBytes = new sbyte[] { -128, 0, 127 },
            ByteList = new List<byte> { 0, 255 },
            Shorts = new short[] { short.MinValue, -1, short.MaxValue },
            UShorts = new List<ushort> { 0, ushort.MaxValue },
            // Past the 64 bytes a typed array holds on V8's heap: read into the buffer read typed arrays share.
            Ints = Enumerable.Range(-50, 100).Select(i => i * 40_000_000).ToArray(),
            UInts = new List<uint> { 0, uint.MaxValue },
            Longs = new[] { long.MinValue, -1, long.MaxValue },
            ULongs = new List<ulong> { 0, ulong.MaxValue },
            Floats = new[] { 1.5f, float.NegativeInfinity, -0f },
            Doubles = new List<double> { double.Epsilon, -2.25, double.MaxValue },
            Bools = new[] { true, false },
            Chars = new List<char> { 'a', '中' },
        };

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static List<(string Hex, string Json, string Error)> RunNode(List<(string Codec, byte[] Bytes)> cases,
            string directory = null)
        {
            directory ??= _directory;
            string input = Path.Combine(directory, "cases.json");
            File.WriteAllText(input, JsonSerializer.Serialize(cases.Select(c => new { codec = c.Codec, hex = Hex(c.Bytes) })));
            var start = new ProcessStartInfo("node",
                new[] { "--no-warnings", "harness.ts", input, directory == _typed ? "typed" : "plain" })
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var node = Process.Start(start)!;
            string stdout = node.StandardOutput.ReadToEnd();
            string stderr = node.StandardError.ReadToEnd();
            node.WaitForExit();
            Assert.AreEqual(0, node.ExitCode, stderr);
            return JsonDocument.Parse(stdout).RootElement.EnumerateArray()
                .Select(e => (e.GetProperty("hex").GetString(), e.GetProperty("json").GetString(),
                    e.GetProperty("error").GetString())).ToList();
        }

        [TestMethod]
        public void LeavesOutWhatItCannotWrite()
        {
            StringAssert.Contains(_output, "warning NINOGEN002: Samples.Halves is not written to TypeScript: System.Half");
            string types = File.ReadAllText(Path.Combine(_directory, "Nino.Codegen.Integration.Tests.ts"));
            Assert.IsFalse(types.Contains("class Halves"));
            // Reference tracking is written (RoundTripsTheBytesDotNetWrites reads and writes a cycle).
            StringAssert.Contains(types, "export class Node {");
            StringAssert.Contains(types, "export class Player {");
            StringAssert.Contains(File.ReadAllText(Path.Combine(_directory, "nino.ts")), "export const ninoVersion = \"4.");
        }

        [TestMethod]
        public void TakesDeferrabilityFromTheLayouts()
        {
            // Every layout says whether its type is deferrable: nothing is guessed.
            Assert.IsFalse(_output.Contains("deferrable"), _output);
            string types = File.ReadAllText(Path.Combine(_directory, "Nino.Codegen.Integration.Tests.ts"));
            // A record is built by its constructor: never deferred, nested in place up to 512 bodies.
            int record = types.IndexOf("function readRecordChain(", StringComparison.Ordinal);
            Assert.IsTrue(record >= 0);
            StringAssert.Contains(types.Substring(record, 300), "return r.deep(");
            int chain = types.IndexOf("function readChain(", StringComparison.Ordinal);
            Assert.IsTrue(chain >= 0);
            StringAssert.Contains(types.Substring(chain, 300), "return r.defer(");
        }

        [TestMethod]
        public void RoundTripsTheBytesDotNetWrites()
        {
            var cases = new List<(string, byte[])>
            {
                ("PlayerCodec", NinoSerializer.Serialize(Full())),
                // DateTime is written as UTC (a Date has no kind).
                ("PlayerCodec", NinoSerializer.Serialize(new Player { Joined = new DateTime(0, DateTimeKind.Utc) })),
                ("PlayerCodec", NinoSerializer.Serialize<Player>(null)),
                ("Vec2Codec", NinoSerializer.Serialize(new Vec2 { X = 1, Y = 2 })),
                ("TagCodec", NinoSerializer.Serialize(new Tag { Name = "ünïcödé ✓", Weight = -1 })),
                ("AnimalCodec", NinoSerializer.Serialize<Animal>(new Puppy { Name = "Rex", Bones = 2 })),
                ("AnimalCodec", NinoSerializer.Serialize<Animal>(null)),
                ("DogCodec", NinoSerializer.Serialize<Dog>(new Dog { Name = "Fido" })),
                ("IShapeCodec", NinoSerializer.Serialize<IShape>(new Square { Area = 1, Side = 1 })),
                ("SavedCodec", NinoSerializer.Serialize(new Saved { Health = 5 })),
                ("TolerantCodec", NinoSerializer.Serialize(new Tolerant { A = 3 })),
                // Reference tracking: two nodes holding each other (written once each, then a back reference; the
                // value has no JSON), a chain and null.
                ("NodeCodec", NinoSerializer.Serialize(Cycle())),
                ("NodeCodec", NinoSerializer.Serialize(new Node { Next = new Node() })),
                ("NodeCodec", NinoSerializer.Serialize<Node>(null)),
                // Strings: Latin-1, UTF-16 with a surrogate pair and an unpaired surrogate, long enough for chunks.
                ("TagCodec", NinoSerializer.Serialize(new Tag { Name = new string('é', 5000) })),
                ("TagCodec", NinoSerializer.Serialize(new Tag { Name = "a😀b\ud800c" + new string('中', 5000) })),
                ("TagCodec", NinoSerializer.Serialize(new Tag { Name = "\ufeff中文" })),
                ("NumbersCodec", NinoSerializer.Serialize(FullNumbers())),
                ("NumbersCodec", NinoSerializer.Serialize(new Numbers { Ints = Array.Empty<int>(), Doubles = new List<double>() })),
                ("ExtrasCodec", NinoSerializer.Serialize(FullExtras())),
                ("ExtrasCodec", NinoSerializer.Serialize(new Extras())),
                // Keys that name prototype members stay Map entries.
                ("PlayerCodec", NinoSerializer.Serialize(new Player
                {
                    Joined = new DateTime(0, DateTimeKind.Utc),
                    Inventory = new Dictionary<string, int[]>
                        { ["__proto__"] = new[] { 1 }, ["constructor"] = new[] { 2 }, ["prototype"] = new[] { 3 } },
                })),
            };

            foreach (string directory in Directories)
                RoundTrips(cases, RunNode(cases, directory));
        }

        private static void RoundTrips(List<(string, byte[])> cases, List<(string Hex, string Json, string Error)> results)
        {
            for (int i = 0; i < cases.Count; i++)
                Assert.AreEqual(Hex(cases[i].Item2), results[i].Hex, $"case {i} ({cases[i].Item1}): {results[i].Json}");

            string full = results[0].Json;
            foreach (string expected in new[]
                     {
                         "\"id\":{\"value\":\"1234567890123\"}", "\"name\":\"Zoë\"", "\"motto\":\"😀 never give up\"",
                         "\"rank\":2", "\"access\":3", "\"big\":\"1099511627776\"", "\"initial\":\"Z\"",
                         "\"balance\":\"-1234.5600\"", "\"session\":\"0f8fad5b-d9cb-469f-a165-70867728950e\"",
                         "\"joined\":\"2026-10-18T12:30:45.123Z\"", "\"played\":\"54300000000\"",
                         "\"avatar\":[0,1,254,255]", "\"inventory\":{\"名字\":[1,2],\"empty\":[],\"none\":null}",
                         "\"gaps\":[\"1\",null,\"-9223372036854775808\"]", "\"pair\":[5,\"\"]", "\"boxed\":[9]",
                         "\"spawn\":[\"home\",{\"x\":7,\"y\":8}]", "\"pet\":{\"name\":\"Rex\",\"bones\":2,\"sleepy\":true}",
                         "\"zoo\":[{\"name\":\"Fido\",\"bones\":1},null,{\"name\":\"Tom\",\"lives\":9}]",
                         "\"saved\":{\"health\":100,\"note\":\"keyed\"}",
                     })
                StringAssert.Contains(full, expected);

            StringAssert.Contains(results[^2].Json, "\"inventory\":{\"__proto__\":[1],\"constructor\":[2],\"prototype\":[3]}");
            Assert.AreEqual("", results[^2].Error);

            string extras = results[^4].Json;
            foreach (string expected in new[]
                     {
                         "\"padded\":{\"a\":7,\"b\":-8}", "\"rawPair\":[9,\"9223372036854775807\"]", "\"maybes\":[1,null,-3]",
                         "\"nullablePair\":[null,\"pair\"]", "\"spots\":{\"1\":{\"x\":1,\"y\":2},\"-5\":{\"x\":0.5,\"y\":-0.25}}",
                         "\"words\":[\"a\",null,\"\",\"Grüße\",", "\"text\":\"\u0080\u0081\u009f text past",
                         "\"decimals\":[\"0\",\"-0.0001\",\"79228162514264337593543950335\",\"-1234567890.123456789\",\"0.5\"]",
                         "\"guids\":[\"00000000-0000-0000-0000-000000000000\",\"0f8fad5b-d9cb-469f-a165-70867728950e\"]",
                     })
                StringAssert.Contains(extras, expected);

            int numbers = cases.FindIndex(c => c.Item1 == "NumbersCodec");
            foreach (string expected in new[]
                     {
                         "\"sBytes\":[-128,0,127]", "\"byteList\":[0,255]", "\"shorts\":[-32768,-1,32767]",
                         "\"uShorts\":[0,65535]", "\"ints\":[-2000000000,-1960000000,", "\"uInts\":[0,4294967295]",
                         "\"longs\":[\"-9223372036854775808\",\"-1\",\"9223372036854775807\"]",
                         "\"uLongs\":[\"0\",\"18446744073709551615\"]", "\"floats\":[1.5,null,0]",
                         "\"doubles\":[5e-324,-2.25,1.7976931348623157e+308]", "\"bools\":[true,false]",
                         "\"chars\":[\"a\",\"中\"]",
                     })
                StringAssert.Contains(results[numbers].Json, expected);
            StringAssert.Contains(results[numbers + 1].Json, "\"ints\":[],\"uInts\":null");
        }

        /// <summary>Closed generic types as members: read with the codecs built from those of their arguments.</summary>
        [TestMethod]
        public void RoundTripsGenericTypes()
        {
            var cases = GenericSamples.Cases(ordered: true)
                .Select(c => (c.Type.Substring("Samples.".Length) + "Codec", c.Bytes)).ToList();
            var results = RunNode(cases);
            for (int i = 0; i < cases.Count; i++)
                Assert.AreEqual(Hex(cases[i].Item2), results[i].Hex, $"case {i} ({cases[i].Item1}): {results[i].Error}");

            // The last case is the full value (the harness adds a value of its own after the cases).
            string full = results[cases.Count - 1].Json;
            foreach (string expected in new[]
                     {
                         "\"number\":{\"label\":\"number\",\"value\":-42}", "\"duo\":{\"first\":\"-9223372036854775808\",\"second\":\"second\"}",
                         "\"nested\":{\"label\":\"outer\",\"value\":{\"label\":\"inner\",\"value\":7}}",
                         "\"mixed\":{\"label\":\"mixed\",\"value\":{\"first\":\"vec\",\"second\":{\"x\":1.5,\"y\":-2}}}",
                         "\"array\":{\"label\":null,\"value\":[1,-2,2147483647]}",
                     })
                StringAssert.Contains(full, expected);
            string types = File.ReadAllText(Path.Combine(_directory, "Nino.Codegen.Integration.Tests.ts"));
            StringAssert.Contains(types, "export class Box<T> {");
            StringAssert.Contains(types, "export class Duo<TFirst, TSecond> {");
            StringAssert.Contains(types,
                "export function BoxCodec<T>(p0: NinoCodec<T>, type: new () => Box<T> = Box): NinoCodec<Box<T> | null> {");
            // A closed generic type a slot holds: a subclass the slots' writers tell apart by its constructor.
            StringAssert.Contains(types, "export class CrateInt32 extends Crate<number> {}");
            StringAssert.Contains(types, "export class BoxInt32Array extends Box<number[] | null> {}");
            StringAssert.Contains(types, "levels: number[] | null = null;");
            Assert.IsFalse(types.Contains("r.numbers(", StringComparison.Ordinal));

            // --numeric-arrays typed: an array or list of fixed-size numbers is a typed array, read and written in one
            // block past 64 bytes; enums keep their constants' type.
            var typedResults = RunNode(cases, _typed);
            for (int i = 0; i < cases.Count; i++)
                Assert.AreEqual(Hex(cases[i].Item2), typedResults[i].Hex, $"typed case {i} ({cases[i].Item1}): {typedResults[i].Error}");
            Assert.AreEqual(full, typedResults[cases.Count - 1].Json);
            string typed = File.ReadAllText(Path.Combine(_typed, "Nino.Codegen.Integration.Tests.ts"));
            StringAssert.Contains(typed, "export class BoxInt32Array extends Box<Int32Array | null> {}");
            StringAssert.Contains(typed, "levels: Int32Array | null = null;");
            StringAssert.Contains(typed, "if (length > 16) return r.numbers(Int32Array, length);");
            StringAssert.Contains(typed, "ranks: Rank[] | null = null;");
        }

        [TestMethod]
        public void DotNetReadsWhatTypeScriptWrites()
        {
            foreach (string directory in Directories)
                ReadsPlayer(RunNode(new List<(string, byte[])>(), directory));
        }

        private static void ReadsPlayer(List<(string Hex, string Json, string Error)> results)
        {
            var player = NinoDeserializer.Deserialize<Player>(Convert.FromHexString(results[^1].Hex));
            Assert.AreEqual(42, player.Id.Value);
            Assert.AreEqual("Zoë", player.Name);
            Assert.AreEqual(Rank.Gold, player.Rank);
            Assert.AreEqual(Access.Read | Access.Admin, player.Access);
            Assert.AreEqual(new DateTime(2026, 10, 18, 12, 30, 45, 123, DateTimeKind.Utc), player.Joined);
            Assert.AreEqual(DateTimeKind.Utc, player.Joined.Kind);
            Assert.AreEqual(-12.345m, player.Balance);
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, player.Levels);
            CollectionAssert.AreEqual(new[] { 7 }, player.Inventory["sword"]);
            Assert.AreEqual((5, "five"), player.Pair);
            Assert.AreEqual("Tom", ((Cat)player.Pet).Name);
            Assert.AreEqual(9f, ((Cat)player.Pet).Lives);
            Assert.AreEqual(2, ((Square)player.Shape).Side);
            Assert.AreEqual(77, player.Saved.Health);
            Assert.IsNull(player.Motto);
            Assert.IsNull(player.Zoo);
        }

        private static Chain MakeChain(int depth)
        {
            Chain head = null;
            for (int i = depth; i > 0; i--)
                head = new Chain { Value = i, Next = head };
            return head;
        }

        private static Link MakeLink(int depth)
        {
            Link head = null;
            for (int i = depth; i > 0; i--)
                head = i % 3 == 0 ? new BigLink { Name = "big" + i, Extra = -i, Next = head } : new Link { Name = "l" + i, Next = head };
            return head;
        }

        private static TolerantChain MakeTolerant(int depth)
        {
            TolerantChain head = null;
            for (int i = depth; i > 0; i--)
                head = new TolerantChain { Value = i, Next = head };
            return head;
        }

        /// <summary>A keyed chain whose every 50th link also holds a short chain in a list.</summary>
        private static KeyedChain MakeKeyed(int depth)
        {
            KeyedChain head = null;
            for (int i = depth; i > 0; i--)
                head = new KeyedChain
                {
                    Value = i, Next = head,
                    More = i % 50 == 0 ? new List<KeyedChain> { new() { Value = -i, Next = new KeyedChain { Value = -i - 1 } } } : null,
                };
            return head;
        }

        /// <summary>A comb: each node holds a leaf and the next node.</summary>
        private static Tree MakeTree(int depth)
        {
            Tree head = null;
            for (int i = depth; i > 0; i--)
                head = new Tree { Name = "t" + i, Children = head == null ? null : new List<Tree> { new() { Name = "leaf" + i }, head } };
            return head;
        }

        private static RecordChain MakeRecord(int depth)
        {
            RecordChain head = null;
            for (int i = depth; i > 0; i--)
                head = new RecordChain(i, head);
            return head;
        }

        private static Nest MakeNest(int depth)
        {
            var nest = new Nest { Value = depth };
            for (int i = depth - 1; i > 0; i--)
                nest = new Nest { Value = i, Inner = new List<Nest> { nest } };
            return nest;
        }

        /// <summary>
        /// Data nested deeper than 128 class bodies, as .NET writes it (bodies deferred after the top-level object, or in
        /// a section of the outermost region): TypeScript reads it and writes the same bytes back.
        /// </summary>
        [TestMethod]
        public void DefersDeepBodiesAsDotNetDoes()
        {
            var cases = new List<(string, byte[])>();
            foreach (int depth in new[] { 1, 127, 128, 129, 130, 255, 256, 300, 2000 })
            {
                cases.Add(("ChainCodec", NinoSerializer.Serialize(MakeChain(depth))));
                cases.Add(("LinkCodec", NinoSerializer.Serialize(MakeLink(depth))));
                cases.Add(("TolerantChainCodec", NinoSerializer.Serialize(MakeTolerant(depth))));
                cases.Add(("KeyedChainCodec", NinoSerializer.Serialize(MakeKeyed(depth))));
                cases.Add(("TreeCodec", NinoSerializer.Serialize(MakeTree(depth))));
            }

            foreach (int depth in new[] { 1, 129, 300, 500 })
            {
                cases.Add(("RecordChainCodec", NinoSerializer.Serialize(MakeRecord(depth))));
                cases.Add(("NestCodec", NinoSerializer.Serialize(MakeNest(depth))));
            }

            cases.Add(("DeepCodec", NinoSerializer.Serialize(new Deep
            {
                Chain = MakeChain(300), Link = MakeLink(300), Tolerant = MakeTolerant(300), Keyed = MakeKeyed(300),
                Tree = MakeTree(300), Record = MakeRecord(300), Nest = MakeNest(150),
            })));

            var results = RunNode(cases);
            for (int i = 0; i < cases.Count; i++)
            {
                Assert.AreEqual("", results[i].Error, $"case {i} ({cases[i].Item1})");
                Assert.AreEqual(Hex(cases[i].Item2), results[i].Hex, $"case {i} ({cases[i].Item1})");
            }
        }

        private static bool DotNetThrows<T>(byte[] bytes)
        {
            try
            {
                NinoDeserializer.Deserialize<T>(bytes);
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Data no writer produces: bodies nested in place far deeper than 128, and truncated deep data. TypeScript reads
        /// what .NET reads (with a bounded stack), and throws a NinoError where .NET throws, never a RangeError.
        /// </summary>
        [TestMethod]
        public void ReadsOrRejectsForgedDeepDataAsDotNetDoes()
        {
            const int depth = 10000;
            // Chain and RecordChain in place: a presence byte, then the value and the next one.
            var inline = new List<byte> { 1 };
            for (int i = 1; i <= depth; i++)
            {
                inline.AddRange(BitConverter.GetBytes(i));
                inline.Add(i < depth ? (byte)1 : (byte)0);
            }

            byte[] chain = inline.ToArray();
            // TolerantChain in place: each body a region holding the value and the next one.
            byte[] tolerant = { 0 };
            for (int i = 1000; i > 0; i--)
            {
                var body = new List<byte>();
                body.AddRange(BitConverter.GetBytes(i));
                body.AddRange(tolerant);
                tolerant = BitConverter.GetBytes(body.Count).Concat(body).Prepend((byte)1).ToArray();
            }

            // Nest in place: the value, then a list of one Nest.
            var nest = new List<byte>();
            for (int i = 1; i < depth; i++)
            {
                nest.AddRange(BitConverter.GetBytes(i));
                nest.AddRange(BitConverter.GetBytes(1));
            }

            nest.AddRange(BitConverter.GetBytes(depth));
            nest.AddRange(BitConverter.GetBytes(-1));
            byte[] deferred = NinoSerializer.Serialize(MakeChain(2000));

            var cases = new List<(string Codec, byte[] Bytes, bool Throws)>
            {
                ("ChainCodec", chain, false),
                ("RecordChainCodec", chain, true),
                ("TolerantChainCodec", tolerant, true),
                ("NestCodec", nest.ToArray(), true),
            };
            for (int cut = 1; cut < deferred.Length; cut += 997)
                cases.Add(("ChainCodec", deferred.Take(cut).ToArray(), true));

            var results = RunNode(cases.Select(c => (c.Codec, c.Bytes)).ToList());
            for (int i = 0; i < cases.Count; i++)
            {
                var (codec, bytes, throws) = cases[i];
                bool dotNetThrows = codec switch
                {
                    "ChainCodec" => DotNetThrows<Chain>(bytes),
                    "RecordChainCodec" => DotNetThrows<RecordChain>(bytes),
                    "TolerantChainCodec" => DotNetThrows<TolerantChain>(bytes),
                    _ => DotNetThrows<Nest>(bytes),
                };
                Assert.AreEqual(throws, dotNetThrows, $"case {i} ({codec}) in .NET");
                if (throws)
                    StringAssert.StartsWith(results[i].Error, "NinoError: ", $"case {i} ({codec})");
                else
                    Assert.AreEqual(Hex(NinoSerializer.Serialize(NinoDeserializer.Deserialize<Chain>(bytes))),
                        results[i].Hex, $"case {i} ({codec}): {results[i].Error}");
            }
        }

        /// <summary>The generated code compiles under <c>tsc --strict</c> (with <c>tsc</c> on the PATH).</summary>
        [TestMethod]
        public void CompilesUnderStrictTypeScript()
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "tsc.cmd" : "tsc", new[]
            {
                "--strict", "--noEmit", "--noUnusedLocals", "--noUnusedParameters", "--target", "es2022",
                "--module", "nodenext", "--allowImportingTsExtensions", "--skipLibCheck",
                "nino.ts", "Nino.Codegen.Integration.Tests.ts", "fetch.ts",
            })
            {
                WorkingDirectory = _directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // The bytes serialize returns are a fetch body as they are (TypeScript 5.7+ types the buffer).
            File.WriteAllText(Path.Combine(_directory, "fetch.ts"),
                "import { serialize } from \"./nino.ts\";\n" +
                "import { Player, PlayerCodec } from \"./Nino.Codegen.Integration.Tests.ts\";\n" +
                "export const body: BodyInit = serialize(PlayerCodec, new Player());\n");
            Process tsc;
            try
            {
                tsc = Process.Start(start)!;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Assert.Inconclusive("tsc is not on the PATH.");
                return;
            }

            using (tsc)
            {
                string output = tsc.StandardOutput.ReadToEnd() + tsc.StandardError.ReadToEnd();
                tsc.WaitForExit();
                Assert.AreEqual(0, tsc.ExitCode, output);
            }
        }
    }
}
