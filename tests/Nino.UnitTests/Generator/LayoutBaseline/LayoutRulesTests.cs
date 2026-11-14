using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;
using Nino.Generator.Common;

#nullable disable
namespace Nino.UnitTests;

/// <summary>
/// The rules of the layout baseline (src/Nino.Generator/Common/LayoutRules.cs, which the analyzer and nino-gen compile
/// in): every rule with a change it lets through and one it reports, and which readers each breaks.
/// </summary>
[TestClass]
public class LayoutRulesTests
{
    private const string Both =
        "this build cannot read data written before the change, and builds before it cannot read data written after it";

    private static LayoutEntry E(string format, string header, string members, int id = 0x11111111,
        string key = "Ns.T", bool? deferrable = true, bool raw = false, bool track = false) =>
        LayoutEntry.FromLayout(key, Array.IndexOf(LayoutEntry.FormatNames, format),
            Array.IndexOf(LayoutEntry.HeaderNames, header), track, raw, id, deferrable, members);

    private static LayoutEntry Keyed(string members, string header = "Presence") => E("Keyed", header, members);
    private static LayoutEntry Tolerant(string members) => E("VersionTolerant", "Presence", members);
    private static LayoutEntry Positional(string members) => E("Positional", "None", members, deferrable: false);

    private static string K(string name) => "0x" + NinoHash.Compute(name).ToString("X8");

    private static LayoutFinding One(LayoutEntry old, LayoutEntry now, bool idWritten = false,
        Func<string, string, bool> sameType = null)
    {
        var findings = LayoutRules.Compare(old, now, idWritten, sameType);
        Assert.AreEqual(1, findings.Count, string.Join("\n", findings.Select(f => f.Change)));
        return findings[0];
    }

    private static void None(LayoutEntry old, LayoutEntry now, bool idWritten = false,
        Func<string, string, bool> sameType = null)
    {
        var findings = LayoutRules.Compare(old, now, idWritten, sameType);
        Assert.AreEqual(0, findings.Count, string.Join("\n", findings.Select(f => f.Change)));
    }

    private static void Breaks(LayoutFinding finding, string member = null)
    {
        Assert.AreEqual(LayoutSeverity.Breaking, finding.Severity, finding.Change);
        StringAssert.EndsWith(finding.Change, "so " + Both);
        Assert.AreEqual(member, finding.Member);
    }

    [TestMethod]
    public void NothingChanged()
    {
        const string members = "A:int:raw(4)";
        None(Positional(members), Positional(members));
        None(Tolerant(members), Tolerant(members));
        None(Keyed($"A:int:raw(4):key={K("A")}"), Keyed($"A:int:raw(4):key={K("A")}"));
    }

    [TestMethod]
    public void FormatAndTracking()
    {
        Breaks(One(Tolerant("A:int:raw(4)"), Keyed($"A:int:raw(4):key={K("A")}")));
        StringAssert.Contains(One(Tolerant("A:int:raw(4)"), Keyed($"A:int:raw(4):key={K("A")}")).Change,
            "from VersionTolerant to Keyed");
        var tracked = E("VersionTolerant", "Presence", "A:int:raw(4)", track: true);
        Breaks(One(Tolerant("A:int:raw(4)"), tracked));
        Breaks(One(tracked, Tolerant("A:int:raw(4)")));
        None(tracked, E("VersionTolerant", "Presence", "A:int:raw(4)", track: true));
    }

    [TestMethod]
    public void SealedAddedOrRemoved()
    {
        var sealedClass = Keyed($"A:int:raw(4):key={K("A")}");
        var open = Keyed($"A:int:raw(4):key={K("A")}", "TypeId");
        var unsealed = One(sealedClass, open);
        Breaks(unsealed);
        Assert.AreEqual(LayoutFix.AddSealed, unsealed.FixKind);
        var sealedNow = One(open, sealedClass);
        Breaks(sealedNow);
        Assert.AreEqual(LayoutFix.RemoveSealed, sealedNow.FixKind);
        var structNow = One(sealedClass, Keyed($"A:int:raw(4):key={K("A")}", "None"));
        Breaks(structNow);
        Assert.AreEqual(LayoutFix.Accept, structNow.FixKind);
    }

