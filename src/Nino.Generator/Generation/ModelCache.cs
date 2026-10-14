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
/// preprocessor symbols, the set of files). Edited files get new <see cref="SyntaxTree"/> instances, so a
/// model stays valid while every tree it depends on is still part of the compilation.
/// </para>
/// </summary>
internal static class ModelCache
{
    private sealed class Entry
    {
        public ProjectState State = null!;
        public string Symbols = "";
        // Null until collected (see GetOrBuild).
        public SyntaxTree[]? Trees;
        public TypeModel? Model;
    }

    /// <summary>Project-wide inputs of one compilation; equal states mean the same references and files.</summary>
    private sealed class ProjectState
    {
        public string AssemblyName = "";
        public MetadataReference[] References = null!;
        public string[] Paths = null!;

        public bool SameAs(ProjectState other) =>
            AssemblyName == other.AssemblyName &&
            References.Length == other.References.Length &&
            References.Zip(other.References, ReferenceEquals).All(x => x) &&
            Paths.SequenceEqual(other.Paths);
    }

    private static readonly object Lock = new();
    private static readonly ConditionalWeakTable<Compilation, ProjectState> States = new();
    private static readonly Dictionary<string, ProjectState> LatestState = new();
    private static readonly Dictionary<(string Assembly, string Type), Entry> Entries = new();

    public static TypeModel? GetOrBuild(INamedTypeSymbol type, Compilation compilation, ParseOptions? options,
        System.Func<TypeModel?> build)
    {
        var state = GetState(compilation);
        string symbols = options == null ? "" : string.Join(";", options.PreprocessorSymbolNames);
        var key = (state.AssemblyName, type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        bool seen;
        lock (Lock)
        {
            seen = Entries.TryGetValue(key, out var entry);
            if (seen && entry!.Trees != null && entry.State == state && entry.Symbols == symbols &&
                entry.Trees.All(compilation.ContainsSyntaxTree))
                return entry.Model;
        }

        var model = build();
        // Dependencies are only collected once a type is built a second time: a command-line build runs the
        // generator once and never pays for them.
        var trees = seen ? Dependencies(type.OriginalDefinition) : null;
        lock (Lock)
        {
            Entries[key] = new Entry { State = state, Symbols = symbols, Trees = trees, Model = model };
        }

        return model;
    }

    private static ProjectState GetState(Compilation compilation)
    {
        lock (Lock)
        {
            if (States.TryGetValue(compilation, out var known))
                return known;

            var state = new ProjectState
            {
                AssemblyName = compilation.AssemblyName ?? "",
                References = compilation.References.ToArray(),
                Paths = compilation.SyntaxTrees.Select(t => t.FilePath).OrderBy(p => p, System.StringComparer.Ordinal)
                    .ToArray(),
            };

            // Reuse the previous state object while nothing project-wide changed, so entries stay valid;
            // otherwise every entry of this project is rebuilt once.
            if (LatestState.TryGetValue(state.AssemblyName, out var latest) && latest.SameAs(state))
            {
                state = latest;
            }
            else
            {
                LatestState[state.AssemblyName] = state;
                foreach (var stale in Entries.Keys.Where(k => k.Assembly == state.AssemblyName).ToList())
                    Entries.Remove(stale);
            }

            States.Add(compilation, state);
            return state;
        }
    }

    /// <summary>Syntax trees declaring the source types a model of <paramref name="root"/> can depend on.</summary>
    private static SyntaxTree[] Dependencies(INamedTypeSymbol root)
    {
        var trees = new HashSet<SyntaxTree>();
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeSymbol>();
        pending.Push(root);

        void Add(ITypeSymbol? t)
        {
            if (t != null && visited.Add(t))
                pending.Push(t);
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

        visited.Add(root);
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

            foreach (var reference in definition.DeclaringSyntaxReferences)
                trees.Add(reference.SyntaxTree);
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
        }

        return trees.ToArray();
    }
}
