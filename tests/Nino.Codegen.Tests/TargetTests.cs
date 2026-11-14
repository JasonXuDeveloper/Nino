using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Nino.Codegen.Tests
{
    /// <summary>What each target decides for the shapes the corpus lacks, and the command line.</summary>
    [TestClass]
    public partial class TargetTests
    {
        [GeneratedRegex(@"typeV\d+ +\*boxCodec")]
        private static partial Regex GoClosedBoxCodec();

        [GeneratedRegex(@"\n\tTags +\[\]\*string ")]
        private static partial Regex GoStringTags();

        [GeneratedRegex(@"\n\tUtf8Tags +\[\]\*string ")]
        private static partial Regex GoUtf8StringTags();

        [GeneratedRegex(@"\n\tByName +\[\]nino\.Entry\[string, T\] ")]
        private static partial Regex GoStringMapEntries();

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
            Assert.IsFalse(GoClosedBoxCodec().IsMatch(go));
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
            Assert.IsGreaterThanOrEqualTo(0, at);
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
        public void EveryTargetLeavesOutSimdVectors()
        {
            foreach (var (lang, file) in new[] { ("go", "nino_types.go"), ("rust", "nino_types.rs"), ("ts", "Nino.Codegen.Tests.ts") })
            {
                var (text, output) = Run(lang, file);
                StringAssert.Contains(output, "System.Runtime.Intrinsics.Vector256`1");
                Assert.IsFalse(text.Contains("Simd", StringComparison.Ordinal), lang);
            }
        }

        [TestMethod]
        public void EveryTargetLeavesOutHostSizedVectors()
        {
            foreach (var (lang, file) in new[] { ("go", "nino_types.go"), ("rust", "nino_types.rs"), ("ts", "Nino.Codegen.Tests.ts") })
            {
                var (text, output) = Run(lang, file);
                StringAssert.Contains(output, "NINOGEN002: Shapes.OldVector");
                StringAssert.Contains(output, "is not serializable");
                Assert.IsFalse(text.Contains("OldVector", StringComparison.Ordinal), lang);
            }
        }

        [TestMethod]
        public void RustPutsStructsBehindTheirIdInSlots()
        {
            var (text, _) = Run("rust", "nino_types.rs");
            StringAssert.Contains(text, "ThingStruct::NINO_TYPE_ID => Self::ThingStruct(Box::new(r.read()?)),");
            StringAssert.Contains(text, "ThingClass::NINO_TYPE_ID => Self::ThingClass(Box::new(r.read_class()?)),");
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
            var (explicitPlain, _) = Run("ts", "Nino.Codegen.Tests.ts", "--numeric-arrays", "plain");
            var (typed, _) = Run("ts", "Nino.Codegen.Tests.ts", "--numeric-arrays", "typed");
            Assert.AreEqual(array, explicitPlain);
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

        /// <summary>
        /// A set's strings may be null (.NET writes a HashSet&lt;string&gt; holding null), in every language and with
        /// every option; a dictionary's keys never are (.NET throws on a null key).
        /// </summary>
        [TestMethod]
        public void SetStringsMayBeNullDictionaryKeysNever()
        {
            var (rust, _) = Run("rust", "nino_types.rs");
            StringAssert.Contains(rust, "pub tags: Option<indexmap::IndexSet<Option<String>>>,");
            StringAssert.Contains(rust, "pub utf8_tags: Option<indexmap::IndexSet<Option<nino::Utf8>>>,");
            var (wide, _) = Run("rust", "nino_types.rs", "--strings", "wide");
            StringAssert.Contains(wide, "pub tags: Option<indexmap::IndexSet<Option<nino::WideString>>>,");
            var (std, _) = Run("rust", "nino_types.rs", "--maps", "std");
            StringAssert.Contains(std, "pub tags: Option<std::collections::HashSet<Option<String>>>,");
            StringAssert.Contains(rust, "pub by_name: Option<indexmap::IndexMap<String, T>>,");
            var (go, _) = Run("go", "nino_types.go");
            Assert.IsTrue(GoStringTags().IsMatch(go));
            Assert.IsTrue(GoUtf8StringTags().IsMatch(go));
            Assert.IsTrue(GoStringMapEntries().IsMatch(go));
            var (ts, _) = Run("ts", "Nino.Codegen.Tests.ts");
            StringAssert.Contains(ts, "tags: Set<string | null> | null");
            StringAssert.Contains(ts, "utf8Tags: Set<string | null> | null");
        }

        /// <summary>
        /// A type declared with <c>NinoNullable&lt;T&gt;</c> gets the member types and the codec of the same type declared
        /// with <c>T?</c>, in every language: single values, arrays and lists (the corpus's types) and inside type
        /// arguments (Shapes.cs). <paramref name="goName"/>: Go keeps names starting with Nino for the runtime's.
        /// </summary>
        [TestMethod]
        [DataRow("conformance", "NinoNullables", "NinoNullablesType", "NullableTwins")]
        [DataRow("shapes", "NinoNullableArgs", "NinoNullableArgsType", "NullableArgs")]
        public void NinoNullableIsGeneratedAsNullable(string assembly, string ninoNullable, string goName, string nullable)
        {
            string path = assembly == "shapes" ? GeneratedFilesTests.Shapes : GeneratedFilesTests.Conformance;
            string Text(string lang)
            {
                var (files, output) = GeneratedFilesTests.Generate("--assembly", path, "--lang", lang);
                Assert.IsFalse(output.Contains(ninoNullable, StringComparison.Ordinal), output);
                return System.Linq.Enumerable.Single(files, f => !f.Key.StartsWith("nino.", StringComparison.Ordinal) &&
                                                                 f.Key != ".nino-gen").Value;
            }

            // The declaration and the two bodies of a type, from each one's first line to the line that closes it.
            static string Parts(string text, string name, params string[] starts)
            {
                var parts = new System.Text.StringBuilder();
                foreach (string start in starts)
                {
                    string first = string.Format(System.Globalization.CultureInfo.InvariantCulture, start, name);
                    int at = text.IndexOf(first, StringComparison.Ordinal);
                    Assert.IsGreaterThanOrEqualTo(0, at, first);
                    int end = text.IndexOf("\n}\n", at, StringComparison.Ordinal);
                    parts.Append(text, at, end + 3 - at);
                }

                return parts.ToString().Replace(name, "X", StringComparison.Ordinal);
            }

            void Same(string lang, string actualName, params string[] starts)
            {
                string text = Text(lang);
                Assert.AreEqual(Parts(text, nullable, starts), Parts(text, actualName, starts), lang);
            }

            Same("ts", ninoNullable, "export class {0} {{", "function readBody${0}(", "function writeBody${0}(");
            Same("go", goName, "type {0} struct {{", "func read{0}Body(", "func write{0}Body(");
            Same("rust", ninoNullable, "pub struct {0} {{", "impl NinoReadBody for {0} {{", "impl NinoWriteBody for {0} {{");
        }

        /// <summary>
        /// Inside a plain struct a <c>NinoNullable&lt;T&gt;</c> is a nullable value in every language where <c>T</c> is as
        /// large as its alignment (the layout of <c>T?</c>, which Rust's <c>Option&lt;T&gt;</c> in raw memory is). A
        /// wider <c>T</c> (decimal) has its value further on: TypeScript and Go read it there, Rust leaves the type out.
        /// </summary>
        [TestMethod]
        public void RustLeavesOutANinoNullableWiderThanItsAlignmentInRawMemory()
        {
            var (rust, output) = Run("rust", "nino_types.rs");
            StringAssert.Contains(rust, "pub struct HoldsNinoNullable {\n    pub a: u8,\n    pub b: Option<i32>,\n}");
            StringAssert.Contains(output, "Shapes.NinoNullableRawWide is not written to Rust");
            StringAssert.Contains(output, "is not the flag then the value at its alignment");
            Assert.IsFalse(rust.Contains("HoldsWideNinoNullable", StringComparison.Ordinal));
            var (ts, tsOutput) = Run("ts", "Nino.Codegen.Tests.ts");
            StringAssert.Contains(ts, "export class HoldsWideNinoNullable {\n  a: number = 0;\n  b: string | null = null;\n}");
            var (go, goOutput) = Run("go", "nino_types.go");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(go,
                @"type HoldsWideNinoNullable struct \{\n\tA uint8 +`[^`]*`\n\tB \*nino\.Decimal "));
            foreach (string text in new[] { tsOutput, goOutput })
                Assert.IsFalse(text.Contains("NinoNullable", StringComparison.Ordinal), text);
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
            StringAssert.Contains(text, "func ReadRecursiveInto(r *nino.Reader, v *Recursive) {\n\tif !r.EnterStruct() {");
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
                Assert.AreSequenceEqual(new[] { ".nino-gen", "Mine.ts", "Nino.Codegen.Tests.ts", "nino.ts" },
                    Array.ConvertAll(Directory.GetFiles(dir), Path.GetFileName), SequenceOrder.InAnyOrder);
                Assert.AreEqual("Nino.Codegen.Tests.ts\nnino.ts\n", File.ReadAllText(Path.Combine(dir, ".nino-gen")));

                Assert.AreEqual(0, Program.Run(new[]
                {
                    "--assembly", GeneratedFilesTests.Shapes, "--lang", "ts", "--out", dir, "--name", "shapes.ts",
                    "--runtime", "nino-serialization",
                }, new StringWriter()));
                Assert.AreSequenceEqual(new[] { ".nino-gen", "Mine.ts", "shapes.ts" },
                    Array.ConvertAll(Directory.GetFiles(dir), Path.GetFileName), SequenceOrder.InAnyOrder);
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