    [TestMethod]
    public void TypeIdMattersOnlyWhereItIsWritten()
    {
        int oldId = NinoHash.Compute("Old.Ns.Item");
        var old = E("Keyed", "TypeId", "", oldId, "Old.Ns.Item");
        var moved = One(old, E("Keyed", "TypeId", "", NinoHash.Compute("Ns.Item"), "Ns.Item"));
        Breaks(moved);
        Assert.AreEqual(LayoutFix.FormerName, moved.FixKind);
        Assert.AreEqual("Old.Ns.Item", moved.Argument);
        // [NinoFormerName("Old.Ns.Item")] keeps the id.
        None(old, E("Keyed", "TypeId", "", oldId, "Ns.Item"));
        // A sealed class writes no id of its own, unless it is written in its base's slot.
        var sealedOld = E("Keyed", "Presence", "", oldId, "Old.Ns.Item");
        var sealedNow = E("Keyed", "Presence", "", NinoHash.Compute("Ns.Item"), "Ns.Item");
        None(sealedOld, sealedNow);
        Breaks(One(sealedOld, sealedNow, idWritten: true));
        // Generic types have no id of their own (0): their arguments' names make it at run time.
        None(E("Keyed", "TypeId", "", 0, "Ns.Box<T>"), E("Keyed", "TypeId", "", 0, "Ns.Bag<T>"));
        // An id from a [NinoFormerName] taken away cannot be brought back by the name alone.
        var lost = One(E("Keyed", "TypeId", "", 0x12345678, "Ns.Item"), E("Keyed", "TypeId", "", NinoHash.Compute("Ns.Item"), "Ns.Item"));
        Breaks(lost);
        Assert.AreEqual(LayoutFix.Accept, lost.FixKind);
    }

    [TestMethod]
    public void RawLayout()
    {
        const string raw = "raw(8){X:float@0:raw(4),Y:float@4:raw(4)}";
        None(E("Positional", "None", raw, raw: true), E("Positional", "None", raw, raw: true));
        Breaks(One(E("Positional", "None", raw, raw: true),
            E("Positional", "None", "raw(12){X:float@0:raw(4),Y:float@4:raw(4),Z:float@8:raw(4)}", raw: true)));
        Breaks(One(E("Positional", "None", raw, raw: true), E("Positional", "None", "X:float:raw(4);Y:float:raw(4)")));
    }

    [TestMethod]
    public void Deferrable()
    {
        var finding = One(Tolerant("A:int:raw(4)"), E("VersionTolerant", "Presence", "A:int:raw(4)", deferrable: false));
        Assert.AreEqual(LayoutSeverity.Info, finding.Severity);
        StringAssert.Contains(finding.Change, "deeper than 128");
        // A baseline from a generator that did not publish it compares nothing.
        None(E("VersionTolerant", "Presence", "A:int:raw(4)", deferrable: null),
            E("VersionTolerant", "Presence", "A:int:raw(4)", deferrable: false));
    }

    [TestMethod]
    public void CollectionElements()
    {
        None(Tolerant("[elements]::seq(raw(4))"), Tolerant("[elements]::seq(raw(4))"));
        Breaks(One(Tolerant("[elements]::seq(raw(4))"), Tolerant("[elements]::seq(raw(8))")));
    }

    /// <summary>
    /// A member changed from <c>T?</c> to <c>NinoNullable&lt;T&gt;</c> (or back) is written the same way: only the type's
    /// name differs, the encoding (what the rules compare) does not, in any format.
    /// </summary>
    [TestMethod]
    public void NinoNullableInPlaceOfNullableChangesNothing()
    {
        const string stride = "seq(raw(16){hasValue:bool@0:raw(1),value:long@8:raw(8)})";
        string Members(string nullable, string key = "") =>
            $"A:{nullable}<long>:nullable(raw(8)){(key.Length == 0 ? "" : ":key=" + K("A"))};" +
            $"B:{nullable}<long>[]:{stride}{(key.Length == 0 ? "" : ":key=" + K("B"))};" +
            $"C:System.Collections.Generic.List<{nullable}<long>>:{stride}{(key.Length == 0 ? "" : ":key=" + K("C"))}";
        const string old = "System.Nullable", now = "Nino.Core.NinoNullable";
        None(Positional(Members(old)), Positional(Members(now)));
        None(Positional(Members(now)), Positional(Members(old)));
        None(Tolerant(Members(old)), Tolerant(Members(now)));
        None(Keyed(Members(old, "key")), Keyed(Members(now, "key")));
        // The same change with another T is a change of the bytes.
        Breaks(One(Positional("A:System.Nullable<long>:nullable(raw(8))"),
            Positional("A:Nino.Core.NinoNullable<int>:nullable(raw(4))")), "A");
    }

