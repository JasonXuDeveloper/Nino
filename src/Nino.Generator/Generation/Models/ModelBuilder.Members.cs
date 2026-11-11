using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;
using Nino.Generator.Analysis;

namespace Nino.Generator.Generation;

internal static partial class ModelBuilder
{
    private static MemberModel? BuildMember(INamedTypeSymbol type, bool keyed, MemberList symbols, int index,
        TypeFacts facts, DiagnosticSink? diagnostics, DiagnosticSink? own, MemberSet set, CancellationToken ct)
    {
        var symbol = symbols[index];
        var memberType = MemberCollector.MemberType(symbol)?.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (memberType == null)
            return null;
        var known = facts.Symbols;

        FormatterRef? formatter;
        string encoding = "";
        FormatterRef? shallowFormatter = null;
        bool registered = false;
        bool readsLoop = false;
        bool opensBodies = true;
        FormatterTraits traits;
        var custom = known.CustomFormatterOf(symbol);
        var customType = custom is { ConstructorArguments: { Length: > 0 } args } ? args[0].Value as INamedTypeSymbol : null;
        if (customType != null && !known.IsCustomFormatterOf(customType, memberType))
            return null; // NINO008, reported by the analyzer
        // Vector<T>, alone or inside: as wide as the CPU's vectors, so no bytes of it read on every host. Skipped (it
        // has no formatter either); a [NinoMemberFormatter] writes what it chooses, unless it is one of Nino.Core's
        // own formatters (UnmanagedFormatter<Vector<int>>), which refuse it.
        ITypeSymbol? holder = memberType;
        var vector = custom == null ? facts.Raw.HostSizedVectorIn(memberType)
            : customType == null ? null : facts.Raw.HostSizedVectorWrittenBy(customType, out holder);
        if (vector != null && holder != null)
        {
            own?.Report(NinoDiagnostics.HostSizedVector, symbol,
                $"Member '{symbol.Name}' of NinoType '{type.ToDisplayString()}'", holder.ToDisplayString(),
                NinoDiagnostics.HoldsVector(holder, vector));
            return null;
        }

        if (customType != null)
        {
            formatter = FormatterRef.Of(customType);
            encoding = WireEncoding.Formatter(formatter.ToString());
            traits = FormatterTraitsOf.Of(customType);
        }
        else
        {
            bool utf8 = known.WritesUtf8(symbol);
            var resolved = facts.Resolver.Resolve(memberType, utf8);
            formatter = resolved?.Code;
            registered = resolved?.Registered == true;
            traits = resolved?.Traits ?? FormatterTraits.None;
            if (resolved is { } r)
            {
                encoding = r.Encoding;
                opensBodies = r.OpensBodies;
                if (r.Factories.Length != 0)
                    set.AddFactories(r.Factories);
                shallowFormatter = facts.ShallowFormatter(r.Code, r, utf8);
                readsLoop = r.Elements is { IsDefault: false, Length: > 0 } && (r.Traits & FormatterTraits.Flat) == 0;
            }
        }

        if (formatter == null)
        {
            own?.Report(NinoDiagnostics.UnserializableMember, symbol, symbol.Name, memberType.ToDisplayString(),
                type.ToDisplayString(), "", "the member is skipped");
            return null;
        }

        // The member is written, but a generic [NinoType] in its type cannot write what it holds (Box<Action>): its
        // formatter takes CachedFormatter<Action>, which throws for every value. Only the analyzer asks.
        if (own != null && custom == null && ArgumentWithoutFormatter(memberType, facts) is var (argument, generic))
            own.Report(NinoDiagnostics.UnserializableMember, symbol, symbol.Name, argument.ToDisplayString(),
                type.ToDisplayString(), $", a type argument of '{generic.ToDisplayString()}',",
                "writing a value of it throws");

        var declaring = symbol.ContainingType;
        // A base member hidden by a derived member of the same name (`new`): generated code names it through a cast
        // to its declaring type, and under its own name when the derived member is serialized too.
        int renamed = DerivedSameNamed(symbols, index);
        // An interface member the type implements explicitly (or by a default implementation): MemberCollector
        // collects the interface's own member, which only the interface names.
        bool viaInterface = declaring.TypeKind == TypeKind.Interface && type.TypeKind != TypeKind.Interface;
        MemberAccess access;
        if (facts.Visibility.IsDirectlyAccessible(symbol))
        {
            access = viaInterface
                ? type.IsValueType ? MemberAccess.Interface : MemberAccess.Cast
                : renamed != 0 || IsHidden(type, symbol, facts.Visibility)
                    ? MemberAccess.Cast
                    : MemberAccess.Direct;
        }
        else if (UnsafeAccessorReaches(type, declaring, facts))
        {
            access = MemberAccess.UnsafeAccessor;
        }
        else if (renamed != 0 || IsHidden(type, symbol, null))
        {
            // The accessor class inside the type would name the hiding member, and cannot reach a protected base
            // member through a cast to the base.
            diagnostics?.Report(NinoDiagnostics.HiddenMember, symbol, symbol.Name, declaring.ToDisplayString(),
                type.ToDisplayString());
            return null;
        }
        else
        {
            access = MemberAccess.Partial;
            set.NeedsPartialAccessor = true;
        }

        // Members join unmanaged blocks when their layout is the same on every runtime; others (rare struct
        // shapes such as { int; long }) go through their formatter, which converts them where needed.
        var raw = custom == null && facts.Raw.IsRawUnmanaged(memberType) && RawLayout.IsPortable(memberType)
            ? RawLayout.SizeOf(memberType) with { Layout = RawLayout.Describe(memberType) }
            : null;

        // Only registered formatters: a member's custom formatter must not become the global one.
        int typeId = registered && raw == null ? facts.TypeId(memberType) : 0;
        string name = renamed == 0 ? symbol.Name : $"{symbol.Name}__{renamed}";
        // Keyed: a renamed member's key is its declaring type's name with its own.
        int key = keyed ? ComputeId(KeyName(symbol.Name, renamed != 0, declaring)) : 0;
        var formerKeys = keyed
            ? new EquatableArray<int>(FormerKeys(symbol, renamed != 0, known).Select(static f => f.Key)
                .Where(former => former != key).Distinct())
            : EquatableArray<int>.Empty;

        // Fresh: a Nino.Core formatter that reads into an existing value (not an object or cached one), or a string,
        // read without the value it replaces.
        const FormatterTraits replaces = FormatterTraits.Cached | FormatterTraits.Object;
        // What the member starts as and what storing it does are the implementation's (an interface member's own
        // declaration says nothing about them).
        var implementation = viaInterface ? type.FindImplementationForInterfaceMember(symbol) : symbol;
        return new MemberModel(
            Name: name,
            SourceName: symbol.Name,
            Key: key,
            FormerKeys: formerKeys,
            TypeName: Global(memberType),
            Formatter: formatter,
            Traits: traits,
            OpensBodies: opensBodies,
            Encoding: raw?.Layout ?? encoding,
            Raw: raw,
            IsProperty: symbol is IPropertySymbol,
            Access: access,
            DeclaringType: access is MemberAccess.Direct or MemberAccess.Partial ? "" : Global(declaring),
            DeclaringTypeIsValueType: declaring.IsValueType,
            ConstructionOnly: Visibility.IsConstructionOnly(symbol),
            // Decided with the construction (ModelBuilder.Build), once every member is known.
            Unassignable: false,
            TypeId: typeId,
            ShallowFormatter: shallowFormatter ?? formatter,
            Fresh: custom == null && memberType.IsReferenceType && (traits & FormatterTraits.Core) != 0 &&
                   (traits & replaces) == 0 && implementation != null && StartsDefault(implementation, ct),
            ReadsLoop: raw == null && readsLoop,
            PlainStore: implementation is IFieldSymbol ||
                        implementation is IPropertySymbol property && IsAutoProperty(property));
    }

