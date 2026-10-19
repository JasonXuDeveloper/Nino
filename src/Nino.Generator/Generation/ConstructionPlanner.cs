using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;
using Nino.Generator.Analysis;

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

        // Static methods returning the type: a [NinoFactory] factory, or a [NinoConstructor] one.
        var factories = type.GetMembers().OfType<IMethodSymbol>().Where(m =>
            m.IsStatic && m.MethodKind == MethodKind.Ordinary &&
            SymbolEqualityComparer.Default.Equals(m.ReturnType, type)).ToList();
        var factory = known.FindFactory(type);
        string factoryName = factory == null ? "" : $"{typeName}.{factory.Name}";

        var constructors = type.InstanceConstructors.Where(facts.Visibility.IsVisible).ToList();
        var marked = type.InstanceConstructors.Concat(factories)
            .FirstOrDefault(m => NinoSymbols.Has(m, known.Constructor));

        // C# 11 required members: a constructor that is not [SetsRequiredMembers] is only callable with an object
        // initializer setting all of them, which only a re-created instance has.
        var required = NinoSymbols.RequiredMembers(type);
        bool SetsRequired(IMethodSymbol m) => required.Count == 0 || NinoSymbols.SetsRequiredMembers(m);

        // A [NinoFactory] replaces the parameterless constructor, which may then be private (or absent).
        if (marked == null && !members.Any(m => m.ConstructionOnly) &&
            (type.IsValueType || factory != null ||
             constructors.Any(c => NinoSymbols.CallableWithoutArguments(c) && SetsRequired(c))))
            return new ConstructionModel(false, $"new {typeName}", EquatableArray<string>.Empty, factoryName);

        string Creator(IMethodSymbol m) => m.MethodKind == MethodKind.Constructor
            ? $"new {typeName}"
            : $"{typeName}.{m.Name}";

        // Constructor parameters match serialized members by name, ignoring case; null when one matches none. A
        // member hidden by a derived one of the same name (renamed) matches no parameter: the derived one does.
        List<string>? Match(IEnumerable<string> names, out string? missing)
        {
            var args = new List<string>();
            foreach (var name in names)
            {
                var match = members.FirstOrDefault(m => m.Name == m.SourceName &&
                                                        string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
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
            // The instance is always created through the [NinoConstructor]: the factory is never called.
            if (factory != null)
                diagnostics?.Report(NinoDiagnostics.FactoryIgnored, factory, factory.Name, type.ToDisplayString(),
                    marked.ToDisplayString());

            // Generated code calls it from outside the type: a private or protected one would not compile.
            if (!facts.Visibility.IsVisible(marked))
            {
                diagnostics?.Report(NinoDiagnostics.InaccessibleConstructor, marked, marked.ToDisplayString(),
                    type.ToDisplayString());
                return Unconstructible;
            }

            var names = NinoSymbols.ConstructorNames(NinoSymbols.Get(marked, known.Constructor)!) ??
                        marked.Parameters.Select(p => p.Name).ToList();
            var args = Match(names, out var missing);
            if (args == null)
            {
                diagnostics?.Report(NinoDiagnostics.MissingConstructorMember, marked, missing!, type.ToDisplayString());
                return Unconstructible;
            }

            return new ConstructionModel(true, Creator(marked), new EquatableArray<string>(args), "");
        }

        // Whether the object initializer (the members that are not arguments and are assigned directly) sets every
        // required member.
        bool InitializerSetsRequired(List<string> args) => required.All(r => members.Any(m =>
            m.Name == r.Name && m.SourceName == r.Name && m.Access == MemberAccess.Direct && !args.Contains(m.Name)));

        // Pick the visible constructor with the most parameters that all map to serialized members.
        IMethodSymbol? best = null;
        List<string>? bestArgs = null;
        foreach (var ctor in constructors)
        {
            if (ctor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type))
                continue; // record copy constructor
            var args = Match(ctor.Parameters.Select(p => p.Name), out _);
            if (args != null && (SetsRequired(ctor) || InitializerSetsRequired(args)) &&
                (bestArgs == null || args.Count > bestArgs.Count))
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