    [TestMethod]
    public void PositionalAnyChangeBreaks()
    {
        // Names are not written: a rename that keeps the bytes is no change.
        None(Positional("A:int:raw(4);B:long:raw(8)"), Positional("First:int:raw(4);B:long:raw(8)"));
        Breaks(One(Positional("A:int:raw(4);B:long:raw(8)"), Positional("A:int:raw(4);B:int:raw(4)")), "B");
        Breaks(One(Positional("A:int:raw(4)"), Positional("A:int:raw(4);B:int:raw(4)")), "B");
        Breaks(One(Positional("A:int:raw(4);B:int:raw(4)"), Positional("A:int:raw(4)")));
        Breaks(One(Positional("A:int:raw(4);B:string:string"), Positional("B:string:string;A:int:raw(4)")), "B");
    }

    [TestMethod]
    public void VersionTolerantAppendsOnly()
    {
        const string old = "A:int:raw(4);B:string:string";
        None(Tolerant(old), Tolerant(old + ";C:long:raw(8)"));
        None(Tolerant(old), Tolerant("A:int:raw(4);Name:string:string"));

        var deleted = One(Tolerant(old), Tolerant("B:string:string"));
        Breaks(deleted);
        StringAssert.Contains(deleted.Change, "no longer has member 'A'");

        var inserted = One(Tolerant(old), Tolerant("A:int:raw(4);C:long:raw(8);B:string:string"));
        Breaks(inserted, "C");
        Assert.AreEqual(LayoutFix.MoveToEnd, inserted.FixKind);
        StringAssert.Contains(inserted.Change, "inserted before 'B'");

        var moved = One(Tolerant(old), Tolerant("B:string:string;A:int:raw(4)"));
        Breaks(moved, "B");
        StringAssert.Contains(moved.Change, "moved before 'A'");

        var encoding = One(Tolerant(old), Tolerant("A:long:raw(8);B:string:string"));
        Breaks(encoding, "A");
        StringAssert.Contains(encoding.Change, "from raw(4) to raw(8)");
    }

    [TestMethod]
    public void KeyedAddsAndRemoves()
    {
        string a = $"A:int:raw(4):key={K("A")}", b = $"B:string:string:key={K("B")}";
        None(Keyed(a), Keyed(a + ";" + b));
        None(Keyed(a + ";" + b), Keyed(b + ";" + a));

        var removed = One(Keyed(a + ";" + b), Keyed(a));
        Assert.AreEqual(LayoutSeverity.Info, removed.Severity);
        Assert.IsNull(removed.Member);
        StringAssert.Contains(removed.Change, "no longer has keyed member 'B'");
        StringAssert.Contains(removed.Change, "this build skips it in data written before the change, and builds before it read their default for it");
    }

    [TestMethod]
    public void KeyedRenames()
    {
        string a = $"A:int:raw(4):key={K("A")}";
        // With the former name: new reads old, old does not know the new key.
        var withFormer = One(Keyed(a), Keyed($"Health:int:raw(4):key={K("Health")}:former={K("A")}"));
        Assert.AreEqual(LayoutSeverity.Info, withFormer.Severity);
        Assert.AreEqual("Health", withFormer.Member);
        StringAssert.Contains(withFormer.Change,
            "this build reads it from data written before the change, but builds before it do not know its new key");
        StringAssert.Contains(withFormer.Fix, "deploy the builds that read the new name before any that writes it");

        // Without it: both sides lose the value without an error.
        var silent = One(Keyed(a), Keyed($"Health:int:raw(4):key={K("Health")}"));
        Assert.AreEqual(LayoutSeverity.SilentLoss, silent.Severity);
        Assert.AreEqual("Health", silent.Member);
        Assert.AreEqual(LayoutFix.FormerName, silent.FixKind);
        Assert.AreEqual("A", silent.Argument);
        StringAssert.Contains(silent.Change,
            "this build reads its default value from data written before the change, and builds before it drop the value it writes");

        // An added member written differently is an addition, and the old one a removal.
        var findings = LayoutRules.Compare(Keyed(a), Keyed($"Health:string:string:key={K("Health")}"), false);
        Assert.AreEqual(1, findings.Count);
        Assert.AreEqual(LayoutSeverity.Info, findings[0].Severity);
        Assert.IsNull(findings[0].Member);

        // Renamed with the former name but written differently: breaks.
        Breaks(One(Keyed(a), Keyed($"Health:long:raw(8):key={K("Health")}:former={K("A")}")), "Health");
    }

