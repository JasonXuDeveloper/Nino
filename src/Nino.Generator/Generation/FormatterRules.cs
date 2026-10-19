using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// One <c>[assembly: NinoFormatter(typeof(F))]</c> registration: a struct <c>F</c> implementing
/// <c>INinoFormatter&lt;Pattern&gt;</c>. The runtime resolver (<c>Nino.Core.NinoFormatterResolver</c>) applies the
/// same registrations with the same rules (see <c>NinoFormatterAttribute</c>).
/// </summary>
internal sealed class FormatterRule
{
    public FormatterRule(INamedTypeSymbol formatter, ITypeSymbol pattern, ImmutableArray<INamedTypeSymbol> targets,
        int priority)
    {
        Formatter = formatter;
        Pattern = pattern;
        Targets = targets;
        Priority = priority;
        var open = TypeNames.OpenName(formatter);
        Prefix = formatter.Arity == 0 ? open : open.Substring(0, open.LastIndexOf('<'));
    }

    /// <summary>The formatter definition; the pattern is written in its type parameters.</summary>
    public INamedTypeSymbol Formatter { get; }

    /// <summary>The type the formatter formats, in terms of its type parameters.</summary>
    public ITypeSymbol Pattern { get; }

    /// <summary>For a bare type parameter pattern, the definitions it is restricted to (empty: unrestricted).</summary>
    public ImmutableArray<INamedTypeSymbol> Targets { get; }

    public int Priority { get; }

    /// <summary>The formatter's C# name without type arguments.</summary>
    public string Prefix { get; }
}

/// <summary>
/// The formatter registrations visible to one compilation: those of Nino.Core (the BCL types), of every referenced
/// assembly that references Nino.Core, and of the compilation itself, indexed by what they format.
/// </summary>
internal sealed class FormatterRules
{
    private readonly Dictionary<ISymbol, List<FormatterRule>> _byDefinition = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<int, List<FormatterRule>> _byArrayRank = new();
    private readonly List<FormatterRule> _open = new();

