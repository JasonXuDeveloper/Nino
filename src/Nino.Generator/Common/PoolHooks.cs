using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Nino.Generator.Common;

/// <summary>
/// The partial methods a <c>[NinoPooled]</c> class implements: the generator declares them, the analyzer reports
/// missing bodies (NINO019) and the code fix adds them. Compiled into both the generator and the code fixes.
/// </summary>
internal static class PoolHooks
{
    public const string Warmup = "OnNinoPoolWarmup";
    public const string Return = "OnNinoPoolReturn";

    /// <summary>Names of the pool hooks <paramref name="type"/> has no body for.</summary>
    public static IEnumerable<string> Missing(INamedTypeSymbol type, CancellationToken cancellationToken) =>
        new[] { Warmup, Return }.Where(name => !type.GetMembers(name).OfType<IMethodSymbol>().Any(m =>
            (m.PartialImplementationPart ?? m).DeclaringSyntaxReferences.Any(r =>
                r.GetSyntax(cancellationToken) is MethodDeclarationSyntax { Body: not null } or
                    MethodDeclarationSyntax { ExpressionBody: not null })));
}
