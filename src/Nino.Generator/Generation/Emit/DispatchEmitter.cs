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
/// Only calls are emitted, never subtype bodies, so the size is two lines and a one-line method per (base, subtype)
/// pair.
/// </summary>
internal static class DispatchEmitter
{
    /// <summary>Above this many subtypes, writes go through the registry rather than a chain of type checks.</summary>
    private const int MaxWriteChain = 64;

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
        foreach (var e in entries)
        {
            if (!e.IsBase)
                continue;
            if (e.TypeParameterList.Length == 0)
            {
                w.Open($"partial struct {e.Self}");
                EmitBase(w, e.TypeName, Subtypes(subtypesOf, e.TypeName, e.TypeId), services);
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
                    EmitBase(w, c, Subtypes(subtypesOf, c, 0), services);
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
            if (!entry.IsBase)
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

    /// <summary>The dispatch of the non-generic (or closed generic) base <paramref name="t"/> to <paramref name="subtypes"/>.</summary>
    private static void EmitBase(CodeWriter w, string t, List<DispatchEntry>? subtypes, string services)
    {
        // value.GetType() == typeof(X) compiles to one method table compare. WriteExactShallow is inlined where the
        // caller's inlining budget allows (the dispatch is inlined into its callers), else called as shared code. A
        // non-generic method per subtype, as on read, was measured: it writes 4.5% fewer instructions for an array of
        // 20 subtypes, but more where the bodies would have been inlined, and one more method per subtype costs about
        // 2.5% more compile time on a workload of hierarchies; outlining only the subtypes whose bodies nest (a70aab2c)
        // cost Neoverse-N2 a call per element (GenericSealedClassTupleBase serialize 0.84x). The depth is checked once
        // here instead: each subtype keeps its write inline without the deep path (a writer copy, or a deferral, per
        // subtype), and a body too deep goes out of line with the writer by value, so its address reaches no call.
        if (subtypes is { Count: > 0 and <= MaxWriteChain })
        {
            w.Open(WriteSubtype(t));
            w.Line($"if ({services}NinoObject.IsDeep(ref writer))");
            w.Open();
            w.Line("writer = __WriteSubtypeDeep(value, writer);");
            w.Line("return;");
            w.Close();
            w.Line();
            foreach (var s in subtypes)
                w.Line($$"""if (value.GetType() == typeof({{s.TypeName}})) { {{services}}NinoObject.{{(s.RawBody ? "WriteExactRawShallow" : "WriteExactShallow")}}<{{s.TypeName}}, {{s.FormatterName}}>(value, ref writer); return; }""");
            w.Line(WriteOther(t, services) + ";");
            w.Close();

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
        w.Open(ReadSubtype(t));
        EmitReadSwitch(w, subtypes);
        w.Line(ReadOther(t, services) + ";");
        w.Close();

        // Each subtype's instance check and body, out of line in a method of this non-generic struct, where
        // NinoObject.ReadExact is not shared code (see there). Inlined into the switch, every subtype's body would be
        // copied into it.
        w.Line();
        for (int i = 0; i < subtypes.Count; i++)
        {
            var s = subtypes[i];
            w.Line($"{GeneratedCode.NoInline} static void __ReadExact{i}(ref {Core}Reader reader, ref {t} value) => {services}NinoObject.ReadExact<{t}, {s.TypeName}, {s.FormatterName}>(ref reader, ref value);");
        }
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
