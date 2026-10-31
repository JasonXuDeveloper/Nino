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
    /// what the Go and Rust tests share: running a tool and the line protocol of their harnesses.
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
}
