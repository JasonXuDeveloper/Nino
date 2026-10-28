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

/// <summary>Turns a <c>[NinoType]</c> symbol into an equatable <see cref="TypeModel"/>.</summary>
internal static class ModelBuilder
{
    /// <param name="ct">Cancels the build (its syntax lookups).</param>
    /// <param name="diagnostics">Receives problems found on the way (the analyzer's); null when generating.</param>
    public static TypeModel? Build(INamedTypeSymbol type, TypeFacts facts, CancellationToken ct,
        DiagnosticSink? diagnostics = null)
    {
        type = type.OriginalDefinition;
        if (!Visibility.IsEligible(type))
            return null;
        if (facts.Shape(type) is not { } shape)
            return null;
        var attr = shape.Options;
        var kind = shape.Kind;
        bool rawStruct = shape.RawStruct;
        var typeParameters = AllTypeParameters(type);
        bool generic = typeParameters.Count != 0;
        var (typeId, nameId) = generic ? (0, 0) : facts.Ids(type);

        var factories = new List<FactoryRegistration>();
        var built = BuildMembers(type, shape, facts, diagnostics, factories, ct);
        var members = built.Models;

        var construction = kind == FormatterKind.Polymorphic || rawStruct
            ? new ConstructionModel(false, "", EquatableArray<string>.Empty, "")
            : ConstructionPlanner.Plan(type, members, facts, diagnostics);

        var collection = kind is FormatterKind.Class or FormatterKind.SealedClass && !construction.Recreate
            ? shape.Collection
            : null;
        if (collection is { } c)
            factories.AddRange(c.Factories);

        if (construction.Recreate)
            MarkUnassignable(type, built, construction, diagnostics);

        // Tracking is inherited: a tracked base's reference header is resolved by the subtype's body as soon as
        // the instance exists, so every subtype of a tracked type must record itself.
        bool trackReferences = shape.TrackReferences;
        if (shape.Tracked && kind == FormatterKind.Struct)
            diagnostics?.Report(NinoDiagnostics.TrackReferencesOnStruct, type, type.ToDisplayString());
        else if (trackReferences && construction.Recreate)
            diagnostics?.Report(NinoDiagnostics.TrackReferencesNeedsAssignableMembers, type, type.ToDisplayString());

        // The nesting bound and the __Shallow twin (non-generic: a twin of a generic formatter is never named).
        int bound = kind == FormatterKind.Polymorphic || generic ? TypeFacts.Unbounded : facts.TypeBound(type);
        bool shallow = !generic && kind is FormatterKind.SealedClass or FormatterKind.Struct && facts.HasShallow(type);

        var (pooled, partial) = BuildPartial(type, kind, built.NeedsPartialAccessor, construction, facts, diagnostics,
            ct);

        bool instantiable = kind is FormatterKind.Class or FormatterKind.SealedClass;
        bool fixedSize = !rawStruct && facts.IsFixedSize(type);
        return new TypeModel(
            TypeName: Global(type),
            OpenTypeName: generic ? OpenName(type) : Global(type),
            FormatterName: FormatterName(type),
            TypeParameters: new EquatableArray<string>(typeParameters.Select(p => p.Name)),
            Constraints: Constraints(typeParameters),
            Kind: kind,
            TypeId: typeId,
            NameId: nameId,
            VersionTolerant: shape.Tolerant,
            Keyed: attr.Keyed,
            TrackReferences: trackReferences,
            RawStruct: rawStruct,
            Collection: collection is { } sc
                ? new CollectionModel(sc.Code, facts.ShallowFormatter(sc.Code, sc, false), sc.Encoding)
                : null,
            Members: new EquatableArray<MemberModel>(members),
            Construction: construction,
            Inheritance: BuildDispatch(type, generic, trackReferences, built, facts),
            Depth: new DepthModel(
                Deferrable: instantiable && !construction.Recreate &&
                            (construction.Factory.Length != 0 || construction.Creator.Length != 0) &&
                            !ComparesByValue(type),
                Nests: shape.Nests,
                Bound: bound,
                Shallow: shallow),
            Pooled: pooled,
            Partial: partial,
            // The analyzer needs none of the types the registry names.
            Registrations: BuildRegistrations(type, kind, generic, built, factories, facts,
                namesTypes: diagnostics == null),
            FixedSize: fixedSize,
            Block: !rawStruct && facts.IsBlock(type),
            FreshRead: kind == FormatterKind.SealedClass && !construction.Recreate &&
                       construction.Factory.Length == 0 && construction.Creator.Length != 0 && collection == null &&
                       !attr.Keyed && members.Skip(built.BaseMemberCount).Any(static m => m.Fresh && !m.IsString) &&
                       !members.Skip(built.BaseMemberCount).Any(static m => m.ReadsLoop) &&
                       ConstructsDefaults(type, ct),
            PublishesDeferrable: facts.Symbols.LayoutDeferrable,
            RawLayout: rawStruct ? RawLayout.Describe(type) : "",
            PrefetchThreshold: PrefetchRules.Threshold(type, kind, generic, fixedSize),
            PrefetchMembers: PrefetchRules.Members(built.Models, built.Symbols));
    }

