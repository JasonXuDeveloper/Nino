using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits, for each base formatter that dispatches subtypes, the dispatch to the subtypes declared in this assembly:
/// on write a chain of exact runtime type checks (one method table compare each), in <c>__WriteSubtype</c> and in the
/// run loop; on read a switch on the type id whose cases read a subtype's body. Nino.Core owns the rest: the depth
/// (checked once, before the chain or the switch), the deep path, a slot holding another type, null, and anything the
/// dispatch does not know (subtypes in other assemblies, generic subtypes, tracked types), all through
/// <c>NinoSubtypes&lt;T&gt;</c>. A generic base dispatches each of its closed instances that subtypes here derive from
/// in the same way.
/// </summary>
internal static class DispatchEmitter
{
    /// <summary>Above this many subtypes, writes go through the registry rather than a chain of type checks.</summary>
    internal const int MaxWriteChain = 64;

    /// <summary>The most types a run loop tests that is marked to inline into the collection's write.</summary>
    private const int MaxInlinedRunLoop = 8;

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
            // base, in a class of its own (where ReadExactShallow is not shared code). The generic formatter selects it
            // by comparing types, which the JIT folds where the type arguments are value types; for reference types it
            // is one lookup in the method's generic context.
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

    /// <summary>A type name as a comment shows it: without the <c>global::</c> qualifier.</summary>
    private static string Display(string typeName) => typeName.Replace("global::", "");

    /// <summary>
    /// <c>__ReadSubtype</c>: the depth checked once, then a switch on the ids whose cases only read a body
    /// (<c>NinoObject.ReadExactShallow</c>: the slot holds null or that subtype), each ending in a tail call;
    /// the rest goes to the registry (<c>NinoSubtypes.DeserializeOther</c>): a body too deep, null or an id the
    /// switch does not know after the switch, a slot holding another type from the case. Each case returning
    /// whether it read, with the registry once after the switch, cost the out-of-line cases their tail call (a
    /// call, a test and a frame; __NinoDispatch0.__ReadSubtype 207 to 331 bytes). The switch compiles to a
    /// binary search of conditional branches. Measured against a dense switch through a perfect hash of the ids
    /// (a multiplication, a shift and an indirect jump through a table) and the registry (an indirect call):
    /// with 20 subtypes in random order the hash read about 45% slower, since the indirect jump mispredicts
    /// where the conditional branches mostly do not, and in a fixed order it was equal; it executes one
    /// instruction more on x64 and three fewer on ARM64. Each case reading its own deep path, replacement and
    /// deferral (as before #355's switch step) spent the containing body's locals (PolymorphicClass.ReadBody:
    /// "too many locals" with FooD's 20 cases inlined). The ids are 32-bit hashes, never dense enough for
    /// Roslyn to emit an IL switch (a jump table) instead.
    /// </summary>
    private static void EmitReadSubtype(CodeWriter w, string t, List<DispatchEntry> subtypes, string services)
    {
        w.Line(GeneratedCode.Inline);
        w.Open(ReadSubtype(t));
        w.Line($"if (!{services}NinoObject.IsDeep(ref reader))");
        w.Open();
        w.Open("switch (id)");
        for (int i = 0; i < subtypes.Count; i++)
        {
            // The subtype's name after the id: the ids alone do not say which type a case reads.
            var s = subtypes[i];
            w.Line($"case {s.TypeId.ToString(CultureInfo.InvariantCulture)}: __ReadExact{i}(ref reader, ref value); return; // {Display(s.TypeName)}");
        }
        w.Close();
        w.Close();
        w.Line();
        w.Line(ReadOther(t, services) + ";");
        w.Close();

        // Each subtype's read in a method of this non-generic struct, where ReadExactShallow is not shared code.
        // Only flat bodies (no class body inside, DispatchEntry.Flat) are inlined into the switch, which is inlined
        // into the element loop, as LuminPack reads its unions: out of line, each element took a call and two jumps
        // (LuminPack's List<IFoo> of 1,000 read in 21.0 us against 18.1 inlined, LuminPack 17.5; local x64, #305).
        // Subtypes holding a class each, inlined too, read 3-14 % slower on every runner CPU
        // (GenericSealedClassTupleBase, GenericOpenClassTupleBase, run 43764424839), the rest 11-20 % faster.
        w.Line();
        for (int i = 0; i < subtypes.Count; i++)
        {
            var s = subtypes[i];
            w.Line($"{(s.Flat ? GeneratedCode.Inline : GeneratedCode.NoInline)} static void __ReadExact{i}(ref {Core}Reader reader, ref {t} value) => {services}NinoObject.ReadExactShallow<{t}, {s.TypeName}, {s.FormatterName}>(ref reader, ref value);");
        }
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
        // subtype), and a body too deep goes to the registry out of line with the writer by value
        // (NinoSubtypes.SerializeDeep), so its address reaches no call and no second chain is generated for it.
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
            w.Line($"writer = {services}NinoSubtypes<{t}>.SerializeDeep(value, writer);");
            w.Line("return;");
            w.Close();
            w.Line();
            foreach (var s in subtypes)
                w.Line(WriteShallow(s, services, "return;"));
            w.Line(WriteOther(t, services) + ";");
            w.Close();
            if (runLoop)
                EmitWriteRun(w, t, root!, subtypes, services);
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
        EmitReadSubtype(w, t, subtypes, services);
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
    /// one loop with the chain of type checks inline and the writer's cursor and free bytes in two locals, which the JIT
    /// keeps in registers.
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
        // A small hierarchy's loop is marked to inline into the collection's write, where the JIT inlined the loop
        // that kept the writer in a local by itself: with the cursor's hand-offs around each body it no longer did,
        // and a collection of one to five values (StateSnapshot's components) paid a call and the run's copy.
        if (subtypes.Count + (root.IsTarget ? 1 : 0) <= MaxInlinedRunLoop)
            w.Line(GeneratedCode.Inline);
        w.Open($"void {loops}.WriteRun({run} run, ref {Core}Writer target)");
        w.Line($"if ({services}NinoObject.IsDeep(ref target))");
        w.Open();
        w.Line($"{run}.WriteEach<{t}, {self}>(run, ref target);");
        w.Line("return;");
        w.Close();
        w.Line();
        EmitRunSplit(w, t, root, subtypes, services);
    }

