using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.DTOs;
using static Nino.Generator.Generation.TypeFacts;

namespace Nino.Generator.Generation;

/// <summary>Chooses how a type is instantiated when deserialized: reused, re-created from a constructor, or a factory.</summary>
internal static class ConstructionPlanner
{
    public static ConstructionModel Plan(INamedTypeSymbol type, List<MemberModel> members,
        List<DiagnosticInfo> diagnostics, LocationInfo? location)
    {
        string typeName = TypeNames.Global(type);
        var refFactory = type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m =>
            m.IsStatic && m.Parameters.Length == 0 && m.DeclaredAccessibility == Accessibility.Public &&
            SymbolEqualityComparer.Default.Equals(m.ReturnType, type) &&
            HasAttribute(m, "NinoRefDeserializationAttribute"));
        string refFactoryName = refFactory == null ? "" : $"{typeName}.{refFactory.Name}";

        var methods = type.InstanceConstructors.Cast<IMethodSymbol>()
            .Concat(type.GetMembers().OfType<IMethodSymbol>().Where(m =>
                m.IsStatic && m.MethodKind == MethodKind.Ordinary &&
                SymbolEqualityComparer.Default.Equals(m.ReturnType, type)))
            .ToList();
        var marked = methods.FirstOrDefault(m => HasAttribute(m, "NinoConstructorAttribute"));
        bool anyConstructionOnly = members.Any(m => m.ConstructionOnly);

        bool Visible(IMethodSymbol m) => m.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            or Accessibility.ProtectedOrInternal;

        var parameterless = type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0 && Visible(c));
        bool hasParameterless = type.IsValueType || parameterless != null;

        if (marked == null && !anyConstructionOnly && hasParameterless)
            return new ConstructionModel(false, $"new {typeName}", EquatableArray<string>.Empty, refFactoryName);

        string Creator(IMethodSymbol m) => m.MethodKind == MethodKind.Constructor
            ? $"new {typeName}"
            : $"{typeName}.{m.Name}";

        var memberNames = members.Select(m => m.Name).ToList();

        string? MatchMember(string name) =>
            memberNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        if (marked != null)
        {
            var attr = GetAttribute(marked, "NinoConstructorAttribute")!;
            var names = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Kind == TypedConstantKind.Array &&
                        attr.ConstructorArguments[0].Values.Length > 0
                ? attr.ConstructorArguments[0].Values.Select(v => v.Value as string ?? "").ToList()
                : marked.Parameters.Select(p => p.Name).ToList();
            var args = new List<string>();
            foreach (var name in names)
            {
                var match = MatchMember(name);
                if (match == null)
                {
                    diagnostics.Add(new DiagnosticInfo(NinoDiagnostics.MissingConstructorMember,
                        LocationInfo.From(marked.Locations.FirstOrDefault()) ?? location,
                        name, type.ToDisplayString()));
                    return new ConstructionModel(true, "", EquatableArray<string>.Empty, "");
                }

                args.Add(match);
            }

            return new ConstructionModel(true, Creator(marked), new EquatableArray<string>(args), "");
        }

        // Pick the visible constructor with the most parameters that all map to serialized members.
        IMethodSymbol? best = null;
        List<string>? bestArgs = null;
        foreach (var ctor in type.InstanceConstructors.Where(Visible))
        {
            if (ctor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type))
                continue; // record copy constructor
            var args = new List<string>();
            bool ok = true;
            foreach (var p in ctor.Parameters)
            {
                var match = MatchMember(p.Name);
                if (match == null)
                {
                    ok = false;
                    break;
                }

                args.Add(match);
            }

            if (ok && (best == null || args.Count > bestArgs!.Count))
            {
                best = ctor;
                bestArgs = args;
            }
        }

        if (best == null)
        {
            if (type.IsValueType)
                return new ConstructionModel(true, $"new {typeName}", EquatableArray<string>.Empty, "");
            diagnostics.Add(new DiagnosticInfo(NinoDiagnostics.NoUsableConstructor, location, type.ToDisplayString()));
            return new ConstructionModel(true, "", EquatableArray<string>.Empty, "");
        }

        return new ConstructionModel(true, Creator(best), new EquatableArray<string>(bestArgs!), "");
    }
}