    [TestMethod]
    public void KeyedEncodingOfAKey()
    {
        var finding = One(Keyed($"Name:string:string:key={K("Name")}"), Keyed($"Name:string:utf8:key={K("Name")}"));
        Breaks(finding, "Name");
        StringAssert.Contains(finding.Change, $"changed what key {K("Name")} is written as from string to utf8");
    }

    [TestMethod]
    public void TypesInEncodingsByTheirOwnSections()
    {
        string old = $"Item:Ns.Old:type(Ns.Old):key={K("Item")}";
        string now = $"Item:Ns.New:type(Ns.New):key={K("Item")}";
        Breaks(One(Keyed(old), Keyed(now)), "Item");
        None(Keyed(old), Keyed(now), sameType: (a, b) => a == "Ns.Old" && b == "Ns.New");
        // The type arguments of a generic type are part of the encoding.
        Breaks(One(Keyed($"Box:Ns.Box<int>:type(Ns.Box<int>):key={K("Box")}"),
                Keyed($"Box:Ns.Box<long>:type(Ns.Box<long>):key={K("Box")}"), sameType: (a, _) => a.StartsWith("Ns.")), "Box");
    }

    [TestMethod]
    public void RemovedType()
    {
        var finding = LayoutRules.Removed(E("Keyed", "Presence", "", key: "Ns.Gone"));
        Breaks(finding);
        StringAssert.Contains(finding.Fix, "[NinoFormerName(\"Ns.Gone\")]");
    }

    [TestMethod]
    public void ScopeIsWhatStoredTypesReach()
    {
        var save = E("Keyed", "Presence", $"Pos:Ns.Vec:type(Ns.Vec):key={K("Pos")};Pets:System.Collections.Generic.List<Ns.Animal>:seq(type(Ns.Animal)):key={K("Pets")};Box:Ns.Box<Ns.Inner>:type(Ns.Box<Ns.Inner>):key={K("Box")}", key: "Ns.Save");
        var vec = E("Positional", "None", "X:float:raw(4)", key: "Ns.Vec");
        var animal = E("Positional", "TypeId", "Name:string:string", key: "Ns.Animal");
        var dog = E("Positional", "Presence", "Name:string:string", key: "Ns.Dog");
        var box = E("Positional", "Presence", "Value:T:type(T)", key: "Ns.Box<T>");
        var inner = E("Positional", "None", "A:int:raw(4)", key: "Ns.Outer+Inner");
        var nested = E("Positional", "None", "A:int:raw(4)", key: "Ns.Inner");
        var alone = E("Positional", "None", "A:int:raw(4)", key: "Ns.Alone");
        var tolerant = E("VersionTolerant", "Presence", "", key: "Ns.Settings");
        var all = new[] { save, vec, animal, dog, box, inner, nested, alone, tolerant };
        var scope = LayoutRules.Scope(all, e => e == dog ? new[] { "Ns.Animal" } : Array.Empty<string>());
        CollectionAssert.AreEquivalent(new[] { "Ns.Save", "Ns.Vec", "Ns.Animal", "Ns.Dog", "Ns.Box<T>", "Ns.Inner", "Ns.Settings" },
            scope.ToArray());
    }

