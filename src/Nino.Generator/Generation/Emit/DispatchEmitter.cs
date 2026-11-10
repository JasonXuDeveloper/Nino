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

    private const string CompactServices = "__NinoCS.";

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
            EmitClosedSelection(w, t, closed, first, write: true);
            // Two one-line forwarders stay together.
            if (closed is { Count: > 0 })
                w.Line();
            EmitClosedSelection(w, t, closed, first, write: false);
            w.Close();
            w.Line();
        }

        // This file has a few lines per (base, subtype) pair, each repeating the same names.
        var aliases = new List<string>();
        if (services == CompactServices)
            aliases.Add($"using __NinoCS = {CoreCompilerServices.TrimEnd('.')};");
        string body = GeneratedCode.WithAliases(w.Finish(), null, aliases);
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
        // Each non-generic or closed dispatch has two registry fallbacks. Two bodies pay for the alias.
        int bodies = 0;
        foreach (var entry in entries)
        {
            if (!entry.IsBase)
                continue;
            if (entry.TypeParameterList.Length == 0)
                bodies++;
            else if (closedOf != null && closedOf.TryGetValue(entry.TypeName, out var instances))
                bodies += instances.Count;
            if (bodies >= 2)
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
    private static void EmitReadSwitch(CodeWriter w, List<DispatchEntry> subtypes)
    {
        w.Open("switch (id)");
        for (int i = 0; i < subtypes.Count; i++)
            w.Line($"case {subtypes[i].TypeId.ToString(CultureInfo.InvariantCulture)}: __ReadExact{i}(ref reader, ref value); return;");
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

    /// <summary>The dispatch of the non-generic (or closed generic) base <paramref name="t"/> to <paramref name="subtypes"/>.</summary>
    private static void EmitBase(CodeWriter w, string t, List<DispatchEntry>? subtypes, string services)
    {
        // value.GetType() == typeof(X) compiles to one method table compare. WriteExact is inlined where the caller's
        // inlining budget allows (the dispatch is inlined into its callers), else called as shared code. A non-generic
        // method per subtype, as on read, was measured: it writes 4.5% fewer instructions for an array of 20
        // subtypes, but more where the bodies would have been inlined, and one more method per subtype costs about
        // 2.5% more compile time on a workload of hierarchies.
        if (subtypes is { Count: > 0 and <= MaxWriteChain })
        {
            // Leave small writes inline. Nested writes repeat the depth handling in a wider dispatch.
            bool outlineNestedWrites = subtypes.Count >= 4;
            w.Open(WriteSubtype(t));
            for (int i = 0; i < subtypes.Count; i++)
            {
                var s = subtypes[i];
                string call = outlineNestedWrites && s.Nests && !s.RawBody
                    ? $"__WriteExact{i}"
                    : $"{services}NinoObject.{(s.RawBody ? "WriteExactRaw" : "WriteExact")}<{s.TypeName}, {s.FormatterName}>";
                // The first exact check establishes non-nullness. A later sealed check can then use is
                // without adding a null branch to the dispatch.
                string check = i != 0 && s.IsSealed
                    ? $"value is {s.TypeName}"
                    : $"value.GetType() == typeof({s.TypeName})";
                w.Line($$"""if ({{check}}) { {{call}}(value, ref writer); return; }""");
            }
            w.Line(WriteOther(t, services) + ";");
            w.Close();

            if (outlineNestedWrites)
            {
                for (int i = 0; i < subtypes.Count; i++)
                {
                    var s = subtypes[i];
                    if (s.Nests && !s.RawBody)
                        w.Line($"{GeneratedCode.NoInline} static void __WriteExact{i}(object value, ref {Core}Writer writer) => {services}NinoObject.WriteExact<{s.TypeName}, {s.FormatterName}>(value, ref writer);");
                }
            }
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
    private static string WriteOther(string t, string services = CoreCompilerServices) =>
        $"{services}NinoSubtypes<{t}>.Serialize(value, ref writer)";

    /// <summary>The read of a subtype the dispatch does not know: through the registry.</summary>
    private static string ReadOther(string t, string services = CoreCompilerServices) =>
        $"{services}NinoSubtypes<{t}>.DeserializeOther(id, ref reader, ref value)";

    /// <summary>
    /// Selects a closed dispatch class in the same sorted order as its declaration, or forwards to the registry.
    /// </summary>
    private static void EmitClosedSelection(CodeWriter w, string t, SortedSet<string>? closed, int first, bool write)
    {
        string signature = write ? WriteSubtype(t) : ReadSubtype(t);
        string fallback = write ? WriteOther(t) : ReadOther(t);
        if (closed is not { Count: > 0 })
        {
            w.Line($"{signature} => {fallback};");
            return;
        }

        w.Open(signature);
        foreach (var c in closed)
        {
            w.Line(write
                ? $$"""if (typeof({{t}}) == typeof({{c}})) { __NinoDispatch{{first}}.__WriteSubtype({{CoreCompilerServices}}NinoObject.SameType<{{c}}>(value), ref writer); return; }"""
                : $$"""if (typeof({{t}}) == typeof({{c}})) { __NinoDispatch{{first}}.__ReadSubtype(id, ref reader, ref {{CoreCompilerServices}}NinoObject.SameTypeSlot<{{t}}, {{c}}>(ref value)); return; }""");
            first++;
        }
        w.Line(fallback + ";");
        w.Close();
    }
}
