using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Tests;
using Samples;

namespace Nino.Codegen.Integration.Tests
{
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
                string runtime = RepositoryFiles.Of("runtimes", "go");
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
}
