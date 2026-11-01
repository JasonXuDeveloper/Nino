using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Tests;
using Samples;

namespace Nino.Codegen.Integration.Tests
{
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
                string runtime = RepositoryFiles.Of("runtimes", "rust").Replace('\\', '/');
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