    /// <summary>
    /// Whether <paramref name="property"/>'s setter only stores the value: declared <c>set;</c> or <c>init;</c> in
    /// source (an auto-property, also one whose getter uses <c>field</c>), or marked compiler-generated in a
    /// referenced assembly. A setter with a body may do more (notify, validate) and is always called.
    /// </summary>
    private static bool IsAutoProperty(IPropertySymbol property)
    {
        var setter = property.SetMethod;
        if (setter == null)
            return false;
        var references = setter.DeclaringSyntaxReferences;
        if (references.Length == 0)
        {
            foreach (var attribute in setter.GetAttributes())
            {
                if (attribute.AttributeClass is { Name: "CompilerGeneratedAttribute" } attributeClass &&
                    attributeClass.ContainingNamespace?.ToDisplayString() == "System.Runtime.CompilerServices")
                    return true;
            }

            return false;
        }

        foreach (var reference in references)
        {
            if (reference.GetSyntax() is not Microsoft.CodeAnalysis.CSharp.Syntax.AccessorDeclarationSyntax
                { Body: null, ExpressionBody: null })
                return false;
        }

        return true;
    }

    /// <summary>
    /// A type argument Nino has no formatter for, of a closed generic <c>[NinoType]</c> anywhere in
    /// <paramref name="type"/> (<c>Box&lt;Action&gt;</c>, <c>List&lt;Box&lt;Action&gt;&gt;</c>, <c>Box&lt;Action&gt;[]</c>)
    /// whose formatter writes it (<see cref="WrittenParameters"/>), with that generic; null when there is none. An
    /// argument the formatter never writes (<c>Tag&lt;Action&gt;</c> of <c>Tag&lt;T&gt; { int Id; }</c>, and whatever
    /// is inside it: <c>Tag&lt;Box&lt;Action&gt;&gt;</c>) costs nothing: its formatter is never called.
    /// </summary>
    private static (ITypeSymbol Argument, INamedTypeSymbol Generic)? ArgumentWithoutFormatter(ITypeSymbol type,
        TypeFacts facts)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return ArgumentWithoutFormatter(array.ElementType, facts);
            case INamedTypeSymbol named:
                var arguments = AllTypeArguments(named);
                if (arguments.Count == 0)
                    return null;
                if (named.OriginalDefinition.GetNinoTypeOptions() != null)
                {
                    var written = new List<ITypeSymbol>();
                    foreach (var (value, inside) in facts.WrittenParameters.ValuesOf(named))
                    {
                        // A value WrittenParameters could not follow: every type inside it may be written.
                        foreach (var candidate in inside ? TypesInside(value) : new[] { value })
                        {
                            if (facts.Resolver.Resolve(candidate, false) == null)
                                return (candidate, named);
                        }

                        written.Add(value);
                    }

                    arguments = written;
                }

