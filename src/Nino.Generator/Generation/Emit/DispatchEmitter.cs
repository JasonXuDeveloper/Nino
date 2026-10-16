using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits, for each base formatter that dispatches subtypes, the dispatch to the subtypes declared in this assembly:
/// on write a chain of exact runtime type checks (one method table compare each), on read a switch on the type id,
/// each writing the subtype's id and body, or calling a method of the base formatter that reads that subtype. Anything
/// else (subtypes in other assemblies, closed generics, tracked types) falls back to <c>NinoPolymorphic&lt;T&gt;</c>.
/// Only calls are emitted, never subtype bodies, so the size is two lines and a one-line method per (base, subtype)
/// pair.
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
        // Aliases for the names every line repeats: this file has a few lines per (base, subtype) pair.
        sb.Append("    using __NinoInline = " + CompilerServices + "MethodImplAttribute;\n");
        sb.Append("    using __NinoInlineOptions = " + CompilerServices + "MethodImplOptions;\n");
        sb.Append("    using __NinoObject = " + Core + "NinoObject;\n");
        sb.Append("    using __NinoWriter = " + Core + "Writer;\n");
        sb.Append("    using __NinoReader = " + Core + "Reader;\n\n");
        sb.Append(w.ToString());
        sb.Append("}\n");
        return sb.ToString();
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
            w.Line($"case {subtypes[i].FormatterName}.Id: __ReadExact{i}(ref value, ref reader); return;");
        w.Close();
    }

    private static void EmitBase(CodeWriter w, DispatchEntry b, List<DispatchEntry> subtypes)
    {
        string t = b.TypeName;
        w.Block($"internal static void __WriteSubtype({t} value, ref __NinoWriter writer)", () =>
        {
            // value.GetType() == typeof(X) compiles to one method table compare. WriteExact is inlined where the
            // caller's inlining budget allows (the dispatch is inlined into its callers), else called as shared code.
            // A non-generic method per subtype, as on read, was measured: it writes 4.5% fewer instructions for an
            // array of 20 subtypes, but more where the bodies would have been inlined, and one more method per
            // subtype costs about 2.5% more compile time on a workload of hierarchies.
            foreach (var s in subtypes.Count <= MaxWriteChain ? subtypes : new List<DispatchEntry>())
                w.Line($"if (value.GetType() == typeof({s.TypeName})) {{ __NinoObject.WriteExact<{s.TypeName}, {s.FormatterName}>(value, ref writer); return; }}");
            w.Line($"{Core}NinoPolymorphic<{t}>.Serialize(value, ref writer);");
        });
        w.Line();
        w.Block($"internal static void __ReadSubtype(int id, ref {t} value, ref __NinoReader reader)", () =>
        {
            if (subtypes.Count != 0)
                EmitReadSwitch(w, subtypes);
            w.Line($"{Core}NinoPolymorphic<{t}>.DeserializeOther(id, ref value, ref reader);");
        });

        // Each subtype's instance check and body, out of line in a method of this non-generic struct, where
        // NinoObject.ReadExact is not shared code (see there). Inlined into the switch, every subtype's body would be
        // copied into it.
        if (subtypes.Count != 0)
            w.Line();
        for (int i = 0; i < subtypes.Count; i++)
        {
            var s = subtypes[i];
            w.Line($"[__NinoInline(__NinoInlineOptions.NoInlining)] static void __ReadExact{i}(ref {t} value, ref __NinoReader reader) => __NinoObject.ReadExact<{t}, {s.TypeName}, {s.FormatterName}>(ref value, ref reader);");
        }
    }
}
