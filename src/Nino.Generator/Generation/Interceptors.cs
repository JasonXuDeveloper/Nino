#if ROSLYN4_8
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// One intercepted top-level call. <see cref="Signature"/> is the interceptor's parameter list and return type,
/// <see cref="Call"/> the <c>NinoDirect</c> call it makes, <see cref="Attribute"/> its <c>[InterceptsLocation]</c>.
/// Calls with the same signature and call share one interceptor.
/// </summary>
internal sealed record InterceptedCall(string Signature, string Call, string Attribute);

/// <summary>
/// Interceptors for the generic <c>NinoSerializer</c>/<c>NinoDeserializer</c> calls whose type argument is known
/// at the call site: they call the resolved formatter directly (<c>Nino.Core.NinoDirect</c>) instead of through
/// the formatter cache, a virtual call. Emitted only by the Roslyn 4.8 and 4.12 builds of the generator, and only
/// where the project enables interceptors in the <c>NinoGen</c> namespace (the package's build props do). Roslyn
/// 4.12+ locates a call by an opaque version and data; Roslyn 4.8-4.11 only know the preview form, the file path
/// and the 1-based line and column of the method name.
/// </summary>
internal static class Interceptors
{
    /// <summary>Whether the project lets generated code intercept calls (the namespace list holds NinoGen).</summary>
    public static bool Enabled(ParseOptions options)
    {
        foreach (var key in new[] { "InterceptorsNamespaces", "InterceptorsPreviewNamespaces" })
        {
            if (options.Features.TryGetValue(key, out var value) &&
                value.Split(';').Any(n => n.Trim() == TypeNames.GenNamespace))
                return true;
        }

        return false;
    }

    public static InterceptedCall? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (!Enabled(ctx.SemanticModel.SyntaxTree.Options))
            return null;
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsGenericMethod: true, TypeArguments.Length: 1
            } method)
            return null;
        var facts = TypeFacts.For(ctx.SemanticModel.Compilation, ctx.SemanticModel.SyntaxTree.Options);
        var owner = method.ContainingType;
        if (!SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Serializer) &&
            !SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Deserializer))
            return null;

        var type = method.TypeArguments[0];
        // Raw values and arrays of raw elements are copied directly by the regular calls already.
        if (!IsNameable(type) || facts.Raw.IsRawUnmanaged(type) ||
            type is IArrayTypeSymbol { ElementType: var element } && facts.Raw.IsRawUnmanaged(element))
            return null;
        // Only the formatters known here: a cached one is what the regular call uses anyway.
        if (facts.Resolver.Resolve(type, false) is not { } formatter ||
            formatter.Code.StartsWith(TypeNames.Core + "CachedFormatter<", StringComparison.Ordinal))
            return null;
#if ROSLYN4_12
        if (ctx.SemanticModel.GetInterceptableLocation(invocation, ct) is not { } location)
            return null;
        string attribute = $"[global::System.Runtime.CompilerServices.InterceptsLocation({location.Version}, \"{location.Data}\")]";
#else
        var access = (MemberAccessExpressionSyntax)invocation.Expression;
        var position = access.Name.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string path = invocation.SyntaxTree.FilePath;
        if (path.Length == 0)
            return null;
        string attribute = "[global::System.Runtime.CompilerServices.InterceptsLocation(@\"" + path.Replace("\"", "\"\"") +
                           $"\", {position.Line + 1}, {position.Character + 1})]";
#endif

        var name = TypeNames.Global(type);
        var signature = new StringBuilder();
        var arguments = new StringBuilder();
        signature.Append(method.ReturnsVoid ? "void" : TypeNames.Global(method.ReturnType))
            .Append(' ').Append(method.Name).Append('(');
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var parameter = method.Parameters[i];
            string refKind = parameter.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In => "in ",
                RefKind.None => "",
                _ => null!,
            };
            if (refKind == null)
                return null;
            if (i != 0)
            {
                signature.Append(", ");
                arguments.Append(", ");
            }

            signature.Append(refKind).Append(TypeNames.Global(parameter.Type)).Append(" a").Append(i);
            arguments.Append(refKind).Append('a').Append(i);
        }

        signature.Append(')');
        return new InterceptedCall(signature.ToString(),
            $"{TypeNames.Core}NinoDirect.{method.Name}<{name}, {formatter.Code}>({arguments})", attribute);
    }

    /// <summary>Whether generated code in this assembly can name the type (no private or protected parts).</summary>
    private static bool IsNameable(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol a => IsNameable(a.ElementType),
        INamedTypeSymbol n => !n.IsAnonymousType && Visibility.IsAccessibleChain(n) &&
                              n.TypeArguments.All(IsNameable),
        _ => false,
    };

    /// <summary>
    /// <paramref name="fileLocal"/>: C# 11 or later, where the attribute and the interceptors are file-local, so
    /// they never clash with another generator's.
    /// </summary>
    public static string Emit(EquatableArray<InterceptedCall> calls, bool fileLocal)
    {
        string scope = fileLocal ? "file" : "internal";
        var w = new StringBuilder();
        w.AppendLine("// <auto-generated/>");
        w.AppendLine("#nullable disable");
        w.AppendLine("#pragma warning disable");
        w.AppendLine();
        w.AppendLine("namespace System.Runtime.CompilerServices");
        w.AppendLine("{");
        w.AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        w.AppendLine($"    {scope} sealed class InterceptsLocationAttribute : global::System.Attribute");
        w.AppendLine("    {");
#if ROSLYN4_12
        w.AppendLine("        public InterceptsLocationAttribute(int version, string data) { }");
#else
        w.AppendLine("        public InterceptsLocationAttribute(string filePath, int line, int character) { }");
#endif
        w.AppendLine("    }");
        w.AppendLine("}");
        w.AppendLine();
        w.AppendLine($"namespace {TypeNames.GenNamespace}");
        w.AppendLine("{");
        w.AppendLine($"    {scope} static class NinoInterceptors");
        w.AppendLine("    {");
        int n = 0;
        foreach (var group in calls.GroupBy(c => (c.Signature, c.Call)))
        {
            if (n != 0)
                w.AppendLine();
            foreach (var call in group)
                w.Append("        ").AppendLine(call.Attribute);
            w.AppendLine("        [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]");
            // "R Name(params)" becomes "public static R NameN(params) => call;"
            var signature = group.Key.Signature;
            int paren = signature.IndexOf('(');
            w.Append("        public static ").Append(signature, 0, paren).Append(n++)
                .Append(signature, paren, signature.Length - paren)
                .Append(" => ").Append(group.Key.Call).AppendLine(";");
        }

        w.AppendLine("    }");
        w.AppendLine("}");
        return w.ToString();
    }
}
#endif