                foreach (var argument in arguments)
                {
                    if (ArgumentWithoutFormatter(argument, facts) is { } found)
                        return found;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary><paramref name="type"/> and every type inside it: elements and type arguments, at any depth.</summary>
    private static IEnumerable<ITypeSymbol> TypesInside(ITypeSymbol type)
    {
        yield return type;
        var inner = type switch
        {
            IArrayTypeSymbol array => new[] { array.ElementType },
            INamedTypeSymbol named => AllTypeArguments(named),
            _ => (IReadOnlyList<ITypeSymbol>)System.Array.Empty<ITypeSymbol>(),
        };
        foreach (var part in inner)
        {
            foreach (var inside in TypesInside(part))
                yield return inside;
        }
    }

    /// <summary>
    /// Whether an <c>[UnsafeAccessor]</c> extern in the formatter of <paramref name="type"/> reaches a member declared
    /// by <paramref name="declaring"/> (the type itself or one of its bases, as the type sees it). .NET 8 has no
    /// accessors in generic types, so both must be non-generic there. .NET 9+ declares them in the generic formatter,
    /// whose type parameters are the type's (with the same constraints): the runtime matches the accessor's signature
    /// against the member's as declared, type parameter by position, so <paramref name="declaring"/> must be
    /// non-generic or have the formatter's first type parameters, in order, as its type arguments (<c>Base&lt;T&gt;</c>
    /// from <c>D&lt;T, U&gt;</c>; not <c>Base&lt;U&gt;</c>, <c>Base&lt;List&lt;T&gt;&gt;</c> or <c>Base&lt;int&gt;</c>,
    /// which the runtime does not find). The form follows the compilation's symbols: Unity, <c>netstandard</c> and
    /// .NET 8 compilations keep the partial accessor class for generic types.
    /// </summary>
    private static bool UnsafeAccessorReaches(INamedTypeSymbol type, INamedTypeSymbol declaring, TypeFacts facts)
    {
        if (!facts.Net8)
            return false;
        if (!declaring.IsGenericType && !type.IsGenericType)
            return true;
        if (!facts.Net9)
            return false;
        var parameters = AllTypeParameters(type);
        var arguments = AllTypeArguments(declaring);
        if (arguments.Count > parameters.Count)
            return false;
        for (int i = 0; i < arguments.Count; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(arguments[i], parameters[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <c>new T()</c> leaves every serialized member at its default value as far as the constructors go: each
    /// class of the chain (up to <c>object</c>) has a parameterless constructor that is implicit, or declared in
    /// source with an empty body and no arguments to the constructor it chains to. Field initializers cannot touch
    /// other members; those of the members themselves are checked by <see cref="StartsDefault"/>.
    /// </summary>
    private static bool ConstructsDefaults(INamedTypeSymbol type, CancellationToken ct)
    {
        for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            var constructor = t.InstanceConstructors.FirstOrDefault(static c => c.Parameters.Length == 0);
            if (constructor == null)
                return false;
            if (constructor.IsImplicitlyDeclared)
                continue;
            if (constructor.DeclaringSyntaxReferences.Length == 0)
                return false;
            foreach (var reference in constructor.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(ct) is not ConstructorDeclarationSyntax syntax ||
                    syntax.ExpressionBody != null || syntax.Body is not { Statements.Count: 0 } ||
                    syntax.Initializer is { } init &&
                    (!init.IsKind(SyntaxKind.BaseConstructorInitializer) || init.ArgumentList.Arguments.Count != 0))
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the member is null in a new instance (given <see cref="ConstructsDefaults"/>): a field or an
    /// auto-property declared in source without an initializer. A property with accessor bodies may compute its
    /// value, so its getter is called as before.
    /// </summary>
    private static bool StartsDefault(ISymbol member, CancellationToken ct)
    {
        if (member.DeclaringSyntaxReferences.Length == 0)
            return false;
        foreach (var reference in member.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax(ct))
            {
                case VariableDeclaratorSyntax { Initializer: null } when member is IFieldSymbol:
                    continue;
                case PropertyDeclarationSyntax { Initializer: null, ExpressionBody: null, AccessorList: { } accessors }
                    when accessors.Accessors.All(static a => a.Body == null && a.ExpressionBody == null):
                    continue;
                default:
                    return false;
            }
        }

        return true;
    }

    private static string KeyName(string name, bool renamed, INamedTypeSymbol declaring) =>
        renamed ? $"{declaring.Name}.{name}" : name;

    /// <summary>
    /// The <c>[NinoFormerName]</c>s of a member and the keys they hash to (keyed bodies; a renamed member's former
    /// names are qualified by its declaring type, as its own name is).
    /// </summary>
    private static IEnumerable<(string Former, int Key)> FormerKeys(ISymbol member, bool renamed, NinoSymbols known) =>
        NinoSymbols.StringArguments(member, known.FormerName)
            .Select(former => (former, ComputeId(KeyName(former, renamed, member.ContainingType))));

    /// <summary>How many more derived members of the same name are serialized after <c>symbols[index]</c>.</summary>
    private static int DerivedSameNamed(MemberList symbols, int index)
    {
        var symbol = symbols[index];
        int count = 0;
        for (int i = 0; i < symbols.Count; i++)
        {
            if (i != index && symbols[i].Name == symbol.Name &&
                InheritsFrom(symbols[i].ContainingType, symbol.ContainingType))
                count++;
        }

        return count;
    }

    private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t, baseType))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <c>value.Name</c> on <paramref name="type"/> names another member than <paramref name="member"/>, a
    /// member of a base class: a class in between declares a member of the same name that
    /// <paramref name="visibility"/> lets generated code see (all members when null), and that is not an override of it.
    /// </summary>
    private static bool IsHidden(INamedTypeSymbol type, ISymbol member, Visibility? visibility)
    {
        var declaring = member.ContainingType;
        if (declaring.TypeKind != TypeKind.Class)
            return false;
        for (var t = type; t != null && !SymbolEqualityComparer.Default.Equals(t, declaring); t = t.BaseType)
        {
            foreach (var other in t.GetMembers(member.Name))
            {
                if ((visibility == null || visibility.IsVisible(other)) && !NinoSymbols.Overrides(other, member))
                    return true;
            }
        }

        return false;
    }
}
