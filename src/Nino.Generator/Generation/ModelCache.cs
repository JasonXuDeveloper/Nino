using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// Reuses a <see cref="TypeModel"/> across compilations while nothing it was built from changed.
/// <para>
/// Roslyn re-runs the transform of every <c>[NinoType]</c> on every compilation change (a keystroke), since
/// semantics may have changed anywhere; building all models again dominated edit latency in large projects.
/// A model only depends on the source of its type and of the types it reaches (bases, interfaces, containing
/// types, member types and their fields, attribute arguments), plus project-wide inputs (references,
/// preprocessor symbols, the set of files, the compilation's own <c>[assembly: NinoFormatter]</c> registrations,
/// <see cref="FormatterRules.OwnSignature"/>). Edited files get new <see cref="SyntaxTree"/> instances, so a
/// model stays valid while every tree it depends on is still part of the compilation.
/// </para>
/// <para>
/// The top-level <c>NinoSerializer</c>/<c>NinoDeserializer</c> calls are bound again on every change too, and are
/// cached the same way by file and position (<see cref="GetOrBuildCall"/>): what a call registers and how it is
/// intercepted depends on its own tree (its location), the method it binds to, and the trees of its type argument.
/// </para>
/// </summary>
internal static class ModelCache
{
    private sealed class Entry
    {
        public string Symbols = "";
        public SyntaxTree[] Trees = null!;
        public TypeModel? Model;
    }

    private sealed class CallEntry
    {
        /// <summary>The tree of the call: the same instance means the same text, location and parse options.</summary>
        public SyntaxTree Tree = null!;

        /// <summary>The method it binds to, with its type arguments (<see cref="CallIdentity"/>).</summary>
        public string Method = "";

        /// <summary>The trees declaring the source types its type arguments reach.</summary>
        public SyntaxTree[] Trees = null!;

        public TopLevelCall? Call;
    }

    /// <summary>
    /// Project-wide inputs of one compilation, and the models built under them; equal states mean the same
    /// references and files.
    /// </summary>
    private sealed class ProjectState
    {
        public string ProjectKey = "";
        public MetadataReference[] References = null!;
        public string[] Paths = null!;
        public string Rules = "";

        /// <summary>Models by type name. Guarded by <see cref="Lock"/>.</summary>
        public readonly Dictionary<string, Entry> Entries = new();

        /// <summary>Entry count at which stale entries are swept next. Guarded by <see cref="Lock"/>.</summary>
        public int SweepAt = MinSweep;

        /// <summary>Top-level calls by file path and position. Guarded by <see cref="Lock"/>.</summary>
        public readonly Dictionary<(string Path, int Position), CallEntry> Calls = new();

        /// <summary>Call count at which stale calls are swept next. Guarded by <see cref="Lock"/>.</summary>
        public int CallSweepAt = MinSweep;

        public bool SameAs(ProjectState other)
        {
            if (ProjectKey != other.ProjectKey || Rules != other.Rules ||
                References.Length != other.References.Length || Paths.Length != other.Paths.Length)
                return false;
            for (int i = 0; i < References.Length; i++)
            {
                if (!ReferenceEquals(References[i], other.References[i]))
                    return false;
            }

            for (int i = 0; i < Paths.Length; i++)
            {
                if (Paths[i] != other.Paths[i])
                    return false;
            }

            return true;
        }
    }

    /// <summary>Entries a project holds before the first sweep.</summary>
    private const int MinSweep = 64;

    private static readonly object Lock = new();
    private static readonly ConditionalWeakTable<Compilation, ProjectState> States = new();
    private static readonly ConditionalWeakTable<ParseOptions, string> SymbolKeys = new();
    /// <summary>The latest state of each project (by <see cref="ProjectState.ProjectKey"/>), weakly. Guarded by <see cref="Lock"/>.</summary>
    private static readonly Dictionary<string, System.WeakReference<ProjectState>> LatestState = new();

    /// <summary>The states used most recently, kept alive (<see cref="Touch"/>). Guarded by <see cref="Lock"/>.</summary>
    private static readonly LinkedList<ProjectState> RecentStates = new();

    /// <summary>How many states <see cref="RecentStates"/> keeps: more than the projects one solution builds at once.</summary>
    private const int Recent = 32;

    private const int MinPrune = 64;

    /// <summary>Size of <see cref="LatestState"/> at which its dead entries are removed. Guarded by <see cref="Lock"/>.</summary>
    private static int PruneAt = MinPrune;

