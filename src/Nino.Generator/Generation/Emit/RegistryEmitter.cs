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
        int caseCapacity = 0;
        foreach (var entry in entries)
        {
            if (entry.Arity != 0)
                preamble.Line($"[assembly: {Core}NinoFormatter(typeof({Gen}{entry.FormatterName}<{new string(',', entry.Arity - 1)}>))]");
            else if (entry.Registration is { } registration)
                caseCapacity += registration.NameId == registration.Id ? 1 : 2;
        }
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
        var cases = new Dictionary<int, IdCase>(caseCapacity);
        foreach (var entry in entries)
            if (entry.Arity == 0 && entry.Registration is { } registration)
                AddStatements(cases, registration);
        var ordered = OrderCases(cases, out int mask);
        EmitIdDispatch(w, "public", $"{UsagesClass}.Register(id);", ordered, mask);
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
        string body = GeneratedCode.WithAliases(w.Finish(), null, aliases);
        return GeneratedCode.File(preamble.Finish(), aliases, body, fileScoped);
    }

    public static string EmitUsages(EquatableArray<FormatterRegistration> usages, EquatableArray<FactoryRegistration> factories,
        EquatableArray<TypeRegistration> generics, EquatableArray<string> layoutTypes, bool fileScoped)
    {
        var w = new CodeWriter();
        var aliases = new List<string>();
        if (layoutTypes.Length != 0)
        {
            const string codeAnalysis = "global::System.Diagnostics.CodeAnalysis.";
            aliases.Add($"using __NinoDynamicDependency = {codeAnalysis}DynamicDependencyAttribute;");
            aliases.Add($"using __NinoMembers = {codeAnalysis}DynamicallyAccessedMemberTypes;");
        }

        w.Open($"internal static class {UsagesClass}");
        if (layoutTypes.Length != 0)
        {
            w.Line("private const __NinoMembers __Fields = __NinoMembers.PublicFields | __NinoMembers.NonPublicFields;");
            w.Line();
        }

        w.Line("""/// <summary>Registers the closed built-in formatters whose type has id <paramref name="id"/>.</summary>""");
        // Formatters first, then creators, then closed generic [NinoType]s, each in their own (sorted) order.
        var registrations = new Dictionary<int, IdCase>(usages.Length + factories.Length + generics.Length);
        foreach (var usage in usages)
            AddCase(registrations, usage.Id,
                $"{Core}NinoFormatterCache<{usage.TypeName}>.TryRegister<{usage.Formatter}>();");
        foreach (var factory in factories)
            AddCase(registrations, factory.Id, $"{Core}NinoActivator<{factory.TypeName}>.TryRegister({factory.Creator});");
        foreach (var registration in generics)
            AddStatements(registrations, registration);
        var cases = OrderCases(registrations, out int mask);
        var compact = new GeneratedCode.NamespaceAliases();
        // The alias counts saturate, so the bucket order does not affect which aliases are declared.
        foreach (var entry in cases)
        {
            if (compact.Ready)
                break;
            compact.Count(entry.First);
            if (entry.Rest != null)
                foreach (var statement in entry.Rest)
                    compact.Count(statement);
        }
        foreach (var type in layoutTypes)
        {
            if (compact.Ready)
                break;
            compact.Count(type);
        }
        compact.Declare(aliases);

        // The registry calls Register, so these stay with it: NativeAOT keeps the fields NinoLayout measures.
        foreach (var type in layoutTypes)
            w.AliasedLine(type, compact, "[__NinoDynamicDependency(__Fields, typeof(", "))]");
        EmitIdDispatch(w, "internal static", "", cases, mask, compact);
        w.Close();
        string body = GeneratedCode.WithAliases(w.Finish(), null, aliases);
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
            atId = ConcreteStatement(r, "id");
            atNameId = r.NameId == r.Id ? atId : ConcreteStatement(r, r.Id.ToString());
        }

        AddCase(cases, r.Id, atId);
        if (r.NameId != r.Id)
            AddCase(cases, r.NameId, atNameId);
    }

    private static string ConcreteStatement(TypeRegistration r, string id)
    {
        // Tracked classes do not have a fixed body or a top-level twin.
        if (!r.Tracked && r.Top)
            return $"{CoreCompilerServices}NinoTypes.RegisterTop<{r.TypeName}, {r.Formatter}, {r.Formatter}{Top}>({id});";

        string method = r.Tracked ? "RegisterTracked" : r.Fixed ? "RegisterFixed" : "Register";
        return $"{CoreCompilerServices}NinoTypes.{method}<{r.TypeName}, {r.Formatter}>({id});";
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

    /// <summary>The cases in bucket order, then ID order, with their statements in insertion order.</summary>
    private static IdCase[] OrderCases(Dictionary<int, IdCase> cases, out int mask)
    {
        var ordered = cases.Values.ToArray();
        int buckets = 1;
        while (buckets < MaxBuckets && buckets * IdsPerBucket < ordered.Length)
            buckets *= 2;

        mask = buckets - 1;
        if (mask == 0)
            Array.Sort(ordered, static (a, b) => a.Id.CompareTo(b.Id));
        else
            Array.Sort(ordered, new BucketOrder(mask));
        return ordered;
    }

    private sealed class BucketOrder(int mask) : IComparer<IdCase>
    {
        public int Compare(IdCase a, IdCase b)
        {
            int bucket = (a.Id & mask).CompareTo(b.Id & mask);
            return bucket == 0 ? a.Id.CompareTo(b.Id) : bucket;
        }
    }

    /// <summary>
    /// Emits <c>Register(int id)</c> as a switch over small bucket methods: the JIT resolves every call in a method
    /// it compiles, so one switch over every type would load every formatter on the first lookup. With buckets of
    /// about <see cref="IdsPerBucket"/> ids, a lookup only loads the types of its bucket.
    /// </summary>
    private static void EmitIdDispatch(CodeWriter w, string modifiers, string prelude, IdCase[] cases, int mask,
        GeneratedCode.NamespaceAliases compact = default)
    {
        w.Open($"{modifiers} void Register(int id)");
        if (prelude.Length != 0)
            w.Line(prelude);
        if (mask == 0)
        {
            if (cases.Length != 0)
                EmitIdSwitch(w, cases, compact);
            w.Close();
            return;
        }

        w.Open($"switch (id & {mask})");
        int previous = -1;
        foreach (var entry in cases)
        {
            int bucket = entry.Id & mask;
            if (bucket != previous)
                w.RegistryBucketCase(bucket);
            previous = bucket;
        }
        w.Close();
        w.Close();
        for (int start = 0; start < cases.Length;)
        {
            int bucket = cases[start].Id & mask;
            int end = start + 1;
            while (end < cases.Length && (cases[end].Id & mask) == bucket)
                end++;

            w.Line();
            w.OpenNumbered("private static void __Bucket", bucket, "(int id)");
            EmitIdSwitch(w, cases.AsSpan(start, end - start), compact);
            w.Close();
            start = end;
        }
    }

    private static void EmitIdSwitch(CodeWriter w, ReadOnlySpan<IdCase> cases, GeneratedCode.NamespaceAliases compact)
    {
        w.Open("switch (id)");
        foreach (var c in cases)
        {
            w.CaseLabel(c.Id);
            using (w.Indent())
            {
                w.AliasedLine(c.First, compact);
                if (c.Rest != null)
                    foreach (var statement in c.Rest)
                        w.AliasedLine(statement, compact);
                w.Line("break;");
            }
        }
        w.Close();
    }
}