    [TestMethod]
    public void PairingRenamedAndMovedTypes()
    {
        var byId = E("Keyed", "Presence", "", 0x0BADF00D, "Old.Save");
        var byName = E("Keyed", "Presence", "", 0x1234, "Old.Profile");
        var ambiguous = E("Keyed", "Presence", "", 0x5678, "Old.Item");
        var pairs = LayoutRules.Pair(new[] { byId, byName, ambiguous }, new[]
        {
            new KeyValuePair<string, int>("New.GameSave", 0x0BADF00D),
            new KeyValuePair<string, int>("New.Profile", 0x9999),
            new KeyValuePair<string, int>("A.Item", 1),
            new KeyValuePair<string, int>("B.Item", 2),
        });
        Assert.AreSame(byId, pairs["New.GameSave"]);
        Assert.AreSame(byName, pairs["New.Profile"]);
        Assert.AreEqual(2, pairs.Count);
        Assert.AreEqual("Inner`2", LayoutRules.SimpleName("Ns.Outer+Inner<T,U>"));
        Assert.AreEqual("Ns.Outer.Inner", LayoutRules.Dotted("Ns.Outer+Inner<T,U>"));
        Assert.AreEqual("Ns.Outer.Inner", LayoutRules.Dotted("Ns.Outer<int>.Inner<System.Collections.Generic.List<int>>"));
    }

    // The baseline format

    [TestMethod]
    public void RoundTripsEveryField()
    {
        var entries = new List<LayoutEntry>
        {
            E("Keyed", "Presence", $"Health:int:raw(4):key={K("Health")}:former={K("Hp")},{K("HitPoints")};Name:string:utf8:key={K("Name")}", key: "Ns.Save"),
            E("Positional", "None", "raw(16){A:System.ValueTuple<int, long>@0:raw(16){Item1:int@0:raw(4),Item2:long@8:raw(8)}}", key: "Ns.Raw", raw: true, deferrable: false),
            E("VersionTolerant", "TypeId", "[elements]::seq(type(Ns.Item));Count:int:raw(4)", key: "Ns.Bag", track: true, deferrable: null),
            E("Positional", "Presence", "", key: "Ns.Outer+Empty<T>", id: 0),
        };
        string text = LayoutBaseline.Write(entries);
        var parsed = LayoutBaseline.Parse(text);
        Assert.AreEqual(text, LayoutBaseline.Write(parsed));
        // Sorted by key, whatever the order given.
        CollectionAssert.AreEqual(new[] { "Ns.Bag", "Ns.Outer+Empty<T>", "Ns.Raw", "Ns.Save" }, parsed.Select(e => e.Key).ToArray());
        var save = parsed.Single(e => e.Key == "Ns.Save");
        Assert.AreEqual(2, save.Members[0].Former.Count);
        Assert.AreEqual(NinoHash.Compute("Health"), save.Members[0].Key);
        Assert.IsNull(parsed.Single(e => e.Key == "Ns.Bag").Deferrable);
        Assert.AreEqual("seq(type(Ns.Item))", parsed.Single(e => e.Key == "Ns.Bag").Elements);
        foreach (var entry in parsed)
            Assert.AreEqual(0, LayoutRules.Compare(entries.Single(e => e.Key == entry.Key), entry, true).Count);
        StringAssert.Contains(text, $"\n['Ns.Save']\nformat = 'Keyed'\nheader = 'Presence'\nid = 0x11111111\ndeferrable = true\nmembers = [\n  {{ name = 'Health', type = 'int', encoding = 'raw(4)', key = {K("Health")}, former = [{K("Hp")}, {K("HitPoints")}] }},\n");
    }

