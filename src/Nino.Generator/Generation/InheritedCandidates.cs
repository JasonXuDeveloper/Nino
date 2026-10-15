using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;

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

    /// <summary>The rightmost identifier of a (qualified) name, e.g. <c>Base</c> for <c>Ns.Base&lt;T&gt;</c>.</summary>
    public static string? SimpleName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax s => s.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Unmarked declarations whose base names reach a NinoType name (source or referenced).</summary>
    /// <param name="declarations">Declarations with a base list or a syntactic <c>[NinoType]</c>.</param>
    /// <param name="referencedNinoTypes">NinoTypes of referenced assemblies.</param>
    /// <param name="markedTypes">Types of this compilation with a bound <c>[NinoType]</c> (aliases included).</param>
    public static EquatableArray<InheritedCandidate> Find(IEnumerable<TypeDeclarationInfo> declarations,
        IEnumerable<string> referencedNinoTypes, IEnumerable<string> markedTypes)
    {
        var all = declarations.ToList();
        var names = new HashSet<string>(referencedNinoTypes);
        names.UnionWith(markedTypes);
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

    /// <summary>What a referenced assembly contributes; computed once per assembly symbol.</summary>
    private sealed class AssemblyInfo
    {
        /// <summary>Names of the assemblies it references.</summary>
        public readonly HashSet<string> References = new();

        /// <summary>Simple names of its NinoTypes (including inherited ones), once collected.</summary>
        public string[]? NinoTypes;
    }

    // Keyed by assembly symbol: the symbols of unchanged references are reused across compilations (edits) and
    // shared by the projects of a solution, which a single cached reference list would make evict each other.
    private static readonly ConditionalWeakTable<IAssemblySymbol, AssemblyInfo> Assemblies = new();

    /// <summary>Simple names of the NinoTypes (including inherited ones) declared in referenced assemblies.</summary>
    public static EquatableArray<string> ReferencedNinoTypes(Compilation compilation, CancellationToken ct)
    {
        var assemblies = new List<(IAssemblySymbol Assembly, AssemblyInfo Info)>();
        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                assemblies.Add((assembly, Assemblies.GetValue(assembly, Describe)));
        }

        // Only assemblies that reference Nino.Core, directly or through another such assembly, can declare or
        // inherit NinoTypes.
        var ninoAware = new HashSet<string> { NinoNames.CoreAssembly };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (assembly, info) in assemblies)
            {
                if (ninoAware.Contains(assembly.Name) || !info.References.Overlaps(ninoAware))
                    continue;
                ninoAware.Add(assembly.Name);
                grew = true;
            }
        }

        var names = new List<string>();
        foreach (var (assembly, info) in assemblies)
        {
            if (assembly.Name == NinoNames.CoreAssembly || !ninoAware.Contains(assembly.Name))
                continue;
            if (info.NinoTypes == null)
            {
                var collected = new HashSet<string>();
                Collect(assembly.GlobalNamespace, collected, ct);
                info.NinoTypes = collected.ToArray(); // races collect the same names
            }

            names.AddRange(info.NinoTypes);
        }

        if (names.Count == 0)
            return EquatableArray<string>.Empty;
        names.Sort(System.StringComparer.Ordinal);
        // Distinct after sorting: the same name may come from several assemblies.
        int count = 1;
        for (int i = 1; i < names.Count; i++)
        {
            if (names[i] != names[count - 1])
                names[count++] = names[i];
        }

        names.RemoveRange(count, names.Count - count);
        return new EquatableArray<string>(names);
    }

    private static AssemblyInfo Describe(IAssemblySymbol assembly)
    {
        var info = new AssemblyInfo();
        foreach (var module in assembly.Modules)
        {
            foreach (var referenced in module.ReferencedAssemblies)
                info.References.Add(referenced.Name);
        }

        return info;
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