    public static TypeModel? GetOrBuild(INamedTypeSymbol type, Compilation compilation, ParseOptions? options,
        System.Func<TypeModel?> build)
    {
        var state = GetState(compilation);
        string symbols = SymbolKey(options);
        var key = TypeNames.Global(type.OriginalDefinition);

        lock (Lock)
        {
            if (state.Entries.TryGetValue(key, out var entry) && entry.Symbols == symbols &&
                ContainsAll(compilation, entry.Trees))
                return entry.Model;
        }

        var model = build();
        // Direct dependencies are memoized per compilation, so collecting them for every type costs a walk
        // over the closure, not a member lookup per reachable type and root.
        var trees = Dependencies(type.OriginalDefinition, compilation);
        lock (Lock)
        {
            state.Entries[key] = new Entry { Symbols = symbols, Trees = trees, Model = model };
            if (state.Entries.Count >= state.SweepAt)
                Sweep(state, compilation);
        }

        return model;
    }

    /// <summary>
    /// The result of the top-level call <paramref name="call"/>, bound to <paramref name="method"/>: the one built for
    /// the same call before, while its tree, the method and the trees of its type arguments are the same; otherwise
    /// <paramref name="build"/>'s.
    /// </summary>
    public static TopLevelCall? GetOrBuildCall(SyntaxNode call, IMethodSymbol method, Compilation compilation,
        System.Func<TopLevelCall?> build)
    {
        var state = GetState(compilation);
        var tree = call.SyntaxTree;
        var key = (tree.FilePath, call.SpanStart);
        string identity = CallIdentity(method);
        lock (Lock)
        {
            if (state.Calls.TryGetValue(key, out var entry) && entry.Tree == tree && entry.Method == identity &&
                ContainsAll(compilation, entry.Trees))
                return entry.Call;
        }

        var result = build();
        var trees = new HashSet<SyntaxTree>();
        foreach (var argument in method.TypeArguments)
            trees.UnionWith(Dependencies(argument, compilation));
        lock (Lock)
        {
            state.Calls[key] = new CallEntry { Tree = tree, Method = identity, Trees = trees.ToArray(), Call = result };
            if (state.Calls.Count >= state.CallSweepAt)
            {
                // Calls whose file changed are bound again under their new positions: drop the old ones.
                foreach (var stale in state.Calls.Where(p => !compilation.ContainsSyntaxTree(p.Value.Tree))
                             .Select(static p => p.Key).ToList())
                    state.Calls.Remove(stale);
                state.CallSweepAt = System.Math.Max(MinSweep, state.Calls.Count * 2);
            }
        }

        return result;
    }

    /// <summary>The method a call binds to, as a string: its definition and its type arguments.</summary>
    private static string CallIdentity(IMethodSymbol method) =>
        method.OriginalDefinition.ToDisplayString() + "<" +
        string.Join(", ", method.TypeArguments.Select(TypeNames.Global)) + ">";

    /// <summary>The preprocessor symbols of <paramref name="options"/>, as one string (memoized per instance).</summary>
    public static string SymbolKey(ParseOptions? options) =>
        options == null ? "" : SymbolKeys.GetValue(options, static o => string.Join(";", o.PreprocessorSymbolNames));

