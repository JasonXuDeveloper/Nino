using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Nino.Generator.Generation;

namespace Nino.Generator;

/// <summary>
/// Private members that Nino serializes are read and written by generated code (through <c>[UnsafeAccessor]</c> on
/// .NET 8+), which the compiler and the IDE cannot see: their "never used", "never assigned" and "make readonly"
/// warnings are false positives.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NinoSuppressor : DiagnosticSuppressor
{
    private const string Justification = "The member is serialized by Nino; generated code reads and writes it";

    private static SuppressionDescriptor Suppress(string id) => new("NINOSPR_" + id, id, Justification);

    private static readonly ImmutableArray<SuppressionDescriptor> Supported = ImmutableArray.Create(
        Suppress("CS0169"), // field is never used
        Suppress("CS0414"), // field is assigned but its value is never used
        Suppress("CS0649"), // field is never assigned
        Suppress("IDE0044"), // make field readonly
        Suppress("IDE0051"), // remove unused private member
        Suppress("IDE0052")); // remove unread private member

    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => Supported;

    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var tree = diagnostic.Location.SourceTree;
            if (tree == null)
                continue;
            var node = tree.GetRoot(context.CancellationToken).FindNode(diagnostic.Location.SourceSpan);
            var member = context.GetSemanticModel(tree).GetDeclaredSymbol(node, context.CancellationToken);
            if (member is not (IFieldSymbol or IPropertySymbol) || member.ContainingType?.GetNinoTypeOptions() is not { } attr ||
                !TypeFacts.For(context.Compilation, tree.Options).Members.Of(member.ContainingType, attr)
                    .Contains(member, SymbolEqualityComparer.Default))
                continue;
            var descriptor = Supported.First(s => s.SuppressedDiagnosticId == diagnostic.Id);
            context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
        }
    }
}
