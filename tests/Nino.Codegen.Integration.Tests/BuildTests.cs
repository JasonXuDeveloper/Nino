using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Codegen;
using Nino.Core;

namespace Nino.Codegen.Integration.Tests
{
    /// <summary>
    /// build/Nino.Codegen.targets in real builds: scratch projects that reference Nino.Core and the generator, declare
    /// <c>&lt;NinoCodegen&gt;</c> items (or Nino.TypeScript's old properties) and import the targets, run with the
    /// nino-gen built here. The files must be what nino-gen writes for the same options.
    /// </summary>
    [TestClass]
    public class BuildTests
    {
        private const string Model = @"using System.Collections.Generic;
using Nino.Core;

namespace Scratch
{
    public enum Kind : byte { None, Some }

    [NinoType]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [NinoType]
    public class Unit
    {
        public string DisplayName;
        public Kind Kind;
        public List<Point> Path;
        public Dictionary<string, int> Counts;
    }
}
";

        private static string Metadata(string key) =>
            typeof(BuildTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value;

        private static string Root
        {
            get
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "Nino.slnx")))
                        return dir.FullName;
                throw new InvalidOperationException("No Nino.slnx above " + AppContext.BaseDirectory);
            }
        }

        private static string Targets => Path.Combine(Root, "src", "Nino.Codegen", "build", "Nino.Codegen.targets");

        /// <summary>A scratch project in a new directory: <paramref name="body"/> goes inside its &lt;Project&gt;.</summary>
        private static string Project(string body)
        {
            string tool = Path.GetFullPath(Metadata("NinoCodegenTool"));
            string generator = Path.GetFullPath(Metadata("NinoGenerator"));
            Assert.IsTrue(File.Exists(tool), tool + " is missing: build src/Nino.Codegen");
            Assert.IsTrue(File.Exists(generator), generator + " is missing: build src/Nino.Generator.Roslyn4.12");
            string dir = Path.Combine(Path.GetTempPath(), "nino-codegen-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            // Nothing above it may change the build (the repository's Directory.*.props, central versions).
            File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project/>");
            File.WriteAllText(Path.Combine(dir, "Directory.Build.targets"), "<Project/>");
            File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"), "<Project/>");
            File.WriteAllText(Path.Combine(dir, "Model.cs"), Model);
            Write(dir, body);
            return dir;
        }

        private static void Write(string dir, string body)
        {
            File.WriteAllText(Path.Combine(dir, "Scratch.csproj"), $@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <_NinoCodegenTool>{Path.GetFullPath(Metadata("NinoCodegenTool"))}</_NinoCodegenTool>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include=""{typeof(NinoSerializer).Assembly.Location}"" />
    <Analyzer Include=""{Path.GetFullPath(Metadata("NinoGenerator"))}"" />
  </ItemGroup>
{body}
  <Import Project=""{Targets}"" />
</Project>
");
        }

        /// <summary>Runs dotnet with the arguments in the project's directory: its exit code and output.</summary>
        private static (int Code, string Output) Dotnet(string dir, params string[] args)
        {
            string host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var start = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args.Concat(new[] { "-nologo", "-nodeReuse:false", "-tl:off", "-v:n" }))
                start.ArgumentList.Add(arg);
            // The test host's MSBuild settings are not the scratch build's.
            foreach (string name in new[] { "MSBuildExtensionsPath", "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
                start.Environment.Remove(name);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd() + error.Result;
            process.WaitForExit();
            return (process.ExitCode, output);
        }

        private static string Build(string dir)
        {
            var (code, output) = Dotnet(dir, "build");
            Assert.AreEqual(0, code, output);
            return output;
        }

        private static int Runs(string output) => Regex.Matches(output, @"^\s*nino-gen: ", RegexOptions.Multiline).Count;

        /// <summary>What nino-gen writes for the scratch assembly with these options.</summary>
        private static Dictionary<string, string> Expected(string dir, params string[] args)
        {
            string output = Path.Combine(dir, "expected-" + Guid.NewGuid().ToString("N"));
            var all = new List<string>
                { "--assembly", Path.Combine(dir, "bin", "Debug", "net10.0", "Scratch.dll"), "--out", output };
            all.AddRange(args);
            Assert.AreEqual(0, Program.Run(all.ToArray(), new StringWriter()));
            var files = Directory.GetFiles(output).ToDictionary(Path.GetFileName, File.ReadAllText);
            Directory.Delete(output, true);
            return files;
        }

        private static void AssertSame(Dictionary<string, string> expected, string dir)
        {
            CollectionAssert.AreEquivalent(expected.Keys.ToList(),
                Directory.GetFiles(dir).Select(Path.GetFileName).ToList(), dir);
            foreach (var (file, text) in expected)
                Assert.AreEqual(text, File.ReadAllText(Path.Combine(dir, file)), file);
        }

        private static void Delete(string dir)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }

        [TestMethod]
        public void WritesEveryLanguageAsNinoGenDoes()
        {
            string dir = Project(@"  <ItemGroup>
    <NinoCodegen Include=""typescript"" Out=""ts/"" Names=""keep"" ImportExtension="".js"" Runtime=""$(TsRuntime)"" />
    <NinoCodegen Include=""go"" Out=""go/model"" Package=""model"" Tags=""json"" TagNames=""snake"" />
    <NinoCodegen Include=""rust"" Out=""rust/src/model"" Serde=""true"" Strings=""wide"" Maps=""std"" />
  </ItemGroup>");
            try
            {
                string first = Build(dir);
                Assert.AreEqual(3, Runs(first), first);
                AssertSame(Expected(dir, "--lang", "ts", "--names", "keep", "--import-extension", ".js"),
                    Path.Combine(dir, "ts"));
                AssertSame(Expected(dir, "--lang", "go", "--package", "model", "--tags", "json", "--tag-names", "snake"),
                    Path.Combine(dir, "go", "model"));
                AssertSame(Expected(dir, "--lang", "rust", "--serde", "--strings", "wide", "--maps", "std"),
                    Path.Combine(dir, "rust", "src", "model"));
                StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "ts", "Scratch.ts")), "DisplayName");
                StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "go", "model", "nino_types.go")),
                    "json:\"display_name\"");
                StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "rust", "src", "model", "nino_types.rs")),
                    "serde");
                StringAssert.Contains(File.ReadAllText(Path.Combine(dir, "rust", "src", "model", "nino_types.rs")),
                    "std::collections::HashMap<");

                // Nothing changed: nothing runs, no file is touched.
                string types = Path.Combine(dir, "go", "model", "nino_types.go");
                var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(types, stamp);
                string again = Build(dir);
                Assert.AreEqual(0, Runs(again), again);
                Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(types));

                // One item's options changed (by a property, so the assembly is not compiled again): that item runs
                // again; the TypeScript runtime is now imported, so the manifest deletes nino.ts.
                var (code, changed) = Dotnet(dir, "build", "-p:TsRuntime=nino-serialization");
                Assert.AreEqual(0, code, changed);
                Assert.AreEqual(1, Runs(changed), changed);
                Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(types));
                Assert.IsFalse(File.Exists(Path.Combine(dir, "ts", "nino.ts")));
                AssertSame(Expected(dir, "--lang", "ts", "--names", "keep", "--import-extension", ".js", "--runtime",
                    "nino-serialization"), Path.Combine(dir, "ts"));
            }
            finally
            {
                Delete(dir);
            }
        }

        [TestMethod]
        public void TakesNinoTypeScriptPropertiesWithAWarning()
        {
            string dir = Project(@"  <PropertyGroup>
    <NinoTypeScriptOutput>$(MSBuildProjectDirectory)/web/</NinoTypeScriptOutput>
    <NinoTypeScriptNames>keep</NinoTypeScriptNames>
    <NinoTypeScriptImportExtension>.ts</NinoTypeScriptImportExtension>
  </PropertyGroup>");
            try
            {
                string output = Build(dir);
                StringAssert.Contains(output, "warning NINOGEN003");
                Assert.AreEqual(1, Runs(output), output);
                AssertSame(Expected(dir, "--lang", "ts", "--names", "keep", "--import-extension", ".ts"),
                    Path.Combine(dir, "web"));

                // Turned off the old way.
                Write(dir, @"  <PropertyGroup>
    <NinoTypeScriptEnabled>false</NinoTypeScriptEnabled>
  </PropertyGroup>");
                Delete(Path.Combine(dir, "web"));
                output = Build(dir);
                Assert.AreEqual(0, Runs(output), output);
                Assert.IsFalse(Directory.Exists(Path.Combine(dir, "web")));
            }
            finally
            {
                Delete(dir);
            }
        }

        [TestMethod]
        public void WarnsWithoutItemsAndFailsOnBadOptions()
        {
            string dir = Project("");
            try
            {
                string output = Build(dir);
                StringAssert.Contains(output, "warning NINOGEN004");
                Assert.AreEqual(0, Runs(output), output);

                // An option of another language fails the build, from nino-gen's own check.
                Write(dir, @"  <ItemGroup>
    <NinoCodegen Include=""typescript"" Package=""model"" />
  </ItemGroup>");
                var (code, failed) = Dotnet(dir, "build");
                Assert.AreNotEqual(0, code, failed);
                StringAssert.Contains(failed, "error NINOGEN001: --package is an option of go, not of ts.");

                // A design-time build (an IDE loading the project) never runs it.
                Write(dir, @"  <ItemGroup>
    <NinoCodegen Include=""go"" />
  </ItemGroup>");
                var (designCode, design) = Dotnet(dir, "build", "-p:DesignTimeBuild=true");
                Assert.AreEqual(0, designCode, design);
                Assert.AreEqual(0, Runs(design), design);
                Assert.IsFalse(Directory.Exists(Path.Combine(dir, "go")));
                // The default output is a folder named after the language.
                Assert.AreEqual(1, Runs(Build(dir)));
                Assert.IsTrue(File.Exists(Path.Combine(dir, "go", "nino_types.go")));
            }
            finally
            {
                Delete(dir);
            }
        }

        /// <summary>The targets pass every option an item can set, by its metadata, and nothing else.</summary>
        [TestMethod]
        public void TargetsMapEveryOption()
        {
            string targets = File.ReadAllText(Targets);
            var mapped = Regex.Matches(targets, @"Condition=""'%\(_NinoCodegenItem\.(\w+)\)' != ''"" NinoArgs=""%\(_NinoCodegenItem\.NinoArgs\) --([\w-]+) ")
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
            mapped.Add(("Out", "out"));
            var options = CodegenOptions.All.Where(o => o.Metadata != null).Select(o => (o.Metadata, o.Name)).ToList();
            CollectionAssert.AreEquivalent(options, mapped);
        }
    }
}
