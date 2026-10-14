using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.DTOs;

namespace Nino.Generator.Generation;

/// <summary>Syntax-only facts about a type declaration that has a base list or a NinoType attribute.</summary>
internal sealed record TypeDeclarationInfo(
    string Name,
    EquatableArray<string> BaseNames,
    bool Marked,
    string FilePath,
    int Position);

/// <summary>A type declaration that may inherit <c>[NinoType]</c>: its name chain reaches a NinoType name.</summary>
internal sealed record InheritedCandidate(string Name, string FilePath, int Position);

/// <summary>
/// Finds the types that may inherit <c>[NinoType]</c> without binding anything. Binding every type with a base
/// list on each edit (every MonoBehaviour in a Unity project) dominated edit latency in large projects; now only
/// types whose base list names a NinoType (directly or through other candidates) are bound.
/// </summary>
internal static class InheritedCandidates
{
    public static TypeDeclarationInfo From(TypeDeclarationSyntax syntax, bool marked)
    {
        var bases = new List<string>();
        if (syntax.BaseList != null)
        {
            foreach (var type in syntax.BaseList.Types)
            {
                var name = SimpleName(type.Type);
                if (name != null)
                    bases.Add(name);
            }
        }

        return new TypeDeclarationInfo(syntax.Identifier.ValueText, new EquatableArray<string>(bases), marked,
            syntax.SyntaxTree.FilePath, syntax.Identifier.SpanStart);
    }

    private static string? SimpleName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax s => s.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Unmarked declarations whose base names reach a NinoType name (source or referenced).</summary>
    public static EquatableArray<InheritedCandidate> Find(IEnumerable<TypeDeclarationInfo> declarations,
        IEnumerable<string> referencedNinoTypes)
    {
        var all = declarations.ToList();
        var names = new HashSet<string>(referencedNinoTypes);
        foreach (var d in all)
        {
            if (d.Marked)
                names.Add(d.Name);
        }

        // Grow the set until no unmarked declaration adds a name (derived types of derived types).
        var pending = all.Where(d => !d.Marked).ToList();
        var result = new List<InheritedCandidate>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var d = pending[i];
                if (!d.BaseNames.Any(names.Contains))
                    continue;
                names.Add(d.Name);
                result.Add(new InheritedCandidate(d.Name, d.FilePath, d.Position));
                pending.RemoveAt(i);
                changed = true;
            }
        }

        result.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.FilePath, b.FilePath);
            return c != 0 ? c : a.Position.CompareTo(b.Position);
        });
        return new EquatableArray<InheritedCandidate>(result);
    }

    /// <summary>Binds each candidate's declaration in the current compilation.</summary>
    public static IEnumerable<(TypeDeclarationSyntax Syntax, SemanticModel Model)> Resolve(
        EquatableArray<InheritedCandidate> candidates, Compilation compilation, CancellationToken ct)
    {
        if (candidates.Length == 0)
            yield break;
        // File paths are normally unique, but in-memory trees may share one; the name check picks the right one.
        var trees = compilation.SyntaxTrees.ToLookup(t => t.FilePath);
        var models = new Dictionary<SyntaxTree, SemanticModel>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var tree in trees[candidate.FilePath])
            {
                if (candidate.Position >= tree.Length)
                    continue;
                var token = tree.GetRoot(ct).FindToken(candidate.Position);
                if (token.Parent is not TypeDeclarationSyntax syntax || syntax.Identifier != token ||
                    token.ValueText != candidate.Name)
                    continue;
                if (!models.TryGetValue(tree, out var model))
                    models[tree] = model = compilation.GetSemanticModel(tree);
                yield return (syntax, model);
                break;
            }
        }
    }

    private static readonly object CacheLock = new();
    private static MetadataReference[]? _cachedReferences;
    private static EquatableArray<string> _cachedNames;

    /// <summary>
    /// Simple names of the NinoTypes (including inherited ones) declared in referenced assemblies. Recomputed only
    /// when the set of references changes, not on every edit.
    /// </summary>
    public static EquatableArray<string> ReferencedNinoTypes(Compilation compilation, CancellationToken ct)
    {
        var references = compilation.References.ToArray();
        lock (CacheLock)
        {
            if (_cachedReferences != null && _cachedReferences.Length == references.Length &&
                _cachedReferences.Zip(references, ReferenceEquals).All(x => x))
                return _cachedNames;
        }

        var assemblies = new List<IAssemblySymbol>();
        foreach (var reference in references)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                assemblies.Add(assembly);
        }

        // Only assemblies that reference Nino.Core, directly or through another such assembly, can declare or
        // inherit NinoTypes.
        var ninoAware = new HashSet<string> { "Nino.Core" };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var assembly in assemblies)
            {
                if (ninoAware.Contains(assembly.Name))
                    continue;
                if (assembly.Modules.Any(m => m.ReferencedAssemblies.Any(r => ninoAware.Contains(r.Name))))
                {
                    ninoAware.Add(assembly.Name);
                    grew = true;
                }
            }
        }

        var names = new HashSet<string>();
        foreach (var assembly in assemblies)
        {
            if (assembly.Name == "Nino.Core" || !ninoAware.Contains(assembly.Name))
                continue;
            Collect(assembly.GlobalNamespace, names, ct);
        }

        var result = new EquatableArray<string>(names.OrderBy(n => n, System.StringComparer.Ordinal));
        lock (CacheLock)
        {
            _cachedReferences = references;
            _cachedNames = result;
        }

        return result;
    }

    private static void Collect(INamespaceOrTypeSymbol container, HashSet<string> names, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var member in container.GetMembers())
        {
            if (member is INamespaceSymbol ns)
            {
                Collect(ns, names, ct);
            }
            else if (member is INamedTypeSymbol type)
            {
                if (type.TypeKind is TypeKind.Class or TypeKind.Interface && type.IsNinoType())
                    names.Add(type.Name);
                Collect(type, names, ct);
            }
        }
    }
}