    /// <summary>
    /// The serialized members of a type (<see cref="Models"/>, with their symbols in <see cref="Symbols"/>), and how
    /// many of the first a base formatter's <c>WriteMembers</c>/<c>ReadMembers</c> cover.
    /// </summary>
    private sealed class MemberSet
    {
        public readonly List<MemberModel> Models = new();
        public readonly List<ISymbol> Symbols = new();

        /// <summary>Whether a member is reached through an accessor class generated in the type's partial.</summary>
        public bool NeedsPartialAccessor;

        /// <summary>Whether the type emits <c>WriteMembers</c>/<c>ReadMembers</c> (<see cref="InheritanceModel.EmitMembers"/>).</summary>
        public bool EmitMembers;

        public string BaseFormatter = "";
        public int BaseMemberCount;
    }

    /// <summary>
    /// The members of <paramref name="type"/> and the base formatter that writes the first of them; reports the
    /// problems of each member, an empty positional struct and colliding or misplaced names.
    /// </summary>
    private static MemberSet BuildMembers(INamedTypeSymbol type, TypeShape shape, TypeFacts facts,
        DiagnosticSink? diagnostics, List<FactoryRegistration> factories, CancellationToken ct)
    {
        var attr = shape.Options;
        var kind = shape.Kind;
        var set = new MemberSet
        {
            // Abstract classes have no body of their own, but emit WriteMembers/ReadMembers for their subtypes.
            // Keyed bodies are self-describing per member: no delegation to or from them.
            EmitMembers = facts.IsDelegatable(type),
        };
        var members = set.Models;

        if ((kind != FormatterKind.Polymorphic || set.EmitMembers) && !shape.RawStruct)
        {
            var symbols = facts.Members.Of(type, attr);
            int basePrefix = BasePrefix(type, attr, symbols, facts, ref set.BaseFormatter);

            // Abstract types do not report member diagnostics: their subtypes do.
            var memberDiagnostics = kind == FormatterKind.Polymorphic ? null : diagnostics;
            for (int i = 0; i < symbols.Count; i++)
            {
                var member = BuildMember(type, attr.Keyed, symbols, i, facts, memberDiagnostics,
                    ref set.NeedsPartialAccessor, factories, ct);
                if (member == null)
                    continue;
                members.Add(member);
                set.Symbols.Add(symbols[i]);
                if (i < basePrefix)
                    set.BaseMemberCount++;
            }

            // Raw members on both sides of the boundary would be written as one block inline; delegating would
            // split it into two reservations (measurably slower for small types), so keep such members inline.
            if (set.BaseMemberCount > 0 && set.BaseMemberCount < members.Count &&
                members[set.BaseMemberCount - 1].Raw != null && members[set.BaseMemberCount].Raw != null)
            {
                set.BaseFormatter = "";
                set.BaseMemberCount = 0;
            }
        }

        // A positional struct without members is written as nothing at all: a collection of it could not be read back,
        // since its count is checked against the bytes left (every element takes at least one byte).
        if (kind == FormatterKind.Struct && !shape.RawStruct && !shape.Tolerant && !attr.Keyed && members.Count == 0)
            diagnostics?.Report(NinoDiagnostics.EmptyStruct, type, type.ToDisplayString());

        if (diagnostics != null)
            ReportNames(type, attr.Keyed, members, set.Symbols, facts.Symbols, diagnostics);
        return set;
    }

    /// <summary>
    /// Read-only members that are not constructor arguments of a recreated type cannot be assigned (init-only
    /// properties can, through the object initializer, when they are accessible): their values are read and dropped.
    /// </summary>
    private static void MarkUnassignable(INamedTypeSymbol type, MemberSet members, ConstructionModel construction,
        DiagnosticSink? diagnostics)
    {
        var args = new HashSet<string>(construction.Arguments);
        for (int i = 0; i < members.Models.Count; i++)
        {
            var m = members.Models[i];
            if (args.Contains(m.Name) || !m.ConstructionOnly ||
                (m.Access == MemberAccess.Direct && members.Symbols[i] is IPropertySymbol { SetMethod: not null }))
                continue;
            members.Models[i] = m with { Unassignable = true };
            diagnostics?.Report(NinoDiagnostics.ReadOnlyMember, members.Symbols[i], m.Name, type.ToDisplayString());
        }
    }