    private static bool ContainsAll(Compilation compilation, SyntaxTree[] trees)
    {
        foreach (var tree in trees)
        {
            if (!compilation.ContainsSyntaxTree(tree))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Drops entries that depend on trees the compilation no longer has: types renamed or deleted while typing
    /// would otherwise keep their entries, and the old syntax trees those reference, until the next project-wide
    /// change. Such entries would be rebuilt anyway. Runs when the count doubles, so its cost is amortized.
    /// </summary>
    private static void Sweep(ProjectState state, Compilation compilation)
    {
        List<string>? stale = null;
        foreach (var pair in state.Entries)
        {
            if (!ContainsAll(compilation, pair.Value.Trees))
                (stale ??= new List<string>()).Add(pair.Key);
        }

        if (stale != null)
        {
            foreach (var key in stale)
                state.Entries.Remove(key);
        }

        state.SweepAt = System.Math.Max(MinSweep, state.Entries.Count * 2);
    }

    private static ProjectState GetState(Compilation compilation)
    {
        lock (Lock)
        {
            if (States.TryGetValue(compilation, out var known))
                return known;
        }

        var paths = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
            paths.Add(tree.FilePath);
        paths.Sort(System.StringComparer.Ordinal);
        var options = compilation.SyntaxTrees.FirstOrDefault()?.Options;
        var state = new ProjectState
        {
            // With the preprocessor symbols: the target frameworks of a multi-targeted project are separate
            // compilations of one assembly, which the IDE keeps alternating between.
            ProjectKey = (compilation.AssemblyName ?? "") + "|" + SymbolKey(options),
            References = compilation.References.ToArray(),
            Paths = paths.ToArray(),
            // The compilation's own formatter registrations: one edited in a file no model depends on still changes
            // the formatters models resolve.
            Rules = FormatterRules.OwnSignature(compilation, TypeFacts.For(compilation, options).Symbols),
        };

        lock (Lock)
        {
            if (States.TryGetValue(compilation, out var known))
                return known;
            // Reuse the previous state object while nothing project-wide changed, so its entries stay valid;
            // otherwise the new state starts empty and every model of this project is rebuilt once.
            if (Latest(state.ProjectKey) is { } latest && latest.SameAs(state))
                state = latest;
            else
                LatestState[state.ProjectKey] = new System.WeakReference<ProjectState>(state);
            Touch(state);
            States.Add(compilation, state);
            return state;
        }
    }

    /// <summary>The latest state of the project <paramref name="key"/>, if it is still alive. Guarded by <see cref="Lock"/>.</summary>
    private static ProjectState? Latest(string key) =>
        LatestState.TryGetValue(key, out var weak) && weak.TryGetTarget(out var state) ? state : null;

    /// <summary>
    /// Keeps <paramref name="state"/> among the <see cref="Recent"/> ones, which stay alive between compilations (the
    /// IDE may drop one compilation before it creates the next); any other state lives only as long as a compilation
    /// of its project, so closed projects (and their syntax trees) are not kept. Guarded by <see cref="Lock"/>.
    /// </summary>
    private static void Touch(ProjectState state)
    {
        RecentStates.Remove(state);
        RecentStates.AddFirst(state);
        if (RecentStates.Count > Recent)
            RecentStates.RemoveLast();
        if (LatestState.Count < PruneAt)
            return;
        foreach (var key in LatestState.Where(static p => !p.Value.TryGetTarget(out _)).Select(static p => p.Key).ToList())
            LatestState.Remove(key);
        PruneAt = System.Math.Max(MinPrune, LatestState.Count * 2);
    }

    /// <summary>Direct dependencies of one source type definition, memoized per compilation.</summary>
    private sealed class Node
    {
        public SyntaxTree[] Trees = null!;
        public List<ITypeSymbol> Types = null!;
    }

    private static readonly ConditionalWeakTable<Compilation, Dictionary<ISymbol, Node>> Nodes = new();

    /// <summary>Syntax trees declaring the source types a model of <paramref name="root"/> can depend on.</summary>
    private static SyntaxTree[] Dependencies(ITypeSymbol root, Compilation compilation)
    {
        var nodes = Nodes.GetValue(compilation, static _ => new Dictionary<ISymbol, Node>(SymbolEqualityComparer.Default));
        var trees = new HashSet<SyntaxTree>();
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>();
        visited.Add(root);
        pending.Push(root);

        void Add(ITypeSymbol? t)
        {
            if (t != null && visited.Add(t))
                pending.Push(t);
        }

        while (pending.Count != 0)
        {
            var type = pending.Pop();
            switch (type)
            {
                case IArrayTypeSymbol array:
                    Add(array.ElementType);
                    continue;
                case IPointerTypeSymbol pointer:
                    Add(pointer.PointedAtType);
                    continue;
                case INamedTypeSymbol named:
                    foreach (var arg in named.TypeArguments)
                        Add(arg);
                    if (!SymbolEqualityComparer.Default.Equals(named, named.OriginalDefinition))
                    {
                        Add(named.OriginalDefinition);
                        continue;
                    }

                    break;
                default:
                    continue;
            }

            var definition = (INamedTypeSymbol)type;
            if (definition.DeclaringSyntaxReferences.Length == 0)
                continue; // metadata: covered by the reference set

            Node? node;
            lock (nodes)
                nodes.TryGetValue(definition, out node);
            if (node == null)
            {
                node = Direct(definition);
                lock (nodes)
                    nodes[definition] = node;
            }

            trees.UnionWith(node.Trees);
            foreach (var t in node.Types)
                Add(t);
        }

        return trees.ToArray();
    }

    private static Node Direct(INamedTypeSymbol definition)
    {
        var types = new List<ITypeSymbol>();

        void Add(ITypeSymbol? t)
        {
            if (t != null)
                types.Add(t);
        }

        void AddAttributes(ISymbol symbol)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                foreach (var arg in attribute.ConstructorArguments.Concat(attribute.NamedArguments.Select(n => n.Value)))
                {
                    if (arg.Kind == TypedConstantKind.Type)
                        Add(arg.Value as ITypeSymbol);
                }
            }
        }

        Add(definition.ContainingType);
        Add(definition.BaseType);
        foreach (var i in definition.Interfaces)
            Add(i);
        AddAttributes(definition);
        foreach (var member in definition.GetMembers())
        {
            switch (member)
            {
                case IFieldSymbol field:
                    Add(field.Type);
                    AddAttributes(field);
                    break;
                case IPropertySymbol property:
                    Add(property.Type);
                    AddAttributes(property);
                    break;
                case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                    foreach (var parameter in constructor.Parameters)
                        Add(parameter.Type);
                    AddAttributes(constructor);
                    break;
            }
        }

        return new Node
        {
            Trees = definition.DeclaringSyntaxReferences.Select(r => r.SyntaxTree).Distinct().ToArray(),
            Types = types,
        };
    }
}