    /// <summary>
    /// The rest of a run loop's method after the depth check: an array's or a list's run, which has no free entries and
    /// no keys, in this method's loop, a set's or a dictionary's entries in a loop of their own (<c>__WriteEntries</c>),
    /// which tests both. One loop testing a local for them held the key and the in-use test in two callee-saved
    /// registers and left the loop's count and value on the stack (x64 has six); closes the method.
    /// </summary>
    private static void EmitRunSplit(CodeWriter w, string t, DispatchEntry? root, List<DispatchEntry> subtypes,
        string services)
    {
        string run = $"{services}NinoRun";
        w.Line($"if ((run.Key | (run.InUse ^ {run}.AllInUse)) != 0)");
        w.Open();
        w.Line("__WriteEntries(run, ref target);");
        w.Line("return;");
        w.Close();
        w.Line();
        EmitRunLoop(w, t, root, subtypes, services, entries: false);
        w.Close();
        w.Line();
        w.Line(GeneratedCode.NoInline);
        w.Open($"private static void __WriteEntries({run} run, ref {Core}Writer target)");
        EmitRunLoop(w, t, root, subtypes, services, entries: true);
        w.Close();
    }

    /// <summary>
    /// The loop of <c>WriteRun</c> over a run's values, with the cursor in locals; <paramref name="entries"/>: the run's
    /// entries may be free or carry keys (a set's or a dictionary's), tested per entry.
    /// </summary>
    private static void EmitRunLoop(CodeWriter w, string t, DispatchEntry? root, List<DispatchEntry> subtypes,
        string services, bool entries)
    {
        string run = $"{services}NinoRun";
        w.Line("ref byte entry = ref run.First;");
        w.Line("int stride = run.Stride;");
        // The cursor and the free bytes in two locals, which the JIT keeps in registers; the target writer takes them
        // back around anything else that writes (a key, a body that is not one raw block, the registry).
        w.Line("ref byte cur = ref target.UnsafeCursor(out int avail);");
        w.Line($"for (int left = run.Count; left > 0; left--, entry = ref {run}.Next(ref entry, stride))");
        w.Open();
        if (entries)
        {
            w.Line($"if ({run}.Skips(ref entry, run.InUse))");
            w.Line("    continue;");
            w.Line($"if (run.Key != {run}.NoKey) {{ target.UnsafeSetCursor(ref cur, avail); {run}.WriteKey(ref entry, run.Key, ref target); cur = ref target.UnsafeCursor(out avail); }}");
        }
        w.Line($"var value = {run}.Value<{t}>(ref entry);");
        w.Line($"if (value == null) {{ if (avail < sizeof(int)) {{ target.UnsafeGrowAt(ref cur, avail, sizeof(int)); cur = ref target.UnsafeCursor(out avail); }} {services}NinoUnsafe.Write(ref cur, 0); cur = ref {services}NinoUnsafe.Add(ref cur, sizeof(int)); avail -= sizeof(int); continue; }}");
        // A root that is a class of its own (not abstract) tests its own type first, as its Serialize does. Left to the
        // registry (NinoSubtypes.Serialize: a lookup and a virtual call), 1,000 values of a root with four subtypes,
        // most of its own type, wrote in 0.76-0.80 of the time (local x64, #355). Classes below the root keep no loop of
        // their own: one per class with subtypes (its own type, then its subtypes) wrote 1,000 values 1.14-1.22x as
        // long for three subtypes and 1.04-1.06x for twelve, against the value-by-value loop with the root's dispatch.
        if (root is { IsTarget: true })
            w.Line(WriteAtCursor(root, services));
        // The chain written out here again, not shared with __WriteSubtype through one AggressiveInlining method
        // (returning whether it wrote, or ending in the registry): the run loop of PolymorphicFooBase's 20 subtypes
        // then called it per element at Tier1 ("too many locals": the inlinee's argument temporaries on top of the
        // loop's; WriteRun 2,620 to 1,391 bytes plus the call), and at FullOpts the chain was a root of its own.
        foreach (var s in subtypes)
            w.Line(WriteAtCursor(s, services));
        // A subtype the chain does not know: the registry, with the target writer.
        w.Line($"target.UnsafeSetCursor(ref cur, avail); {WriteOther(t, services, "target")}; cur = ref target.UnsafeCursor(out avail);");
        w.Close();
        w.Line();
        w.Line("target.UnsafeSetCursor(ref cur, avail);");
    }

