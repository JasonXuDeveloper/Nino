using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// The names of the assemblies an assembly references: only assemblies that reference Nino.Core can declare
/// <c>[NinoType]</c>s or register formatters. Keyed by assembly symbol, whose instances for unchanged references are
/// reused across compilations (edits) and shared by the projects of a solution.
/// </summary>
internal static class AssemblyReferences
{
    private static readonly ConditionalWeakTable<IAssemblySymbol, HashSet<string>> Names = new();

    public static HashSet<string> Of(IAssemblySymbol assembly) => Names.GetValue(assembly, static a =>
    {
        var names = new HashSet<string>();
        foreach (var module in a.Modules)
        {
            foreach (var referenced in module.ReferencedAssemblies)
                names.Add(referenced.Name);
        }

        return names;
    });
}
