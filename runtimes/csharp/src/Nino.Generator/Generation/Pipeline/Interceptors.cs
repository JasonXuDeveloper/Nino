#if ROSLYN4_8
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nino.Generator.Generation.Emit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// Interceptors for the generic <c>NinoSerializer</c>/<c>NinoDeserializer</c> calls whose type argument is known
/// at the call site: they call the resolved formatter directly (<c>Nino.Core.CompilerServices.NinoDirect</c>) instead of through
/// the formatter cache, a virtual call. Emitted only by the Roslyn 4.8 and 4.12 builds of the generator, and only
/// where the project enables interceptors in the <c>NinoGen</c> namespace (the package's build props do). Roslyn
/// 4.12+ locates a call by an opaque version and data; Roslyn 4.8-4.11 only know the preview form, the file path
/// and the 1-based line and column of the method name.
/// </summary>
internal static class Interceptors
{
    /// <summary>Whether the project lets generated code intercept calls (the namespace list holds NinoGen).</summary>
    public static bool Enabled(ParseOptions options) =>
        ContainsNamespace(options, "InterceptorsNamespaces") ||
        ContainsNamespace(options, "InterceptorsPreviewNamespaces");

    private static bool ContainsNamespace(ParseOptions options, string key)
    {
        if (!options.Features.TryGetValue(key, out var value))
            return false;

        string name = TypeNames.GenNamespace;
        for (int start = 0; start < value.Length;)
        {
            int separator = value.IndexOf(';', start);
            int end = separator < 0 ? value.Length : separator;
            while (start < end && char.IsWhiteSpace(value[start]))
                start++;
            while (end > start && char.IsWhiteSpace(value[end - 1]))
                end--;
            if (end - start == name.Length && string.CompareOrdinal(value, start, name, 0, name.Length) == 0)
                return true;
            if (separator < 0)
                break;
            start = separator + 1;
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
            return Intercept(ctx, invocation, method, type, "DeserializeRaw", null, ct);
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
    /// <paramref name="target"/> with <paramref name="formatter"/> (none when null), passing the arguments on. Only
    /// a call to the method of the same name may use the <c>__Top</c> twins (<see cref="InterceptedCall.Inlinable"/>).
    /// </summary>
    private static InterceptedCall? Intercept(GeneratorSyntaxContext ctx, InvocationExpressionSyntax invocation,
        IMethodSymbol method, ITypeSymbol type, string target, FormatterRef? formatter, CancellationToken ct)
    {
        if (Location(ctx, invocation, ct) is not { } location)
            return null;
        // Overloads given a reader or writer leave its fields in the caller's memory.
        bool inlinable = target == method.Name;
        string signature, arguments;
        var parameters = method.Parameters;
        var text = TextScratch.Rent(128);
        try
        {
            if (method.ReturnsVoid)
                text.Append("void");
            else
                TypeNames.AppendGlobal(text, method.ReturnType);
            text.Append(' ').Append(method.Name).Append('(');

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                string? refKind = RefPrefix(parameter.RefKind);
                if (refKind == null)
                    return null;
                if (i != 0)
                    text.Append(", ");
                text.Append(refKind);
                TypeNames.AppendGlobal(text, parameter.Type);
                text.Append(" a").Append(i);
                if (parameter.RefKind == RefKind.Ref && parameter.Type.Name is "Reader" or "Writer")
                    inlinable = false;
            }

            signature = text.Append(')').ToString();
            text.Clear();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i != 0)
                    text.Append(", ");
                // The NinoDirect target may take an in argument by value.
                if (parameters[i].RefKind != RefKind.In)
                    text.Append(RefPrefix(parameters[i].RefKind));
                text.Append('a').Append(i);
            }

            arguments = text.ToString();
        }
        finally
        {
            TextScratch.Return(text);
        }

