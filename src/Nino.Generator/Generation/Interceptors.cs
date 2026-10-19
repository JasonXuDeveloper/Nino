#if ROSLYN4_8
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Nino.Generator.Generation.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

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

    /// <summary>
    /// The interceptor of a top-level call of <paramref name="method"/> (of <c>NinoSerializer</c> or
    /// <c>NinoDeserializer</c>), or null; <paramref name="ctx"/> must enable interceptors (<see cref="Enabled"/>).
    /// </summary>
    public static InterceptedCall? Transform(GeneratorSyntaxContext ctx, IMethodSymbol method, TypeFacts facts,
        CancellationToken ct)
    {
        if (method.TypeArguments.Length != 1)
            return null;
        var invocation = (InvocationExpressionSyntax)ctx.Node;
        var owner = method.ContainingType;
        var type = method.TypeArguments[0];
        if (!facts.Visibility.CanName(type))
            return null;
        // Raw values and arrays of raw elements are copied directly by the regular calls already, except a raw value
        // read into a new one, which NinoDirect.DeserializeRaw returns without a stack temporary.
        bool raw = facts.Raw.IsRawUnmanaged(type);
        if (raw && SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Deserializer) &&
            method.Name == "Deserialize" && method.Parameters.Length == 1 &&
            method.Parameters[0].Type is INamedTypeSymbol { Name: "ReadOnlySpan" })
            return Intercept(ctx, invocation, method, type, "DeserializeRaw", "", ct);
        if (raw || type is IArrayTypeSymbol { ElementType: var element } && facts.Raw.IsRawUnmanaged(element))
            return null;
        // Only the formatters known here: a cached one is what the regular call uses anyway.
        if (facts.Resolver.Resolve(type, false) is not { } formatter ||
            (formatter.Traits & FormatterTraits.Cached) != 0)
            return null;
        // A value of fixed size serialized into a new array: allocated at that size and written straight into.
        if (SymbolEqualityComparer.Default.Equals(owner, facts.Symbols.Serializer) && method.Name == "Serialize" &&
            method.Parameters.Length == 1 && method.ReturnType is IArrayTypeSymbol &&
            type is INamedTypeSymbol named && facts.IsFixedSize(named) &&
            (formatter.Traits & FormatterTraits.Generated) != 0)
            return Intercept(ctx, invocation, method, type, "SerializeFixed", formatter.Code, ct);
        return Intercept(ctx, invocation, method, type, method.Name, formatter.Code, ct);
    }

    /// <summary>
    /// The interceptor of <paramref name="invocation"/>: a call to the <c>NinoDirect</c> method
    /// <paramref name="target"/> with <paramref name="formatter"/> (none when empty), passing the arguments on. Only
    /// a call to the method of the same name may use the <c>__Top</c> twins (<see cref="InterceptedCall.Inlinable"/>).
    /// </summary>
    private static InterceptedCall? Intercept(GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation,
        IMethodSymbol method, ITypeSymbol type, string target, string formatter, CancellationToken ct)
    {
#if ROSLYN4_12
        if (ctx.SemanticModel.GetInterceptableLocation(invocation, ct) is not { } location)
            return null;
        string attribute = $"{location.Version}, \"{location.Data}\"";
#else
        var access = (MemberAccessExpressionSyntax)invocation.Expression;
        var position = access.Name.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string path = invocation.SyntaxTree.FilePath;
        if (path.Length == 0)
            return null;
        string attribute = "@\"" + path.Replace("\"", "\"\"") + $"\", {position.Line + 1}, {position.Character + 1}";
#endif

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
            // an in argument needs no keyword, and the NinoDirect target may take it by value
            arguments.Append(parameter.RefKind == RefKind.In ? "" : refKind).Append('a').Append(i);
        }

        signature.Append(')');
        // Overloads given a reader or writer leave its fields in the caller's memory: nothing to gain there.
        bool inlinable = target == method.Name &&
                         !method.Parameters.Any(p => p.RefKind == RefKind.Ref && p.Type.Name is "Reader" or "Writer");
        return new InterceptedCall(signature.ToString(), target, TypeNames.Global(type), formatter,
            arguments.ToString(), attribute, inlinable);
    }

    /// <summary>
    /// <paramref name="formatter"/> with the <c>__Top</c> twins of the generated formatters in <paramref name="tops"/>,
    /// and the <c>__Shallow</c> twins of the others in <paramref name="shallow"/>: the formatter itself, or the element
    /// formatters it is composed of (type arguments of built-in collection, nullable and tuple formatters). Those are
    /// called with no class body open, as the formatter is, since only generated bodies open one and generated
    /// formatters take no formatter type arguments; and a <c>__Top</c> twin writes and reads the same bytes as its
    /// formatter wherever no class body is open (its body opens none), a <c>__Shallow</c> twin wherever its nesting
    /// bound stays below <c>NinoDepth.Inline</c> (<see cref="TypeFacts.DepthBound"/>), which it does with none open.
    /// </summary>
    internal static string WithTop(string formatter, HashSet<string> tops, HashSet<string> shallow) =>
        TypeNames.WithTwin(TypeNames.WithTwin(formatter, tops, ".__Top"), shallow, ".__Shallow");

    /// <summary>
    /// The call of an <see cref="InterceptedCall.Inlinable"/> call with the twins of <see cref="WithTop"/>. A write of a
    /// formatter that has a <c>__Top</c> twin goes through <c>NinoDirect.SerializeTop</c>, which hands the twin the
    /// buffer to create its writer in (<c>INinoTopFormatter</c>), and a read of a new value through
    /// <c>NinoDirect.DeserializeTop</c>, which hands it the data to create its reader on.
    /// </summary>
    private static string WithTopCall(InterceptedCall c, HashSet<string> tops, HashSet<string> shallow)
    {
        string method = !tops.Contains(c.Formatter) ? c.Method : c.Method switch
        {
            "Serialize" => "SerializeTop",
            // Deserialize(data) returning a new value; not Deserialize(data, ref value), which reads into an instance.
            "Deserialize" when c.Arguments == "a0" => "DeserializeTop",
            _ => c.Method,
        };
        return c.CallWith(method, WithTop(c.Formatter, tops, shallow));
    }

    /// <summary>
    /// <paramref name="fileLocal"/>: C# 11 or later, where the attribute and the interceptors are file-local, so
    /// they never clash with another generator's.
    /// </summary>
    public static string Emit(EquatableArray<InterceptedCall> calls, bool fileLocal, EquatableArray<string> top,
        EquatableArray<string> shallowTwins)
    {
        var tops = new HashSet<string>(top.Select(n => TypeNames.Gen + n));
        var shallow = new HashSet<string>(shallowTwins.Select(n => TypeNames.Gen + n));
        string scope = fileLocal ? "file" : "internal";
        var w = new CodeWriter(calls.Length * 384);
        w.Raw(FormatterEmitter.Header);
        w.Open("namespace System.Runtime.CompilerServices");
        w.Line("[global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]");
        w.Open($"{scope} sealed class InterceptsLocationAttribute : global::System.Attribute");
#if ROSLYN4_12
        w.Line("public InterceptsLocationAttribute(int version, string data) { }");
#else
        w.Line("public InterceptsLocationAttribute(string filePath, int line, int character) { }");
#endif
        w.Close();
        w.Close();
        w.Line();
        w.Open($"namespace {TypeNames.GenNamespace}");
        // Aliases: each interceptor names them, more than a thousand times in a large project.
        w.Line($"using __NinoIntercepts = {TypeNames.CompilerServices}InterceptsLocationAttribute;");
        w.Line($"using __NinoInline = {TypeNames.CompilerServices}MethodImplAttribute;");
        w.Line($"using __NinoInlineOptions = {TypeNames.CompilerServices}MethodImplOptions;");
        w.Line($"using __NinoDirect = {TypeNames.Core}NinoDirect;");
        w.Line();
        w.Open($"{scope} static class NinoInterceptors");
        int n = 0;
        foreach (var group in calls.GroupBy(c => (c.Signature, Call: c.Inlinable ? WithTopCall(c, tops, shallow) : c.Call)))
        {
            if (n != 0)
                w.Line();
            foreach (var call in group)
                w.Line($"[__NinoIntercepts({call.Location})]");
            w.Line("[__NinoInline(__NinoInlineOptions.AggressiveInlining)]");
            // "R Name(params)" becomes "public static R NameN(params) => call;"
            var signature = group.Key.Signature;
            int paren = signature.IndexOf('(');
            w.Line($"public static {signature.Substring(0, paren)}{n++}{signature.Substring(paren)} => __NinoDirect.{group.Key.Call};");
        }

        w.Close();
        w.Close();
        return w.ToString();
    }
}
#endif
