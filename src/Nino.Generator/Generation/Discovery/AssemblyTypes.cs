using System.Collections.Generic;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>Every type an assembly declares, nested ones included: the one walk over an assembly's namespaces.</summary>
internal static class AssemblyTypes
{
    /// <summary>
    /// The types <paramref name="assembly"/> declares, of any accessibility, nested ones after the type that holds
    /// them, in an order that is the same for the same symbols.
    /// </summary>
    public static IEnumerable<INamedTypeSymbol> Of(IAssemblySymbol assembly, CancellationToken ct)
    {
        var namespaces = new Stack<INamespaceSymbol>();
        var types = new Stack<INamedTypeSymbol>();
        namespaces.Push(assembly.GlobalNamespace);
        while (namespaces.Count != 0)
        {
            var ns = namespaces.Pop();
            foreach (var child in ns.GetNamespaceMembers())
                namespaces.Push(child);
            foreach (var type in ns.GetTypeMembers())
                types.Push(type);
            while (types.Count != 0)
            {
                ct.ThrowIfCancellationRequested();
                var type = types.Pop();
                yield return type;
                foreach (var nested in type.GetTypeMembers())
                    types.Push(nested);
            }
        }
    }
}