        return new InterceptedCall(signature, target, TypeNames.Global(type), formatter,
            arguments, location.Attribute, inlinable, location.Where);
    }

    private static string? RefPrefix(RefKind kind) => kind switch
    {
        RefKind.Ref => "ref ",
        RefKind.Out => "out ",
        RefKind.In => "in ",
        RefKind.None => "",
        _ => null,
    };

    /// <summary>Updates a reused call with the current tree's interceptor coordinates and checksum.</summary>
    public static InterceptedCall? AtLocation(GeneratorSyntaxContext ctx, InterceptedCall call, CancellationToken ct) =>
        Location(ctx, (InvocationExpressionSyntax)ctx.Node, ct) is { } location
            ? call with { Location = location.Attribute, Where = location.Where }
            : null;

    private static (string Attribute, string Where)? Location(GeneratorSyntaxContext ctx,
        InvocationExpressionSyntax invocation, CancellationToken ct)
    {
#if ROSLYN4_12
        if (ctx.SemanticModel.GetInterceptableLocation(invocation, ct) is not { } location)
            return null;
        string attribute = $"{location.Version}, \"{location.Data}\"";
        // The opaque data does not say where the call is: a comment does, "File.cs(line,column)" (no directory, so
        // the code is the same on every machine).
        string where = System.IO.Path.GetFileName(location.GetDisplayLocation());
#else
        // The method name: after the receiver, or alone (using static).
        var name = invocation.Expression is MemberAccessExpressionSyntax access
            ? access.Name
            : (SimpleNameSyntax)invocation.Expression;
        var position = name.Identifier.GetLocation().GetLineSpan().StartLinePosition;
        string path = invocation.SyntaxTree.FilePath;
        if (path.Length == 0)
            return null;
        string attribute = "@\"" + path.Replace("\"", "\"\"") + $"\", {position.Line + 1}, {position.Character + 1}";
        string where = ""; // the attribute says it
#endif

        return (attribute, where);
    }

    /// <summary>
    /// The call of an <see cref="InterceptedCall.Inlinable"/> call with the twins of its formatter
    /// (<see cref="FormatterRef.WithTwins"/>): the formatter itself, or the element formatters it is composed of (type
    /// arguments of built-in collection, nullable and tuple formatters). Those are called with no class body open, as
    /// the formatter is, since only generated bodies open one and generated formatters take no formatter type
    /// arguments; and a <c>__Top</c> twin writes and reads the same bytes as its formatter wherever no class body is
    /// open (its body opens none), a <c>__Shallow</c> twin wherever its nesting bound stays below
    /// <c>NinoDepth.Inline</c> (<see cref="TypeFacts.DepthBound"/>), which it does with none open. A write of a
    /// formatter that has a <c>__Top</c> twin goes through <c>NinoDirect.SerializeTop</c>, which hands the twin the
    /// buffer to create its writer in (<c>INinoTopFormatter</c>), and a read of a new value through
    /// <c>NinoDirect.DeserializeTop</c>, which hands it the data to create its reader on.
    /// </summary>
    private static string WithTopCall(InterceptedCall c, HashSet<string> tops, HashSet<string> shallow,
        HashSet<string> prefetch)
    {
        string method = c.Formatter is not { Generated: true, Args.Length: 0 } f || !tops.Contains(f.Name)
            ? c.Method
            : c.Method switch
        {
            "Serialize" => "SerializeTop",
            // Deserialize(data) returning a new value; not Deserialize(data, ref value), which reads into an instance.
            "Deserialize" when c.Arguments == "a0" => "DeserializeTop",
            _ => c.Method,
        };
        var formatter = c.Formatter?.WithTwins(tops, shallow);
        string? prefetched = prefetch.Count == 0 ? null : method switch
        {
            // An array or a list of a class whose formatter prefetches: written, or read into its existing objects
            // (a new collection's objects are allocated in order).
            "Serialize" => "SerializePrefetched",
            "Deserialize" when c.Arguments == "a0, ref a1" => "DeserializePrefetched",
            _ => null,
        };
        if (prefetched == null || !Prefetches(c.Formatter, prefetch))
            return c.CallWith(method, formatter);
        // The prefetched methods take the element type and formatter, so the collection formatters stay as they are.
        var args = formatter!.Args;
        return $"{prefetched}<{args[args.Length - 2]}, {args[args.Length - 1]}>({c.Arguments})";
    }

    /// <summary>
    /// Whether <paramref name="formatter"/> is Nino.Core's formatter of arrays or of <c>List&lt;T&gt;</c> of a class
    /// whose generated formatter is in <paramref name="prefetch"/> (TypeModel.PrefetchBytes).
    /// </summary>
    private static bool Prefetches(FormatterRef? formatter, HashSet<string> prefetch) =>
        formatter is { Generated: false, Args.Length: > 1 } &&
        formatter.Args[formatter.Args.Length - 1] is { Generated: true, Args.Length: 0 } element &&
        prefetch.Contains(element.Name) &&
        (formatter.Name == TypeNames.Core + "ArrayFormatter" ||
         formatter.Name == TypeNames.Core + "ListFormatter" && formatter.Args.Length == 3 &&
         formatter.Args[0].ToString() == "global::System.Collections.Generic.List<" + formatter.Args[1] + ">");

    private static HashSet<string> GeneratedNames(EquatableArray<string> names)
    {
        var generated = new HashSet<string>();
        foreach (var name in names)
            generated.Add(TypeNames.Gen + name);
        return generated;
    }

    private const string AttributePrefix = "namespace System.Runtime.CompilerServices\n{\n" +
        "    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]\n    ";

    private const string AttributeSuffix = " sealed class InterceptsLocationAttribute : global::System.Attribute\n    {\n" +
