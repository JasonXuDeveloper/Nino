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
/// Calls with the same signature and call share one interceptor. <see cref="Inlinable"/>: the call creates its own
/// reader or writer, so no class body is open around <see cref="Formatter"/>: the call then uses the <c>__Top</c>
/// twin (<see cref="TypeModel.HasTop"/>) of the formatter, or of the element formatters of a built-in collection
/// (<c>List&lt;Small&gt;</c>, <c>Dictionary&lt;int, Small&gt;</c>), whose elements are read and written there too.
/// </summary>
internal sealed record InterceptedCall(string Signature, string Call, string Attribute, string Formatter = "",
    bool Inlinable = false);

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
        if (!IsNameable(type))
            return null;
        // Raw values and arrays of raw elements are copied directly by the regular calls already, except a raw value
        // read into a new one, which NinoDirect.DeserializeRaw returns without a stack temporary.
        bool raw = facts.Raw.IsRawUnmanaged(type);
        if (raw && SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Deserializer) &&
            method.Name == "Deserialize" && method.Parameters.Length == 1 &&
            method.Parameters[0].Type is INamedTypeSymbol { Name: "ReadOnlySpan" })
            return Intercept(ctx, invocation, method, type, ct, $"{TypeNames.Core}NinoDirect.DeserializeRaw<{TypeNames.Global(type)}>(a0)");
        if (raw || type is IArrayTypeSymbol { ElementType: var element } && facts.Raw.IsRawUnmanaged(element))
            return null;
        // Only the formatters known here: a cached one is what the regular call uses anyway.
        if (facts.Resolver.Resolve(type, false) is not { } formatter ||
            formatter.Code.StartsWith(TypeNames.Core + "CachedFormatter<", StringComparison.Ordinal))
            return null;
        // A value of fixed size serialized into a new array: allocated at that size and written straight into.
        if (SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Serializer) && method.Name == "Serialize" &&
            method.Parameters.Length == 1 && method.ReturnType is IArrayTypeSymbol &&
            type is INamedTypeSymbol named && facts.IsFixedSize(named) &&
            formatter.Code == FormatterResolver.GeneratedFormatter(named))
            return Intercept(ctx, invocation, method, type, ct,
                $"{TypeNames.Core}NinoDirect.SerializeFixed<{TypeNames.Global(type)}, {formatter.Code}>(a0)");
        return Intercept(ctx, invocation, method, type, ct, null, formatter.Code);
    }

    /// <summary>
    /// The interceptor of <paramref name="invocation"/>: a call to <paramref name="call"/>, or (null) to the
    /// <c>NinoDirect</c> method of the same name with <paramref name="formatter"/>.
    /// </summary>
    private static InterceptedCall? Intercept(GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation,
        IMethodSymbol method, ITypeSymbol type, CancellationToken ct, string? call, string formatter = "")
    {
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
        // Overloads given a reader or writer leave its fields in the caller's memory: nothing to gain there.
        bool inlinable = !method.Parameters.Any(p => p.RefKind == RefKind.Ref && p.Type.Name is "Reader" or "Writer");
        if (call != null)
            return new InterceptedCall(signature.ToString(), call, attribute);
        return new InterceptedCall(signature.ToString(),
            $"{TypeNames.Core}NinoDirect.{method.Name}<{name}, {formatter}>({arguments})", attribute,
            formatter, inlinable);
    }

    /// <summary>
    /// <paramref name="formatter"/> with the <c>__Top</c> twins of the generated formatters in <paramref name="tops"/>:
    /// the formatter itself, or the element formatters it is composed of (type arguments of built-in collection,
    /// nullable and tuple formatters). Those are called with no class body open, as the formatter is, since only
    /// generated bodies open one and generated formatters take no formatter type arguments; and a <c>__Top</c> twin
    /// writes and reads the same bytes as its formatter wherever no class body is open (its body opens none).
    /// </summary>
    internal static string WithTop(string formatter, HashSet<string> tops)
    {
        if (tops.Count == 0)
            return formatter;
        if (tops.Contains(formatter))
            return formatter + ".__Top";
        const string gen = TypeNames.Gen;
        var result = new StringBuilder(formatter.Length);
        int i = 0;
        while (true)
        {
            int start = formatter.IndexOf(gen, i, StringComparison.Ordinal);
            if (start < 0)
                break;
            int end = start + gen.Length;
            while (end < formatter.Length && (char.IsLetterOrDigit(formatter[end]) || formatter[end] == '_'))
                end++;
            result.Append(formatter, i, end - i);
            // A whole type argument: followed by ',' or '>' (not a generic formatter or a nested name).
            if (end < formatter.Length && formatter[end] is ',' or '>' &&
                tops.Contains(formatter.Substring(start, end - start)))
                result.Append(".__Top");
            i = end;
        }

        return result.Append(formatter, i, formatter.Length - i).ToString();
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
    public static string Emit(EquatableArray<InterceptedCall> calls, bool fileLocal, EquatableArray<string> top)
    {
        var tops = new HashSet<string>(top.Select(n => TypeNames.Gen + n));
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
        foreach (var group in calls.GroupBy(c => (c.Signature, Call: c.Inlinable
                     ? c.Call.Replace(", " + c.Formatter + ">(", ", " + WithTop(c.Formatter, tops) + ">(")
                     : c.Call)))
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
