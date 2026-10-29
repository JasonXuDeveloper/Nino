using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Codegen;
using Nino.Tests;

namespace Nino.Codegen.Tests
{
    /// <summary>
    /// The generated files committed under runtimes/ are what nino-gen writes today: each runtime's tests compile and
    /// run them (the corpus's types through the corpus; Shapes.cs compiled), so a template change is tested end to
    /// end only once they are regenerated. <c>NINO_UPDATE_GENERATED=1</c> rewrites them instead of failing.
    /// </summary>
    [TestClass]
    public class GeneratedFilesTests
    {
        /// <summary>The corpus's types, as this build built them (its target path, from the project file).</summary>
        internal static string Conformance
        {
            get
            {
                string path = typeof(GeneratedFilesTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(a => a.Key == "NinoConformance").Value;
                Assert.IsTrue(File.Exists(path), path + " is missing: build conformance/Nino.Conformance");
                return path;
            }
        }

        internal static string Shapes => typeof(global::Shapes.Holder).Assembly.Location;

        /// <summary>Runs nino-gen into a new directory: its files, and what it printed.</summary>
        internal static (Dictionary<string, string> Files, string Output) Generate(params string[] args)
        {
            string dir = Path.Combine(Path.GetTempPath(), "nino-gen-" + Guid.NewGuid().ToString("N"));
            try
            {
                var output = new StringWriter();
                var all = new List<string>(args) { "--out", dir };
                Assert.AreEqual(0, Program.Run(all.ToArray(), output));
                var files = new Dictionary<string, string>();
                foreach (var file in Directory.GetFiles(dir))
                    files[Path.GetFileName(file)] = File.ReadAllText(file);
                return (files, output.ToString());
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        private static void Check(string committed, string file, params string[] args)
        {
            var (files, _) = Generate(args);
            string text = files[file];
            string path = RepositoryFiles.Of(committed);
            if (Environment.GetEnvironmentVariable("NINO_UPDATE_GENERATED") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                Program.WriteIfChanged(path, text);
                return;
            }

            Assert.IsTrue(File.Exists(path), $"{committed} is missing: run with NINO_UPDATE_GENERATED=1");
            Assert.AreEqual(File.ReadAllText(path).Replace("\r\n", "\n"), text,
                $"{committed} is not what nino-gen writes: run the tests with NINO_UPDATE_GENERATED=1, then the " +
                "runtime's tests over the new file");
        }

        [TestMethod]
        public void GoConformance() => Check("runtimes/go/internal/generated/nino_types.go", "nino_types.go",
            "--assembly", Conformance, "--lang", "go", "--package", "generated");

        [TestMethod]
        public void GoShapes() => Check("runtimes/go/internal/generated/shapes/nino_types.go", "nino_types.go",
            "--assembly", Shapes, "--lang", "go", "--package", "shapes");

        [TestMethod]
        public void RustConformance() => Check("runtimes/rust/tests/generated/nino_types.rs", "nino_types.rs",
            "--assembly", Conformance, "--lang", "rust", "--serde", "--strings", "wide");

        [TestMethod]
        public void RustShapes() => Check("runtimes/rust/tests/generated/shapes.rs", "shapes.rs",
            "--assembly", Shapes, "--lang", "rust", "--serde", "--serde-names", "snake", "--name", "shapes.rs");

        [TestMethod]
        public void TypeScriptConformance() => Check("runtimes/typescript/test/generated/Nino.Conformance.ts",
            "Nino.Conformance.ts", "--assembly", Conformance, "--lang", "ts", "--names", "keep",
            "--import-extension", ".ts", "--runtime", "../../src/nino.ts");

        /// <summary>The corpus's types with <c>--numeric-arrays typed</c>, which corpus.test.ts runs the corpus over too.</summary>
        [TestMethod]
        public void TypeScriptConformanceTypedArrays() => Check(
            "runtimes/typescript/test/generated/Nino.Conformance.TypedArrays.ts", "Nino.Conformance.TypedArrays.ts",
            "--assembly", Conformance, "--lang", "ts", "--names", "keep", "--import-extension", ".ts",
            "--runtime", "../../src/nino.ts", "--numeric-arrays", "typed", "--name", "Nino.Conformance.TypedArrays.ts");

        [TestMethod]
        public void TypeScriptShapes() => Check("runtimes/typescript/test/generated/Nino.Codegen.Tests.ts",
            "Nino.Codegen.Tests.ts", "--assembly", Shapes, "--lang", "ts", "--import-extension", ".ts",
            "--runtime", "../../src/nino.ts");
    }
}
