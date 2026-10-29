using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nino.Codegen.Tests
{
    /// <summary>What each target decides for the shapes the corpus lacks, and the command line.</summary>
    [TestClass]
    public class TargetTests
    {
        private static (string Text, string Output) Run(string lang, string file, params string[] extra)
        {
            var args = new System.Collections.Generic.List<string>
                { "--assembly", GeneratedFilesTests.Shapes, "--lang", lang };
            args.AddRange(extra);
            var (files, output) = GeneratedFilesTests.Generate(args.ToArray());
            return (files[file], output);
        }

        /// <summary>
        /// The closed generic types the shapes name (Box&lt;int&gt;...) sit in no slot (no slot's type is assignable
        /// from them, and no member is an object): no target builds a dispatch case or a subclass for them.
        /// </summary>
        [TestMethod]
        public void ClosedTypesNoSlotHoldsAreLeftOutOfTheDispatch()
        {
            var (go, _) = Run("go", "nino_types.go");
            StringAssert.Contains(go, "func BoxCodec[T any](");
            Assert.IsFalse(go.Contains("newBoxCodec", StringComparison.Ordinal));
            // The closed types slots do hold (MoreOpen<int> in OpenUse's slot of Open<int>) have codecs; Box<int> none.
            StringAssert.Contains(go, "typeV0 = newMoreOpenCodec(");
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(go, @"typeV\d+ +\*boxCodec"));
            var (ts, _) = Run("ts", "Nino.Codegen.Tests.ts");
            StringAssert.Contains(ts, "interface Box$Codecs<T> {\n  p0: NinoCodec<T>;\n  self: NinoCodec<Box<T> | null>;\n}");
            Assert.IsFalse(ts.Contains("= Box$codecs<", StringComparison.Ordinal));
            var (rust, _) = Run("rust", "nino_types.rs");
            Assert.IsFalse(rust.Contains("BoxInt32", StringComparison.Ordinal));
        }

        [TestMethod]
        public void RustCountsAStructHoldingItself()
        {
            var (text, _) = Run("rust", "nino_types.rs");
            int at = text.IndexOf("impl NinoRead for Recursive {", StringComparison.Ordinal);
            Assert.IsTrue(at >= 0);
            StringAssert.Contains(text.Substring(at, 400), "r.enter_struct()?;");
        }

        [TestMethod]
        public void RustNamesKeywordsAndReservedNames()
        {
            var (text, _) = Run("rust", "nino_types.rs");
            StringAssert.Contains(text, "pub r#type: i32,");
            StringAssert.Contains(text, "pub self_: i32,");
            StringAssert.Contains(text, "pub r#match: i32,");
            // Two types named Item: each takes its namespace.
            StringAssert.Contains(text, "pub struct ShapesLeftItem {");
            StringAssert.Contains(text, "pub struct ShapesRightItem {");
        }

        [TestMethod]
        public void RustLeavesOutKeysItCannotHash()
        {
            var (text, output) = Run("rust", "nino_types.rs");
            StringAssert.Contains(output, "Shapes.FloatKeys is not written to Rust");
            Assert.IsFalse(text.Contains("pub struct FloatKeys", StringComparison.Ordinal));
        }

        [TestMethod]
        public void RustPutsStructsBehindTheirIdInSlots()
        {
            var (text, _) = Run("rust", "nino_types.rs");
            StringAssert.Contains(text, "ThingStruct::TYPE_ID => Self::ThingStruct(Box::new(r.read()?)),");
            StringAssert.Contains(text, "ThingClass::TYPE_ID => Self::ThingClass(Box::new(r.read_class()?)),");
        }

        [TestMethod]
        public void RustSerdeNames()
        {
            var (camel, _) = Run("rust", "nino_types.rs", "--serde");
            StringAssert.Contains(camel, "serde(rename_all = \"camelCase\")");
            var (original, _) = Run("rust", "nino_types.rs", "--serde", "--serde-names", "original");
            StringAssert.Contains(original, "serde(rename_all = \"PascalCase\")");
            var (none, _) = Run("rust", "nino_types.rs");
            Assert.IsFalse(none.Contains("serde", StringComparison.Ordinal));
        }

        [TestMethod]
        public void TypeScriptNumericArrays()
        {
            var (array, _) = Run("ts", "Nino.Codegen.Tests.ts");
            var (explicitArray, _) = Run("ts", "Nino.Codegen.Tests.ts", "--numeric-arrays", "array");
            var (typed, _) = Run("ts", "Nino.Codegen.Tests.ts", "--numeric-arrays", "typed");
            Assert.AreEqual(array, explicitArray);
            StringAssert.Contains(array, "Box<number[] | null>");
            Assert.IsFalse(array.Contains("Int32Array", StringComparison.Ordinal));
            StringAssert.Contains(typed, "Box<Int32Array | null>");
            StringAssert.Contains(typed, "return r.numbers(Int32Array, length);");
            Assert.ThrowsExactly<ArgumentException>(() => Run("ts", "Nino.Codegen.Tests.ts", "--numeric-arrays", "list"));
            Assert.ThrowsExactly<ArgumentException>(() => Run("go", "nino_types.go", "--numeric-arrays", "typed"));
        }

        [TestMethod]
        public void RustMaps()
        {
            var (indexmap, _) = Run("rust", "nino_types.rs");
            var (explicitIndexmap, _) = Run("rust", "nino_types.rs", "--maps", "indexmap");
            var (std, _) = Run("rust", "nino_types.rs", "--maps", "std");
            Assert.AreEqual(indexmap, explicitIndexmap);
            StringAssert.Contains(indexmap, "indexmap::IndexMap<");
            StringAssert.Contains(indexmap, "indexmap::IndexSet<");
            Assert.IsFalse(std.Contains("indexmap", StringComparison.Ordinal));
            StringAssert.Contains(std, "std::collections::HashMap<");
            StringAssert.Contains(std, "std::collections::HashSet<");
            Assert.ThrowsExactly<ArgumentException>(() => Run("rust", "nino_types.rs", "--maps", "btree"));
        }

        [TestMethod]
        public void GenericTypesAreGenericInEveryLanguage()
        {
            var (rust, rustOutput) = Run("rust", "nino_types.rs");
            StringAssert.Contains(rust, "pub struct BoxType<T> {");
            StringAssert.Contains(rust, "pub number: Option<Box<BoxType<i32>>>,");
            var (go, goOutput) = Run("go", "nino_types.go", "--package", "shapes");
            StringAssert.Contains(go, "type Box[T any] struct {");
            StringAssert.Contains(go, "func BoxCodec[T any](p0 NinoArg[T]) NinoArg[*Box[T]] {");
            var (ts, tsOutput) = Run("ts", "Nino.Codegen.Tests.ts");
            StringAssert.Contains(ts, "export class Box<T> {");
            StringAssert.Contains(ts, "export function PairCodec<TKey, TValue>(");
            // Open<T> is written behind a type id (the hash of a closed .NET name): a slot per closed type, which
            // allows the closed types of its generic subtypes too, those the assembly never names included.
            foreach (string output in new[] { rustOutput, goOutput, tsOutput })
                Assert.IsFalse(output.Contains("Shapes.Generic.Open`1 is not written to", StringComparison.Ordinal), output);
            StringAssert.Contains(ts, "export class Open<T> {");
            StringAssert.Contains(ts, "Shapes.Generic.MoreOpen<System.Int64>");
            StringAssert.Contains(go, "type OpenType[T any] interface {");
            StringAssert.Contains(go, "func (*MoreOpen[T]) isOpenType(T) {");
            StringAssert.Contains(go, "Shapes.Generic.MoreOpen<System.Int64>");
            // A set element or map key in Rust needs Eq and Hash, which a class (Box<long>) does not have.
            StringAssert.Contains(rustOutput, "Shapes.Generic.UnhashedKeys is not written to Rust");
            StringAssert.Contains(rust, "pub struct Keys<T: Eq + core::hash::Hash> {");
            StringAssert.Contains(rust, "pub int_keys: Option<Box<Keys<i32>>>,");
        }

        [TestMethod]
        public void GoKeepsItsOwnNamesFree()
        {
            var (text, _) = Run("go", "nino_types.go", "--package", "shapes");
            StringAssert.Contains(text, "type NinoGridType struct {");
            StringAssert.Contains(text, "type ShapesLeftItem struct {");
            StringAssert.Contains(text, "func ReadRecursive(r *nino.Reader) (v Recursive) {\n\tif !r.EnterStruct() {");
            StringAssert.Contains(text, "\tcase ThingStruct:\n\t\tw.TypeID(");
        }

        [TestMethod]
        public void GoTags()
        {
            var (text, _) = Run("go", "nino_types.go", "--package", "shapes", "--tags", "json",
                "--tag-names", "snake");
            StringAssert.Contains(text, "`nino:\"0\" json:\"value\"`");
            Assert.IsFalse(text.Contains("yaml:", StringComparison.Ordinal));
        }

        [TestMethod]
        public void TypeScriptWritesTheRuntimeUnlessImported()
        {
            var (files, _) = GeneratedFilesTests.Generate("--assembly", GeneratedFilesTests.Shapes, "--lang", "ts");
            Assert.IsTrue(files.ContainsKey("nino.ts"));
            StringAssert.Contains(files["Nino.Codegen.Tests.ts"], "from \"./nino\";");
            var (imported, _) = GeneratedFilesTests.Generate("--assembly", GeneratedFilesTests.Shapes, "--lang",
                "ts", "--runtime", "nino-serialization");
            Assert.IsFalse(imported.ContainsKey("nino.ts"));
            StringAssert.Contains(imported["Nino.Codegen.Tests.ts"], "from \"nino-serialization\";");
        }

        [TestMethod]
        public void CommandLineErrors()
        {
            Assert.ThrowsExactly<ArgumentException>(() => Program.Run(new[] { "--lang", "go" }, new StringWriter()));
            Assert.ThrowsExactly<ArgumentException>(() => Program.Run(
                new[] { "--assembly", GeneratedFilesTests.Shapes, "--lang", "cobol", "--out", Path.GetTempPath() },
                new StringWriter()));
            Assert.ThrowsExactly<ArgumentException>(() => Program.Run(
                new[] { "--assembly", GeneratedFilesTests.Shapes, "--lang", "go", "--out", Path.GetTempPath(),
                    "--tag-names", "kebab" }, new StringWriter()));
            Assert.AreEqual(1, Program.Main(new[] { "--lang" }));
            // Unknown options, options of another language and options given twice are errors, not ignored.
            foreach (var extra in new[]
                     {
                         new[] { "--tag-name", "snake" }, new[] { "--serde" }, new[] { "--package", "a", "--package", "b" },
                     })
            {
                var args = new System.Collections.Generic.List<string>
                    { "--assembly", GeneratedFilesTests.Shapes, "--lang", "go", "--out", Path.GetTempPath() };
                args.AddRange(extra);
                Assert.ThrowsExactly<ArgumentException>(() => Program.Run(args.ToArray(), new StringWriter()),
                    string.Join(" ", extra));
            }

            Assert.ThrowsExactly<ArgumentException>(() => Program.Run(
                new[] { "--assembly", GeneratedFilesTests.Shapes, "--lang", "rust", "--out", Path.GetTempPath(),
                    "--serde", "yes" }, new StringWriter()));

            var help = new StringWriter();
            Assert.AreEqual(0, Program.Run(new[] { "--help" }, help));
            StringAssert.Contains(help.ToString(), "rust: [--serde] [--serde-names camel|snake|original] [--strings string|wide] [--maps indexmap|std]");
        }

        [TestMethod]
        public void FlagsTakeAnOptionalValue()
        {
            var (on, _) = Run("rust", "nino_types.rs", "--serde", "true");
            var (flag, _) = Run("rust", "nino_types.rs", "--serde");
            var (off, _) = Run("rust", "nino_types.rs", "--serde", "false");
            var (none, _) = Run("rust", "nino_types.rs");
            Assert.AreEqual(flag, on);
            Assert.AreEqual(none, off);
            Assert.AreNotEqual(none, on);
        }

        [TestMethod]
        public void TheManifestDeletesFilesNoLongerWritten()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nino-gen-" + Guid.NewGuid().ToString("N"));
            try
            {
                // nino-ts's manifest, from a Nino.TypeScript build, is taken over.
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "Old.ts"), "");
                File.WriteAllText(Path.Combine(dir, "Mine.ts"), "");
                File.WriteAllText(Path.Combine(dir, ".nino-ts"), "Old.ts\nnino.ts\n");
                string[] args = { "--assembly", GeneratedFilesTests.Shapes, "--lang", "ts", "--out", dir };
                Assert.AreEqual(0, Program.Run(args, new StringWriter()));
                CollectionAssert.AreEquivalent(new[] { ".nino-gen", "Mine.ts", "Nino.Codegen.Tests.ts", "nino.ts" },
                    Array.ConvertAll(Directory.GetFiles(dir), f => Path.GetFileName(f)));
                Assert.AreEqual("Nino.Codegen.Tests.ts\nnino.ts\n", File.ReadAllText(Path.Combine(dir, ".nino-gen")));

                Assert.AreEqual(0, Program.Run(new[]
                {
                    "--assembly", GeneratedFilesTests.Shapes, "--lang", "ts", "--out", dir, "--name", "shapes.ts",
                    "--runtime", "nino-serialization",
                }, new StringWriter()));
                CollectionAssert.AreEquivalent(new[] { ".nino-gen", "Mine.ts", "shapes.ts" },
                    Array.ConvertAll(Directory.GetFiles(dir), f => Path.GetFileName(f)));
            }
            finally
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
        }

        [TestMethod]
        public void UnchangedFilesAreLeftAlone()
        {
            string path = Path.Combine(Path.GetTempPath(), "nino-gen-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                Program.WriteIfChanged(path, "a\r\nb\n");
                Assert.AreEqual("a\nb\n", File.ReadAllText(path));
                var stamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, stamp);
                Program.WriteIfChanged(path, "a\nb\n");
                Assert.AreEqual(stamp, File.GetLastWriteTimeUtc(path));
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