#if ROSLYN4_12
        "        public InterceptsLocationAttribute(int version, string data) { }\n" +
#else
        "        public InterceptsLocationAttribute(string filePath, int line, int character) { }\n" +
#endif
        "    }\n}\n\n";

    /// <summary>
    /// <paramref name="fileLocal"/>: C# 11 or later, where the attribute and the interceptors are file-local, so
    /// they never clash with another generator's.
    /// </summary>
    public static string Emit(EquatableArray<InterceptedCall> calls, bool fileLocal, EquatableArray<string> top,
        EquatableArray<string> shallowTwins, EquatableArray<string> prefetchers)
    {
        var tops = GeneratedNames(top);
        var shallow = GeneratedNames(shallowTwins);
        var prefetch = GeneratedNames(prefetchers);
        string scope = fileLocal ? "file" : "internal";
        // Bound the initial reserve since many locations can share the same rendered call.
        var callText = new Dictionary<(string Method, string Type, FormatterRef? Formatter, string Arguments, bool Inlinable), string>(
            System.Math.Min(calls.Length, 256));
        string RenderCall(InterceptedCall call)
        {
            var key = (call.Method, call.TypeName, call.Formatter, call.Arguments, call.Inlinable);
            if (!callText.TryGetValue(key, out string? text))
            {
                text = call.Inlinable ? WithTopCall(call, tops, shallow, prefetch) : call.Call;
                callText.Add(key, text);
            }

            return text;
        }

        var groups = calls.ToLookup(c => (c.Signature, Call: RenderCall(c)));
        var w = new CodeWriter();
        w.Open($"{scope} static class NinoInterceptors");
        var aliases = new List<string> { GeneratedCode.InterceptsAlias };
        aliases.AddRange(GeneratedCode.InlineAliases);
        aliases.Add(GeneratedCode.DirectAlias);
        int n = 0;
        foreach (var group in groups)
        {
            if (n != 0)
                w.Line();
            foreach (var call in group)
                AttributeLine(w, call);
            w.Line(GeneratedCode.Inline);
            Forwarder(w, group.Key.Signature, n++, group.Key.Call);
        }

        w.Close();
        var body = GeneratedCode.WithAliases(w, null, aliases);
        // A block namespace: the file declares the attribute in another namespace.
        return GeneratedCode.File(AttributePrefix + scope + AttributeSuffix, aliases, body, fileScoped: false);
    }

    /// <summary>The location attribute of <paramref name="call"/>, with the comment saying where it is.</summary>
    private static void AttributeLine(CodeWriter w, InterceptedCall call)
    {
        var line = w.BeginLine('[', ']')
            .Append('[').Append(GeneratedCode.InterceptsName).Append('(').Append(call.Location).Append(")]");
        if (call.Where.Length != 0)
            line.Append(" // ").Append(call.Where);
        w.EndLine();
    }

    /// <summary>"R Name(params)" as "public static R Name<paramref name="number"/>(params) => __NinoDirect.call;".</summary>
    private static void Forwarder(CodeWriter w, string signature, int number, string call)
    {
        int parameters = signature.IndexOf('(');
        w.BeginLine('p', ';').Append("public static ").Append(signature, 0, parameters).Append(number)
            .Append(signature, parameters, signature.Length - parameters).Append(" => __NinoDirect.").Append(call)
            .Append(';');
        w.EndLine();
    }
}
#endif