    /// <summary>
    /// Whether the type is pooled (<c>[NinoPooled]</c> on a sealed class that is not recreated, declared partial),
    /// and the generated partial it needs for that and for its accessors.
    /// </summary>
    private static (bool Pooled, PartialModel Partial) BuildPartial(INamedTypeSymbol type, FormatterKind kind,
        bool needsPartialAccessor, ConstructionModel construction, TypeFacts facts, DiagnosticSink? diagnostics,
        CancellationToken ct)
    {
        bool pooled = NinoSymbols.Has(type, facts.Symbols.Pooled);
        if (pooled && (kind != FormatterKind.SealedClass || construction.Recreate))
        {
            pooled = false;
            diagnostics?.Report(NinoDiagnostics.InvalidPooledType, type, type.ToDisplayString());
        }

        if (!needsPartialAccessor && !pooled)
            return (pooled, PartialModel.None);
        if (!IsPartialChain(type, ct))
        {
            diagnostics?.Report(NinoDiagnostics.NotPartial, type, type.ToDisplayString());
            return (false, PartialModel.None);
        }

        var declarations = new List<string>();
        for (var t = type; t != null; t = t.ContainingType)
            declarations.Insert(0, Declaration(t));
        return (pooled, new PartialModel(
            type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : "",
            new EquatableArray<string>(declarations)));
    }

    /// <summary>
    /// What the type shares with its hierarchy: the base formatter that writes its first members, and the
    /// <c>[NinoType]</c> ancestors and dispatch root of the subtype dispatch.
    /// </summary>
    private static InheritanceModel BuildDispatch(INamedTypeSymbol type, bool generic, bool trackReferences,
        MemberSet members, TypeFacts facts)
    {
        var ancestors = type.NinoAncestors().ToList();
        var dispatchRoot = type.TypeKind == TypeKind.Class && !generic && !trackReferences
            ? DispatchRoot(type, ancestors, facts)
            : null;
        return new InheritanceModel(
            EmitMembers: members.EmitMembers,
            BaseFormatter: members.BaseFormatter,
            BaseMemberCount: members.BaseMemberCount,
            SharedBase: type.TypeKind == TypeKind.Class && type.BaseType is { } baseType &&
                        facts.IsDelegatable(baseType) && SharedInstance(baseType),
            DispatchRoot: dispatchRoot == null ? "" : Global(dispatchRoot),
            DispatchRootFormatter: dispatchRoot == null ? "" : FormatterName(dispatchRoot),
            NinoBaseClasses: new EquatableArray<string>(ancestors
                .Where(static a => a.TypeKind != TypeKind.Interface).Select(Global)),
            NinoInterfaces: new EquatableArray<string>(ancestors
                .Where(static a => a.TypeKind == TypeKind.Interface).Select(Global)),
            NinoGenericAncestors: new EquatableArray<GenericAncestor>(ancestors
                .Where(a => !a.IsDefinition && facts.Visibility.CanName(a))
                .Select(a => new GenericAncestor(Global(a), Global(a.OriginalDefinition)))));
    }