    /// <summary>
    /// The write of <paramref name="s"/> in a run loop that holds the cursor in locals (<c>cur</c>, <c>avail</c>): a
    /// raw body's type id and block at the cursor after one check of the free bytes, any other body through the target
    /// writer, given the cursor back around it.
    /// </summary>
    private static string WriteAtCursor(DispatchEntry s, string services) => s.RawBody
        ? $$"""if (value.GetType() == typeof({{s.TypeName}})) { int __n = sizeof(int) + default({{s.FormatterName}}).BodySize; if ((uint)__n > (uint)avail) { target.UnsafeGrowAt(ref cur, avail, __n); cur = ref target.UnsafeCursor(out avail); } {{services}}NinoUnsafe.Write(ref cur, {{s.TypeId.ToString(CultureInfo.InvariantCulture)}}); default({{s.FormatterName}}).WriteRawBody({{services}}NinoObject.SameType<{{s.TypeName}}>(value), ref cur, sizeof(int)); cur = ref {{services}}NinoUnsafe.Add(ref cur, __n); avail -= __n; continue; }"""
        : $$"""if (value.GetType() == typeof({{s.TypeName}})) { target.UnsafeSetCursor(ref cur, avail); {{services}}NinoObject.WriteExactShallow<{{s.TypeName}}, {{s.FormatterName}}>(value, ref target); cur = ref target.UnsafeCursor(out avail); continue; }""";

    private static string WriteSubtype(string t) => $"internal static void __WriteSubtype({t} value, ref {Core}Writer writer)";

    private static string ReadSubtype(string t) =>
        $"internal static void __ReadSubtype(int id, ref {Core}Reader reader, ref {t} value)";

    /// <summary>The write of a subtype the dispatch does not know: through the registry.</summary>
    private static string WriteOther(string t, string services, string writer = "writer") =>
        $"{services}NinoSubtypes<{t}>.Serialize(value, ref {writer})";

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
