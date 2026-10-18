using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Samples;

namespace Nino.TypeScript.Tests
{
    /// <summary>
    /// Generates the TypeScript of <see cref="Samples"/>, then has Node read the bytes .NET wrote and write them back:
    /// the bytes must be the same, and .NET must read a value made in TypeScript.
    /// </summary>
    [TestClass]
    public class TypeScriptTests
    {
        private static string _directory;
        private static string _output;

        [ClassInitialize]
        public static void Generate(TestContext _)
        {
            _directory = Path.Combine(Path.GetTempPath(), "nino-ts-" + Guid.NewGuid().ToString("N"));
            var console = Console.Out;
            using var log = new StringWriter();
            Console.SetOut(log);
            try
            {
                Assert.AreEqual(0, Program.Run(new[]
                {
                    "--assembly", typeof(Player).Assembly.Location, "--out", _directory, "--import-extension", ".ts",
                }));
            }
            finally
            {
                Console.SetOut(console);
            }

            _output = log.ToString();
            File.Copy(Path.Combine(AppContext.BaseDirectory, "harness.ts"), Path.Combine(_directory, "harness.ts"));
        }

        [ClassCleanup]
        public static void Cleanup()
        {
            if (_directory != null && Directory.Exists(_directory))
                Directory.Delete(_directory, true);
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

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static List<(string Hex, string Json)> RunNode(List<(string Codec, byte[] Bytes)> cases)
        {
            string input = Path.Combine(_directory, "cases.json");
            File.WriteAllText(input, JsonSerializer.Serialize(cases.Select(c => new { codec = c.Codec, hex = Hex(c.Bytes) })));
            var start = new ProcessStartInfo("node", new[] { "--no-warnings", "harness.ts", input })
            {
                WorkingDirectory = _directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var node = Process.Start(start)!;
            string stdout = node.StandardOutput.ReadToEnd();
            string stderr = node.StandardError.ReadToEnd();
            node.WaitForExit();
            Assert.AreEqual(0, node.ExitCode, stderr);
            return JsonDocument.Parse(stdout).RootElement.EnumerateArray()
                .Select(e => (e.GetProperty("hex").GetString(), e.GetProperty("json").GetString())).ToList();
        }

        [TestMethod]
        public void LeavesOutWhatItCannotWrite()
        {
            StringAssert.Contains(_output, "warning NINOTS002: Samples.Node is not written to TypeScript: reference tracking");
            string types = File.ReadAllText(Path.Combine(_directory, "Nino.TypeScript.Tests.ts"));
            Assert.IsFalse(types.Contains("class Node"));
            StringAssert.Contains(types, "export class Player {");
            StringAssert.Contains(File.ReadAllText(Path.Combine(_directory, "nino.ts")), "export const ninoVersion = \"4.");
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
                // Strings: Latin-1, UTF-16 with a surrogate pair and an unpaired surrogate, long enough for chunks.
                ("TagCodec", NinoSerializer.Serialize(new Tag { Name = new string('é', 5000) })),
                ("TagCodec", NinoSerializer.Serialize(new Tag { Name = "a😀b\ud800c" + new string('中', 5000) })),
            };

            var results = RunNode(cases);
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
        }

        [TestMethod]
        public void DotNetReadsWhatTypeScriptWrites()
        {
            var results = RunNode(new List<(string, byte[])>());
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

        /// <summary>The generated code compiles under <c>tsc --strict</c> (with <c>tsc</c> on the PATH).</summary>
        [TestMethod]
        public void CompilesUnderStrictTypeScript()
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "tsc.cmd" : "tsc", new[]
            {
                "--strict", "--noEmit", "--noUnusedLocals", "--noUnusedParameters", "--target", "es2022",
                "--module", "nodenext", "--allowImportingTsExtensions", "--skipLibCheck",
                "nino.ts", "Nino.TypeScript.Tests.ts",
            })
            {
                WorkingDirectory = _directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
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
