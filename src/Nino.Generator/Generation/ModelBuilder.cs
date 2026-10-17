using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;
using Nino.Generator.Analysis;

namespace Nino.Generator.Generation;

/// <summary>Turns a <c>[NinoType]</c> symbol into an equatable <see cref="TypeModel"/>.</summary>
internal static class ModelBuilder
{
    /// <param name="diagnostics">Receives problems found on the way (the analyzer's); null when generating.</param>
    public static TypeModel? Build(INamedTypeSymbol type, TypeFacts facts, DiagnosticSink? diagnostics = null)
    {
        type = type.OriginalDefinition;
        if (!Visibility.IsEligible(type))
            return null;
        var attr = type.GetNinoTypeOptions();
        if (attr == null)
            return null;

        var known = facts.Symbols;
        bool rawStruct = facts.Raw.IsRawStruct(type);

        var kind = type.TypeKind == TypeKind.Interface || type.IsAbstract
            ? FormatterKind.Polymorphic
            : type.IsValueType
                ? FormatterKind.Struct
                : type.IsSealed
                    ? FormatterKind.SealedClass
                    : FormatterKind.Class;

        var typeParameters = AllTypeParameters(type);
        bool generic = typeParameters.Count != 0;

        var formerName = NinoSymbols.Get(type, known.FormerName)?.ConstructorArguments.FirstOrDefault().Value as string;
        string idName = formerName ?? RuntimeName(type);
        int typeId = generic ? 0 : ComputeId(idName);

        var members = new List<MemberModel>();
        var memberSymbols = new List<ISymbol>();
        var factories = new List<FactoryRegistration>();
        bool needsPartialAccessor = false;

        bool tolerant = attr.VersionTolerant || facts.WeakTolerance;
        // Abstract classes have no body of their own, but emit WriteMembers/ReadMembers for their subtypes.
        // Keyed bodies are self-describing per member: no delegation to or from them.
        bool emitMembers = facts.IsDelegatable(type);
        string baseFormatter = "";
        int baseMemberCount = 0;

        if ((kind != FormatterKind.Polymorphic || emitMembers) && !rawStruct)
        {
            var symbols = facts.Members.Of(type, attr);

            // Reuse the base formatter's member code when the base's members are exactly our first members, in
            // the same order: the bytes are identical, and each member is compiled once instead of once per subtype.
            int basePrefix = 0;
            if (type.TypeKind == TypeKind.Class && !attr.Keyed && type.BaseType is { } baseType &&
                facts.IsDelegatable(baseType) &&
                (baseType.GetNinoTypeOptions() is { } baseAttr && (baseAttr.VersionTolerant || facts.WeakTolerance) == tolerant))
            {
                var baseSymbols = facts.Members.Of(baseType, baseAttr);
                if (baseSymbols.Count <= symbols.Count &&
                    baseSymbols.Select((x, i) => SymbolEqualityComparer.Default.Equals(x, symbols[i])).All(x => x))
                {
                    basePrefix = baseSymbols.Count;
                    baseFormatter = FormatterResolver.GeneratedFormatter(baseType);
                }
            }

            // Abstract types do not report member diagnostics: their subtypes do.
            var memberDiagnostics = kind == FormatterKind.Polymorphic ? null : diagnostics;
            for (int i = 0; i < symbols.Count; i++)
            {
                var member = BuildMember(type, symbols, i, facts, memberDiagnostics, ref needsPartialAccessor,
                    factories);
                if (member == null)
                    continue;
                members.Add(member);
                memberSymbols.Add(symbols[i]);
                if (i < basePrefix)
                    baseMemberCount++;
            }

            // Raw members on both sides of the boundary would be written as one block inline; delegating would
            // split it into two reservations (measurably slower for small types), so keep such members inline.
            if (baseMemberCount > 0 && baseMemberCount < members.Count &&
                members[baseMemberCount - 1].Raw != null && members[baseMemberCount].Raw != null)
            {
                baseFormatter = "";
                baseMemberCount = 0;
            }
        }

        // A positional struct without members is written as nothing at all: a collection of it could not be read back,
        // since its count is checked against the bytes left (every element takes at least one byte).
        if (kind == FormatterKind.Struct && !rawStruct && !tolerant && !attr.Keyed && members.Count == 0)
            diagnostics?.Report(NinoDiagnostics.EmptyStruct, type, type.ToDisplayString());

        // Closed generic [NinoType]s named by the members and base types, for the registry (not needed by the analyzer).
        var genericTypes = new List<ClosedTypeRegistration>();
        // Structs whose layout the runtime measures, for the same registry (LayoutTypes).
        var layoutTypes = new List<string>();
        if (diagnostics == null)
        {
            foreach (var symbol in memberSymbols)
            {
                if (MemberCollector.MemberType(symbol) is { } memberType)
                {
                    ClosedGenerics.Collect(memberType, facts, genericTypes);
                    LayoutTypes.Collect(memberType, facts, layoutTypes);
                }
            }

            ClosedGenerics.CollectBases(type, facts, genericTypes);
            // The type itself (a raw struct has no member models) and what its base types name (List<Point>).
            for (ITypeSymbol? t = type; t != null; t = t.BaseType)
                LayoutTypes.Collect(t, facts, layoutTypes);
        }

        if (attr.Keyed && diagnostics != null)
        {
            // Current names first, so a former name that collides names the member it collides with.
            var seen = new Dictionary<int, string>();
            foreach (var member in members)
            {
                if (seen.TryGetValue(member.Key, out var other))
                    diagnostics.Report(NinoDiagnostics.KeyCollision, type, other, member.Name, type.ToDisplayString());
                else
                    seen[member.Key] = member.Name;
            }

            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member.FormerKeys.Length == 0)
                    continue;
                var claimed = new HashSet<int> { member.Key };
                foreach (var former in NinoSymbols.StringArguments(memberSymbols[i], known.FormerName))
                {
                    int formerKey = ComputeId(member.Name == member.SourceName
                        ? former
                        : $"{memberSymbols[i].ContainingType.Name}.{former}");
                    if (!claimed.Add(formerKey))
                        continue;
                    string label = $"{member.Name} (former name '{former}')";
                    if (seen.TryGetValue(formerKey, out var other))
                        diagnostics.Report(NinoDiagnostics.KeyCollision, memberSymbols[i], other, label,
                            type.ToDisplayString());
                    else
                        seen[formerKey] = label;
                }
            }
        }
        else if (!attr.Keyed && diagnostics != null)
        {
            // Only keyed bodies write member names; elsewhere a member's former name means nothing.
            foreach (var symbol in type.GetMembers())
            {
                if (symbol is IFieldSymbol or IPropertySymbol && NinoSymbols.Has(symbol, known.FormerName))
                    diagnostics.Report(NinoDiagnostics.FormerNameNotKeyed, symbol, symbol.Name, type.ToDisplayString());
            }
        }

        var construction = kind == FormatterKind.Polymorphic || rawStruct
            ? new ConstructionModel(false, "", EquatableArray<string>.Empty, "")
            : ConstructionPlanner.Plan(type, members, facts, diagnostics);

        var collection = kind is FormatterKind.Class or FormatterKind.SealedClass && !construction.Recreate
            ? facts.Resolver.ResolveCollection(type)
            : null;
        string collectionFormatter = collection?.Code ?? "";
        if (collection is { } c)
            factories.AddRange(c.Factories);

        if (construction.Recreate)
        {
            // Read-only members that are not constructor arguments cannot be assigned (init-only properties can,
            // through the object initializer, when they are accessible): their values are read and dropped.
            var args = new HashSet<string>(construction.Arguments);
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (args.Contains(m.Name) || !m.ConstructionOnly ||
                    (m.Access == MemberAccess.Direct && memberSymbols[i] is IPropertySymbol { SetMethod: not null }))
                    continue;
                members[i] = m with { Unassignable = true };
                diagnostics?.Report(NinoDiagnostics.ReadOnlyMember, memberSymbols[i], m.Name, type.ToDisplayString());
            }
        }

        // Tracking is inherited: a tracked base's reference header is resolved by the subtype's body as soon as
        // the instance exists, so every subtype of a tracked type must record itself.
        bool tracked = IsTracked(type);
        bool trackReferences = tracked && kind != FormatterKind.Struct;
        if (tracked && kind == FormatterKind.Struct)
            diagnostics?.Report(NinoDiagnostics.TrackReferencesOnStruct, type, type.ToDisplayString());
        else if (trackReferences && construction.Recreate)
            diagnostics?.Report(NinoDiagnostics.TrackReferencesNeedsAssignableMembers, type, type.ToDisplayString());

        // The nesting bound and the __Shallow twin (non-generic: a twin of a generic formatter is never named).
        int bound = kind == FormatterKind.Polymorphic || generic ? TypeFacts.Unbounded : facts.TypeBound(type);
        bool shallow = !generic && kind is FormatterKind.SealedClass or FormatterKind.Struct && facts.HasShallow(type);

        var used = new List<FormatterRegistration>();
        if (!generic && kind != FormatterKind.Polymorphic)
        {
            foreach (var m in members)
            {
                if (m.TypeId != 0)
                    used.Add(new FormatterRegistration(m.TypeName, m.Formatter, m.TypeId));
            }
        }

        bool pooled = NinoSymbols.Has(type, known.Pooled);
        if (pooled && (kind != FormatterKind.SealedClass || construction.Recreate))
        {
            pooled = false;
            diagnostics?.Report(NinoDiagnostics.InvalidPooledType, type, type.ToDisplayString());
        }

        string partialNamespace = "";
        var partialDeclarations = new List<string>();
        if (needsPartialAccessor || pooled)
        {
            if (!IsPartialChain(type))
            {
                pooled = false;
                diagnostics?.Report(NinoDiagnostics.NotPartial, type, type.ToDisplayString());
            }
            else
            {
                partialNamespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns
                    ? ns.ToDisplayString()
                    : "";
                for (var t = type; t != null; t = t.ContainingType)
                    partialDeclarations.Insert(0, Declaration(t));
            }
        }

        // The topmost base class of this assembly whose formatter dispatches subtypes: this type's subtypes are
        // dispatched there, so each subtype is listed once per hierarchy rather than once per ancestor.
        string dispatchRoot = "", dispatchRootFormatter = "";
        if (type.TypeKind == TypeKind.Class && !generic && !trackReferences)
        {
            for (var b = type.BaseType; b != null; b = b.BaseType)
            {
                if (b.IsNinoType() && !b.IsSealed && !b.IsGenericType && Visibility.IsEligible(b) &&
                    SymbolEqualityComparer.Default.Equals(b.ContainingAssembly, type.ContainingAssembly) &&
                    !IsTracked(b))
                {
                    dispatchRoot = Global(b);
                    dispatchRootFormatter = FormatterName(b);
                }
            }
        }

        var baseClasses = new List<string>();
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.IsNinoType())
                baseClasses.Add(Global(b));
        }

        var interfaces = new List<string>();
        foreach (var i in type.AllInterfaces)
        {
            if (i.IsNinoType())
                interfaces.Add(Global(i));
        }

        return new TypeModel(
            Global(type),
            FormatterName(type),
            new EquatableArray<string>(typeParameters.Select(p => p.Name)),
            Constraints(typeParameters),
            kind,
            typeId,
            idName,
            generic ? 0 : formerName == null ? typeId : ComputeId(RuntimeName(type)),
            tolerant,
            attr.Keyed,
            trackReferences,
            rawStruct,
            collectionFormatter,
            emitMembers,
            baseFormatter,
            baseMemberCount,
            new EquatableArray<MemberModel>(members),
            construction,
            new EquatableArray<FormatterRegistration>(used),
            // Generic types name no closed collection types; their formatters resolve creators at runtime.
            generic || kind == FormatterKind.Polymorphic
                ? EquatableArray<FactoryRegistration>.Empty
                : new EquatableArray<FactoryRegistration>(factories.Distinct()
                    .OrderBy(f => f.TypeName, System.StringComparer.Ordinal)),
            new EquatableArray<ClosedTypeRegistration>(genericTypes.Distinct()
                .OrderBy(g => g.TypeName, System.StringComparer.Ordinal)),
            dispatchRoot,
            dispatchRootFormatter,
            new EquatableArray<string>(baseClasses),
            new EquatableArray<string>(interfaces),
            pooled,
            partialNamespace,
            new EquatableArray<string>(partialDeclarations),
            kind is FormatterKind.Class or FormatterKind.SealedClass && !construction.Recreate &&
            (construction.Factory.Length != 0 || construction.Creator.Length != 0) && !ComparesByValue(type),
            kind is not (FormatterKind.Class or FormatterKind.SealedClass) ||
            collection is { OpensBodies: true } || members.Any(m => m.OpensBodies),
            !rawStruct && facts.IsFixedSize(type),
            bound,
            shallow,
            collection is { } sc ? facts.ShallowFormatter(sc.Code, sc, false) : "",
            kind == FormatterKind.SealedClass && !construction.Recreate &&
            construction.Factory.Length == 0 && construction.Creator.Length != 0 && collection == null &&
            !attr.Keyed && members.Skip(baseMemberCount).Any(static m => m.Fresh) &&
            !members.Skip(baseMemberCount).Any(static m => m.ReadsLoop) && ConstructsDefaults(type),
            LayoutTypes.ToArray(layoutTypes),
            rawStruct ? RawLayout.Describe(type) : "");
    }

    /// <summary>
    /// Whether instances of the type are compared by their members (a record, an override of GetHashCode, or
    /// IComparable): a hashed or sorted collection must not hold one before its members are read, so its body is
    /// never deferred.
    /// </summary>
    private static bool ComparesByValue(INamedTypeSymbol type)
    {
        if (type.IsRecord)
            return true;
        foreach (var i in type.AllInterfaces)
        {
            if (i.ContainingNamespace?.ToDisplayString() == "System" && i.Name == "IComparable")
                return true;
        }

        for (var t = type; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            foreach (var member in t.GetMembers(nameof(GetHashCode)))
            {
                if (member is IMethodSymbol { IsOverride: true, Parameters.Length: 0 })
                    return true;
            }
        }

        return false;
    }

    /// <summary>Whether the type, a base class or an interface asks for reference tracking.</summary>
    internal static bool IsTracked(INamedTypeSymbol type) =>
        type.GetNinoTypeOptions() is { TrackReferences: true } || InheritsTracking(type);

    /// <summary>Whether a base class or interface asks for reference tracking.</summary>
    private static bool InheritsTracking(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.GetDirectNinoTypeAttribute() is { } a && NinoTypeOptions.From(a).TrackReferences)
                return true;
        }

        foreach (var i in type.AllInterfaces)
        {
            if (i.GetDirectNinoTypeAttribute() is { } a && NinoTypeOptions.From(a).TrackReferences)
                return true;
        }

        return false;
    }

    private static string Declaration(INamedTypeSymbol type)
    {
        string keyword = type.IsRecord
            ? type.IsValueType ? "record struct" : "record"
            : type.TypeKind == TypeKind.Interface
                ? "interface"
                : type.IsValueType ? "struct" : "class";
        string args = type.TypeParameters.Length == 0
            ? ""
            : "<" + string.Join(", ", type.TypeParameters.Select(p => p.Name)) + ">";
        return $"partial {keyword} {type.Name}{args}";
    }

    private static bool IsPartialChain(INamedTypeSymbol type)
    {
        for (var t = type; t != null; t = t.ContainingType)
        {
            if (t.DeclaringSyntaxReferences.Length == 0)
                return false;
            foreach (var reference in t.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is TypeDeclarationSyntax syntax &&
                    !syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
                    return false;
            }
        }

        return true;
    }

    private static MemberModel? BuildMember(INamedTypeSymbol type, MemberList symbols, int index,
        TypeFacts facts, DiagnosticSink? diagnostics, ref bool needsPartialAccessor,
        List<FactoryRegistration> factories)
    {
        var symbol = symbols[index];
        var memberType = MemberCollector.MemberType(symbol)?.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (memberType == null)
            return null;
        var known = facts.Symbols;

        string? formatter;
        string? shallowFormatter = null;
        bool registered = false;
        bool opensBodies = true;
        bool readsLoop = false;
        var custom = NinoSymbols.Get(symbol, known.CustomFormatter);
        if (custom is { ConstructorArguments: { Length: > 0 } args } && args[0].Value is INamedTypeSymbol customType)
        {
            if (!known.IsCustomFormatterOf(customType, memberType))
                return null; // NINO008, reported by the analyzer
            formatter = Global(customType);
        }
        else
        {
            bool utf8 = NinoSymbols.Has(symbol, known.Utf8);
            var resolved = facts.Resolver.Resolve(memberType, utf8);
            formatter = resolved?.Code;
            registered = resolved?.Registered == true;
            if (resolved is { } r)
            {
                factories.AddRange(r.Factories);
                opensBodies = r.OpensBodies;
                shallowFormatter = facts.ShallowFormatter(r.Code, r, utf8);
                readsLoop = r.Elements is { IsDefault: false, Length: > 0 } && !TypeNames.IsFlat(r.Code);
            }
        }

        if (formatter == null)
        {
            diagnostics?.Report(NinoDiagnostics.UnserializableMember, symbol, symbol.Name, memberType.ToDisplayString(),
                type.ToDisplayString());
            return null;
        }

        var declaring = symbol.ContainingType;
        // A base member hidden by a derived member of the same name (`new`): generated code names it through a cast
        // to its declaring type, and under its own name when the derived member is serialized too.
        int renamed = DerivedSameNamed(symbols, index);
        MemberAccess access;
        if (facts.Visibility.IsDirectlyAccessible(symbol))
        {
            access = renamed != 0 || IsHidden(type, symbol, facts.Visibility.IsVisible)
                ? MemberAccess.Cast
                : MemberAccess.Direct;
        }
        else if (facts.Net8 && !declaring.IsGenericType && !type.IsGenericType)
        {
            access = MemberAccess.UnsafeAccessor;
        }
        else if (renamed != 0 || IsHidden(type, symbol, _ => true))
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
            needsPartialAccessor = true;
        }

        // Members join unmanaged blocks when their layout is the same on every runtime; others (rare struct
        // shapes such as { int; long }) go through their formatter, which converts them where needed.
        var raw = custom == null && facts.Raw.IsRawUnmanaged(memberType) && RawLayout.IsPortable(memberType)
            ? FormatterResolver.RawSizeOf(memberType) with { Layout = RawLayout.Describe(memberType) }
            : null;

        // Only registered formatters: a member's custom formatter must not become the global one.
        int typeId = registered && raw == null ? facts.TypeId(memberType) : 0;
        string name = renamed == 0 ? symbol.Name : $"{symbol.Name}__{renamed}";
        // Keyed: a renamed member's key is its declaring type's name with its own.
        int key = ComputeId(KeyName(symbol.Name, renamed, declaring));
        var formerKeys = EquatableArray<int>.Empty;
        if (type.GetNinoTypeOptions()?.Keyed == true)
        {
            formerKeys = new EquatableArray<int>(NinoSymbols.StringArguments(symbol, known.FormerName)
                .Select(former => ComputeId(KeyName(former, renamed, declaring)))
                .Where(former => former != key)
                .Distinct());
        }

        return new MemberModel(name, symbol.Name, key, formerKeys, Global(memberType), formatter, raw,
            symbol is IPropertySymbol, access, Global(declaring), declaring.IsValueType,
            Visibility.IsConstructionOnly(symbol), false, typeId, opensBodies, shallowFormatter ?? formatter,
            custom == null && memberType.IsReferenceType && formatter.StartsWith(Core, System.StringComparison.Ordinal) &&
            !formatter.StartsWith(Core + "CachedFormatter<", System.StringComparison.Ordinal) &&
            formatter != Core + "ObjectFormatter" && formatter != Core + "StringFormatter" &&
            formatter != Core + "Utf8StringFormatter" && StartsDefault(symbol),
            raw == null && readsLoop);
    }

    /// <summary>
    /// Whether <c>new T()</c> leaves every serialized member at its default value as far as the constructors go: each
    /// class of the chain (up to <c>object</c>) has a parameterless constructor that is implicit, or declared in
    /// source with an empty body and no arguments to the constructor it chains to. Field initializers cannot touch
    /// other members; those of the members themselves are checked by <see cref="StartsDefault"/>.
    /// </summary>
    private static bool ConstructsDefaults(INamedTypeSymbol type)
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
                if (reference.GetSyntax() is not ConstructorDeclarationSyntax syntax ||
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
    private static bool StartsDefault(ISymbol member)
    {
        if (member.DeclaringSyntaxReferences.Length == 0)
            return false;
        foreach (var reference in member.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax())
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

    private static string KeyName(string name, int renamed, INamedTypeSymbol declaring) =>
        renamed == 0 ? name : $"{declaring.Name}.{name}";

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
    /// <paramref name="visible"/> lets generated code see, and that is not an override of it.
    /// </summary>
    private static bool IsHidden(INamedTypeSymbol type, ISymbol member, System.Func<ISymbol, bool> visible)
    {
        var declaring = member.ContainingType;
        if (declaring.TypeKind != TypeKind.Class)
            return false;
        for (var t = type; t != null && !SymbolEqualityComparer.Default.Equals(t, declaring); t = t.BaseType)
        {
            foreach (var other in t.GetMembers(member.Name))
            {
                if (visible(other) && !Overrides(other, member))
                    return true;
            }
        }

        return false;
    }

    private static bool Overrides(ISymbol other, ISymbol member)
    {
        for (var p = (other as IPropertySymbol)?.OverriddenProperty; p != null; p = p.OverriddenProperty)
        {
            if (SymbolEqualityComparer.Default.Equals(p, member))
                return true;
        }

        return false;
    }
}
