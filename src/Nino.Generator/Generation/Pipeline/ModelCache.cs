using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

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
/// model stays valid while every tree it depends on is still part of the compilation. An identical reparse or
/// an isolated ordinary method-body edit can carry those dependencies to the replacement trees.
/// </para>
/// <para>
/// The top-level <c>NinoSerializer</c>/<c>NinoDeserializer</c> calls are cached by file and position
/// (<see cref="GetOrBuildCall"/>): what a call registers and how it is intercepted depends on its own tree (its
/// location), the method it binds to, and the declarations of the type it names. Binding them was most of the
/// generator's time on every keystroke. Within the same declaration epoch, an identical reparse keeps the result.
/// After an isolated method-body edit, calls outside that body keep their bindings and get current interceptor
/// locations.
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

        /// <summary>The declarations it was last seen under (<see cref="CompilationState.Epoch"/>).</summary>
        public int Epoch;

        /// <summary>The method it binds to, with the type it names (<see cref="CallIdentity"/>).</summary>
        public string Method = "";

        /// <summary>The trees declaring the source types the type it names reaches.</summary>
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
        public CompilationOptions Options = null!;

        /// <summary>Models by type name. Guarded by <see cref="Lock"/>.</summary>
        public readonly Dictionary<string, Entry> Entries = new();

        /// <summary>Entry count at which stale entries are swept next. Guarded by <see cref="Lock"/>.</summary>
        public int SweepAt = MinSweep;

        /// <summary>Top-level calls by file path and position. Guarded by <see cref="Lock"/>.</summary>
        public readonly Dictionary<(string Path, int Position), CallEntry> Calls = new();

        /// <summary>Call count at which stale calls are swept next. Guarded by <see cref="Lock"/>.</summary>
        public int CallSweepAt = MinSweep;

        /// <summary>
        /// The trees of the compilation the current <see cref="Epoch"/> was last seen in, or null before the first.
        /// Guarded by <see cref="Lock"/>.
        /// </summary>
        public SyntaxTree[]? Declared;

        /// <summary>
        /// Counts the changes of declarations: a compilation whose trees all declare what <see cref="Declared"/>
        /// declares (<see cref="SyntaxTree.IsEquivalentTo"/> at the top level, which ignores method bodies and the
        /// initializers of fields other than constants) keeps it, any other starts the next. Guarded by
        /// <see cref="Lock"/>.
        /// </summary>
        public int Epoch;

        public bool SameAs(ProjectState other)
        {
            // Some compiler-host resolvers cannot be compared by value.
            if (ProjectKey != other.ProjectKey || Rules != other.Rules || !ReferenceEquals(Options, other.Options) ||
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

    /// <summary>The state of a compilation's project, and the declarations it holds.</summary>
    private sealed class CompilationState
    {
        public ProjectState Project = null!;
        public MovedCalls? Edit;

        /// <summary>
        /// The <see cref="ProjectState.Epoch"/> of the compilation: two compilations of a project with the same epoch
        /// declare the same symbols, so a call in a tree both share binds to the same method in both.
        /// </summary>
        public int Epoch;
    }

    private sealed record BodyEdit(SyntaxTree Previous, SyntaxTree Current, TextSpan Body, int Delta);

    private sealed record MovedCalls(SyntaxTree Current, TextSpan Body, int Delta)
    {
        public readonly Dictionary<int, CallEntry> Calls = new();

        public CallEntry? At(int position)
        {
            if (Body.Contains(position))
                return null;
            int oldPosition = position < Body.Start ? position : position - Delta;
            return Calls.TryGetValue(oldPosition, out var entry) ? entry : null;
        }
    }

    private static readonly object Lock = new();
    private static readonly ConditionalWeakTable<Compilation, CompilationState> States = new();
    private static readonly ConditionalWeakTable<ParseOptions, string> SymbolKeys = new();
    /// <summary>The latest state of each project (by <see cref="ProjectState.ProjectKey"/>), weakly. Guarded by <see cref="Lock"/>.</summary>
    private static readonly Dictionary<string, System.WeakReference<ProjectState>> LatestState = new();

    private const int MinPrune = 64;

    /// <summary>Size of <see cref="LatestState"/> at which its dead entries are removed. Guarded by <see cref="Lock"/>.</summary>
    private static int PruneAt = MinPrune;

    public static TypeModel? GetOrBuild(INamedTypeSymbol type, Compilation compilation, ParseOptions? options,
        System.Threading.CancellationToken ct)
    {
        var state = GetState(compilation).Project;
        string symbols = SymbolKey(options);
        var key = TypeNames.Global(type.OriginalDefinition);

        lock (Lock)
        {
            if (state.Entries.TryGetValue(key, out var entry) && entry.Symbols == symbols &&
                ContainsAll(compilation, entry.Trees))
                return entry.Model;
        }

        var model = ModelBuilder.Build(type, TypeFacts.For(compilation, options), ct);
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
    /// The result of the top-level call <paramref name="call"/>: the one built for the same call before, while its tree
    /// is the same and so are the declarations of every other tree, without binding it; otherwise
    /// <paramref name="bind"/> gives the method it binds to and the type it names (its type argument, or its
    /// <c>typeof</c> argument), null for a call that names none, and the result is the one built before while the
    /// method, the type and the trees the type reaches are the same, else <paramref name="build"/>'s.
    /// </summary>
    public static TopLevelCall? GetOrBuildCall(GeneratorSyntaxContext context,
        System.Func<GeneratorSyntaxContext, System.Threading.CancellationToken, (IMethodSymbol Method, ITypeSymbol Type)?> bind,
        System.Func<GeneratorSyntaxContext, IMethodSymbol, ITypeSymbol, System.Threading.CancellationToken, TopLevelCall?> build,
        System.Threading.CancellationToken ct)
    {
        var call = context.Node;
        var compilation = context.SemanticModel.Compilation;
        var known = GetState(compilation);
        var state = known.Project;
        var tree = call.SyntaxTree;
        var key = (tree.FilePath, call.SpanStart);
        CallEntry? entry;
        CallEntry? moved = null;
        lock (Lock)
        {
            // What a call binds to and builds depends on declarations only (no method body is read).
            if (state.Calls.TryGetValue(key, out entry) && entry.Epoch == known.Epoch && entry.Tree == tree)
                return entry.Call;
            if (known.Edit is { } edit && edit.Current == tree)
                moved = edit.At(call.SpanStart);
        }

        if (moved != null)
        {
            var relocated = moved.Call;
#if ROSLYN4_8
            if (relocated?.Intercepted is { } interceptor)
            {
                var located = Interceptors.AtLocation(context, interceptor, ct);
                relocated = located == null && relocated.Usage == null ? null : relocated with { Intercepted = located };
            }
#endif
            SaveCall(state, key, new CallEntry
            {
                Tree = tree, Epoch = known.Epoch, Method = moved.Method, Trees = moved.Trees, Call = relocated,
            }, compilation);
            return relocated;
        }

        var bound = bind(context, ct);
        string identity = bound is { } named ? CallIdentity(named.Method, named.Type) : "";
        lock (Lock)
        {
            if (entry != null && entry.Tree == tree && entry.Method == identity && ContainsAll(compilation, entry.Trees))
            {
                entry.Epoch = known.Epoch;
                return entry.Call;
            }
        }

        TopLevelCall? result = null;
        var trees = System.Array.Empty<SyntaxTree>();
        if (bound is { } found)
        {
            result = build(context, found.Method, found.Type, ct);
            trees = Dependencies(found.Type, compilation);
        }

        SaveCall(state, key, new CallEntry
        {
            Tree = tree, Epoch = known.Epoch, Method = identity, Trees = trees, Call = result,
        }, compilation);
        return result;
    }

    private static void SaveCall(ProjectState state, (string Path, int Position) key, CallEntry entry,
        Compilation compilation)
    {
        lock (Lock)
        {
            state.Calls[key] = entry;
            if (state.Calls.Count >= state.CallSweepAt)
            {
                // Calls in changed files are stored at their new positions: drop old entries.
                List<(string Path, int Position)>? stale = null;
                foreach (var pair in state.Calls)
                    if (!compilation.ContainsSyntaxTree(pair.Value.Tree))
                        (stale ??= new List<(string, int)>()).Add(pair.Key);
                if (stale != null)
                    foreach (var removed in stale)
                        state.Calls.Remove(removed);
                state.CallSweepAt = System.Math.Max(MinSweep, state.Calls.Count * 2);
            }
        }
    }

    /// <summary>The method a call binds to and the type it names, as a string.</summary>
    private static string CallIdentity(IMethodSymbol method, ITypeSymbol type) =>
        method.OriginalDefinition.ToDisplayString() + "<" + TypeNames.Global(type) + ">";

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

    private static CompilationState GetState(Compilation compilation)
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
            Options = compilation.Options,
            // The compilation's own formatter registrations: one edited in a file no model depends on still changes
            // the formatters models resolve.
            Rules = FormatterRules.OwnSignature(compilation, TypeFacts.For(compilation, options).Symbols),
        };

        ProjectState? latest;
        SyntaxTree[]? declared;
        lock (Lock)
        {
            if (States.TryGetValue(compilation, out var known))
                return known;
            latest = Latest(state.ProjectKey);
            declared = latest?.Declared;
        }

        // Outside the lock: comparing an edited tree walks its declarations.
        var trees = compilation.SyntaxTrees.ToArray();
        bool comparable = latest != null && latest.SameAs(state) && declared != null;
        var reparsed = comparable ? MatchReparsedSources(declared!, trees) : null;
        bool sameDeclarations = comparable && (reparsed != null || DeclareTheSame(declared!, trees));
        var edit = sameDeclarations && reparsed == null ? MatchBodyEdit(declared!, trees) : null;

        lock (Lock)
        {
            if (States.TryGetValue(compilation, out var known))
                return known;
            // Reuse the previous state object while nothing project-wide changed, so its entries stay valid;
            // otherwise the new state starts empty and every model of this project is rebuilt once.
            if (Latest(state.ProjectKey) is { } current && current.SameAs(state))
                state = current;
            else
                LatestState[state.ProjectKey] = new System.WeakReference<ProjectState>(state);
            // Compared against the trees another compilation may have replaced meanwhile: then a new epoch.
            if (!sameDeclarations || state != latest || state.Declared != declared)
            {
                state.Epoch++;
                reparsed = null;
                edit = null;
            }
            if (reparsed != null || edit != null)
                RebaseModelTrees(state, reparsed, edit);
            if (reparsed != null)
                RebaseReparsedCalls(state, reparsed);
            var moved = edit != null ? CaptureMovedCalls(state, edit) : null;
            state.Declared = trees;
            PruneDeadProjects();
            var created = new CompilationState { Project = state, Epoch = state.Epoch, Edit = moved };
            States.Add(compilation, created);
            return created;
        }
    }

    /// <summary>Detaches the edited tree's calls before a shifted call can take another old call's position.</summary>
    private static MovedCalls CaptureMovedCalls(ProjectState state, BodyEdit edit)
    {
        var moved = new MovedCalls(edit.Current, edit.Body, edit.Delta);
        List<(string Path, int Position)>? oldKeys = null;
        foreach (var pair in state.Calls)
        {
            var entry = pair.Value;
            if (entry.Tree == edit.Previous)
            {
                (oldKeys ??= new List<(string, int)>()).Add(pair.Key);
                if (entry.Epoch == state.Epoch &&
                    (pair.Key.Position < edit.Body.Start || pair.Key.Position >= edit.Body.End - edit.Delta))
                {
                    entry.Tree = edit.Current;
                    moved.Calls.Add(pair.Key.Position, entry);
                }
            }

            if (entry.Epoch != state.Epoch)
                continue;
            var dependencies = entry.Trees;
            for (int i = 0; i < dependencies.Length; i++)
                if (dependencies[i] == edit.Previous)
                    dependencies[i] = edit.Current;
        }

        if (oldKeys != null)
            foreach (var key in oldKeys)
                state.Calls.Remove(key);
        return moved;
    }

    /// <summary>Updates identical reparses before publishing the state, so it need not keep the old tree map.</summary>
    private static void RebaseReparsedCalls(ProjectState state, Dictionary<SyntaxTree, SyntaxTree> reparsed)
    {
        foreach (var entry in state.Calls.Values)
        {
            if (entry.Epoch != state.Epoch)
                continue;
            if (reparsed.TryGetValue(entry.Tree, out var tree))
                entry.Tree = tree;
            var dependencies = entry.Trees;
            for (int i = 0; i < dependencies.Length; i++)
                if (reparsed.TryGetValue(dependencies[i], out var replacement))
                    dependencies[i] = replacement;
        }
    }

    /// <summary>
    /// Carries cached model dependencies over a proven replacement. An ordinary method body cannot change the
    /// constructor, member initializer or accessor syntax the model reads. Called under <see cref="Lock"/>.
    /// </summary>
    private static void RebaseModelTrees(ProjectState state, Dictionary<SyntaxTree, SyntaxTree>? reparsed, BodyEdit? edit)
    {
        foreach (var entry in state.Entries.Values)
        {
            var trees = entry.Trees;
            for (int i = 0; i < trees.Length; i++)
            {
                var tree = trees[i];
                if (edit != null && tree == edit.Previous)
                    trees[i] = edit.Current;
                else if (reparsed != null && reparsed.TryGetValue(tree, out var replacement))
                    trees[i] = replacement;
            }
        }
    }

    /// <summary>Finds a single edit confined to a directive-free method body. The other bodies keep their bindings.</summary>
    private static BodyEdit? MatchBodyEdit(SyntaxTree[] before, SyntaxTree[] now)
    {
        SyntaxTree? previous = null, current = null;
        for (int i = 0; i < now.Length; i++)
        {
            if (before[i] == now[i])
                continue;
            if (current != null || now[i].Options.Kind != SourceCodeKind.Regular ||
                !now[i].Options.Equals(before[i].Options))
                return null;
            previous = before[i];
            current = now[i];
        }

        if (current == null)
            return null;
        var changes = current.GetText().GetChangeRanges(previous!.GetText());
        if (changes.Count != 1)
            return null;
        var change = changes[0];
        var oldBody = MethodBodyContaining(previous, change.Span);
        var newBody = MethodBodyContaining(current, new TextSpan(change.Span.Start, change.NewLength));
        int delta = change.NewLength - change.Span.Length;
        return oldBody != null && newBody != null && oldBody.SpanStart == newBody.SpanStart &&
               oldBody.Span.End + delta == newBody.Span.End
            ? new BodyEdit(previous!, current, newBody.Span, delta)
            : null;
    }

    private static BlockSyntax? MethodBodyContaining(SyntaxTree tree, TextSpan change)
    {
        var body = tree.GetRoot().FindToken(change.Start).Parent?.FirstAncestorOrSelf<BlockSyntax>(
            static block => block.Parent is MethodDeclarationSyntax);
        return body != null && !body.ContainsDirectives && !body.OpenBraceToken.IsMissing &&
               !body.CloseBraceToken.IsMissing && change.Start >= body.OpenBraceToken.Span.End &&
               change.End <= body.CloseBraceToken.SpanStart ? body : null;
    }

    /// <summary>Maps old trees to identical reparses, including the interceptor's file checksum.</summary>
    private static Dictionary<SyntaxTree, SyntaxTree>? MatchReparsedSources(SyntaxTree[] before, SyntaxTree[] now)
    {
        if (before.Length != now.Length)
            return null;
        Dictionary<SyntaxTree, SyntaxTree>? matches = null;
        for (int i = 0; i < now.Length; i++)
        {
            var current = now[i];
            var previous = before[i];
            if (current.Options.Kind != SourceCodeKind.Regular)
                return null;
            if (current == previous)
                continue;
            if (current.FilePath != previous.FilePath || !current.Options.Equals(previous.Options))
                return null;
            var text = current.GetText();
            var oldText = previous.GetText();
            if (!ReferenceEquals(text, oldText) &&
                (text.ChecksumAlgorithm != oldText.ChecksumAlgorithm || !text.ContentEquals(oldText) ||
                 !text.GetChecksum().SequenceEqual(oldText.GetChecksum())))
                return null;
            if (!current.IsEquivalentTo(previous))
                return null;
            (matches ??= new Dictionary<SyntaxTree, SyntaxTree>()).Add(previous, current);
        }

        return matches;
    }

    /// <summary>
    /// Whether <paramref name="now"/> declares what <paramref name="before"/> declares, tree by tree in the same order:
    /// the same instance, or a tree equivalent at the top level (an edit inside a method body or a comment).
    /// </summary>
    private static bool DeclareTheSame(SyntaxTree[] before, SyntaxTree[] now)
    {
        if (before.Length != now.Length)
            return false;
        for (int i = 0; i < now.Length; i++)
        {
            if (!ReferenceEquals(before[i], now[i]) &&
                (before[i].FilePath != now[i].FilePath || !now[i].IsEquivalentTo(before[i], topLevel: true)))
                return false;
        }

        return true;
    }

    /// <summary>The latest state of the project <paramref name="key"/>, if it is still alive. Guarded by <see cref="Lock"/>.</summary>
    private static ProjectState? Latest(string key) =>
        LatestState.TryGetValue(key, out var weak) && weak.TryGetTarget(out var state) ? state : null;

    /// <summary>
    /// Drops names of projects whose compilations no longer keep their state alive. Guarded by <see cref="Lock"/>.
    /// </summary>
    private static void PruneDeadProjects()
    {
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
                foreach (var arg in attribute.ConstructorArguments)
                {
                    if (arg.Kind == TypedConstantKind.Type)
                        Add(arg.Value as ITypeSymbol);
                }

                foreach (var arg in attribute.NamedArguments)
                {
                    if (arg.Value.Kind == TypedConstantKind.Type)
                        Add(arg.Value.Value as ITypeSymbol);
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
