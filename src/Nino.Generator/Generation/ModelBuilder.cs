using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.DTOs;
using static Nino.Generator.Generation.TypeFacts;

namespace Nino.Generator.Generation;

/// <summary>Turns a <c>[NinoType]</c> symbol into an equatable <see cref="TypeModel"/>.</summary>
internal static class ModelBuilder
{
    private const string Core = "global::Nino.Core.";

    /// <param name="diagnostics">Receives problems found on the way (the analyzer's); null when generating.</param>
    public static TypeModel? Build(INamedTypeSymbol type, Compilation compilation, ParseOptions? parseOptions,
        DiagnosticSink? diagnostics = null)
    {
        type = type.OriginalDefinition;
        if (!IsEligible(type))
            return null;
        var attr = type.GetNinoTypeAttribute();
        if (attr == null)
            return null;

        var facts = TypeFacts.For(compilation, parseOptions);
        bool weakTolerance = facts.WeakTolerance;
        var resolver = facts.Resolver;
        bool rawStruct = facts.IsRawStruct(type);

        var kind = type.TypeKind == TypeKind.Interface || type.IsAbstract
            ? FormatterKind.Polymorphic
            : type.IsValueType
                ? FormatterKind.Struct
                : type.IsSealed
                    ? FormatterKind.SealedClass
                    : FormatterKind.Class;

        var typeParameters = TypeNames.AllTypeParameters(type);
        string typeName = TypeNames.Global(type);
        string formatterName = TypeNames.FormatterName(type);
        string typeParameterList = typeParameters.Count == 0
            ? ""
            : "<" + string.Join(", ", typeParameters.Select(p => p.Name)) + ">";
        string openTypeName = typeParameters.Count == 0
            ? ""
            : OpenName(type);

        var formerName = GetAttribute(type, "NinoFormerNameAttribute")?.ConstructorArguments.FirstOrDefault().Value
            as string;
        string idName = formerName ?? TypeNames.RuntimeName(type);
        int typeId = typeParameters.Count == 0 ? TypeNames.ComputeId(idName) : 0;

        var members = new List<MemberModel>();
        var used = new List<FormatterRegistration>();
        bool needsPartialAccessor = false;

        bool tolerant = attr.versionTolerant || weakTolerance;
        bool isClass = type.TypeKind == TypeKind.Class;
        // Abstract classes have no body of their own, but emit WriteMembers/ReadMembers for their subtypes.
        // Keyed bodies are self-describing per member: no delegation to or from them.
        bool emitMembers = isClass && !attr.keyed && facts.IsDelegatable(type);
        string baseFormatter = "";
        int baseMemberCount = 0;

        if ((kind != FormatterKind.Polymorphic || emitMembers) && !rawStruct)
        {
            var symbolsToBuild = facts.Members(type, attr);

            // Reuse the base formatter's member code when the base's members are exactly our first members, in
            // the same order: the bytes are identical, and each member is compiled once instead of once per subtype.
            int basePrefix = 0;
            if (isClass && !attr.keyed && type.BaseType is { } baseType && baseType.SpecialType != SpecialType.System_Object &&
                facts.IsDelegatable(baseType) &&
                (baseType.GetNinoTypeAttribute()!.versionTolerant || weakTolerance) == tolerant)
            {
                var baseSymbols = facts.Members(baseType, baseType.GetNinoTypeAttribute()!);
                if (baseSymbols.Count <= symbolsToBuild.Count &&
                    baseSymbols.Select((x, i) => SymbolEqualityComparer.Default.Equals(x, symbolsToBuild[i])).All(x => x))
                {
                    basePrefix = baseSymbols.Count;
                    baseFormatter = FormatterResolver.GeneratedFormatter(baseType);
                }
            }

            // Abstract types do not report member diagnostics: their subtypes do.
            var memberDiagnostics = kind == FormatterKind.Polymorphic ? null : diagnostics;
            for (int i = 0; i < symbolsToBuild.Count; i++)
            {
                var member = BuildMember(type, symbolsToBuild[i], facts, resolver, memberDiagnostics,
                    ref needsPartialAccessor);
                if (member == null)
                    continue;
                members.Add(member);
                if (i < basePrefix)
                    baseMemberCount++;
            }

            // Raw members on both sides of the boundary would be written as one block inline; delegating would
            // split it into two reservations (measurably slower for small types), so keep such members inline.
            if (baseMemberCount > 0 && baseMemberCount < members.Count &&
                members[baseMemberCount - 1].RawSize.Length != 0 && members[baseMemberCount].RawSize.Length != 0)
            {
                baseFormatter = "";
                baseMemberCount = 0;
            }
        }

        if (attr.keyed)
        {
            var seen = new Dictionary<int, string>();
            foreach (var member in members)
            {
                int key = TypeNames.ComputeId(member.Name);
                if (seen.TryGetValue(key, out var other))
                {
                    diagnostics?.Report(NinoDiagnostics.KeyCollision, type, other, member.Name, type.ToDisplayString());
                }
                else
                {
                    seen[key] = member.Name;
                }
            }
        }

        var construction = kind == FormatterKind.Polymorphic || rawStruct
            ? new ConstructionModel(false, "", EquatableArray<string>.Empty, "")
            : ConstructionPlanner.Plan(type, members, diagnostics);

        string collectionFormatter = kind is FormatterKind.Class or FormatterKind.SealedClass && !construction.Recreate
            ? resolver.ResolveUserCollection(type) ?? ""
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
                diagnostics?.Report(NinoDiagnostics.ReadOnlyMember, type, m.Name, type.ToDisplayString());
            }
        }

        // Tracking is inherited: a tracked base's reference header is resolved by the subtype's body as soon as
        // the instance exists, so every subtype of a tracked type must record itself.
        bool tracked = attr.trackReferences || InheritsTracking(type);
        bool trackReferences = tracked && kind != FormatterKind.Struct;
        if (tracked && kind == FormatterKind.Struct)
        {
            diagnostics?.Report(NinoDiagnostics.TrackReferencesOnStruct, type, type.ToDisplayString());
        }
        else if (trackReferences && construction.Recreate)
        {
            diagnostics?.Report(NinoDiagnostics.TrackReferencesNeedsAssignableMembers, type, type.ToDisplayString());
        }

        if (typeParameters.Count == 0 && kind != FormatterKind.Polymorphic)
        {
            foreach (var m in members)
            {
                // Only built-in formatters: a member's custom formatter must not become the global one.
                if (IsBuiltInFormatter(m.Formatter) && m.RawSize.Length == 0)
                    used.Add(new FormatterRegistration(m.TypeName, m.Formatter, m.TypeId));
            }
        }

        string accessorNamespace = "";
        var accessorDeclarations = new List<string>();
        if (needsPartialAccessor)
        {
            if (!IsPartialChain(type))
            {
                diagnostics?.Report(NinoDiagnostics.NotPartial, type, type.ToDisplayString());
            }
            else
            {
                accessorNamespace = type.ContainingNamespace is { IsGlobalNamespace: false } ns
                    ? ns.ToDisplayString()
                    : "";
                var chain = new List<INamedTypeSymbol>();
                for (var t = type; t != null; t = t.ContainingType)
                    chain.Add(t);
                chain.Reverse();
                accessorDeclarations.AddRange(chain.Select(Declaration));
            }
        }

        return new TypeModel(
            typeName,
            formatterName,
            typeParameterList,
            TypeNames.Constraints(typeParameters),
            openTypeName,
            kind,
            typeId,
            idName,
            typeParameters.Count == 0 ? (formerName == null ? typeId : TypeNames.ComputeId(TypeNames.RuntimeName(type))) : 0,
            tolerant,
            attr.keyed,
            trackReferences,
            rawStruct,
            collectionFormatter,
            emitMembers,
            baseFormatter,
            baseMemberCount,
            new EquatableArray<MemberModel>(members),
            construction,
            new EquatableArray<FormatterRegistration>(used),
            accessorNamespace,
            new EquatableArray<string>(accessorDeclarations));
    }

    /// <summary>Whether a base class or interface asks for reference tracking.</summary>
    private static bool InheritsTracking(INamedTypeSymbol type)
    {
        for (var b = type.BaseType; b != null; b = b.BaseType)
        {
            if (b.GetDirectNinoTypeAttribute() is { } a && NinoTypeAttribute.GetAttribute(a).trackReferences)
                return true;
        }

        foreach (var i in type.AllInterfaces)
        {
            if (i.GetDirectNinoTypeAttribute() is { } a && NinoTypeAttribute.GetAttribute(a).trackReferences)
                return true;
        }

        return false;
    }

    public static bool IsBuiltInFormatter(string formatter) =>
        (Starts(formatter, Core) && !Starts(formatter, Core + "CachedFormatter<") &&
         !Starts(formatter, Core + "CustomFormatter<")) ||
        Starts(formatter, "global::NinoGen.NinoImmutable") ||
        Starts(formatter, "global::NinoGen.NinoPriorityQueueFormatter<");

    private static bool Starts(string s, string prefix) => s.StartsWith(prefix, StringComparison.Ordinal);

    private static string OpenName(INamedTypeSymbol type)
    {
        string own = type.Name + (type.Arity > 0 ? "<" + new string(',', type.Arity - 1) + ">" : "");
        if (type.ContainingType != null)
            return OpenName(type.ContainingType) + "." + own;
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? "global::" + TypeNames.Namespace(ns) + "." + own
            : "global::" + own;
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
            foreach (var reference in t.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is TypeDeclarationSyntax syntax &&
                    !syntax.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)))
                    return false;
            }

            if (t.DeclaringSyntaxReferences.Length == 0)
                return false;
        }

        return true;
    }

    private static MemberModel? BuildMember(INamedTypeSymbol type, ISymbol symbol, TypeFacts facts,
        FormatterResolver resolver, DiagnosticSink? diagnostics, ref bool needsPartialAccessor)
    {
        var memberType = symbol switch
        {
            IFieldSymbol f => f.Type,
            IPropertySymbol p => p.Type,
            _ => null,
        };
        if (memberType == null)
            return null;
        memberType = memberType.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        bool utf8 = HasAttribute(symbol, "NinoUtf8Attribute");

        string? formatter;
        var custom = GetAttribute(symbol, "NinoCustomFormatterAttribute");
        if (custom != null && custom.ConstructorArguments.Length > 0 &&
            custom.ConstructorArguments[0].Value is INamedTypeSymbol customType)
        {
            formatter = customType.IsValueType
                ? TypeNames.Global(customType)
                : $"{Core}CustomFormatter<{TypeNames.Global(memberType)}, {TypeNames.Global(customType)}>";
        }
        else
        {
            formatter = resolver.Resolve(memberType, utf8);
        }

        if (formatter == null)
        {
            diagnostics?.Report(NinoDiagnostics.UnserializableMember, symbol, symbol.Name, memberType.ToDisplayString(),
                type.ToDisplayString());
            return null;
        }

        bool isProperty = symbol is IPropertySymbol;
        bool constructionOnly = symbol switch
        {
            IFieldSymbol f => f.IsReadOnly,
            IPropertySymbol p => p.SetMethod == null || p.SetMethod.IsInitOnly,
            _ => false,
        };

        var declaring = symbol.ContainingType;
        bool accessible = symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Internal or Accessibility.ProtectedOrInternal =>
                SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, type.ContainingAssembly),
            _ => false,
        };
        if (accessible && symbol is IPropertySymbol prop)
        {
            // the accessors themselves may be more restrictive than the property
            accessible = IsAccessorVisible(prop.GetMethod, type) && IsAccessorVisible(prop.SetMethod, type);
        }

        MemberAccess access;
        if (accessible)
            access = MemberAccess.Direct;
        else if (facts.Net8 && !declaring.IsGenericType && !type.IsGenericType)
            access = MemberAccess.UnsafeAccessor;
        else
        {
            access = MemberAccess.Partial;
            needsPartialAccessor = true;
        }

        // Members join unmanaged blocks when their layout is the same on every runtime; others (rare struct
        // shapes such as { int; long }) go through their formatter, which converts them where needed.
        string rawSize = custom == null && facts.IsRawUnmanaged(memberType) &&
                         RawLayout.IsPortable(memberType)
            ? FormatterResolver.RawSize(memberType)
            : "";

        int typeId = IsBuiltInFormatter(formatter) && rawSize.Length == 0
            ? facts.TypeId(memberType)
            : 0;
        return new MemberModel(symbol.Name, TypeNames.Global(memberType), formatter, rawSize, isProperty, access,
            TypeNames.Global(declaring), declaring.IsValueType, constructionOnly, false, typeId);
    }

    private static bool IsAccessorVisible(IMethodSymbol? method, INamedTypeSymbol type)
    {
        if (method == null)
            return true;
        return method.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Internal or Accessibility.ProtectedOrInternal =>
                SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, type.ContainingAssembly),
            _ => false,
        };
    }

}