    /// <summary>
    /// What the assembly registers for the type (<see cref="RegistrationModel"/>): the built-in formatters and
    /// creators its members use, and, when <paramref name="namesTypes"/>, the closed generic <c>[NinoType]</c>s and
    /// the measured structs it names.
    /// </summary>
    private static RegistrationModel BuildRegistrations(INamedTypeSymbol type, FormatterKind kind, bool generic,
        MemberSet members, List<FactoryRegistration> factories, TypeFacts facts, bool namesTypes)
    {
        var used = new List<FormatterRegistration>();
        if (!generic && kind != FormatterKind.Polymorphic)
        {
            foreach (var m in members.Models)
            {
                if (m.TypeId != 0)
                    used.Add(new FormatterRegistration(m.TypeName, m.Formatter, m.TypeId));
            }
        }

        // Closed generic [NinoType]s named by the members and base types, and the structs whose layout the runtime
        // measures (LayoutTypes).
        var genericTypes = new List<TypeRegistration>();
        var layoutTypes = new List<string>();
        if (namesTypes)
        {
            foreach (var symbol in members.Symbols)
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

        return new RegistrationModel(
            Formatters: new EquatableArray<FormatterRegistration>(used),
            // Generic types name no closed collection types; their formatters resolve creators at runtime.
            Factories: generic || kind == FormatterKind.Polymorphic
                ? EquatableArray<FactoryRegistration>.Empty
                : new EquatableArray<FactoryRegistration>(factories.Distinct()
                    .OrderBy(f => f.TypeName, System.StringComparer.Ordinal)),
            GenericTypes: new EquatableArray<TypeRegistration>(genericTypes.Distinct()
                .OrderBy(g => g.TypeName, System.StringComparer.Ordinal)),
            LayoutTypes: LayoutTypes.ToArray(layoutTypes));
    }

    /// <summary>
    /// How many of the first <paramref name="symbols"/> a base formatter's <c>WriteMembers</c>/<c>ReadMembers</c>
    /// write and read (that formatter in <paramref name="baseFormatter"/>), or 0: reused when the base's members are
    /// exactly the first members, in the same order, and in the same format. The bytes are identical, and each
    /// member is compiled once instead of once per subtype.
    /// </summary>
    private static int BasePrefix(INamedTypeSymbol type, NinoTypeOptions attr, MemberList symbols, TypeFacts facts,
        ref string baseFormatter)
    {
        if (type.TypeKind != TypeKind.Class || attr.Keyed || type.BaseType is not { } baseType ||
            !facts.IsDelegatable(baseType) || baseType.GetNinoTypeOptions() is not { } baseAttr ||
            facts.IsTolerant(baseAttr) != facts.IsTolerant(attr) || SharedInstance(baseType))
            return 0;
        var baseSymbols = facts.Members.Of(baseType, baseAttr);
        if (baseSymbols.Count > symbols.Count ||
            !baseSymbols.Select((x, i) => SymbolEqualityComparer.Default.Equals(x, symbols[i])).All(x => x))
            return 0;
        baseFormatter = FormatterResolver.GeneratedFormatter(baseType).ToString();
        return baseSymbols.Count;
    }

    /// <summary>
    /// Whether a closed generic base runs the runtime's shared code: a type argument (of it or of a containing type)
    /// is a reference type, or a generic struct over one, such as <c>Box&lt;(Item, int)&gt;</c>. Its
    /// <c>WriteMembers</c>/<c>ReadMembers</c> then look each <c>T</c> member's formatter up at run time and call it
    /// virtually, so a subtype that is not generic itself writes and reads those members with their concrete types.
    /// A base over a subtype's own type parameters is shared either way, and stays reused.
    /// </summary>
    private static bool SharedInstance(INamedTypeSymbol type)
    {
        var arguments = new List<ITypeSymbol>();
        for (var t = type; t != null; t = t.ContainingType)
            arguments.AddRange(t.TypeArguments);
        return !arguments.Any(ContainsTypeParameter) && arguments.Any(Shares);

        static bool Shares(ITypeSymbol t) =>
            t.IsReferenceType || t is INamedTypeSymbol { IsGenericType: true } n && n.TypeArguments.Any(Shares);

        static bool ContainsTypeParameter(ITypeSymbol t) => t switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol a => ContainsTypeParameter(a.ElementType),
            INamedTypeSymbol n => n.TypeArguments.Any(ContainsTypeParameter) ||
                                  n.ContainingType != null && ContainsTypeParameter(n.ContainingType),
            _ => false,
        };
    }

