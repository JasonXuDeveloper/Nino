using System.Collections.Generic;
using System.Linq;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Emits the assembly registry and the registrations of built-in formatters the assembly uses.</summary>
internal static class RegistryEmitter
{
    private const string CodeAnalysis = "global::System.Diagnostics.CodeAnalysis.";

    /// <summary>Class registering the built-in formatters the assembly uses (<see cref="EmitUsages"/>).</summary>
    private const string UsagesClass = "NinoGenUsages";

    /// <summary>Ids per bucket method the dispatch aims for: few enough that a lookup loads few types.</summary>
    private const int IdsPerBucket = 8;

    /// <summary>Upper bound on bucket methods (a power of two, so a bucket is selected with a mask).</summary>
    private const int MaxBuckets = 256;

    public static string EmitRegistry(GeneratorOptions options, IEnumerable<RegistryEntry> entries)
    {
        string name = "NinoGenRegistry_" + new string(options.AssemblyName
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        var w = new CodeWriter();
        w.Raw(FormatterEmitter.Header);
        w.Line($"[assembly: {Core}NinoGenerated(typeof({Gen}{name}))]");
        var list = entries.OrderBy(e => e.FormatterName, System.StringComparer.Ordinal).ToList();
        // Generic [NinoType]s are closed at runtime: their formatter definitions are ordinary formatter
        // registrations (each implements INinoFormatter<Box<T>> in exactly the type's own parameters).
        foreach (var entry in list.Where(e => e.Arity != 0))
            w.Line($"[assembly: {Core}NinoFormatter(typeof({Gen}{entry.FormatterName}<{new string(',', entry.Arity - 1)}>))]");
        w.Line();
        w.Open($"namespace {GenNamespace}");
        w.Line("/// <summary>");
        w.Line("/// Registers the Nino formatters generated for this assembly, lazily: a lookup that misses asks for the");
        w.Line("/// type id it needs, so startup does not touch types that are never used.");
        w.Line("/// </summary>");
        w.Line(HiddenAttribute);
        w.Open($"internal sealed class {name} : {Core}INinoRegistry");

        w.Block($"static {name}()", () =>
        {
            w.Line($"{Core}NinoRegistry.Add(new {name}());");
            // Libraries declaring formatters this assembly may resolve at runtime (see FormatterRules.RegisteringReferences).
            foreach (var formatter in options.RegisteringReferences)
                w.Line($"{Core}NinoRegistry.EnsureInitialized(typeof({formatter}).Assembly);");
        });
        w.Line();
        EmitIdDispatch(w, "public", $"{Gen}{UsagesClass}.Register(id);",
            // Keyed by wire id (polymorphic reads) and, for [NinoFormerName] types, also by name id (by-type lookups).
            list.Where(e => e.Arity == 0)
                .SelectMany(e => e.NameId == e.TypeId
                    ? new[] { (Id: e.TypeId, Entry: e) }
                    : new[] { (Id: e.TypeId, Entry: e), (Id: e.NameId, Entry: e) })
                .GroupBy(x => x.Id, x => x.Entry).OrderBy(g => g.Key)
                .Select(g => new KeyValuePair<int, List<string>>(g.Key,
                    g.SelectMany(e => e.Registration).ToList()))
                .ToList());
        w.Line();
        w.Line("/// <summary>Runs the registration (once).</summary>");
        w.Block("public static void Init()", () => { });
        if (options.HasModuleInitializer)
        {
            w.Line();
            w.Line($"[{CompilerServices}ModuleInitializer]");
            w.Line("internal static void ModuleInit() => Init();");
        }

        if (options.HasUnityEngine)
        {
            w.Line();
            w.Line("[global::UnityEngine.RuntimeInitializeOnLoadMethod(global::UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]");
            w.Line("private static void UnityInit() => Init();");
        }

        if (options.HasUnityEditor)
        {
            w.Line();
            w.Line("[global::UnityEditor.InitializeOnLoadMethod]");
            w.Line("private static void UnityEditorInit() => Init();");
        }

        w.Close();
        w.Close();
        return w.ToString();
    }

    public static string EmitUsages(IEnumerable<FormatterRegistration> usages, IEnumerable<FactoryRegistration> factories,
        IEnumerable<ClosedTypeRegistration> generics, IEnumerable<string> layoutTypes)
    {
        var w = new CodeWriter();
        w.Raw(FormatterEmitter.Header);
        w.Open($"namespace {GenNamespace}");
        w.Open($"internal static class {UsagesClass}");
        w.Line("/// <summary>Registers the closed built-in formatters whose type has id <paramref name=\"id\"/>.</summary>");
        // Formatters first, then creators, then closed generic [NinoType]s, each in their own (sorted) order.
        var statements = usages
            .Select(u => (u.Id, Code: $"{Core}NinoFormatterCache<{u.TypeName}>.TryRegister<{u.Formatter}>();"))
            .Concat(factories.Select(f => (f.Id, Code: $"{Core}NinoActivator<{f.TypeName}>.TryRegister({f.Creator});")))
            .Concat(generics.SelectMany(GenericStatements));
        // The registry calls Register, so these stay with it: NativeAOT keeps the fields NinoLayout measures.
        foreach (var type in layoutTypes)
            w.Line($"[{CodeAnalysis}DynamicDependency({CodeAnalysis}DynamicallyAccessedMemberTypes.PublicFields | " +
                   $"{CodeAnalysis}DynamicallyAccessedMemberTypes.NonPublicFields, typeof({type}))]");
        EmitIdDispatch(w, "internal static", "",
            statements.GroupBy(u => u.Id).OrderBy(g => g.Key)
                .Select(g => new KeyValuePair<int, List<string>>(g.Key, g.Select(u => u.Code).ToList()))
                .ToList());
        w.Close();
        w.Close();
        return w.ToString();
    }

    /// <summary>
    /// The registration of a closed generic <c>[NinoType]</c>, under its wire id (polymorphic reads) and, when
    /// <c>[NinoFormerName]</c> makes it differ, its name id (by-type lookups), like a non-generic type's.
    /// </summary>
    private static IEnumerable<(int Id, string Code)> GenericStatements(ClosedTypeRegistration g)
    {
        var code = new List<string>();
        if (g.Formatter.Length == 0)
        {
            // Generated by another assembly, whose formatter is internal: closed by reflection.
            if (g.Polymorphic)
                code.Add($"{Core}NinoPolymorphic.Register(typeof({g.TypeName}));");
        }
        else
        {
            code.Add($"{Core}NinoFormatterCache<{g.TypeName}>.TryRegister<{g.Formatter}>();");
            if (g.Polymorphic)
            {
                string register = g.Tracked ? "RegisterTracked" : "Register";
                code.Add($"{Core}NinoPolymorphic.{register}<{g.TypeName}, {g.Formatter}>({g.Id});");
            }
        }

        foreach (var statement in code)
        {
            yield return (g.Id, statement);
            if (g.NameId != g.Id)
                yield return (g.NameId, statement);
        }
    }

    /// <summary>
    /// Emits <c>Register(int id)</c> as a switch over small bucket methods: the JIT resolves every call in a method
    /// it compiles, so one switch over every type would load every formatter on the first lookup. With buckets of
    /// about <see cref="IdsPerBucket"/> ids, a lookup only loads the types of its bucket.
    /// </summary>
    private static void EmitIdDispatch(CodeWriter w, string modifiers, string prelude,
        List<KeyValuePair<int, List<string>>> cases)
    {
        int buckets = 1;
        while (buckets < MaxBuckets && buckets * IdsPerBucket < cases.Count)
            buckets *= 2;

        w.Open($"{modifiers} void Register(int id)");
        if (prelude.Length != 0)
            w.Line(prelude);
        if (buckets == 1)
        {
            EmitIdSwitch(w, cases);
            w.Close();
            return;
        }

        var byBucket = cases.GroupBy(c => c.Key & (buckets - 1)).OrderBy(g => g.Key).ToList();
        w.Block($"switch (id & {buckets - 1})", () =>
        {
            foreach (var bucket in byBucket)
                w.Line($"case {bucket.Key}: __Bucket{bucket.Key}(id); break;");
        });
        w.Close();
        foreach (var bucket in byBucket)
        {
            w.Line();
            w.Block($"private static void __Bucket{bucket.Key}(int id)", () => EmitIdSwitch(w, bucket.ToList()));
        }
    }

    private static void EmitIdSwitch(CodeWriter w, List<KeyValuePair<int, List<string>>> cases)
    {
        if (cases.Count == 0)
            return;
        w.Block("switch (id)", () =>
        {
            foreach (var c in cases)
            {
                w.Line($"case {c.Key}:");
                w.Indented(() =>
                {
                    foreach (var statement in c.Value)
                        w.Line(statement);
                    w.Line("break;");
                });
            }
        });
    }
}
