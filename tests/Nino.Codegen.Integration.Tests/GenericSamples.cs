using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Samples;

namespace Nino.Codegen.Integration.Tests
{
    /// <summary>
    /// Values of <see cref="Generics"/> (closed generic types as members) for the round trips in each language, and
    /// what the Go and Rust tests share: the repository, running a tool and the line protocol of their harnesses.
    /// </summary>
    internal static class GenericSamples
    {
        internal static Generics Full() => new()
        {
            Number = new Box<int> { Label = "number", Value = -42 },
            Text = new Box<string> { Label = null, Value = "text ✓" },
            Duo = new Duo<long, string> { First = long.MinValue, Second = "second" },
            Nested = new Box<Box<int>> { Label = "outer", Value = new Box<int> { Label = "inner", Value = 7 } },
            Mixed = new Box<Duo<string, Vec2>>
            {
                Label = "mixed", Value = new Duo<string, Vec2> { First = "vec", Second = new Vec2 { X = 1.5f, Y = -2 } },
            },
            Boxes = new List<Box<int>> { new() { Label = "a", Value = 1 }, null, new() { Value = 3 } },
            Many = new Many<Box<long>>
            {
                Items = new List<Box<long>> { new() { Value = long.MaxValue }, null },
                ByName = new Dictionary<string, Box<long>> { ["one"] = new() { Value = 1 }, ["none"] = null },
                Inner = new Box<Box<long>> { Label = "inner", Value = new Box<long> { Value = -1 } },
                Pair = new Duo<int, Box<long>> { First = 5, Second = new Box<long> { Label = "second", Value = 2 } },
                Next = new Many<Box<long>> { Items = new List<Box<long>>(), Tuple = (1, null) },
                Tuple = (-1, new Box<long> { Value = 10 }),
            },
            Array = new Box<int[]> { Value = new[] { 1, -2, int.MaxValue } },
            ByName = new Dictionary<string, Duo<int, Box<string>>>
            {
                ["x"] = new() { First = 1, Second = new Box<string> { Value = "x" } },
                ["y"] = new() { First = 2 },
            },
            Tags = new Box<List<Tag>> { Value = new List<Tag> { new() { Name = "tag", Weight = 3 } } },
            Tuple = new Box<(int, string)> { Value = (9, "nine") },
            Maybe = new Box<int?> { Label = "maybe", Value = 11 },
            Entry = new Box<KeyValuePair<string, long>> { Value = new KeyValuePair<string, long>("key", -5) },
            Ints = new Many<int>
            {
                Items = new List<int> { 1, 2, 3 }, Pair = new Duo<int, int> { First = 4, Second = 5 },
                Tuple = (6, 7), Inner = new Box<int> { Value = 8 },
            },
        };

        /// <summary>Closed generic types in polymorphic slots, among the other types the slots allow.</summary>
        internal static Pens Pens() => new()
        {
            Star = new Crate<int> { Name = "int crate", Content = 42 },
            Animals = new List<Animal>
            {
                new Crate<string> { Name = "string crate", Content = "s" },
                new Cat { Name = "Tom", Lives = 9 },
                null,
                new Crate<List<Tag>> { Content = new List<Tag> { new() { Name = "tag", Weight = 1 } } },
                new Crate<Box<long>> { Name = "boxed", Content = new Box<long> { Label = "box", Value = -7 } },
            },
            Shape = new Patch<string> { Area = 1.5, Value = "patch" },
            Shapes = new List<IShape> { new Square { Area = 4, Side = 2 }, new Patch<int> { Area = 2, Value = -3 }, null },
            Anything = new Crate<int> { Name = "anything", Content = 1 },
        };

        /// <summary><see cref="Full"/> with at most one entry in each map: written in the same order by any map.</summary>
        internal static Generics SingleEntries()
        {
            var value = Full();
            value.Many.ByName.Remove("none");
            value.ByName.Remove("y");
            return value;
        }

        /// <summary>
        /// The cases each harness reads and writes back: the codec's .NET type name and the bytes; with maps of more
        /// than one entry when <paramref name="ordered"/> (the reader keeps their order).
        /// </summary>
        internal static List<(string Type, byte[] Bytes)> Cases(bool ordered)
        {
            var cases = new List<(string, byte[])>
            {
                ("Samples.Generics", NinoSerializer.Serialize(SingleEntries())),
                ("Samples.Generics", NinoSerializer.Serialize(new Generics())),
                ("Samples.Generics", NinoSerializer.Serialize<Generics>(null)),
                ("Samples.Tag", NinoSerializer.Serialize(new Tag { Name = "tag", Weight = -1 })),
                ("Samples.Pens", NinoSerializer.Serialize(Pens())),
                ("Samples.Pens", NinoSerializer.Serialize(new Pens { Anything = new Patch<int> { Value = 5 } })),
            };
            if (ordered)
                cases.Add(("Samples.Generics", NinoSerializer.Serialize(Full())));
            return cases;
        }