    public FormatterRules(Compilation compilation, NinoSymbols symbols)
    {
        if (symbols.FormatterAttribute == null || symbols.IFormatter == null)
            return;
        var core = symbols.FormatterAttribute.ContainingAssembly;
        foreach (var assembly in RegisteringAssemblies(compilation, core))
        {
            foreach (var attribute in assembly.GetAttributes())
            {
                if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, symbols.FormatterAttribute))
                    continue;
                var rule = Read(attribute, symbols, out _);
                if (rule != null && !IsGeneratedNinoTypeFormatter(rule) &&
                    compilation.IsSymbolAccessibleWithin(rule.Formatter, compilation.Assembly))
                    Add(rule);
            }
        }

        foreach (var list in _byDefinition.Values.Concat(_byArrayRank.Values).Append(_open))
            list.Sort(static (a, b) => b.Priority != a.Priority
                ? b.Priority.CompareTo(a.Priority)
                : string.CompareOrdinal(a.Prefix, b.Prefix));
    }

    /// <summary>Rules for a type of this definition, best first; empty if none.</summary>
    public IReadOnlyList<FormatterRule> ForDefinition(INamedTypeSymbol definition) =>
        _byDefinition.TryGetValue(definition, out var list) ? list : (IReadOnlyList<FormatterRule>)[];

    /// <summary>Rules for arrays of this rank (1 = single-dimensional, zero-based), best first.</summary>
    public IReadOnlyList<FormatterRule> ForArray(int rank) =>
        _byArrayRank.TryGetValue(rank, out var list) ? list : (IReadOnlyList<FormatterRule>)[];

    /// <summary>Unrestricted rules of a bare type parameter, best first.</summary>
    public IReadOnlyList<FormatterRule> Open => _open;

    private void Add(FormatterRule rule)
    {
        switch (rule.Pattern)
        {
            case ITypeParameterSymbol when rule.Targets.IsEmpty:
                _open.Add(rule);
                break;
            case ITypeParameterSymbol:
                foreach (var target in rule.Targets)
                    Key(target).Add(rule);
                break;
            case IArrayTypeSymbol array:
            {
                int rank = array.IsSZArray ? 1 : -array.Rank;
                if (!_byArrayRank.TryGetValue(rank, out var list))
                    _byArrayRank[rank] = list = new List<FormatterRule>();
                list.Add(rule);
                break;
            }
            case INamedTypeSymbol named:
                Key(named.OriginalDefinition).Add(rule);
                break;
        }
    }

    private List<FormatterRule> Key(INamedTypeSymbol definition)
    {
        if (!_byDefinition.TryGetValue(definition, out var list))
            _byDefinition[definition] = list = new List<FormatterRule>();
        return list;
    }

    /// <summary>
    /// For each referenced assembly (other than Nino.Core) that registers formatters this compilation can use, the
    /// open name of one of its formatters (<c>global::Lib.ImmutableArrayFormatter&lt;,&gt;</c>). The generated registry
    /// names them so the runtime resolver sees their registrations even before anything else loads the assembly
    /// (a formatter only needed by a generic <c>[NinoType]</c>, closed at runtime, is not referenced by any code).
    /// </summary>
    public static IEnumerable<string> RegisteringReferences(Compilation compilation)
    {
        var attributeType = compilation.GetTypeByMetadataName(NinoNames.Core("NinoFormatterAttribute"));
        if (attributeType == null)
            yield break;
        var core = attributeType.ContainingAssembly;
        foreach (var assembly in RegisteringAssemblies(compilation, core))
        {
            if (SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly) ||
                SymbolEqualityComparer.Default.Equals(assembly, core))
                continue;
            foreach (var attribute in assembly.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType) &&
                    attribute.ConstructorArguments.Length != 0 &&
                    attribute.ConstructorArguments[0].Value is INamedTypeSymbol { OriginalDefinition: var formatter } &&
                    SymbolEqualityComparer.Default.Equals(formatter.ContainingAssembly, assembly) &&
                    formatter.ContainingType == null &&
                    compilation.IsSymbolAccessibleWithin(formatter, compilation.Assembly))
                {
                    yield return TypeNames.OpenName(formatter);
                    break;
                }
            }
        }
    }

    // Generated code registers the formatters of generic [NinoType]s for the runtime resolver. The generator resolves
    // [NinoType]s itself (its own generated formatter, or CachedFormatter<T> for other assemblies'), so it skips them.
    private static bool IsGeneratedNinoTypeFormatter(FormatterRule rule) =>
        rule.Formatter.ContainingNamespace is { Name: TypeNames.GenNamespace, ContainingNamespace.IsGlobalNamespace: true } &&
        rule.Pattern is INamedTypeSymbol pattern && pattern.OriginalDefinition.IsNinoType();

    // Registrations can only come from Nino.Core, assemblies that reference it, and the compilation itself.
    private static IEnumerable<IAssemblySymbol> RegisteringAssemblies(Compilation compilation, IAssemblySymbol core)
    {
        yield return compilation.Assembly;
        foreach (var reference in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            if (SymbolEqualityComparer.Default.Equals(reference, core) ||
                AssemblyReferences.Of(reference).Contains(core.Name))
                yield return reference;
        }
    }

    /// <summary>
    /// Reads a registration, or explains (<paramref name="error"/>) why it is invalid. Shared with the analyzer,
    /// which reports NINO020 for invalid registrations of the compilation itself.
    /// </summary>
    public static FormatterRule? Read(AttributeData attribute, NinoSymbols symbols, out string? error)
    {
        error = null;
        var args = attribute.ConstructorArguments;
        if (args.Length == 0 || args[0].Value is not INamedTypeSymbol type || type.TypeKind == TypeKind.Error)
            return null;
        var formatter = type.OriginalDefinition;
        if (formatter.TypeKind != TypeKind.Struct || formatter.IsRefLikeType)
        {
            error = "it is not a struct";
            return null;
        }

        if (type.IsGenericType && !type.IsUnboundGenericType)
        {
            error = "pass the open generic definition (typeof(F<,>))";
            return null;
        }

        for (var containing = formatter.ContainingType; containing != null; containing = containing.ContainingType)
        {
            if (containing.IsGenericType)
            {
                error = "it is nested in a generic type";
                return null;
            }
        }

        ITypeSymbol? pattern = null;
        foreach (var i in formatter.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, symbols.IFormatter))
                continue;
            if (pattern != null)
            {
                error = "it implements INinoFormatter<T> more than once";
                return null;
            }

            pattern = i.TypeArguments[0];
        }

        if (pattern == null)
        {
            error = "it does not implement INinoFormatter<T>";
            return null;
        }

        var targets = ImmutableArray<INamedTypeSymbol>.Empty;
        if (args.Length > 1 && args[1].Kind == TypedConstantKind.Array)
        {
            var builder = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
            foreach (var value in args[1].Values)
            {
                if (value.Value is INamedTypeSymbol target)
                    builder.Add(target.OriginalDefinition);
            }

            targets = builder.ToImmutable();
        }

        if (!targets.IsEmpty && pattern is not ITypeParameterSymbol)
        {
            error = "targets only apply to a formatter of a bare type parameter (INinoFormatter<T>)";
            return null;
        }

        if (Uninferable(formatter, pattern, symbols) is { } parameter)
        {
            error = $"its type parameter '{parameter.Name}' cannot be inferred from the formatted type";
            return null;
        }

        int priority = 0;
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "Priority" && named.Value.Value is int p)
                priority = p;
        }

        return new FormatterRule(formatter, pattern, targets, priority);
    }

    // A type parameter is inferred when it occurs in the pattern, in a type constraint of an inferred parameter, or
    // is a formatter (INinoFormatter<E>) of an inferred element type.
    private static ITypeParameterSymbol? Uninferable(INamedTypeSymbol formatter, ITypeSymbol pattern,
        NinoSymbols symbols)
    {
        var known = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        Collect(pattern, known);
        bool progress = true;
        while (progress)
        {
            progress = false;
            foreach (var parameter in formatter.TypeParameters)
            {
                if (known.Contains(parameter))
                {
                    foreach (var constraint in parameter.ConstraintTypes)
                    {
                        if (!IsFormatterConstraint(constraint, symbols, out _))
                            progress |= Collect(constraint, known);
                    }
                }
                else if (FormatterElement(parameter, symbols) is { } element && AllKnown(element, known))
                {
                    progress |= known.Add(parameter);
                }
            }
        }

        return formatter.TypeParameters.FirstOrDefault(p => !known.Contains(p));
    }

    /// <summary>The element type <c>E</c> of a parameter constrained to <c>INinoFormatter&lt;E&gt;</c>, if any.</summary>
    public static ITypeSymbol? FormatterElement(ITypeParameterSymbol parameter, NinoSymbols symbols)
    {
        foreach (var constraint in parameter.ConstraintTypes)
        {
            if (IsFormatterConstraint(constraint, symbols, out var element))
                return element;
        }

        return null;
    }

    private static bool IsFormatterConstraint(ITypeSymbol constraint, NinoSymbols symbols, out ITypeSymbol? element)
    {
        if (constraint is INamedTypeSymbol named &&
            SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, symbols.IFormatter))
        {
            element = named.TypeArguments[0];
            return true;
        }

        element = null;
        return false;
    }

    private static bool Collect(ITypeSymbol type, HashSet<ITypeParameterSymbol> known)
    {
        switch (type)
        {
            case ITypeParameterSymbol p:
                return known.Add(p);
            case IArrayTypeSymbol a:
                return Collect(a.ElementType, known);
            case INamedTypeSymbol n:
            {
                bool added = false;
                foreach (var arg in n.TypeArguments)
                    added |= Collect(arg, known);
                return added;
            }
            default:
                return false;
        }
    }

    private static bool AllKnown(ITypeSymbol type, HashSet<ITypeParameterSymbol> known) => type switch
    {
        ITypeParameterSymbol p => known.Contains(p),
        IArrayTypeSymbol a => AllKnown(a.ElementType, known),
        INamedTypeSymbol n => n.TypeArguments.All(a => AllKnown(a, known)),
        _ => true,
    };
}
