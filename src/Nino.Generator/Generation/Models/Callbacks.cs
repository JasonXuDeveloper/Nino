using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.Analysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>When Nino calls a serialization callback (<c>[NinoOnSerializing]</c> ...), in the order of the attributes.</summary>
internal enum CallbackKind
{
    Serializing,
    Serialized,
    Deserializing,
    Deserialized,
}

/// <summary>
/// The rules of the serialization callbacks: which marked methods a formatter calls, and why the others are never
/// called. The generator calls what <see cref="Build"/> returns; the analyzer reports the rest (NINO038-NINO044):
/// per method (<see cref="Problem"/>, <see cref="Duplicates"/>) and, for what depends on the type calling it, through
/// the <see cref="DiagnosticSink"/> of <see cref="Build"/>.
/// </summary>
internal static class Callbacks
{
    /// <summary>The attributes as written in source, indexed by <see cref="CallbackKind"/>.</summary>
    public static readonly string[] AttributeNames =
        { "NinoOnSerializing", "NinoOnSerialized", "NinoOnDeserializing", "NinoOnDeserialized" };

    /// <summary>The callbacks <paramref name="method"/> is marked as.</summary>
    public static IEnumerable<(CallbackKind Kind, AttributeData Attribute)> Marks(IMethodSymbol method,
        NinoSymbols symbols)
    {
        var attributes = method.GetAttributes();
        if (attributes.IsEmpty)
            yield break;
        for (int k = 0; k < symbols.Callbacks.Length; k++)
        {
            if (NinoSymbols.Get(method, symbols.Callbacks[k]) is { } attribute)
                yield return ((CallbackKind)k, attribute);
        }
    }

