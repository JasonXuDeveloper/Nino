using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.DTOs;
using static Nino.Generator.Generation.TypeNames;

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
                var member = BuildMember(type, symbols[i], facts, memberDiagnostics, ref needsPartialAccessor);
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

        if (attr.Keyed && diagnostics != null)
        {
            var seen = new Dictionary<int, string>();
            foreach (var member in members)
            {
                if (seen.TryGetValue(member.Key, out var other))
                    diagnostics.Report(NinoDiagnostics.KeyCollision, type, other, member.Name, type.ToDisplayString());
                else
                    seen[member.Key] = member.Name;
            }
        }

        var construction = kind == FormatterKind.Polymorphic || rawStruct
            ? new ConstructionModel(false, "", EquatableArray<string>.Empty, "")
            : ConstructionPlanner.Plan(type, members, facts, diagnostics);

        string collectionFormatter = kind is FormatterKind.Class or FormatterKind.SealedClass && !construction.Recreate
            ? facts.Resolver.ResolveCollection(type) ?? ""
            : "";

        if (construction.Recreate)
        {
            // Read-only members that are not constructor arguments cannot be assigned (init-only properties can,
            // through the object initializer, when they are accessible): their values are read and dropped.
            var args = new HashSet<string>(construction.Arguments);
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                if (args.Contains(m.Name) || !m.ConstructionOnly ||
                    (m.Access == MemberAccess.Direct && m.IsProperty))
                    continue;
                members[i] = m with { Unassignable = true };
                diagnostics?.Report(NinoDiagnostics.ReadOnlyMember, memberSymbols[i], m.Name, type.ToDisplayString());
            }
        }

        // Tracking is inherited: a tracked base's reference header is resolved by the subtype's body as soon as
        // the instance exists, so every subtype of a tracked type must record itself.
        bool tracked = attr.TrackReferences || InheritsTracking(type);
        bool trackReferences = tracked && kind != FormatterKind.Struct;
        if (tracked && kind == FormatterKind.Struct)
            diagnostics?.Report(NinoDiagnostics.TrackReferencesOnStruct, type, type.ToDisplayString());
        else if (trackReferences && construction.Recreate)
            diagnostics?.Report(NinoDiagnostics.TrackReferencesNeedsAssignableMembers, type, type.ToDisplayString());

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
            pooled,
            partialNamespace,
            new EquatableArray<string>(partialDeclarations));
    }

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

    private static MemberModel? BuildMember(INamedTypeSymbol type, ISymbol symbol, TypeFacts facts,
        DiagnosticSink? diagnostics, ref bool needsPartialAccessor)
    {
        var memberType = MemberCollector.MemberType(symbol)?.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (memberType == null)
            return null;
        var known = facts.Symbols;

        string? formatter;
        bool registered = false;
        var custom = NinoSymbols.Get(symbol, known.CustomFormatter);
        if (custom is { ConstructorArguments: { Length: > 0 } args } && args[0].Value is INamedTypeSymbol customType)
        {
            if (!known.IsCustomFormatterOf(customType, memberType))
                return null; // NINO008, reported by the analyzer
            formatter = customType.IsValueType
                ? Global(customType)
                : $"{Core}CustomFormatter<{Global(memberType)}, {Global(customType)}>";
        }
        else
        {
            var resolved = facts.Resolver.Resolve(memberType, NinoSymbols.Has(symbol, known.Utf8));
            formatter = resolved?.Code;
            registered = resolved?.Registered == true;
        }

        if (formatter == null)
        {
            diagnostics?.Report(NinoDiagnostics.UnserializableMember, symbol, symbol.Name, memberType.ToDisplayString(),
                type.ToDisplayString());
            return null;
        }

        var declaring = symbol.ContainingType;
        MemberAccess access;
        if (facts.Visibility.IsDirectlyAccessible(symbol))
        {
            access = MemberAccess.Direct;
        }
        else if (facts.Net8 && !declaring.IsGenericType && !type.IsGenericType)
        {
            access = MemberAccess.UnsafeAccessor;
        }
        else
        {
            access = MemberAccess.Partial;
            needsPartialAccessor = true;
        }

        // Members join unmanaged blocks when their layout is the same on every runtime; others (rare struct
        // shapes such as { int; long }) go through their formatter, which converts them where needed.
        var raw = custom == null && facts.Raw.IsRawUnmanaged(memberType) && RawLayout.IsPortable(memberType)
            ? FormatterResolver.RawSizeOf(memberType)
            : null;

        // Only registered formatters: a member's custom formatter must not become the global one.
        int typeId = registered && raw == null ? facts.TypeId(memberType) : 0;
        return new MemberModel(symbol.Name, ComputeId(symbol.Name), Global(memberType), formatter, raw,
            symbol is IPropertySymbol, access, Global(declaring), declaring.IsValueType,
            Visibility.IsConstructionOnly(symbol), false, typeId);
    }
}