        internal static string Root
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "Nino.slnx")))
                        return dir.FullName;
                throw new InvalidOperationException("No Nino.slnx above " + AppContext.BaseDirectory);
            }
        }

        internal static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        /// <summary>
        /// Runs <paramref name="tool"/> in <paramref name="dir"/>: its exit code and output, or null when the tool is
        /// not on the PATH.
        /// </summary>
        internal static (int Code, string Output)? Run(string dir, string tool, params string[] args)
        {
            var start = new ProcessStartInfo(tool, args)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            Process process;
            try
            {
                process = Process.Start(start)!;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }

            using (process)
            {
                var stderr = process.StandardError.ReadToEndAsync();
                string stdout = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return (process.ExitCode, stdout + stderr.Result);
            }
        }

        /// <summary>Writes the cases one per line (<c>type hex</c>), as the Go and Rust harnesses read them.</summary>
        internal static void WriteCases(string path, List<(string Type, byte[] Bytes)> cases) =>
            File.WriteAllText(path, string.Concat(cases.Select(c => c.Type + " " + Hex(c.Bytes) + "\n")));

        /// <summary>The harness's lines (<c>ok hex</c> or <c>error message</c>): the bytes it wrote back are .NET's.</summary>
        internal static void AssertRoundTrips(List<(string Type, byte[] Bytes)> cases, string output)
        {
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.StartsWith("ok ", StringComparison.Ordinal) ||
                            l.StartsWith("error ", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(cases.Count, lines.Count, output);
            for (int i = 0; i < cases.Count; i++)
                Assert.AreEqual("ok " + Hex(cases[i].Bytes), lines[i], $"case {i} ({cases[i].Type})");
        }

        /// <summary>A tool the test needs is not on the PATH: inconclusive locally, a failure in CI (which sets it up).</summary>
        internal static void Missing(string tool)
        {
            if (Environment.GetEnvironmentVariable("CI") == "true")
                Assert.Fail(tool + " is not on the PATH (CI sets it up: .github/workflows/ci.yml).");
            Assert.Inconclusive(tool + " is not on the PATH.");
        }

        internal static void Delete(string dir)
        {
            if (dir != null && Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Generates the Go of <see cref="Samples"/>, then a Go program reads the bytes .NET wrote and writes them back:
    /// the bytes must be the same (with <c>go</c> on the PATH).
    /// </summary>
    [TestClass]
    public class GoTests
    {
        private const string Harness = @"package main

import (
	""bufio""
	""encoding/hex""
	""fmt""
	""os""
	""strings""

	nino ""github.com/JasonXuDeveloper/Nino/runtimes/go/v4""
	""ninotest/samples""
)

// Reads each case (a .NET type name and hex) with its codec, writes the value back and prints the bytes.
func main() {
	file, err := os.Open(os.Args[1])
	if err != nil {
		panic(err)
	}
	lines := bufio.NewScanner(file)
	lines.Buffer(nil, 1<<24)
	for lines.Scan() {
		name, text, _ := strings.Cut(lines.Text(), "" "")
		codec, ok := samples.NinoCodecOf(name)
		if !ok {
			fmt.Println(""error no codec"", name)
			continue
		}
		data, _ := hex.DecodeString(text)
		v, err := nino.DeserializeExact(data, codec.Read)
		if err != nil {
			fmt.Println(""error"", err)
			continue
		}
		back, err := nino.Serialize(v, codec.Write)
		if err != nil {
			fmt.Println(""error"", err)
			continue
		}
		fmt.Println(""ok"", hex.EncodeToString(back))
	}
}
";

        [TestMethod]
        public void RoundTripsGenericTypes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nino-go-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var log = new StringWriter();
                Assert.AreEqual(0, Program.Run(new[]
                {
                    "--assembly", typeof(Player).Assembly.Location, "--lang", "go", "--package", "samples",
                    "--out", Path.Combine(dir, "samples"),
                }, log), log.ToString());
                string runtime = Path.Combine(GenericSamples.Root, "runtimes", "go");
                File.WriteAllText(Path.Combine(dir, "go.mod"),
                    "module ninotest\n\ngo 1.22\n\nrequire github.com/JasonXuDeveloper/Nino/runtimes/go/v4 v4.0.0\n\n" +
                    "replace github.com/JasonXuDeveloper/Nino/runtimes/go/v4 => " + runtime.Replace('\\', '/') + "\n");
                File.WriteAllText(Path.Combine(dir, "main.go"), Harness);
                var cases = GenericSamples.Cases(ordered: true);
                GenericSamples.WriteCases(Path.Combine(dir, "cases.txt"), cases);

                var vet = GenericSamples.Run(dir, "go", "vet", "./...");
                if (vet == null)
                {
                    GenericSamples.Missing("go");
                    return;
                }

                Assert.AreEqual(0, vet.Value.Code, vet.Value.Output);
                var run = GenericSamples.Run(dir, "go", "run", ".", "cases.txt")!.Value;
                Assert.AreEqual(0, run.Code, run.Output);
                GenericSamples.AssertRoundTrips(cases, run.Output);
            }
            finally
            {
                GenericSamples.Delete(dir);
            }
        }
    }

    /// <summary>
    /// Generates the Rust of <see cref="Samples"/> with each <c>--maps</c>, then a Rust program reads the bytes .NET
    /// wrote and writes them back: the same bytes (with <c>cargo</c> on the PATH).
    /// </summary>
    [TestClass]
    public class RustTests
    {
        private const string Harness = @"#[rustfmt::skip]
#[allow(clippy::all)]
mod nino_types;

use nino::{NinoRead, NinoWrite};

struct RoundTrip<'a>(&'a [u8]);

impl nino_types::NinoVisit<Result<Vec<u8>, String>> for RoundTrip<'_> {
    fn visit<T: NinoRead + NinoWrite + 'static>(self) -> Result<Vec<u8>, String> {
        let value: T = nino::deserialize_exact(self.0).map_err(|e| format!(""{e:?}""))?;
        nino::serialize(&value).map_err(|e| format!(""{e:?}""))
    }
}

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!(""{b:02x}"")).collect()
}

