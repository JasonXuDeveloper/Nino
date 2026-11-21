using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits, for each base formatter that dispatches subtypes, the dispatch to the subtypes declared in this assembly:
/// on write a chain of exact runtime type checks (one method table compare each), on read a switch on the type id,
/// each writing the subtype's id and body, or calling a method of the base formatter that reads that subtype. Anything
/// else (subtypes in other assemblies, generic subtypes, tracked types) falls back to <c>NinoSubtypes&lt;T&gt;</c>.
/// A generic base dispatches each of its closed instances that subtypes here derive from in the same way.
/// Only calls are emitted, never subtype bodies (a raw body's type id and block reserved here, then its formatter's
/// WriteRawBody called), so the size is two lines and a one-line method per (base, subtype) pair.
/// </summary>
internal static class DispatchEmitter
{
    /// <summary>Above this many subtypes, writes go through the registry rather than a chain of type checks.</summary>
    internal const int MaxWriteChain = 64;

    /// <summary>
    /// A bucket of at most this many subtypes with distinct ids compares the ids directly, without the collision
    /// filter larger buckets need (5cf67a6d).
    /// </summary>
    private const int MaxDirectBucket = 4;

    private const string CompactServices = GeneratedCode.CompilerServicesPrefix;

    public static string Emit(EquatableArray<DispatchEntry> entries, bool fileScoped)
    {
        // Subtypes by base type name, in entry order.
        var subtypesOf = new Dictionary<string, List<DispatchEntry>>();
        Dictionary<string, SortedSet<string>>? closedOf = null;
        foreach (var e in entries)
        {
            if (!e.IsTarget)
                continue;
            AddSubtype(subtypesOf, e.BaseClasses, e);
            AddSubtype(subtypesOf, e.Interfaces, e);

            foreach (var ancestor in e.GenericAncestors)
            {
                closedOf ??= new Dictionary<string, SortedSet<string>>();
                if (!closedOf.TryGetValue(ancestor.Definition, out var instances))
                    closedOf[ancestor.Definition] = instances = new SortedSet<string>(StringComparer.Ordinal);
                instances.Add(ancestor.Type);
            }
        }

        string services = UseCompactServices(entries, closedOf) ? CompactServices : CoreCompilerServices;
        // Alias qualification bypasses a generic parameter with the same name.
        string genericServices = services == CompactServices ? GeneratedCode.CompilerServicesAlias + "::" : services;
        var w = new CodeWriter(4096);
        int closedCount = 0;
        Dictionary<string, DispatchEntry>? dispatchers = null;
        Dictionary<string, bool>? ownDispatch = null;
        foreach (var e in entries)
        {
            if (e.Middle || (e.IsBase && e.TypeParameterList.Length == 0))
                (dispatchers ??= new Dictionary<string, DispatchEntry>(StringComparer.Ordinal))[e.TypeName] = e;
        }

        // The nearest base of a class below a root that has a dispatch of its own, when the class forwards to it:
        // its own subtypes are more than half of that base's. A dispatch per (base, subtype) pair would grow with the
        // square of the depth of a chain of classes (PolymorphicFooBase's FooA to FooH, 16 to 19 of 20 subtypes each,
        // 1.8x the generated dispatch of the benchmark project and its generator benchmark 1.5x as long to analyze,
        // for no measured gain: PolymorphicClass 0.99-1.05x); halving at each level keeps it to n log n. A class with
        // few of the root's subtypes (CustomerEvent, 2 of DomainEvent's 8) tests only those: the root's whole chain
        // took the inlining budget of every caller (MethodJitInliningFailed "inline exceeds budget").
        DispatchEntry? ForwardTarget(DispatchEntry e, int count)
        {
            foreach (var ancestor in e.BaseClasses.Concat(e.Interfaces))
            {
                if (dispatchers == null || !dispatchers.TryGetValue(ancestor, out var a) || !OwnDispatchOf(a))
                    continue;
                int baseCount = Subtypes(subtypesOf, a.TypeName, a.TypeId)?.Count ?? 0;
                return count * 2 > baseCount ? a : null;
            }

            return null;
        }

        bool OwnDispatchOf(DispatchEntry e)
        {
            if (!e.Middle)
                return true;
            ownDispatch ??= new Dictionary<string, bool>(StringComparer.Ordinal);
            if (!ownDispatch.TryGetValue(e.TypeName, out bool own))
                ownDispatch[e.TypeName] = own =
                    ForwardTarget(e, Subtypes(subtypesOf, e.TypeName, e.TypeId)?.Count ?? 0) == null;
            return own;
        }

        foreach (var e in entries)
        {
            // A class below a root dispatches its own subtypes only where they are at most half of the subtypes of the
            // nearest base with a dispatch of its own, without the run loop: its collections go value by value, which
            // measured faster than a loop per such class (see EmitWriteRun). Any other class below a root forwards to
            // that base's dispatch (OwnDispatchOf).
            if (e.Middle)
            {
                var own = Subtypes(subtypesOf, e.TypeName, e.TypeId);
                var via = ForwardTarget(e, own?.Count ?? 0);
                w.Open($"partial struct {e.Self}");
                if (via == null)
                    EmitBase(w, e.TypeName, own, services, closedForm: false, e, runLoop: false);
                else
                    EmitForward(w, e.TypeName, via, services);
                w.Close();
                w.Line();
                continue;
            }

            if (!e.IsBase)
                continue;
            if (e.TypeParameterList.Length == 0)
            {
                var own = Subtypes(subtypesOf, e.TypeName, e.TypeId);
                w.Open($"partial struct {e.Self}");
                EmitBase(w, e.TypeName, own, services, closedForm: false, e, runLoop: true);
                w.Close();
                w.Line();
                continue;
            }

            // A generic base: each closed instance its subtypes here derive from gets the dispatch of a non-generic
            // base, in a class of its own (where ReadExact is not shared code). The generic formatter selects it by
            // comparing types, which the JIT folds where the type arguments are value types; for reference types it is
            // one lookup in the method's generic context.
            var closed = closedOf != null && closedOf.TryGetValue(e.TypeName, out var instances) ? instances : null;
            int first = closedCount;
            if (closed != null)
            {
                foreach (var c in closed)
                {
                    w.Open($"static class __NinoDispatch{closedCount++}");
                    EmitBase(w, c, Subtypes(subtypesOf, c, 0), services, closedForm: true, root: null,
                        runLoop: false);
                    w.Close();
                    w.Line();
                }
            }

            string t = e.TypeName;
            w.Open($"partial struct {e.Self}");
            EmitClosedSelection(w, t, closed, first, genericServices, write: true);
            // Two one-line forwarders stay together.
            if (closed is { Count: > 0 })
                w.Line();
            EmitClosedSelection(w, t, closed, first, genericServices, write: false);
            w.Close();
            w.Line();
        }

        // This file has a few lines per (base, subtype) pair, each repeating the same names.
        var aliases = new List<string>();
        if (services == CompactServices)
            aliases.Add(GeneratedCode.CompilerServicesDeclaration);
        var body = GeneratedCode.WithAliases(w, null, aliases);
        return GeneratedCode.File("", GeneratedCode.InlineAliases.Concat(aliases), body, fileScoped);
    }