    /// <summary>
    /// Keyed: the keys of two members, or of a member's former name and another member, that collide. Elsewhere:
    /// former names, which mean nothing where member names are not written.
    /// </summary>
    private static void ReportNames(INamedTypeSymbol type, bool keyed, List<MemberModel> members,
        List<ISymbol> memberSymbols, NinoSymbols known, DiagnosticSink diagnostics)
    {
        if (!keyed)
        {
            foreach (var symbol in type.GetMembers())
            {
                if (symbol is IFieldSymbol or IPropertySymbol && NinoSymbols.Has(symbol, known.FormerName))
                    diagnostics.Report(NinoDiagnostics.FormerNameNotKeyed, symbol, symbol.Name, type.ToDisplayString());
            }

            return;
        }

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
            foreach (var (former, formerKey) in FormerKeys(memberSymbols[i], member.Name != member.SourceName, known))
            {
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

    /// <summary>
    /// The topmost base class of this assembly whose formatter dispatches subtypes (of <paramref name="ancestors"/>,
    /// nearest first), or null: this type's subtypes are dispatched there, so each subtype is listed once per
    /// hierarchy rather than once per ancestor.
    /// </summary>
    private static INamedTypeSymbol? DispatchRoot(INamedTypeSymbol type, List<INamedTypeSymbol> ancestors,
        TypeFacts facts)
    {
        INamedTypeSymbol? root = null;
        foreach (var b in ancestors)
        {
            if (b.TypeKind == TypeKind.Class && !b.IsGenericType && Visibility.IsEligible(b) &&
                SymbolEqualityComparer.Default.Equals(b.ContainingAssembly, type.ContainingAssembly) &&
                facts.Shape(b) is { } shape && TypeRules.DispatchesSubtypes(shape.Kind, shape.TrackReferences))
                root = b;
        }

        return root;
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
            if (HasMetadataName(i.OriginalDefinition, "System.IComparable") ||
                HasMetadataName(i.OriginalDefinition, "System.IComparable`1"))
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

    private static bool IsPartialChain(INamedTypeSymbol type, CancellationToken ct)
    {
        for (var t = type; t != null; t = t.ContainingType)
        {
            if (t.DeclaringSyntaxReferences.Length == 0)
                return false;
            foreach (var reference in t.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(ct) is TypeDeclarationSyntax syntax &&
                    !syntax.Modifiers.Any(SyntaxKind.PartialKeyword))
                    return false;
            }
        }

        return true;
    }

    private static MemberModel? BuildMember(INamedTypeSymbol type, bool keyed, MemberList symbols, int index,
        TypeFacts facts, DiagnosticSink? diagnostics, ref bool needsPartialAccessor,
        List<FactoryRegistration> factories, CancellationToken ct)
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
        FormatterTraits traits;
        var custom = NinoSymbols.Get(symbol, known.CustomFormatter);
        if (custom is { ConstructorArguments: { Length: > 0 } args } && args[0].Value is INamedTypeSymbol customType)
        {
            if (!known.IsCustomFormatterOf(customType, memberType))
                return null; // NINO008, reported by the analyzer
            formatter = FormatterRef.Of(customType);
            encoding = WireEncoding.Formatter(formatter.ToString());
            traits = FormatterTraitsOf.Of(customType);
        }
        else
        {
            bool utf8 = NinoSymbols.Has(symbol, known.Utf8);
            var resolved = facts.Resolver.Resolve(memberType, utf8);
            formatter = resolved?.Code;
            registered = resolved?.Registered == true;
            traits = resolved?.Traits ?? FormatterTraits.None;
            if (resolved is { } r)
            {
                encoding = r.Encoding;
                factories.AddRange(r.Factories);
                shallowFormatter = facts.ShallowFormatter(r.Code, r, utf8);
                readsLoop = r.Elements is { IsDefault: false, Length: > 0 } && (r.Traits & FormatterTraits.Flat) == 0;
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
        else if (UnsafeAccessorReaches(type, declaring, facts))
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
            ? RawLayout.SizeOf(memberType) with { Layout = RawLayout.Describe(memberType) }
            : null;

        // Only registered formatters: a member's custom formatter must not become the global one.
        int typeId = registered && raw == null ? facts.TypeId(memberType) : 0;
        string name = renamed == 0 ? symbol.Name : $"{symbol.Name}__{renamed}";
        // Keyed: a renamed member's key is its declaring type's name with its own.
        int key = ComputeId(KeyName(symbol.Name, renamed != 0, declaring));
        var formerKeys = keyed
            ? new EquatableArray<int>(FormerKeys(symbol, renamed != 0, known).Select(static f => f.Key)
                .Where(former => former != key).Distinct())
            : EquatableArray<int>.Empty;

        // Fresh: a Nino.Core formatter that reads into an existing value (not an object or cached one), or a string,
        // read without the value it replaces.
        const FormatterTraits replaces = FormatterTraits.Cached | FormatterTraits.Object;
        return new MemberModel(
            Name: name,
            SourceName: symbol.Name,
            Key: key,
            FormerKeys: formerKeys,
            TypeName: Global(memberType),
            Formatter: formatter,
            Traits: traits,
            Encoding: raw?.Layout ?? encoding,
            Raw: raw,
            IsProperty: symbol is IPropertySymbol,
            Access: access,
            DeclaringType: Global(declaring),
            DeclaringTypeIsValueType: declaring.IsValueType,
            ConstructionOnly: Visibility.IsConstructionOnly(symbol),
            // Decided with the construction (ModelBuilder.Build), once every member is known.
            Unassignable: false,
            TypeId: typeId,
            ShallowFormatter: shallowFormatter ?? formatter,
            Fresh: custom == null && memberType.IsReferenceType && (traits & FormatterTraits.Core) != 0 &&
                   (traits & replaces) == 0 && StartsDefault(symbol, ct),
            ReadsLoop: raw == null && readsLoop);
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
