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

        private const string Roots =
            "Ints=System.Collections.Generic.List<System.Int32>;Things=System.Collections.Generic.Dictionary<System.String,Shapes.ThingClass>;MaybeLong=System.Nullable<System.Int64>";

        /// <summary>--roots names whole payloads of types with no layout of their own, in every language.</summary>
        [TestMethod]
        public void EveryTargetWritesRoots()
        {
            var (ts, _) = Run("ts", "Nino.Codegen.Tests.ts", "--roots", Roots);
            StringAssert.Contains(ts, "export const IntsCodec: NinoCodec<number[] | null> = {");
            StringAssert.Contains(ts, "export const ThingsCodec: NinoCodec<Map<string | null, ThingClass | null> | null> = {");
            StringAssert.Contains(ts, "export const MaybeLongCodec: NinoCodec<bigint | null> = {");

            var (go, _) = Run("go", "nino_types.go", "--roots", Roots);
            StringAssert.Contains(go, "func ReadInts(r *nino.Reader) []int32 {");
            StringAssert.Contains(go, "func WriteMaybeLong(w *nino.Writer, v *int64) {");
            StringAssert.Contains(go, "case \"System.Collections.Generic.List<System.Int32>\":\n\t\treturn codecOf(ReadInts, ");

            var (rust, _) = Run("rust", "nino_types.rs", "--roots", Roots);
            StringAssert.Contains(rust, "pub type Ints = Option<Vec<i32>>;");
            StringAssert.Contains(rust, "pub type MaybeLong = Option<i64>;");
            StringAssert.Contains(rust, "\"System.Collections.Generic.List<System.Int32>\" => f.visit::<Ints>(),");

            // The other collections Nino.Core writes as a sequence or a map are roots too; a root whose formatter
            // nino-gen does not model (a Stack, which .NET writes from its top) is left out with a warning.
            var (others, output) = Run("go", "nino_types.go", "--roots",
                "Queue=System.Collections.Generic.Queue<System.Int32>;" +
                "Sorted=System.Collections.Generic.SortedDictionary<System.Int32,System.String>;" +
                "Stack=System.Collections.Generic.Stack<System.Int32>");
            StringAssert.Contains(others, "func ReadQueue(r *nino.Reader) []int32 {");
            StringAssert.Contains(others, "func ReadSorted(r *nino.Reader) []nino.Entry[int32, *string] {");
            StringAssert.Contains(output, "root Stack (System.Collections.Generic.Stack<System.Int32>) is not written to Go");
            Assert.IsFalse(others.Contains("ReadStack", StringComparison.Ordinal));

            // A root met after the model is built gets the types its slots hold (Based<int>'s subtype).
            var (based, _) = Run("go", "nino_types.go", "--roots", "Baseds=Shapes.Generic.Based<System.Int32>[]");
            StringAssert.Contains(based, "func ReadBaseds(r *nino.Reader) []Based[int32] {");
            StringAssert.Contains(based, "\tcase *BasedInt:\n");

            var (none, _) = Run("go", "nino_types.go");
            Assert.IsFalse(none.Contains("ReadInts", StringComparison.Ordinal));
        }

        /// <summary>A class and a generic class of one name and namespace (Twin, Twin&lt;T&gt;) get two names.</summary>
        [TestMethod]
        public void EveryTargetNamesAClassAndAGenericClassOfOneName()
        {
            foreach (var (lang, file, plain, generic) in new[]
                     {
                         ("go", "nino_types.go", "type ShapesGenericTwin interface {", "type ShapesGenericTwin1[T any] struct {"),
                         ("rust", "nino_types.rs", "pub enum ShapesGenericTwinRef {", "pub struct ShapesGenericTwin1<T> {"),
                         ("ts", "Nino.Codegen.Tests.ts", "export abstract class Shapes_Generic_Twin {", "export class Shapes_Generic_Twin1<T> {"),
                     })
            {
                var (text, _) = Run(lang, file);
                StringAssert.Contains(text, plain, lang);
                StringAssert.Contains(text, generic, lang);
            }
        }

        [TestMethod]
        public void RootsRejectBadRequests()
        {
            foreach (var roots in new[]
            {
                "ints=System.Int32[]x",                                   // an unknown type
                "1x=System.Int32[]",                                      // not an identifier
                "A=System.Int32[];A=System.Int64[]",                      // a name twice
                "A",                                                      // no type
                "ThingClass=System.Int32[]",                              // a generated name
                "A=System.Int32[];B=System.Int32[]",                      // one type twice
                "A=Shapes.ThingClass",                                    // a [NinoType]: it has its codec
            })
                Assert.ThrowsExactly<ArgumentException>(() => Run("rust", "nino_types.rs", "--roots", roots), roots);
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
        /// A raw <c>T?</c> as the element of a sequence is a slot of a fixed size in every language, whatever the
        /// sequence, under the same name in every language: Rust <c>nino::NullableSlot&lt;T&gt;</c> and Go
        /// <c>nino.NullableSlot[T]</c> as the element of a list, a set or a grid alike (<c>Option&lt;T&gt;</c> and
        /// <c>*T</c> are the single value), TypeScript the runtime's alias <c>NullableSlot&lt;T&gt;</c> of
        /// <c>T | null</c>; it stays a single value in a dictionary and a tuple.
        /// </summary>
        [TestMethod]
        public void RawNullableSlotsAreGeneratedInEveryLanguage()
        {
            var (rust, rustOutput) = Run("rust", "nino_types.rs");
            StringAssert.Contains(rust, "    pub queue: Option<Vec<nino::NullableSlot<i32>>>,\n    pub stack: Option<Vec<nino::NullableSlot<i64>>>,");
            StringAssert.Contains(rust, "    pub sorted: Option<indexmap::IndexSet<nino::NullableSlot<i32>>>,");
            StringAssert.Contains(rust, "    pub nested: Option<Vec<Option<Vec<nino::NullableSlot<i32>>>>>,");
            StringAssert.Contains(rust, "    pub boxed_nested: Option<Box<BoxType<Option<Vec<Option<indexmap::IndexSet<nino::NullableSlot<i64>>>>>>>>,");
            StringAssert.Contains(rust, "    pub map: Option<indexmap::IndexMap<i32, Option<i64>>>,");
            StringAssert.Contains(rust, "    pub tuples: Option<Vec<(i32, Option<i32>)>>,");
            StringAssert.Contains(rust, "    pub grid: Option<nino::Grid<nino::NullableSlot<i32>, 2>>,\n    pub cube: Option<nino::Grid<nino::NullableSlot<i64>, 3>>,");
            var (ts, tsOutput) = Run("ts", "Nino.Codegen.Tests.ts");
            StringAssert.Contains(ts, "  queue: NullableSlot<number>[] | null = null;\n  stack: NullableSlot<bigint>[] | null = null;");
            StringAssert.Contains(ts, "  sorted: Set<NullableSlot<number>> | null = null;");
            StringAssert.Contains(ts, "  boxedNested: Box<(Set<NullableSlot<bigint>> | null)[] | null> | null = null;");
            StringAssert.Contains(ts, "  tuples: ([number, number | null])[] | null = null;");
            StringAssert.Contains(ts, "import { NinoReader, NinoSubtypes, NinoWriter, type NinoCodec, type NinoGrid as NinoGrid$, type NinoNullable, type NullableSlot } from");
            StringAssert.Contains(ts, "  map: Map<number, bigint | null> | null = null;");
            // A set's slots are taken as one run, then each read at its offset.
            StringAssert.Contains(ts, "for (let i = 0, at = r.take(length * 8); i < length; i++, at += 8) values.add(");
            StringAssert.Contains(ts, "  grid: NinoGrid$<NullableSlot<number>[]> | null = null;");
            StringAssert.Contains(ts, "  cube: NinoGrid$<NullableSlot<bigint>[]> | null = null;");
            Assert.IsFalse(tsOutput.Contains("NullableGrids", StringComparison.Ordinal), tsOutput);
            var (go, goOutput) = Run("go", "nino_types.go");
            Assert.IsTrue(Regex.IsMatch(go, @"\n\tQueue +\[\]nino\.NullableSlot\[int32\] "));
            Assert.IsTrue(Regex.IsMatch(go, @"\n\tSorted +\[\]nino\.NullableSlot\[int32\] "));
            Assert.IsTrue(Regex.IsMatch(go, @"\n\tBoxedNested +\*Box\[\[\]\[\]nino\.NullableSlot\[int64\]\] "));
            Assert.IsTrue(Regex.IsMatch(go, @"\n\tMap +\[\]nino\.Entry\[int32, \*int64\] "));
            Assert.IsTrue(Regex.IsMatch(go, @"\n\tTuples +\[\]NinoTuple2\[int32, \*int32\] "));
            Assert.IsTrue(Regex.IsMatch(go, @"\n\tCube +\*NinoGrid\[nino\.NullableSlot\[int64\]\] "));
            // A run of a number is one block; any other element is decoded at its offsets into the value type.
            StringAssert.Contains(go, "v.Queue = nino.ReadNullableSlots[int32](r)");
            StringAssert.Contains(go, "return nino.NullableSlot[nino.Int128]{HasValue: true, Value: ");
            foreach (string output in new[] { rustOutput, goOutput })
                Assert.IsFalse(output.Contains("Nullable", StringComparison.Ordinal), output);
            Assert.IsFalse(tsOutput.Contains("NullableSlots", StringComparison.Ordinal), tsOutput);
        }

        [TestMethod]
        public void ClosedGenericStructsGiveGoCollectionsTheirMinimumSize()
        {
            var (go, _) = Run("go", "nino_types.go");
            Assert.IsTrue(Regex.IsMatch(go, @"func readV\d+\(r \*nino.Reader\) \[\]nino.Entry\[string, Pair\[int32, int32\]\] \{\n\tn := r.Length\(9\)"));
        }

        /// <summary>
        /// A <c>NinoNullable&lt;T&gt;</c> has a type of its own in Go and Rust (<c>nino.NinoNullable[T]</c>, a value
        /// and whether it has one; <c>nino::NinoNullable&lt;T&gt;</c>, an <c>Option&lt;T&gt;</c> with those bytes),
        /// where a single <c>T?</c> is a <c>*T</c> and an <c>Option&lt;T&gt;</c>, and in TypeScript the runtime's alias
        /// <c>NinoNullable&lt;T&gt;</c> of the <c>T | null</c> a <c>T?</c> is, with nothing at run time; every
        /// type holding one is written: alone, in every collection, in type arguments, and inside raw structs
        /// whatever the size of <c>T</c>.
        /// </summary>
        [TestMethod]
        public void NinoNullableIsGeneratedInEveryLanguage()
        {
            string[] names = { "NinoNullable", "HoldsNinoNullable", "HoldsWideNinoNullable" };
            var (rust, rustOutput) = Run("rust", "nino_types.rs");
            StringAssert.Contains(rust, "pub struct HoldsNinoNullable {\n    pub a: u8,\n    pub b: nino::NinoNullable<i32>,\n}");
            StringAssert.Contains(rust, "pub struct HoldsWideNinoNullable {\n    pub a: u8,\n    pub b: nino::NinoNullable<nino::Decimal>,\n}");
            StringAssert.Contains(rust, "    pub r#enum: nino::NinoNullable<i64>,\n    pub enums: Option<Vec<nino::NinoNullable<i64>>>,");
            StringAssert.Contains(rust, "    pub one: Option<Box<BoxType<nino::NinoNullable<i32>>>>,");
            StringAssert.Contains(rust, "    pub set: Option<indexmap::IndexSet<nino::NinoNullable<i32>>>,");
            var (ts, tsOutput) = Run("ts", "Nino.Codegen.Tests.ts");
            StringAssert.Contains(ts, "export class HoldsWideNinoNullable {\n  a: number = 0;\n  b: NinoNullable<string> = null;\n}");
            StringAssert.Contains(ts, "  one: Box<NinoNullable<number>> | null = null;");
            // The alias of T | null is imported by the file that names it, and a T? member stays T | null.
            StringAssert.Contains(ts, ", type NinoNullable");
            StringAssert.Contains(ts, "  maybe: Box<number | null> | null = null;");
            StringAssert.Contains(ts, "r.ninoNullableAt(");
            var (go, goOutput) = Run("go", "nino_types.go");
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(go,
                @"type HoldsWideNinoNullable struct \{\n\tA uint8 +`[^`]*`\n\tB nino\.NinoNullable\[nino\.Decimal\] "));
            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(go, @"\n\tOne +\*Box\[nino\.NinoNullable\[int32\]\] "));
            // A run of a number is one block; any other element is decoded at its offsets into the value type.
            StringAssert.Contains(go, "v.Set = nino.ReadNinoNullables[int32](r)");
            StringAssert.Contains(go, "return nino.NinoNullable[nino.Decimal]{HasValue: true, Value: ");
            StringAssert.Contains(go, "r.NinoNullableAt(b[0:16], b[16:32])");
            foreach (string output in new[] { rustOutput, tsOutput, goOutput })
            foreach (string name in names)
                Assert.IsFalse(output.Contains(name, StringComparison.Ordinal), output);
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
        [TestMethod]
        public void EveryTargetLeavesOutGenericUnionsAndTupleCases()
        {
            foreach (var (lang, file) in new[] { ("go", "nino_types.go"), ("rust", "nino_types.rs"), ("ts", "Nino.Codegen.Tests.ts") })
            {
                var (text, output) = Run(lang, file);
                StringAssert.Contains(output, "NINOGEN002: Shapes.Either`1 is not written");
                StringAssert.Contains(output, "a generic C# union is not supported");
                StringAssert.Contains(output, "NINOGEN002: Shapes.TupleCase is not written");
                StringAssert.Contains(output, "whose encoding in a union case is not published");
                Assert.IsFalse(text.Contains("TupleCase", StringComparison.Ordinal), lang);
                Assert.IsFalse(text.Contains("Either", StringComparison.Ordinal), lang);
            }
        }

        /// <summary>
        /// A union is each language's sum type: a TypeScript class naming its case, a Go interface with a struct per
        /// case, a Rust enum with a variant per case (a case named Empty after the empty union's own variant).
        /// </summary>
        [TestMethod]
        public void EveryTargetWritesAUnionAsItsSumType()
        {
            var (ts, _) = Run("ts", "Nino.Codegen.Tests.ts");
            // Every case told apart by its value: a plain union, written by testing the value.
            StringAssert.Contains(ts, "export type Pick = Empty | PickPair | number;");
            StringAssert.Contains(ts, "  if (v instanceof Empty) {");
            StringAssert.Contains(ts, "  else if (typeof v === \"number\") {");
            // An int and a double, two lists: a union tagged with each case's name, and its constructors.
            StringAssert.Contains(ts, "export type Measure =\n  | { readonly case: \"Int32\"; readonly value: number }\n" +
                                      "  | { readonly case: \"Double\"; readonly value: number }");
            StringAssert.Contains(ts, "  Int32: (value: number): Measure => ({ case: \"Int32\", value }),");
            StringAssert.Contains(ts, "  switch (v.case) {");
            Assert.IsFalse(ts.Contains("PickObject", StringComparison.Ordinal), "no object member: no class for one");
            var (go, _) = Run("go", "nino_types.go");
            StringAssert.Contains(go, "type Pick interface {\n\tisPick()\n}");
            // A generated class is the case itself; a byte and a raw struct are held by a struct of their own.
            StringAssert.Contains(go, "func (*Empty) isPick() {");
            StringAssert.Contains(go, "type PickByte struct {\n\tValue uint8");
            StringAssert.Contains(go, "func ReadPick(r *nino.Reader) Pick {");
            var (rust, _) = Run("rust", "nino_types.rs");
            StringAssert.Contains(rust, "pub enum Pick {\n    /// The empty union.\n    #[default]\n    Empty,");
            StringAssert.Contains(rust, "    EmptyCase(Option<Box<Empty>>),");
            StringAssert.Contains(rust, "    Byte(u8),");
        }
    }
}
