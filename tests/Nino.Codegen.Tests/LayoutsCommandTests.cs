using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Nino.Codegen.Tests
{
    /// <summary>
    /// <c>nino-gen layouts</c>: the layout baseline of a built assembly (Shapes.cs), written and checked with the rules the
    /// analyzer uses.
    /// </summary>
    [TestClass]
    public class LayoutsCommandTests
    {
        private static string NewFile() =>
            Path.Combine(Path.GetTempPath(), "nino-layouts-" + Guid.NewGuid().ToString("N"), "NinoLayouts.cs");

        private static (int Exit, string Output) Run(params string[] args)
        {
            var output = new StringWriter();
            int exit = Program.Run(args, output);
            return (exit, output.ToString());
        }

        private static (int Exit, string Output) Write(string file) =>
            Run("layouts", "--assembly", GeneratedFilesTests.Shapes, "--out", file);

        private static (int Exit, string Output) Check(string file) =>
            Run("layouts", "--assembly", GeneratedFilesTests.Shapes, "--out", file, "--check");

        private static void InTemp(Action<string> test)
        {
            string file = NewFile();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            try
            {
                test(file);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(file), true);
            }
        }

        [TestMethod]
        public void WritesTheTypesStoredDataReaches() => InTemp(file =>
        {
            var (exit, output) = Write(file);
            Assert.AreEqual(0, exit, output);
            string text = File.ReadAllText(file);
            StringAssert.StartsWith(text, "// The layout baseline of this assembly");
            StringAssert.Contains(text, "[assembly: Nino.Core.NinoLayouts(@\"\n");
            // The keyed and version-tolerant types and what they hold, by runtime-form name; not the positional
            // types nothing stored holds (Shapes.Left.Item...).
            CollectionAssert.AreEqual(new[]
            {
                "['InspectSamples.KeyedCustom']", "['Shapes.Generic.Bag<T>']", "['Shapes.Generic.Box<T>']",
                "['Shapes.Generic.Node<T>']",
                "['Shapes.Generic.Pair<TKey,TValue>']", "['Shapes.Renamed']",
            }, text.Split('\n').Where(l => l.StartsWith("['", StringComparison.Ordinal)).ToArray());
            StringAssert.Contains(text,
                "  { name = 'NewName', type = 'int', encoding = 'raw(4)', key = 0x");
            StringAssert.Contains(output, "layout baseline of 6 type(s)");
        });

        [TestMethod]
        public void CheckPassesOnItsOwnBaseline() => InTemp(file =>
        {
            Write(file);
            var (exit, output) = Check(file);
            Assert.AreEqual(0, exit, output);
            StringAssert.Contains(output, "the layouts match the baseline");
        });

        [TestMethod]
        public void CheckFailsOnABreakingChangeAndNamesTheLine() => InTemp(file =>
        {
            Write(file);
            string text = File.ReadAllText(file);
            // As if the assembly had changed Other from a UTF-8 string to... the baseline remembers it as utf8.
            string changed = text.Replace("{ name = 'Other', type = 'string', encoding = 'string'",
                "{ name = 'Other', type = 'string', encoding = 'utf8'");
            Assert.AreNotEqual(text, changed);
            File.WriteAllText(file, changed);
            var (exit, output) = Check(file);
            Assert.AreEqual(1, exit, output);
            int line = changed.Split('\n').ToList().FindIndex(l => l.Contains("name = 'Other'")) + 1;
            StringAssert.Contains(output, $"NinoLayouts.cs({line}): warning NINO034: 'Shapes.Renamed.Other' changed what key 0x");
            StringAssert.Contains(output, "is written as from utf8 to string");
            StringAssert.Contains(output, "1 change(s) from the baseline");
        });

        [TestMethod]
        public void CheckReportsRemovedTypesAndInformationalChanges() => InTemp(file =>
        {
            Write(file);
            string text = File.ReadAllText(file);
            // A section of a type the assembly no longer has, and a keyed member it no longer has.
            string changed = text.Replace("['Shapes.Renamed']",
                    "['Shapes.Gone']\nformat = 'Keyed'\nheader = 'Presence'\nid = 0x12345678\nmembers = []\n\n['Shapes.Renamed']")
                .Replace("members = [\n  { name = 'NewName'", "members = [\n  { name = 'Lost', type = 'int', encoding = 'raw(4)', key = 0x0000002A },\n  { name = 'NewName'");
            File.WriteAllText(file, changed);
            var (exit, output) = Check(file);
            Assert.AreEqual(1, exit, output);
            StringAssert.Contains(output, "warning NINO034: 'Shapes.Gone' is no longer a [NinoType] of this assembly");
            StringAssert.Contains(output, "info NINO036: 'Shapes.Renamed' no longer has keyed member 'Lost'");
        });

        [TestMethod]
        public void WritingAgainKeepsTheRestOfTheFile() => InTemp(file =>
        {
            Write(file);
            string text = File.ReadAllText(file);
            string edited = "// Ours.\n" + text.Replace("['Shapes.Renamed']", "['Shapes.Stale']\nformat = 'Keyed'\nheader = 'None'\nid = 0x1\n\n['Shapes.Renamed']");
            File.WriteAllText(file, edited);
            Assert.AreEqual(0, Write(file).Exit);
            Assert.AreEqual("// Ours.\n" + text, File.ReadAllText(file));
        });

        [TestMethod]
        public void UnreadableBaselineFailsWithItsLine() => InTemp(file =>
        {
            Write(file);
            string text = File.ReadAllText(file).Replace("format = 'Keyed'", "format = Keyed");
            File.WriteAllText(file, text);
            var (exit, output) = Check(file);
            Assert.AreEqual(1, exit);
            int line = text.Split('\n').ToList().FindIndex(l => l == "format = Keyed") + 1;
            StringAssert.Contains(output, $"NinoLayouts.cs({line}): error NINO037: The layout baseline cannot be read: 'Keyed' is not a value");
        });

        [TestMethod]
        public void CommandLineMistakes()
        {
            Assert.ThrowsExactly<ArgumentException>(() => Run("layouts", "--assembly", GeneratedFilesTests.Shapes));
            Assert.ThrowsExactly<ArgumentException>(() => Run("layouts", "--lang", "ts"));
            Assert.ThrowsExactly<ArgumentException>(() => Run("layouts", "--assembly", GeneratedFilesTests.Shapes,
                "--out", NewFile(), "--check"));
            var (exit, output) = Run("layouts", "--help");
            Assert.AreEqual(0, exit);
            StringAssert.StartsWith(output, "Usage: nino-gen layouts");
            StringAssert.Contains(Run("--help").Output, "nino-gen layouts --assembly <dll> --out <NinoLayouts.cs>");
        }
    }
}