fn unhex(text: &str) -> Vec<u8> {
    (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).unwrap()).collect()
}

/// Reads each case (a .NET type name and hex) as its Rust type, writes the value back and prints the bytes.
fn main() {
    let path = std::env::args().nth(1).unwrap();
    for line in std::fs::read_to_string(path).unwrap().lines() {
        let (name, text) = line.split_once(' ').unwrap();
        match nino_types::with_nino_type(name, RoundTrip(&unhex(text))) {
            None => println!(""error no type {name}""),
            Some(Err(e)) => println!(""error {e}""),
            Some(Ok(bytes)) => println!(""ok {}"", hex(&bytes)),
        }
    }
}
";

        private static void RoundTrip(string maps)
        {
            string dir = Path.Combine(Path.GetTempPath(), "nino-rust-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var log = new StringWriter();
                Assert.AreEqual(0, Program.Run(new[]
                {
                    "--assembly", typeof(Player).Assembly.Location, "--lang", "rust", "--maps", maps,
                    "--name", "nino_types.rs", "--out", Path.Combine(dir, "src"),
                }, log), log.ToString());
                string runtime = Path.Combine(GenericSamples.Root, "runtimes", "rust").Replace('\\', '/');
                bool indexmap = maps == "indexmap";
                File.WriteAllText(Path.Combine(dir, "Cargo.toml"),
                    "[package]\nname = \"ninotest-" + maps + "\"\nversion = \"0.0.0\"\nedition = \"2021\"\npublish = false\n\n" +
                    "[dependencies]\nnino = { package = \"nino-serialization\", path = \"" + runtime + "\"" +
                    (indexmap ? ", features = [\"indexmap\"] }\nindexmap = \"2\"\n" : " }\n") + "\n[workspace]\n");
                File.WriteAllText(Path.Combine(dir, "src", "main.rs"), Harness);
                // std's HashMap writes in its own order: only maps of one entry are written back byte for byte.
                var cases = GenericSamples.Cases(ordered: indexmap);
                GenericSamples.WriteCases(Path.Combine(dir, "cases.txt"), cases);

                var run = GenericSamples.Run(dir, "cargo", "run", "--quiet", "--", "cases.txt");
                if (run == null)
                {
                    GenericSamples.Missing("cargo");
                    return;
                }

                Assert.AreEqual(0, run.Value.Code, run.Value.Output);
                GenericSamples.AssertRoundTrips(cases, run.Value.Output);
                if (!indexmap)
                    StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "src", "nino_types.rs")),
                        "std::collections::HashMap<");
            }
            finally
            {
                GenericSamples.Delete(dir);
            }
        }

        [TestMethod]
        public void RoundTripsGenericTypesWithIndexMap() => RoundTrip("indexmap");

        [TestMethod]
        public void RoundTripsGenericTypesWithStdMaps() => RoundTrip("std");
    }
}