    [TestMethod]
    public void RoundTripsPublishedLayouts()
    {
        var assembly = CornerCompiler.Compile(@"
using System.Collections.Generic;
using Nino.Core;
namespace Rt
{
    [NinoType] public struct Move { public float X; public float Y; }
    [NinoType(format: NinoFormat.Keyed)] public sealed class Save { [NinoFormerName(""Hp"")] public int Health; [NinoUtf8] public string Name; public List<Move> Path; public Dictionary<string, int[]> Index; public (int, string) Pair; public Box<Move> Boxed; }
    [NinoType(format: NinoFormat.VersionTolerant)] public sealed class Box<T> { public T Value; }
    [NinoType(format: NinoFormat.VersionTolerant)] public class Animal { public string Name; }
    [NinoType(trackReferences: true)] public sealed class Node { public Node Next; }
}").Assembly;
        var entries = assembly.GetCustomAttributes<NinoTypeLayoutAttribute>().Select(a =>
            LayoutEntry.FromLayout(a.Type.FullName, (int)a.Format, (int)a.Header, a.TrackReferences, a.Raw, a.Id,
                a.ToString().Contains("deferrable=") ? a.Deferrable : null, a.Members)).ToList();
        Assert.AreEqual(5, entries.Count);
        string text = LayoutBaseline.Write(entries);
        Assert.AreEqual(text, LayoutBaseline.Write(LayoutBaseline.Parse(text)));
        // Nothing in it needs escaping in a C# verbatim string.
        Assert.IsFalse(text.Contains('"'));
        foreach (var section in LayoutBaseline.Parse(text))
            Assert.AreEqual(0, LayoutRules.Compare(section, entries.Single(e => e.Key == section.Key), true).Count);
    }

    [TestMethod]
    public void ParseErrorsNameTheLine()
    {
        void Fails(string text, int line, string message)
        {
            var e = Assert.ThrowsExactly<LayoutBaselineException>(() => LayoutBaseline.Parse(text));
            Assert.AreEqual(line, e.Line, e.Message);
            StringAssert.Contains(e.Message, message);
        }

        Fails("\nformat = 'Keyed'\n", 2, "expected a table");
        Fails("['T']\nformat = 'Keyed'\nheader = 'Presence'\n", 1, "has no id");
        Fails("['T']\nformat = 'Keyed'\nheader = 'Presence'\nid = 0x1\ncolour = 'red'\n", 5, "unknown key 'colour'");
        Fails("['T']\nformat = \"Keyed\"\n", 2, "strings are in single quotes");
        Fails("['T']\nformat = 'Fast'\n", 2, "format must be Positional, VersionTolerant, Keyed");
        Fails("['T']\nformat = 'Keyed'\nheader = 'None'\nid = 0x1\nmembers = [\n  { name = 'A', encoding = 'raw(4)', size = 4 },\n]\n", 6, "unknown member key 'size'");
        Fails("['T']\nformat = 'Keyed'\nheader = 'None'\nid = 0x1\n['T']\n", 5, "second table");
        Fails("['T']\nformat = 'Keyed\n", 2, "ends on its line");
        Fails("['T']\nid = 0x1 0x2\n", 2, "after the value");
        // Comments and blank lines are fine.
        Assert.AreEqual(1, LayoutBaseline.Parse("# saves\n\n['T'] # the type\nformat = 'Keyed'\nheader = 'None'\nid = 12\nmembers = [ # none\n]\n").Count);
    }

    [TestMethod]
    public void UpdateRewritesOneSection()
    {
        var a = E("Keyed", "Presence", $"A:int:raw(4):key={K("A")}", key: "Ns.A");
        var c = E("Keyed", "Presence", $"C:int:raw(4):key={K("C")}", key: "Ns.C");
        string text = "\n# kept\n" + LayoutBaseline.Write(new[] { a, c }).Substring(1);
        var b = E("Keyed", "Presence", $"B:int:raw(4):key={K("B")}", key: "Ns.B");
        string inserted = LayoutBaseline.Update(text, new[] { new KeyValuePair<string, string>("Ns.B", LayoutBaseline.Section(b)) });
        Assert.AreEqual("\n# kept\n" + LayoutBaseline.Write(new[] { a, b, c }).Substring(1), inserted);
        var a2 = E("VersionTolerant", "Presence", "A:int:raw(4)", key: "Ns.A");
        string replaced = LayoutBaseline.Update(inserted, new[]
        {
            new KeyValuePair<string, string>("Ns.A", LayoutBaseline.Section(a2)),
            new KeyValuePair<string, string>("Ns.C", null),
        });
        Assert.AreEqual("\n# kept\n" + LayoutBaseline.Write(new[] { a2, b }).Substring(1), replaced);
        Assert.AreEqual(LayoutBaseline.Write(new[] { a2, b }),
            LayoutBaseline.Write(LayoutBaseline.Parse(replaced.Replace("\n", "\r\n"))));
    }
}