    /// <summary>Whether a method of <paramref name="type"/> is marked as a callback (a cheap test for the type's rules).</summary>
    private static bool Declares(INamedTypeSymbol type, NinoSymbols symbols)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method || method.GetAttributes().IsEmpty)
                continue;
            foreach (var callback in symbols.Callbacks)
                if (NinoSymbols.Get(method, callback) != null)
                    return true;
        }

        return false;
    }

    /// <summary>Whether the type or one of its serializable base classes declares a callback.</summary>
    public static bool HasInHierarchy(INamedTypeSymbol type, NinoSymbols symbols)
    {
        for (var level = type; level != null; level = level.BaseType is { } parent && parent.IsNinoType() ? parent : null)
            if (Declares(level, symbols))
                return true;

        return false;
    }

    /// <summary>
    /// Why <paramref name="method"/>, marked as a <paramref name="kind"/> callback, is never called whatever type calls
    /// it, as (rule, reason): its signature (NINO038), static where the callback runs on the value (NINO042), its
    /// type is not a <c>[NinoType]</c> class or struct (NINO040), the generated code of its own assembly cannot call
    /// it (NINO039) or its struct is copied as raw memory (NINO044); null when it can be called.
    /// </summary>
    public static (DiagnosticDescriptor Rule, string Reason)? Problem(IMethodSymbol method, CallbackKind kind,
        TypeFacts facts)
    {
        if (method.MethodKind != MethodKind.Ordinary)
            return (NinoDiagnostics.CallbackSignature, "is not an ordinary method");
        if (method.IsGenericMethod)
            return (NinoDiagnostics.CallbackSignature, "is generic");
        if (method.Parameters.Length != 0)
            return (NinoDiagnostics.CallbackSignature, "takes parameters");
        if (!method.ReturnsVoid)
            return (NinoDiagnostics.CallbackSignature, $"returns '{method.ReturnType.ToDisplayString()}'");
        if (method.IsStatic && kind != CallbackKind.Deserializing)
            return (NinoDiagnostics.CallbackStatic, "");
        var type = method.ContainingType;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || !type.IsNinoType())
            return (NinoDiagnostics.CallbackNotNinoType, type.TypeKind == TypeKind.Interface
                ? $"is declared by interface '{type.ToDisplayString()}'"
                : $"'{type.ToDisplayString()}' is not a [NinoType]");
        if (!new Visibility(type.ContainingAssembly).IsVisible(method))
            return (NinoDiagnostics.CallbackInaccessible, Accessibility(method));
        if (facts.Raw.IsRawStruct(type))
            return (NinoDiagnostics.CallbackOnRawStruct, "");
        return null;
    }

    /// <summary>The accessibility of <paramref name="method"/> as C# spells it.</summary>
    public static string Accessibility(IMethodSymbol method) => method.DeclaredAccessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Private => "private",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        _ => method.DeclaredAccessibility.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// The methods <paramref name="type"/> marks as a callback that already has a method in it (NINO041), each with
    /// the first one, which Nino calls.
    /// </summary>
    public static IEnumerable<(IMethodSymbol Method, IMethodSymbol First, CallbackKind Kind)> Duplicates(
        INamedTypeSymbol type, NinoSymbols symbols)
    {
        var first = new IMethodSymbol?[AttributeNames.Length];
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            foreach (var (kind, _) in Marks(method, symbols))
            {
                if (first[(int)kind] is { } earlier)
                    yield return (method, earlier, kind);
                else
                    first[(int)kind] = method;
            }
        }
    }

    /// <summary>
    /// The callbacks the formatter of <paramref name="type"/> calls: those of the type and of its <c>[NinoType]</c>
    /// base classes (the levels whose members it writes), base classes first; per level and callback the first method
    /// without a <see cref="Problem"/>; an override of a method called already is not called again (the virtual call
    /// reaches it). An instance <c>[NinoOnDeserializing]</c> is called only where the instance exists before its
    /// members are read (not <paramref name="recreated"/>, else NINO043), and a method of another assembly only when
    /// generated code here can call it (else NINO039); both reported to <paramref name="diagnostics"/>. Null when
    /// it calls none: abstract classes and interfaces (their subtypes call theirs) and structs copied as raw memory.
    /// </summary>
    public static CallbackModel? Build(INamedTypeSymbol type, FormatterKind kind, bool rawStruct,
        ConstructionModel construction, TypeFacts facts, DiagnosticSink? diagnostics)
    {
        if (kind == FormatterKind.Polymorphic || rawStruct || facts.Shape(type) is not { HasCallbacks: true })
            return null;
        var levels = new List<INamedTypeSymbol>();
        for (var t = type; t != null; t = t.BaseType is { } b && b.IsNinoType() ? b : null)
            levels.Insert(0, t);

        var calls = new List<CallbackCall>[AttributeNames.Length];
        for (int k = 0; k < calls.Length; k++)
            calls[k] = new List<CallbackCall>();
        var called = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var level in levels)
        {
            var taken = new bool[AttributeNames.Length];
            foreach (var method in level.GetMembers().OfType<IMethodSymbol>())
            {
                foreach (var (callback, _) in Marks(method, facts.Symbols))
                {
                    if (taken[(int)callback] || Problem(method, callback, facts) != null)
                        continue;
                    taken[(int)callback] = true;
                    if (Overrides(method, called))
                        continue;
                    if (!facts.Visibility.IsVisible(method))
                    {
                        diagnostics?.Report(NinoDiagnostics.CallbackInaccessible, type, Display(method),
                            AttributeNames[(int)callback], $"internal to {method.ContainingAssembly.Name}",
                            type.ToDisplayString(), "make it public");
                        continue;
                    }

                    if (callback == CallbackKind.Deserializing && !method.IsStatic && construction.Recreate)
                    {
                        diagnostics?.Report(NinoDiagnostics.CallbackBeforeInstance,
                            SymbolEqualityComparer.Default.Equals(level, type) ? method : type, Display(method),
                            type.ToDisplayString(), construction.Arguments.Length != 0
                                ? "a constructor that takes them"
                                : "it has init-only or readonly members");
                        continue;
                    }

                    called.Add(method);
                    calls[(int)callback].Add(new CallbackCall(method.Name, TypeNames.Global(level), method.IsStatic,
                        !method.IsStatic && IsHidden(type, level, method.Name)));
                }
            }
        }

        if (calls.All(static c => c.Count == 0))
            return null;
        // Static [NinoOnDeserializing]s run before the instance exists, so before every instance one.
        var deserializing = calls[(int)CallbackKind.Deserializing].Where(static c => c.Static)
            .Concat(calls[(int)CallbackKind.Deserializing].Where(static c => !c.Static));
        return new CallbackModel(new EquatableArray<CallbackCall>(calls[(int)CallbackKind.Serializing]),
            new EquatableArray<CallbackCall>(calls[(int)CallbackKind.Serialized]),
            new EquatableArray<CallbackCall>(deserializing),
            new EquatableArray<CallbackCall>(calls[(int)CallbackKind.Deserialized]));
    }

    /// <summary>The method as diagnostics name it: <c>Type.Method</c>.</summary>
    public static string Display(IMethodSymbol method) => $"{method.ContainingType.ToDisplayString()}.{method.Name}";

    /// <summary>Whether <paramref name="method"/> overrides one of <paramref name="called"/>.</summary>
    private static bool Overrides(IMethodSymbol method, HashSet<IMethodSymbol> called)
    {
        for (var m = method.OverriddenMethod; m != null; m = m.OverriddenMethod)
        {
            if (called.Contains(m.OriginalDefinition) || called.Contains(m))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a class between <paramref name="type"/> and <paramref name="level"/> (it included) declares a member
    /// named <paramref name="name"/> that hides <paramref name="level"/>'s: the call then casts to the level.
    /// </summary>
    private static bool IsHidden(INamedTypeSymbol type, INamedTypeSymbol level, string name)
    {
        for (var t = type; t != null && !SymbolEqualityComparer.Default.Equals(t, level); t = t.BaseType)
        {
            if (t.GetMembers(name).Any(static m => m is not IMethodSymbol { IsOverride: true }))
                return true;
        }

        return false;
    }
}
