using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.DTOs;

namespace Nino.Generator.Generation;

/// <summary>Turns a <c>[NinoType]</c> symbol into an equatable <see cref="TypeModel"/>.</summary>
internal static class ModelBuilder
{
    private const string Core = "global::Nino.Core.";

    /// <summary>Whether a formatter can be generated for the type (it must be visible to generated code).</summary>
    public static bool IsEligible(INamedTypeSymbol type)
    {
        if (type.IsStatic || type.IsRefLikeType || type.TypeKind is not (TypeKind.Class or TypeKind.Struct
                or TypeKind.Interface))
            return false;
        for (ISymbol? s = type; s is INamedTypeSymbol t; s = t.ContainingType)
        {
            if (t.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal
                or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    private static bool HasAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.Name == name);

    private static AttributeData? GetAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == name);

    private sealed class Candidate
    {
        public ISymbol Symbol = null!;
        public string Name = null!;
        public int Index;
    }

    public static TypeModel? Build(INamedTypeSymbol type, Compilation compilation, ParseOptions? parseOptions)
    {
        ResetMemo();
        type = type.OriginalDefinition;
        if (!IsEligible(type))
            return null;
        var attr = type.GetNinoTypeAttribute();
        if (attr == null)
            return null;

        var diagnostics = new List<DiagnosticInfo>();
        var location = LocationInfo.From(type.Locations.FirstOrDefault());
        var symbols = parseOptions?.PreprocessorSymbolNames.ToArray() ?? Array.Empty<string>();
        bool weakTolerance = symbols.Contains(NinoTypeHelper.WeakVersionToleranceSymbol);
        bool net8 = symbols.Contains("NET8_0_OR_GREATER");
        var resolver = new FormatterResolver(compilation.Assembly, weakTolerance);
        var weakAssembly = weakTolerance ? compilation.Assembly : null;
        bool rawStruct = IsRawStruct(type, weakAssembly);

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
        bool emitMembers = isClass && IsDelegatable(type, compilation.Assembly);
        string baseFormatter = "";
        int baseMemberCount = 0;

        if ((kind != FormatterKind.Polymorphic || emitMembers) && !rawStruct)
        {
            var symbolsToBuild = CollectMembers(type, attr).ToList();

            // Reuse the base formatter's member code when the base's members are exactly our first members, in
            // the same order: the bytes are identical, and each member is compiled once instead of once per subtype.
            int basePrefix = 0;
            if (isClass && type.BaseType is { } baseType && baseType.SpecialType != SpecialType.System_Object &&
                IsDelegatable(baseType, compilation.Assembly) &&
                (baseType.GetNinoTypeAttribute()!.versionTolerant || weakTolerance) == tolerant)
            {
                var baseSymbols = CollectMembers(baseType, baseType.GetNinoTypeAttribute()!).ToList();
                if (baseSymbols.Count <= symbolsToBuild.Count &&
                    baseSymbols.Select((x, i) => SymbolEqualityComparer.Default.Equals(x, symbolsToBuild[i])).All(x => x))
                {
                    basePrefix = baseSymbols.Count;
                    baseFormatter = FormatterResolver.GeneratedFormatter(baseType);
                }
            }

            // Abstract types do not report member diagnostics: their subtypes do.
            var memberDiagnostics = kind == FormatterKind.Polymorphic ? new List<DiagnosticInfo>() : diagnostics;
            for (int i = 0; i < symbolsToBuild.Count; i++)
            {
                var member = BuildMember(type, symbolsToBuild[i], resolver, net8, weakAssembly, memberDiagnostics,
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

        var construction = kind == FormatterKind.Polymorphic || rawStruct
            ? new ConstructionModel(false, "", EquatableArray<string>.Empty, "")
            : BuildConstruction(type, members, diagnostics, location);

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
                diagnostics.Add(new DiagnosticInfo("NINO014", "Member cannot be deserialized",
                    "Member '{0}' of '{1}' is read-only and is not a constructor parameter; its serialized value is ignored when deserializing",
                    DiagnosticSeverity.Warning, location, m.Name, type.ToDisplayString()));
            }
        }

        if (typeParameters.Count == 0 && kind != FormatterKind.Polymorphic)
        {
            foreach (var m in members)
            {
                // Only built-in formatters: a member's custom formatter must not become the global one.
                if (IsBuiltInFormatter(m.Formatter) && m.RawSize.Length == 0)
                    used.Add(new FormatterRegistration(m.TypeName, m.Formatter));
            }
        }

        string accessorNamespace = "";
        var accessorDeclarations = new List<string>();
        if (needsPartialAccessor)
        {
            if (!IsPartialChain(type))
            {
                diagnostics.Add(new DiagnosticInfo("NINO013", "Type must be partial",
                    "'{0}' serializes non-public members, so it (and every type containing it) must be declared partial",
                    DiagnosticSeverity.Error, location, type.ToDisplayString()));
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
            tolerant,
            rawStruct,
            collectionFormatter,
            emitMembers,
            baseFormatter,
            baseMemberCount,
            new EquatableArray<MemberModel>(members),
            construction,
            new EquatableArray<FormatterRegistration>(used),
            accessorNamespace,
            new EquatableArray<string>(accessorDeclarations),
            new EquatableArray<DiagnosticInfo>(diagnostics));
    }

    // Per-transform memos: a struct or base class is typically referenced by many members and subtypes.
    [ThreadStatic] private static Dictionary<ISymbol, bool>? _rawStructs;
    [ThreadStatic] private static Dictionary<ISymbol, bool>? _delegatable;

    /// <summary>Clears per-transform memos (symbols differ per compilation; never keep them across transforms).</summary>
    public static void ResetMemo()
    {
        _rawStructs?.Clear();
        _delegatable?.Clear();
    }

    /// <summary>
    /// Whether a class's formatter emits static <c>WriteMembers</c>/<c>ReadMembers</c> that subtypes can call: a
    /// <c>[NinoType]</c> class generated in this assembly whose members are all directly readable and assignable.
    /// Computed from symbols only, so a subtype's transform reaches the same answer as the base's own.
    /// </summary>
    public static bool IsDelegatable(INamedTypeSymbol type, IAssemblySymbol assembly)
    {
        if (type.TypeKind != TypeKind.Class || type.IsStatic)
            return false;
        var memo = _delegatable ??= new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
        if (memo.TryGetValue(type, out bool known))
            return known;
        bool result = ComputeIsDelegatable(type, assembly);
        memo[type] = result;
        return result;
    }

    private static bool ComputeIsDelegatable(INamedTypeSymbol type, IAssemblySymbol assembly)
    {
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly) ||
            !IsEligible(type.OriginalDefinition))
            return false;
        var attr = type.GetNinoTypeAttribute();
        if (attr == null)
            return false;

        bool Visible(ISymbol? s) => s == null || s.DeclaredAccessibility == Accessibility.Public ||
                                    (s.DeclaredAccessibility is Accessibility.Internal or Accessibility.ProtectedOrInternal &&
                                     SymbolEqualityComparer.Default.Equals(s.ContainingAssembly, assembly));

        foreach (var member in CollectMembers(type, attr))
        {
            switch (member)
            {
                case IFieldSymbol field:
                    if (field.IsReadOnly || !Visible(field))
                        return false;
                    break;
                case IPropertySymbol property:
                    if (property.GetMethod == null || property.SetMethod == null || property.SetMethod.IsInitOnly ||
                        !Visible(property) || !Visible(property.GetMethod) || !Visible(property.SetMethod))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a <c>[NinoType]</c> struct is written as its raw memory: unmanaged, not version tolerant, and every
    /// instance field is serialized (nested <c>[NinoType]</c> structs must qualify too). Such structs are copied as
    /// one block, and arrays/lists of them with a single memory copy.
    /// </summary>
    public static bool IsRawStruct(INamedTypeSymbol type, IAssemblySymbol? weakAssembly)
    {
        if (!type.IsValueType || type.TypeKind != TypeKind.Struct)
            return false;
        var memo = _rawStructs ??= new Dictionary<ISymbol, bool>(SymbolEqualityComparer.Default);
        if (memo.TryGetValue(type, out bool known))
            return known;
        bool raw = ComputeIsRawStruct(type, weakAssembly);
        memo[type] = raw;
        return raw;
    }

    private static bool ComputeIsRawStruct(INamedTypeSymbol type, IAssemblySymbol? weakAssembly)
    {
        if (!type.IsValueType || !type.IsUnmanagedType || type.TypeKind != TypeKind.Struct)
            return false;
        // WEAK_VERSION_TOLERANCE only applies to the types of the assembly that defines it.
        if (weakAssembly != null && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, weakAssembly))
            return false;
        var attr = type.GetNinoTypeAttribute();
        if (attr == null || attr.versionTolerant)
            return false;
        var layout = GetAttribute(type, "StructLayoutAttribute");
        if (layout != null && layout.ConstructorArguments.Length > 0 &&
            layout.ConstructorArguments[0].Value is int kind && kind != 0 /* Sequential */)
            return false;

        var members = new HashSet<ISymbol>(CollectMembers(type.OriginalDefinition, attr),
            SymbolEqualityComparer.Default);
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst)
                continue;
            var member = field.AssociatedSymbol ?? field;
            if (!members.Contains(member.OriginalDefinition))
                return false;
            if (HasAttribute(member, "NinoCustomFormatterAttribute"))
                return false;
            if (field.Type is INamedTypeSymbol nested && nested.IsNinoType() && !IsRawStruct(nested, weakAssembly))
                return false;
        }

        return true;
    }

    public static bool IsBuiltInFormatter(string formatter) =>
        (formatter.StartsWith(Core) && !formatter.StartsWith(Core + "CachedFormatter<") &&
         !formatter.StartsWith(Core + "CustomFormatter<")) ||
        formatter.StartsWith("global::NinoGen.NinoImmutable") ||
        formatter.StartsWith("global::NinoGen.NinoPriorityQueueFormatter<");

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

    /// <summary>The access helper names used for a member's partial accessor.</summary>
    public static string AccessorClass(TypeModel model) => "__NinoAccess_" + model.FormatterName;

    private static IEnumerable<ISymbol> CollectMembers(INamedTypeSymbol type, NinoTypeAttribute attr)
    {
        bool autoCollect = attr.autoCollect;
        bool containNonPublic = attr.containNonPublicMembers;

        var explicitOrder = GetAttribute(type, "NinoExplicitOrderAttribute");
        var order = new Dictionary<string, int>();
        if (explicitOrder != null && explicitOrder.ConstructorArguments.Length > 0)
        {
            var values = explicitOrder.ConstructorArguments[0].Values;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].Value is string s)
                    order[s] = i;
            }
        }

        var levels = new List<INamedTypeSymbol>();
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        void AddLevel(INamedTypeSymbol t)
        {
            if (!visited.Add(t))
                return;
            // Base types first (root to leaf), so members appended to a derived type never shift base members.
            if (t.BaseType != null && t.BaseType.IsNinoType())
                AddLevel(t.BaseType);
            foreach (var i in t.AllInterfaces)
            {
                if (i.IsNinoType())
                    AddLevel(i);
            }

            levels.Add(t);
        }

        AddLevel(type);

        var inserted = new HashSet<string>();
        var result = new List<ISymbol>();
        foreach (var level in levels)
        {
            var positional = PositionalParameters(level);
            var candidates = new List<Candidate>();
            int declarationIndex = 0;
            foreach (var member in level.GetMembers())
            {
                if (!IsCandidate(member, containNonPublic, positional))
                    continue;

                var attrs = member.GetAttributes();
                if (attrs.Any(a => a.AttributeClass?.Name == "NinoPrivateProxyAttribute"))
                    continue;
                if (autoCollect && attrs.Any(a => a.AttributeClass?.Name == "NinoIgnoreAttribute"))
                    continue;

                // Private members of a base type cannot be reached from the derived type (protected can).
                if (!SymbolEqualityComparer.Default.Equals(member.ContainingType, type) &&
                    member.DeclaredAccessibility is Accessibility.Private)
                    continue;

                int index;
                if (autoCollect)
                {
                    index = declarationIndex++;
                }
                else
                {
                    var ninoMember = attrs.FirstOrDefault(a => a.AttributeClass?.Name == "NinoMemberAttribute");
                    if (ninoMember == null || ninoMember.ConstructorArguments.Length == 0 ||
                        ninoMember.ConstructorArguments[0].Value == null)
                        continue;
                    index = Convert.ToInt32(ninoMember.ConstructorArguments[0].Value);
                }

                candidates.Add(new Candidate { Symbol = member, Name = member.Name, Index = index });
            }

            var sorted = candidates
                .OrderBy(c => order.Count > 0 && order.TryGetValue(c.Name, out var o) ? o : int.MaxValue)
                .ThenBy(c => c.Index)
                .ThenBy(c => c.Name, StringComparer.Ordinal);

            foreach (var c in sorted)
            {
                if (!inserted.Add(c.Name))
                    continue;
                // Interface members are serialized through the implementing type's member of the same name.
                result.Add(level.TypeKind == TypeKind.Interface ? FindImplementation(type, c.Symbol) : c.Symbol);
            }
        }

        return result;
    }

    private static ISymbol FindImplementation(INamedTypeSymbol type, ISymbol member)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var found = t.GetMembers(member.Name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol);
            if (found != null)
                return found;
        }

        return member;
    }

    private static HashSet<string> PositionalParameters(INamedTypeSymbol type)
    {
        var result = new HashSet<string>();
        if (!type.IsRecord)
            return result;
        foreach (var reference in type.DeclaringSyntaxReferences)
        {
            if (reference.GetSyntax() is RecordDeclarationSyntax { ParameterList: { } list })
            {
                foreach (var p in list.Parameters)
                    result.Add(p.Identifier.ValueText);
            }
        }

        return result;
    }

    private static bool IsCandidate(ISymbol member, bool containNonPublic, HashSet<string> positional)
    {
        if (member.IsStatic)
            return false;
        bool isPublic = member.DeclaredAccessibility == Accessibility.Public;
        switch (member)
        {
            case IFieldSymbol field:
                return !field.IsImplicitlyDeclared && !field.IsConst && !field.IsFixedSizeBuffer &&
                       (containNonPublic || isPublic);
            case IPropertySymbol property:
                if (property.IsIndexer || property.GetMethod == null || property.SetMethod == null)
                    return false;
                if (property.IsImplicitlyDeclared && !positional.Contains(property.Name))
                    return false;
                return containNonPublic || isPublic;
            default:
                return false;
        }
    }

    private static MemberModel? BuildMember(INamedTypeSymbol type, ISymbol symbol, FormatterResolver resolver,
        bool net8, IAssemblySymbol? weakAssembly, List<DiagnosticInfo> diagnostics, ref bool needsPartialAccessor)
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
            diagnostics.Add(new DiagnosticInfo("NINO011", "Member type cannot be serialized",
                "Member '{0}' of type '{1}' in NinoType '{2}' has an unrecognizable type and will be skipped during serialization/deserialization",
                DiagnosticSeverity.Warning, LocationInfo.From(symbol.Locations.FirstOrDefault()), symbol.Name, memberType.ToDisplayString(), type.ToDisplayString()));
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
        else if (net8 && !declaring.IsGenericType && !type.IsGenericType)
            access = MemberAccess.UnsafeAccessor;
        else
        {
            access = MemberAccess.Partial;
            needsPartialAccessor = true;
        }

        string rawSize = custom == null && FormatterResolver.IsRawUnmanaged(memberType, weakAssembly)
            ? FormatterResolver.RawSize(memberType)
            : "";

        return new MemberModel(symbol.Name, TypeNames.Global(memberType), formatter, rawSize, isProperty, access,
            TypeNames.Global(declaring), declaring.IsValueType, constructionOnly, false);
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

    private static ConstructionModel BuildConstruction(INamedTypeSymbol type, List<MemberModel> members,
        List<DiagnosticInfo> diagnostics, LocationInfo? location)
    {
        string typeName = TypeNames.Global(type);
        var refFactory = type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m =>
            m.IsStatic && m.Parameters.Length == 0 && m.DeclaredAccessibility == Accessibility.Public &&
            SymbolEqualityComparer.Default.Equals(m.ReturnType, type) &&
            HasAttribute(m, "NinoRefDeserializationAttribute"));
        string refFactoryName = refFactory == null ? "" : $"{typeName}.{refFactory.Name}";

        var methods = type.InstanceConstructors.Cast<IMethodSymbol>()
            .Concat(type.GetMembers().OfType<IMethodSymbol>().Where(m =>
                m.IsStatic && m.MethodKind == MethodKind.Ordinary &&
                SymbolEqualityComparer.Default.Equals(m.ReturnType, type)))
            .ToList();
        var marked = methods.FirstOrDefault(m => HasAttribute(m, "NinoConstructorAttribute"));
        bool anyConstructionOnly = members.Any(m => m.ConstructionOnly);

        bool Visible(IMethodSymbol m) => m.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal
            or Accessibility.ProtectedOrInternal;

        var parameterless = type.InstanceConstructors.FirstOrDefault(c => c.Parameters.Length == 0 && Visible(c));
        bool hasParameterless = type.IsValueType || parameterless != null;

        if (marked == null && !anyConstructionOnly && hasParameterless)
            return new ConstructionModel(false, $"new {typeName}", EquatableArray<string>.Empty, refFactoryName);

        string Creator(IMethodSymbol m) => m.MethodKind == MethodKind.Constructor
            ? $"new {typeName}"
            : $"{typeName}.{m.Name}";

        var memberNames = members.Select(m => m.Name).ToList();

        string? MatchMember(string name) =>
            memberNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

        if (marked != null)
        {
            var attr = GetAttribute(marked, "NinoConstructorAttribute")!;
            var names = attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Kind == TypedConstantKind.Array &&
                        attr.ConstructorArguments[0].Values.Length > 0
                ? attr.ConstructorArguments[0].Values.Select(v => v.Value as string ?? "").ToList()
                : marked.Parameters.Select(p => p.Name).ToList();
            var args = new List<string>();
            foreach (var name in names)
            {
                var match = MatchMember(name);
                if (match == null)
                {
                    diagnostics.Add(new DiagnosticInfo("NINO001", "Missing constructor member",
                        "Missing constructor member {0} for {1}",
                        DiagnosticSeverity.Error, LocationInfo.From(marked.Locations.FirstOrDefault()) ?? location,
                        name, type.ToDisplayString()));
                    return new ConstructionModel(true, "", EquatableArray<string>.Empty, "");
                }

                args.Add(match);
            }

            return new ConstructionModel(true, Creator(marked), new EquatableArray<string>(args), "");
        }

        // Pick the visible constructor with the most parameters that all map to serialized members.
        IMethodSymbol? best = null;
        List<string>? bestArgs = null;
        foreach (var ctor in type.InstanceConstructors.Where(Visible))
        {
            if (ctor.Parameters.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, type))
                continue; // record copy constructor
            var args = new List<string>();
            bool ok = true;
            foreach (var p in ctor.Parameters)
            {
                var match = MatchMember(p.Name);
                if (match == null)
                {
                    ok = false;
                    break;
                }

                args.Add(match);
            }

            if (ok && (best == null || args.Count > bestArgs!.Count))
            {
                best = ctor;
                bestArgs = args;
            }
        }

        if (best == null)
        {
            if (type.IsValueType)
                return new ConstructionModel(true, $"new {typeName}", EquatableArray<string>.Empty, "");
            diagnostics.Add(new DiagnosticInfo("NINO001", "No usable constructor",
                "'{0}' has no accessible parameterless constructor and no constructor whose parameters all match serialized members; mark one with [NinoConstructor]",
                DiagnosticSeverity.Error, location, type.ToDisplayString()));
            return new ConstructionModel(true, "", EquatableArray<string>.Empty, "");
        }

        return new ConstructionModel(true, Creator(best), new EquatableArray<string>(bestArgs!), "");
    }
}
