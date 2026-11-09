using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Generator.Common;
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

    /// <summary>
    /// The registry of <paramref name="entries"/> (sorted by formatter name), registering the function pointer fields
    /// <paramref name="pointerFields"/> (arguments of <c>NinoFunctionPointerFields.Register</c>, sorted).
    /// </summary>
    public static string EmitRegistry(GeneratorOptions options, EquatableArray<RegistryEntry> entries,
        EquatableArray<string> pointerFields, bool fileScoped)
    {
        string name = "NinoGenRegistry_" + new string(options.AssemblyName
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        var preamble = new CodeWriter();
        preamble.Line($"[assembly: {CoreCompilerServices}NinoGenerated(typeof({Gen}{name}))]");
        // Generic [NinoType]s are closed at runtime: their formatter definitions are ordinary formatter
        // registrations (each implements INinoFormatter<Box<T>> in exactly the type's own parameters).
        foreach (var entry in entries)
            if (entry.Arity != 0)
                preamble.Line($"[assembly: {Core}NinoFormatter(typeof({Gen}{entry.FormatterName}<{new string(',', entry.Arity - 1)}>))]");
        preamble.Line();
        var w = new CodeWriter();
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
            // Before Add, which hands the assembly's [NinoFormatter] registrations to the runtime resolver: it binds
            // them by these marks where the runtime drops attributes of generic parameters (IL2CPP).
            foreach (var marks in options.FormatterMarks)
                w.Line($"{CoreCompilerServices}NinoParameterMarks.Register(typeof({marks.Formatter}), {string.Join(", ", marks.Marks)});");
            // Before Add too: the function pointer fields Nino.Core lays out as pointers, zero-extended, on every
            // runtime (reflection reports them as IntPtr on Mono and IL2CPP).
            foreach (var fields in pointerFields)
                w.Line($"{CoreCompilerServices}NinoFunctionPointerFields.Register({fields});");
            w.Line($"{Core}NinoRegistry.Add(new {name}());");
            // Libraries declaring formatters this assembly may resolve at runtime (see FormatterRules.RegisteringReferences).
            foreach (var formatter in options.RegisteringReferences)
                w.Line($"{Core}NinoRegistry.EnsureInitialized(typeof({formatter}).Assembly);");
        });
        w.Line();
        var cases = new Dictionary<int, IdCase>();
        foreach (var entry in entries)
            if (entry.Arity == 0 && entry.Registration is { } registration)
                AddStatements(cases, registration);
        EmitIdDispatch(w, "public", $"{Gen}{UsagesClass}.Register(id);", OrderCases(cases));
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
        w.Line();
        w.Open($"internal static class {GeneratedCode.MethodFlags}");
        w.Line($"internal const {CompilerServices}MethodImplOptions Inline = {CompilerServices}MethodImplOptions.AggressiveInlining;");
        w.Line($"internal const {CompilerServices}MethodImplOptions NoInline = {CompilerServices}MethodImplOptions.NoInlining;");
        w.Close();
        var aliases = new List<string>();
        string body = GeneratedCode.WithAliases(w.ToString(), null, aliases);
        return GeneratedCode.File(preamble.ToString(), aliases, body, fileScoped);
    }

    public static string EmitUsages(IEnumerable<FormatterRegistration> usages, IEnumerable<FactoryRegistration> factories,
        IEnumerable<TypeRegistration> generics, IEnumerable<string> layoutTypes, bool fileScoped)
    {
        var w = new CodeWriter();
        var kept = layoutTypes.ToList();
        var aliases = new List<string>();
        if (kept.Count != 0)
        {
            const string codeAnalysis = "global::System.Diagnostics.CodeAnalysis.";
            aliases.Add($"using __NinoDynamicDependency = {codeAnalysis}DynamicDependencyAttribute;");
            aliases.Add($"using __NinoMembers = {codeAnalysis}DynamicallyAccessedMemberTypes;");
        }

        w.Open($"internal static class {UsagesClass}");
        if (kept.Count != 0)
        {
            w.Line("private const __NinoMembers __Fields = __NinoMembers.PublicFields | __NinoMembers.NonPublicFields;");
            w.Line();
        }

        w.Line("""/// <summary>Registers the closed built-in formatters whose type has id <paramref name="id"/>.</summary>""");
        // Formatters first, then creators, then closed generic [NinoType]s, each in their own (sorted) order.
        var registrations = new Dictionary<int, IdCase>();
        foreach (var usage in usages)
            AddCase(registrations, usage.Id,
                $"{Core}NinoFormatterCache<{usage.TypeName}>.TryRegister<{usage.Formatter}>();");
        foreach (var factory in factories)
            AddCase(registrations, factory.Id, $"{Core}NinoActivator<{factory.TypeName}>.TryRegister({factory.Creator});");
        foreach (var registration in generics)
            AddStatements(registrations, registration);
        var cases = OrderCases(registrations);
        var compact = new GeneratedCode.NamespaceAliases();
        foreach (var entry in cases)
        {
            if (compact.Ready)
                break;
            compact.Count(entry.First);
            if (entry.Rest != null)
                foreach (var statement in entry.Rest)
                    compact.Count(statement);
        }
        foreach (var type in kept)
        {
            if (compact.Ready)
                break;
            compact.Count(type);
        }
        compact.Declare(aliases);

        // The registry calls Register, so these stay with it: NativeAOT keeps the fields NinoLayout measures.
        foreach (var type in kept)
            w.AliasedLine(type, compact, "[__NinoDynamicDependency(__Fields, typeof(", "))]");
        EmitIdDispatch(w, "internal static", "", cases, compact);
        w.Close();
        string body = GeneratedCode.WithAliases(w.ToString(), null, aliases);
        return GeneratedCode.File("", aliases, body, fileScoped);
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
    /// <c>[NinoFormerName]</c> makes it differ, its name id (by-type lookups). A concrete type's formatter and handler
    /// are one <c>NinoTypes</c> call, which takes the wire id: under that case it is the <c>id</c> switched on.
    /// </summary>
    private static void AddStatements(Dictionary<int, IdCase> cases, TypeRegistration r)
    {
        string atId, atNameId;
        if (r.Formatter.Length == 0)
        {
            // Generated by another assembly, whose formatter is internal: closed by reflection.
            if (!r.Concrete)
                return;
            atId = atNameId = $"{Core}NinoPolymorphic.TryRegister(typeof({r.TypeName}));";
        }
        else if (!r.Concrete)
        {
            // An abstract type or interface: a formatter, no handler.
            atId = atNameId = $"{Core}NinoFormatterCache<{r.TypeName}>.TryRegister<{r.Formatter}>();";
        }
        else
        {
            // A tracked class is neither of fixed size nor has a __Top twin (TypeFacts.IsFixedSize, TypeRules.HasTop).
            string call = CoreCompilerServices + "NinoTypes." + (r.Tracked ? $"RegisterTracked<{r.TypeName}, {r.Formatter}>"
                : r.Top ? $"RegisterTop<{r.TypeName}, {r.Formatter}, {r.Formatter}{Top}>"
                : $"{(r.Fixed ? "RegisterFixed" : "Register")}<{r.TypeName}, {r.Formatter}>");
            atId = call + "(id);";
            atNameId = r.NameId == r.Id ? atId : call + "(" + r.Id + ");";
        }

        AddCase(cases, r.Id, atId);
        if (r.NameId != r.Id)
            AddCase(cases, r.NameId, atNameId);
    }

    private readonly record struct IdCase(int Id, string First, List<string>? Rest);

    private static void AddCase(Dictionary<int, IdCase> cases, int id, string code)
    {
        if (!cases.TryGetValue(id, out var entry))
            cases.Add(id, new IdCase(id, code, null));
        else if (entry.Rest == null)
            cases[id] = entry with { Rest = new List<string> { code } };
        else
            entry.Rest.Add(code);
    }

    /// <summary>The statements by id, in the order of the ids, statements in insertion order.</summary>
    private static List<IdCase> OrderCases(Dictionary<int, IdCase> cases)
    {
        var ordered = cases.Values.ToList();
        ordered.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        return ordered;
    }

    /// <summary>
    /// Emits <c>Register(int id)</c> as a switch over small bucket methods: the JIT resolves every call in a method
    /// it compiles, so one switch over every type would load every formatter on the first lookup. With buckets of
    /// about <see cref="IdsPerBucket"/> ids, a lookup only loads the types of its bucket.
    /// </summary>
    private static void EmitIdDispatch(CodeWriter w, string modifiers, string prelude, List<IdCase> cases,
        GeneratedCode.NamespaceAliases compact = default)
    {
        int buckets = 1;
        while (buckets < MaxBuckets && buckets * IdsPerBucket < cases.Count)
            buckets *= 2;

        w.Open($"{modifiers} void Register(int id)");
        if (prelude.Length != 0)
            w.Line(prelude);
        if (buckets == 1)
        {
            if (cases.Count != 0)
                EmitIdSwitch(w, cases, compact);
            w.Close();
            return;
        }

        var byBucket = cases.GroupBy(c => c.Id & (buckets - 1)).OrderBy(g => g.Key).ToList();
        w.Block($"switch (id & {buckets - 1})", () =>
        {
            foreach (var bucket in byBucket)
                w.Line($"case {bucket.Key}: __Bucket{bucket.Key}(id); break;");
        });
        w.Close();
        foreach (var bucket in byBucket)
        {
            w.Line();
            w.Block($"private static void __Bucket{bucket.Key}(int id)", () => EmitIdSwitch(w, bucket, compact));
        }
    }

    private static void EmitIdSwitch(CodeWriter w, IEnumerable<IdCase> cases, GeneratedCode.NamespaceAliases compact)
    {
        w.Block("switch (id)", () =>
        {
            foreach (var c in cases)
            {
                w.Line($"case {c.Id}:");
                using (w.Indent())
                {
                    w.AliasedLine(c.First, compact);
                    if (c.Rest != null)
                        foreach (var statement in c.Rest)
                            w.AliasedLine(statement, compact);
                    w.Line("break;");
                }
            }
        });
    }
}
