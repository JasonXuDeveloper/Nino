using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits, for each base formatter that dispatches subtypes, the dispatch to the subtypes declared in this assembly:
/// on write a chain of exact runtime type checks (one method table compare each), on read a switch on the type id,
/// each calling the subtype formatter's body directly. Anything else (subtypes in other assemblies, closed generics,
/// tracked types) falls back to <c>NinoPolymorphic&lt;T&gt;</c>. Only calls are emitted, never subtype bodies, so the
/// size is one case per (base, subtype) pair.
/// </summary>
internal static class DispatchEmitter
{
    /// <summary>Above this many subtypes, writes go through the registry rather than a chain of type checks.</summary>
    private const int MaxWriteChain = 64;

    public static string Emit(EquatableArray<DispatchEntry> entries)
    {
        // Subtypes by base type name, in entry order.
        var subtypesOf = new Dictionary<string, List<DispatchEntry>>();
        foreach (var e in entries)
        {
            if (!e.IsTarget)
                continue;
            foreach (var a in e.BaseClasses.Concat(e.Interfaces))
            {
                if (!subtypesOf.TryGetValue(a, out var list))
                    subtypesOf[a] = list = new List<DispatchEntry>();
                list.Add(e);
            }
        }

        var w = new CodeWriter(4096, indent: 1);
        foreach (var e in entries)
        {
            if (!e.IsBase)
                continue;
            var subtypes = subtypesOf.TryGetValue(e.TypeName, out var all)
                ? all.Where(t => t.TypeId != e.TypeId).ToList()
                : new List<DispatchEntry>();
            // Colliding ids cannot be switch cases; the registry reports the collision when one is used.
            var ids = new HashSet<int>(subtypes.GroupBy(static t => t.TypeId).Where(static g => g.Count() > 1)
                .Select(static g => g.Key));
            subtypes.RemoveAll(t => ids.Contains(t.TypeId));
            w.Open($"partial struct {e.FormatterName}");
            EmitBase(w, e, subtypes);
            w.Close();
            w.Line();
        }

        var sb = new StringBuilder(FormatterEmitter.Header);
        sb.Append("namespace " + GenNamespace + "\n{\n");
        sb.Append(w.ToString());
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// A switch on the ids, which compiles to a binary search of conditional branches; each case ends with a tail
    /// call (see <c>NinoObject.ReadExact</c>), so the dispatch keeps nothing across a call and saves no registers.
    /// Measured against a dense switch through a perfect hash of the ids (a multiplication, a shift and an indirect
    /// jump through a table) and the registry (an indirect call): with 20 subtypes in random order the hash read
    /// about 45% slower, since the indirect jump mispredicts where the conditional branches mostly do not, and in a
    /// fixed order it was equal; it executes one instruction more on x64 and three fewer on ARM64.
    /// </summary>
    private static void EmitReadSwitch(CodeWriter w, DispatchEntry b, List<DispatchEntry> subtypes)
    {
        w.Open("switch (id)");
        foreach (var s in subtypes)
            w.Line($"case {s.FormatterName}.Id: {Core}NinoObject.ReadExact<{b.TypeName}, {s.TypeName}, {s.FormatterName}>(ref value, ref reader); return;");
        w.Close();
    }

    private static void EmitBase(CodeWriter w, DispatchEntry b, List<DispatchEntry> subtypes)
    {
        string t = b.TypeName;
        w.Block($"internal static void __WriteSubtype({t} value, ref {Core}Writer writer)", () =>
        {
            // value.GetType() == typeof(X) compiles to one method table compare.
            foreach (var s in subtypes.Count <= MaxWriteChain ? subtypes : new List<DispatchEntry>())
                w.Line($"if (value.GetType() == typeof({s.TypeName})) {{ {Core}NinoObject.WriteExact<{s.TypeName}, {s.FormatterName}>(value, ref writer); return; }}");
            w.Line($"{Core}NinoPolymorphic<{t}>.Serialize(value, ref writer);");
        });
        w.Line();
        w.Block($"internal static void __ReadSubtype(int id, ref {t} value, ref {Core}Reader reader)", () =>
        {
            if (subtypes.Count != 0)
                EmitReadSwitch(w, b, subtypes);
            w.Line($"{Core}NinoPolymorphic<{t}>.DeserializeOther(id, ref value, ref reader);");
        });
    }
}
