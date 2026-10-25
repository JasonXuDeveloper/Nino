using System.Collections.Generic;
using System.Linq;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Emits the assembly registry and the registrations of built-in formatters the assembly uses.</summary>
internal static class RegistryEmitter
{

    /// <summary>Class registering the built-in formatters the assembly uses (<see cref="EmitUsages"/>).</summary>
    private const string UsagesClass = "NinoGenUsages";

    /// <summary>Ids per bucket method the dispatch aims for: few enough that a lookup loads few types.</summary>
    private const int IdsPerBucket = 8;

    /// <summary>Upper bound on bucket methods (a power of two, so a bucket is selected with a mask).</summary>
    private const int MaxBuckets = 256;

    /// <summary>The registry of <paramref name="entries"/> (sorted by formatter name).</summary>
    public static string EmitRegistry(GeneratorOptions options, IEnumerable<RegistryEntry> entries)
    {
        string name = "NinoGenRegistry_" + new string(options.AssemblyName
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        var w = new CodeWriter();
        w.Raw(GeneratedCode.Header);
        w.Line($"[assembly: {CoreCompilerServices}NinoGenerated(typeof({Gen}{name}))]");
        var list = entries.ToList();
        // Generic [NinoType]s are closed at runtime: their formatter definitions are ordinary formatter
        // registrations (each implements INinoFormatter<Box<T>> in exactly the type's own parameters).
        foreach (var entry in list.Where(e => e.Arity != 0))
            w.Line($"[assembly: {Core}NinoFormatter(typeof({Gen}{entry.FormatterName}<{new string(',', entry.Arity - 1)}>))]");
        w.Line();
        w.Open($"namespace {GenNamespace}");
        w.Lines($$"""
            /// <summary>
            /// Registers the Nino formatters generated for this assembly, lazily: a lookup that misses asks for the
            /// type id it needs, so startup does not touch types that are never used.
            /// </summary>
            {{HiddenAttribute}}
            """);
        w.Open($"internal sealed class {name} : {CoreCompilerServices}INinoRegistry");

        w.Block($"static {name}()", () =>
        {
            w.Line($"{Core}NinoRegistry.Add(new {name}());");
            // Libraries declaring formatters this assembly may resolve at runtime (see FormatterRules.RegisteringReferences).
            foreach (var formatter in options.RegisteringReferences)
                w.Line($"{Core}NinoRegistry.EnsureInitialized(typeof({formatter}).Assembly);");
        });
        w.Line();
        EmitIdDispatch(w, "public", $"{Gen}{UsagesClass}.Register(id);",
            ById(list.Where(e => e.Arity == 0 && e.Registration != null).SelectMany(e => Statements(e.Registration!))));
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
        IEnumerable<TypeRegistration> generics, IEnumerable<string> layoutTypes)
    {
        var w = new CodeWriter();
        w.Raw(GeneratedCode.Header);
        w.Open($"namespace {GenNamespace}");
        var kept = layoutTypes.ToList();
        if (kept.Count != 0)
        {
            const string codeAnalysis = "global::System.Diagnostics.CodeAnalysis.";
            w.Line($"using __NinoDynamicDependency = {codeAnalysis}DynamicDependencyAttribute;");
            w.Line($"using __NinoMembers = {codeAnalysis}DynamicallyAccessedMemberTypes;");
            w.Line();
        }

        w.Open($"internal static class {UsagesClass}");
        if (kept.Count != 0)
        {
            w.Line("private const __NinoMembers __Fields = __NinoMembers.PublicFields | __NinoMembers.NonPublicFields;");
            w.Line();
        }

        w.Line("""/// <summary>Registers the closed built-in formatters whose type has id <paramref name="id"/>.</summary>""");
        // Formatters first, then creators, then closed generic [NinoType]s, each in their own (sorted) order.
        var statements = usages
            .Select(u => (u.Id, Code: $"{Core}NinoFormatterCache<{u.TypeName}>.TryRegister<{u.Formatter}>();"))
            .Concat(factories.Select(f => (f.Id, Code: $"{Core}NinoActivator<{f.TypeName}>.TryRegister({f.Creator});")))
            .Concat(generics.SelectMany(Statements));
        // The registry calls Register, so these stay with it: NativeAOT keeps the fields NinoLayout measures.
        foreach (var type in kept)
            w.Line($"[__NinoDynamicDependency(__Fields, typeof({type}))]");
        EmitIdDispatch(w, "internal static", "", ById(statements));
        w.Close();
        w.Close();
        return w.ToString();
    }

    /// <summary>How the registry registers the formatter of <paramref name="m"/>; null for generic types.</summary>
    public static TypeRegistration? Registration(TypeModel m) => m.IsGeneric
        ? null
        : new TypeRegistration(m.TypeName, m.FormatterName, m.TypeId, m.NameId,
            Concrete: m.Kind != FormatterKind.Polymorphic, Tracked: m.TrackReferences,
            // A __Top twin writes the regular top-level calls too (a formatter of fixed size writes them inline).
            Top: m.HasTop && !m.FixedSize, Fixed: m.FixedSize);

    /// <summary>
    /// The statements registering a <c>[NinoType]</c>'s formatter, under its wire id (polymorphic reads) and, when
    /// <c>[NinoFormerName]</c> makes it differ, its name id (by-type lookups).
    /// </summary>
    private static IEnumerable<(int Id, string Code)> Statements(TypeRegistration r)
    {
        var code = new List<string>(2);
        if (r.Formatter.Length == 0)
        {
            // Generated by another assembly, whose formatter is internal: closed by reflection.
            if (r.Concrete)
                code.Add($"{Core}NinoPolymorphic.Register(typeof({r.TypeName}));");
        }
        else
        {
            string cache = $"{Core}NinoFormatterCache<{r.TypeName}>";
            code.Add(r.Top
                ? $"{cache}.TryRegisterTop<{r.Formatter}, {r.Formatter}{Top}>();"
                : $"{cache}.{(r.Fixed ? "TryRegisterFixed" : "TryRegister")}<{r.Formatter}>();");
            if (r.Concrete)
            {
                string register = r.Tracked ? "RegisterTracked" : "Register";
                code.Add($"{Core}NinoPolymorphic.{register}<{r.TypeName}, {r.Formatter}>({r.Id});");
            }
        }

        foreach (var statement in code)
        {
            yield return (r.Id, statement);
            if (r.NameId != r.Id)
                yield return (r.NameId, statement);
        }
    }

    /// <summary>The statements by id, in the order of the ids, each id's in the order given.</summary>
    private static List<KeyValuePair<int, List<string>>> ById(IEnumerable<(int Id, string Code)> statements) =>
        statements.GroupBy(s => s.Id).OrderBy(g => g.Key)
            .Select(g => new KeyValuePair<int, List<string>>(g.Key, g.Select(s => s.Code).ToList()))
            .ToList();

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
