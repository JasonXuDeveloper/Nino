using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.DTOs;

namespace Nino.Generator.Generation;

/// <summary>Chooses how a type is instantiated when deserialized: reused, re-created from a constructor, or a factory.</summary>
internal static class ConstructionPlanner
{
    private static readonly ConstructionModel Unconstructible = new(true, "", EquatableArray<string>.Empty, "");

    public static ConstructionModel Plan(INamedTypeSymbol type, List<MemberModel> members, TypeFacts facts,
        DiagnosticSink? diagnostics)
    {
        var known = facts.Symbols;
        string typeName = TypeNames.Global(type);

        // Static methods returning the type: a [NinoRefDeserialization] factory, or a [NinoConstructor] one.
        var factories = type.GetMembers().OfType<IMethodSymbol>().Where(m =>
            m.IsStatic && m.MethodKind == MethodKind.Ordinary &&
            SymbolEqualityComparer.Default.Equals(m.ReturnType, type)).ToList();
        var refFactory = factories.FirstOrDefault(m => m.Parameters.Length == 0 &&
                                                       m.DeclaredAccessibility == Accessibility.Public &&
                                                       NinoSymbols.Has(m, known.RefDeserialization));
        string refFactoryName = refFactory == null ? "" : $"{typeName}.{refFactory.Name}";

        var constructors = type.InstanceConstructors.Where(facts.Visibility.IsVisible).ToList();
        var marked = type.InstanceConstructors.Concat(factories)
            .FirstOrDefault(m => NinoSymbols.Has(m, known.Constructor));

        if (marked == null && !members.Any(m => m.ConstructionOnly) &&
            (type.IsValueType || constructors.Any(c => c.Parameters.Length == 0)))
            return new ConstructionModel(false, $"new {typeName}", EquatableArray<string>.Empty, refFactoryName);

        string Creator(IMethodSymbol m) => m.MethodKind == MethodKind.Constructor
            ? $"new {typeName}"
            : $"{typeName}.{m.Name}";

        // Constructor parameters match serialized members by name, ignoring case; null when one matches none.
        List<string>? Match(IEnumerable<string> names, out string? missing)
        {
            var args = new List<string>();
            foreach (var name in names)
            {
                var match = members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    missing = name;
                    return null;
                }

                args.Add(match.Name);
            }

            missing = null;
            return args;
        }

        if (marked != null)
        {
            // Generated code calls it from outside the type: a private or protected one would not compile.
            if (!facts.Visibility.IsVisible(marked))
            {
                diagnostics?.Report(NinoDiagnostics.InaccessibleConstructor, marked, marked.ToDisplayString(),
                    type.ToDisplayString());
                return Unconstructible;
            }

            var attr = NinoSymbols.Get(marked, known.Constructor)!;
            var names = attr.ConstructorArguments is { Length: > 0 } a && a[0].Kind == TypedConstantKind.Array &&
                        a[0].Values.Length > 0
                ? a[0].Values.Select(v => v.Value as string ?? "").ToList()
                : marked.Parameters.Select(p => p.Name).ToList();
            var args = Match(names, out var missing);
            if (args == null)
            {
                diagnostics?.Report(NinoDiagnostics.MissingConstructorMember, marked, missing!, type.ToDisplayString());
                return Unconstructible;
            }

            return new ConstructionModel(true, Creator(marked), new EquatableArray<string>(args), "");
        }

        // Pick the visible constructor with the most parameters that all map to serialized members.
        IMethodSymbol? best = null;
        List<string>? bestArgs = null;
        foreach (var ctor in constructors)
        {
            if (ctor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type))
                continue; // record copy constructor
            var args = Match(ctor.Parameters.Select(p => p.Name), out _);
            if (args != null && (bestArgs == null || args.Count > bestArgs.Count))
            {
                best = ctor;
                bestArgs = args;
            }
        }

        if (best == null)
        {
            if (type.IsValueType)
                return new ConstructionModel(true, $"new {typeName}", EquatableArray<string>.Empty, "");
            diagnostics?.Report(NinoDiagnostics.NoUsableConstructor, type, type.ToDisplayString());
            return Unconstructible;
        }

        return new ConstructionModel(true, Creator(best), new EquatableArray<string>(bestArgs!), "");
    }
}
