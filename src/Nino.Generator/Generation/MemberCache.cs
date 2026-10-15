using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation;

/// <summary>
/// The serialized members of each type (<see cref="MemberCollector"/>), memoized for one compilation: a base class is
/// reached by every subtype, so a deep hierarchy collects each level once. The analyzer's member findings come from the
/// same pass, which then also fills the member cache; model builds never pay for them.
/// </summary>
internal sealed class MemberCache
{
    private readonly NinoSymbols _symbols;
    private readonly ConcurrentDictionary<ISymbol, IReadOnlyList<ISymbol>> _members = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ISymbol, IReadOnlyList<MemberFinding>> _findings = new(SymbolEqualityComparer.Default);

    public MemberCache(NinoSymbols symbols) => _symbols = symbols;

    /// <summary>Serialized members of <paramref name="type"/> (whose effective attribute is <paramref name="attr"/>), in order.</summary>
    public IReadOnlyList<ISymbol> Of(INamedTypeSymbol type, NinoTypeOptions attr)
    {
        // No GetOrAdd overload passes attr without allocating a closure on every hit.
        return _members.TryGetValue(type, out var result)
            ? result
            : _members.GetOrAdd(type, MemberCollector.Collect(type, attr, _symbols));
    }

    /// <summary>What is wrong with the annotations of the members <paramref name="type"/> declares.</summary>
    public IReadOnlyList<MemberFinding> FindingsOf(INamedTypeSymbol type, NinoTypeOptions attr)
    {
        if (_findings.TryGetValue(type, out var result))
            return result;
        var findings = new List<MemberFinding>();
        var members = MemberCollector.Collect(type, attr, _symbols, findings);
        _members.TryAdd(type, members);
        return _findings.GetOrAdd(type, findings);
    }
}