    private static void AddSubtype(Dictionary<string, List<DispatchEntry>> subtypesOf,
        EquatableArray<string> ancestors, DispatchEntry entry)
    {
        foreach (var ancestor in ancestors)
        {
            if (!subtypesOf.TryGetValue(ancestor, out var list))
                subtypesOf[ancestor] = list = new List<DispatchEntry>();
            list.Add(entry);
        }
    }

    private static bool UseCompactServices(EquatableArray<DispatchEntry> entries,
        Dictionary<string, SortedSet<string>>? closedOf)
    {
        // Each base dispatch has two registry fallbacks. Two bodies pay for the alias.
        int bodies = 0;
        foreach (var entry in entries)
        {
            if (!entry.IsBase && !entry.Middle)
                continue;
            if (++bodies == 2 || (entry.TypeParameterList.Length != 0 && closedOf != null &&
                closedOf.ContainsKey(entry.TypeName)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A switch on the ids, which compiles to a binary search of conditional branches; each case ends with a tail
    /// call, so the dispatch keeps nothing across a call and saves no registers.
    /// Measured against a dense switch through a perfect hash of the ids (a multiplication, a shift and an indirect
    /// jump through a table) and the registry (an indirect call): with 20 subtypes in random order the hash read
    /// about 45% slower, since the indirect jump mispredicts where the conditional branches mostly do not, and in a
    /// fixed order it was equal; it executes one instruction more on x64 and three fewer on ARM64.
    /// </summary>
    /// <summary>A type name as a comment shows it: without the <c>global::</c> qualifier.</summary>
    private static string Display(string typeName) => typeName.Replace("global::", "");

    private static void EmitReadSwitch(CodeWriter w, List<DispatchEntry> subtypes)
    {
        w.Open("switch (id)");
        for (int i = 0; i < subtypes.Count; i++)
            // The subtype's name after the id: the ids alone do not say which type a case reads.
            w.Line($"case {subtypes[i].TypeId.ToString(CultureInfo.InvariantCulture)}: __ReadExact{i}(ref reader, ref value); return; // {Display(subtypes[i].TypeName)}");
        w.Close();
    }

    /// <summary>
    /// The subtypes of <paramref name="baseType"/> (other than itself, id <paramref name="baseId"/>) that bases dispatch
    /// to, without colliding ids, which cannot be switch cases: the registry reports the collision when one is used.
    /// </summary>
    private static List<DispatchEntry>? Subtypes(Dictionary<string, List<DispatchEntry>> subtypesOf, string baseType,
        int baseId)
    {
        if (!subtypesOf.TryGetValue(baseType, out var all))
            return null;
        if (all.Count == 1)
            return all[0].TypeId == baseId ? null : all;

        if (all.Count <= MaxDirectBucket && HasUniqueIds(all, baseId))
            return all;

        var subtypes = new List<DispatchEntry>();
        var seen = new HashSet<int>();
        HashSet<int>? collisions = null;
        foreach (var subtype in all)
        {
            if (subtype.TypeId == baseId)
                continue;
            if (seen.Add(subtype.TypeId))
                subtypes.Add(subtype);
            else
                (collisions ??= new HashSet<int>()).Add(subtype.TypeId);
        }

        if (collisions != null)
            subtypes.RemoveAll(t => collisions.Contains(t.TypeId));
        return subtypes;
    }

    private static bool HasUniqueIds(List<DispatchEntry> subtypes, int baseId)
    {
        for (int i = 0; i < subtypes.Count; i++)
        {
            int id = subtypes[i].TypeId;
            if (id == baseId)
                return false;
            for (int j = 0; j < i; j++)
            {
                if (subtypes[j].TypeId == id)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The dispatch of a class below a root through the dispatch of <paramref name="via"/>, a base of it: writing
    /// needs nothing more (its checks are exact); reading checks that the type the data named is one of this class.
    /// </summary>
    private static void EmitForward(CodeWriter w, string t, DispatchEntry via, string services)
    {
        w.Line($"{WriteSubtype(t)} => {via.Self}.__WriteSubtype(value, ref writer);");
        w.Line();
        w.Line(GeneratedCode.Inline);
        w.Open(ReadSubtype(t));
        w.Line($"{via.Self}.__ReadSubtype(id, ref reader, ref {services}NinoObject.AsBaseSlot<{t}, {via.TypeName}>(ref value));");
        w.Line($"{services}NinoObject.EnsureSubtype<{t}>(value);");
        w.Close();
    }

    /// <summary>The dispatch of the non-generic (or closed generic) base <paramref name="t"/> to <paramref name="subtypes"/>.</summary>
    private static void EmitBase(CodeWriter w, string t, List<DispatchEntry>? subtypes, string services, bool closedForm,
        DispatchEntry? root, bool runLoop)
    {
        // value.GetType() == typeof(X) compiles to one method table compare. WriteExactShallow is inlined where the
        // caller's inlining budget allows (the dispatch is inlined into its callers), else called as shared code. A
        // non-generic method per subtype, as on read, was measured: it writes 4.5% fewer instructions for an array of
        // 20 subtypes, but more where the bodies would have been inlined, and one more method per subtype costs about
        // 2.5% more compile time on a workload of hierarchies; outlining only the subtypes whose bodies nest (a70aab2c)
        // cost Neoverse-N2 a call per element (GenericSealedClassTupleBase serialize 0.84x). The depth is checked once
        // here instead: each subtype keeps its write inline without the deep path (a writer copy, or a deferral, per
        // subtype), and a body too deep goes out of line with the writer by value, so its address reaches no call.
        // A closed generic base's dispatch (__NinoDispatch<n>) is reached through the base formatter's selection, one
        // inlining level more than a non-generic base's: it is marked to inline, or a collection loop calls it per
        // element (341560ef without it: GenericIntBase serialize 0.84x on x64 runners, run 43174700489). The
        // non-generic one stays to the JIT's heuristics: forced, a 20-subtype dispatch grew its callers up to 9x.
        if (subtypes is { Count: > 0 and <= MaxWriteChain })
        {
            if (closedForm)
                w.Line(GeneratedCode.Inline);
            w.Open(WriteSubtype(t));
            w.Line($"if ({services}NinoObject.IsDeep(ref writer))");
            w.Open();
            w.Line("writer = __WriteSubtypeDeep(value, writer);");
            w.Line("return;");
            w.Close();
            w.Line();
            foreach (var s in subtypes)
                w.Line(WriteShallow(s, services, "return;"));
            w.Line(WriteOther(t, services) + ";");
            w.Close();
            if (runLoop)
                EmitWriteRun(w, t, root!, subtypes, services);

            w.Line();
            w.Line($"{GeneratedCode.NoInline} static {Core}Writer __WriteSubtypeDeep({t} value, {Core}Writer writer)");
            w.Open();
            foreach (var s in subtypes)
                w.Line($$"""if (value.GetType() == typeof({{s.TypeName}})) { {{services}}NinoObject.{{(s.RawBody ? "WriteExactRaw" : "WriteExact")}}<{{s.TypeName}}, {{s.FormatterName}}>(value, ref writer); return writer; }""");
            w.Line(WriteOther(t, services) + ";");
            w.Line("return writer;");
            w.Close();
        }
        else
        {
            w.Line($"{WriteSubtype(t)} => {WriteOther(t, services)};");
        }

        if (subtypes is not { Count: > 0 })
        {
            w.Line($"{ReadSubtype(t)} => {ReadOther(t, services)};");
            return;
        }

        w.Line();
        w.Line(GeneratedCode.Inline);
        w.Open(ReadSubtype(t));
        EmitReadSwitch(w, subtypes);
        w.Line(ReadOther(t, services) + ";");
        w.Close();

        // Each subtype's instance check and body in a method of this non-generic struct, where NinoObject.ReadExact is
        // not shared code (see there), and the switch inlined into the element loop, as LuminPack reads its unions: out
        // of line, each element took a call and two jumps (LuminPack's List<IFoo> of 1,000 read in 21.0 us against 18.1
        // inlined, LuminPack 17.5; Dictionary<int, IFoo> 362 against 314 ns; local x64, #305). Only flat bodies (no
        // class body inside, DispatchEntry.Flat) are inlined into the switch: subtypes holding a class each, inlined
        // too, read 3-14 % slower on every runner CPU (GenericSealedClassTupleBase, GenericOpenClassTupleBase, run
        // 43764424839), the rest 11-20 % faster.
        w.Line();
        for (int i = 0; i < subtypes.Count; i++)
        {
            var s = subtypes[i];
            w.Line($"{(s.Flat ? GeneratedCode.Inline : GeneratedCode.NoInline)} static void __ReadExact{i}(ref {Core}Reader reader, ref {t} value) => {services}NinoObject.ReadExact<{t}, {s.TypeName}, {s.FormatterName}>(ref reader, ref value);");
        }
    }

    /// <summary>
    /// The write of <paramref name="s"/> when the value is exactly that subtype and its body is not too deep, ending in
    /// <paramref name="exit"/>. A raw body's type id and block go into one reservation, written here rather than
    /// through NinoObject.WriteExactRawShallow: one inlining level fewer per subtype, so a dispatch of many keeps more of
    /// them within the JIT's budget, and one it leaves out is a call to the subtype's own formatter, not to shared
    /// generic code that looks the formatter up (#305).
    /// </summary>
    private static string WriteShallow(DispatchEntry s, string services, string exit) => s.RawBody
        ? $$"""if (value.GetType() == typeof({{s.TypeName}})) { ref byte __block = ref writer.Reserve(sizeof(int) + default({{s.FormatterName}}).BodySize); {{services}}NinoUnsafe.Write(ref __block, {{s.TypeId.ToString(CultureInfo.InvariantCulture)}}); default({{s.FormatterName}}).WriteRawBody({{services}}NinoObject.SameType<{{s.TypeName}}>(value), ref __block, sizeof(int)); {{exit}} }"""
        : $$"""if (value.GetType() == typeof({{s.TypeName}})) { {{services}}NinoObject.WriteExactShallow<{{s.TypeName}}, {{s.FormatterName}}>(value, ref writer); {{exit}} }""";

    /// <summary>
    /// <c>WriteRun</c> (<c>HasRunLoop</c>): the values of a run of any collection's storage (<c>NinoRun</c>: an array's,
    /// a list's, a ring half's, a set's or a dictionary's entries, each value after its key where the run has keys) in
    /// one loop with the chain of type checks inline and the writer in a local, which the JIT keeps in registers.
    /// Nino.Core owns the rest (the count, the depth check once per collection, the steps over the entries, the
    /// in-use test, the key's write, the deep path); only the chain is the base's. Written one value at a time through
    /// <c>Serialize</c>, each value took a call into the dispatch (too large to inline into a Nino.Core loop) with the
    /// writer in memory: LuminPack's List&lt;IFoo&gt; of 1,000 values of 20 subtypes took 5.0 us that way and 3.85 in
    /// this form (LuminPack 4.4; local x64, #305). A loop in Nino.Core calling a generated step per value lost on
    /// large hierarchies (#355's probe, run 44379569172: 1.4-1.8x the time of this loop for 20 subtypes), so the
    /// generator writes the loop and Nino.Core its pieces. A root that is a class of its own tests its own type first.
    /// </summary>
    private static void EmitWriteRun(CodeWriter w, string t, DispatchEntry root, List<DispatchEntry> subtypes,
        string services)
    {
        string self = root.Self;
        string run = $"{services}NinoRun";
        string loops = $"{Core}INinoFormatter";
        w.Line();
        w.Line($"bool {loops}.HasRunLoop => true;");
        w.Line();
        w.Open($"void {loops}.WriteRun({run} run, ref {Core}Writer target)");
        w.Line($"if ({services}NinoObject.IsDeep(ref target))");
        w.Open();
        w.Line($"{run}.WriteEach<{t}, {self}>(run, ref target);");
        w.Line("return;");
        w.Close();
        w.Line();
        w.Line("ref byte entry = ref run.First;");
        w.Line("int stride = run.Stride, key = run.Key, inUse = run.InUse;");
        w.Line($"{Core}Writer writer = default;");
        w.Line("target.CopyTo(ref writer);");
        w.Line($"for (int left = run.Count; left > 0; left--, entry = ref {run}.Next(ref entry, stride))");
        w.Open();
        w.Line($"if ({run}.Skips(ref entry, inUse))");
        w.Line("    continue;");
        w.Line($"{run}.WriteKey(ref entry, key, ref writer);");
        w.Line($"var value = {run}.Value<{t}>(ref entry);");
        w.Line("if (value == null) { writer.UnsafeWrite(0); continue; }");
        // A root that is a class of its own (not abstract) tests its own type first, as its Serialize does. Left to the
        // registry (NinoSubtypes.Serialize: a lookup and a virtual call), 1,000 values of a root with four subtypes,
        // most of its own type, wrote in 0.76-0.80 of the time (local x64, #355). Classes below the root keep no loop of
        // their own: one per class with subtypes (its own type, then its subtypes) wrote 1,000 values 1.14-1.22x as
        // long for three subtypes and 1.04-1.06x for twelve, against the value-by-value loop with the root's dispatch.
        if (root.IsTarget)
            w.Line(WriteShallow(root, services, "continue;"));
        foreach (var s in subtypes)
            w.Line(WriteShallow(s, services, "continue;"));
        // A subtype the chain does not know: the registry, with the writer by reference. With the writer by value
        // (writer = __WriteSubtypeDeep(value, writer)), dictionaries' pair loops wrote 12-100 pairs 15-25 % faster
        // (local x64), but arrays' element loops PolymorphicFooArray (20 subtypes) 12 % slower on Neoverse-N2 and 5 %
        // on EPYC 9V74, and NonGenericBase 6 % slower on 9V74 (run 43849791156); one loop now serves both.
        w.Line(WriteOther(t, services) + ";");
        w.Close();
        w.Line();
        w.Line("writer.CopyTo(ref target);");
        w.Close();
    }

    private static string WriteSubtype(string t) => $"internal static void __WriteSubtype({t} value, ref {Core}Writer writer)";

    private static string ReadSubtype(string t) =>
        $"internal static void __ReadSubtype(int id, ref {Core}Reader reader, ref {t} value)";

    /// <summary>The write of a subtype the dispatch does not know: through the registry.</summary>
    private static string WriteOther(string t, string services) =>
        $"{services}NinoSubtypes<{t}>.Serialize(value, ref writer)";

    /// <summary>The read of a subtype the dispatch does not know: through the registry.</summary>
    private static string ReadOther(string t, string services) =>
        $"{services}NinoSubtypes<{t}>.DeserializeOther(id, ref reader, ref value)";

    /// <summary>
    /// Selects a closed dispatch class in the same sorted order as its declaration, or forwards to the registry.
    /// </summary>
    private static void EmitClosedSelection(CodeWriter w, string t, SortedSet<string>? closed, int first,
        string services, bool write)
    {
        string signature = write ? WriteSubtype(t) : ReadSubtype(t);
        string fallback = write ? WriteOther(t, services) : ReadOther(t, services);
        if (closed is not { Count: > 0 })
        {
            w.Line($"{signature} => {fallback};");
            return;
        }

        w.Open(signature);
        foreach (var c in closed)
        {
            w.Line(write
                ? $$"""if (typeof({{t}}) == typeof({{c}})) { __NinoDispatch{{first}}.__WriteSubtype({{services}}NinoObject.SameType<{{c}}>(value), ref writer); return; }"""
                : $$"""if (typeof({{t}}) == typeof({{c}})) { __NinoDispatch{{first}}.__ReadSubtype(id, ref reader, ref {{services}}NinoObject.SameTypeSlot<{{t}}, {{c}}>(ref value)); return; }""");
            first++;
        }
        w.Line(fallback + ";");
        w.Close();
    }
}
