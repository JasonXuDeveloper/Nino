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
internal static partial class ModelBuilder
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
        bool generic = type.IsGenericType;
        var typeParameters = generic ? AllTypeParameters(type) : null;
        var (typeId, nameId) = generic ? (0, 0) : facts.Ids(type);

        var built = BuildMembers(type, shape, facts, diagnostics, ct);
        var members = built.Models;

        var construction = kind == FormatterKind.Polymorphic || rawStruct
            ? new ConstructionModel(false, "", EquatableArray<string>.Empty, "")
            : ConstructionPlanner.Plan(type, members, facts, diagnostics);

        var collection = kind is FormatterKind.Class or FormatterKind.SealedClass && !construction.Recreate
            ? shape.Collection
            : null;
        if (collection is { } c && c.Factories.Length != 0)
            built.AddFactories(c.Factories);

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
        int prefetchBytes = diagnostics == null ? PrefetchRules.Bytes(type, kind, generic, fixedSize) : 0;
        string typeName = Global(type);
        return new TypeModel(
            TypeName: typeName,
            OpenTypeName: generic ? OpenName(type) : typeName,
            FormatterName: FormatterName(type),
            TypeParameters: typeParameters == null ? EquatableArray<string>.Empty
                : new EquatableArray<string>(typeParameters.Select(static p => p.Name)),
            Constraints: typeParameters == null ? "" : Constraints(typeParameters),
            Kind: kind,
            TypeId: typeId,
            NameId: nameId,
            Format: shape.Format,
            TrackReferences: trackReferences,
            RawStruct: rawStruct,
            Collection: collection is { } sc
                ? new CollectionModel(sc.Code, facts.ShallowFormatter(sc.Code, sc, false), sc.Encoding, sc.OpensBodies)
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
            Registrations: BuildRegistrations(type, kind, generic, built, collection?.Code, facts,
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
            PrefetchBytes: prefetchBytes,
            PrefetchMembers: prefetchBytes != 0
                ? PrefetchRules.Members(built.Models, built.Symbols, ct)
                : EquatableArray<string>.Empty,
            Callbacks: Callbacks.Build(type, kind, rawStruct, construction, facts, diagnostics));
    }

    /// <summary>
    /// The serialized members of a type (<see cref="Models"/>, with their symbols in <see cref="Symbols"/>), and how
    /// many of the first a base formatter's <c>WriteMembers</c>/<c>ReadMembers</c> cover.
    /// </summary>
    private sealed class MemberSet
    {
        public readonly List<MemberModel> Models = new();
        public readonly List<ISymbol> Symbols = new();
        public List<FactoryRegistration>? Factories;

        public void AddFactories(EquatableArray<FactoryRegistration> additional)
        {
            foreach (var factory in additional)
                (Factories ??= new List<FactoryRegistration>()).Add(factory);
        }

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
        DiagnosticSink? diagnostics, CancellationToken ct)
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

        if (BuildsMembers(type, shape, facts))
        {
            var symbols = facts.Members.Of(type, attr);
            int basePrefix = BasePrefix(type, attr, symbols, facts, ref set.BaseFormatter);

            // What depends on the type writing the member (NINO022) is reported by each concrete type; abstract
            // types leave it to their subtypes.
            var memberDiagnostics = kind == FormatterKind.Polymorphic ? null : diagnostics;
            for (int i = 0; i < symbols.Count; i++)
            {
                // A member's own problems (its type: NINO007, NINO029) are reported once, by the type declaring it.
                var own = diagnostics != null && ReportsOwnProblems(type, symbols[i].ContainingType, facts)
                    ? diagnostics
                    : null;
                var member = BuildMember(type, attr.Keyed, symbols, i, facts, memberDiagnostics, own, set, ct);
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
        // One that has only Vector<T> members is NINO029's alone.
        if (kind == FormatterKind.Struct && !shape.RawStruct && !shape.Tolerant && !attr.Keyed && members.Count == 0 &&
            diagnostics != null && facts.Members.Of(type, attr).All(m =>
                MemberCollector.MemberType(m) is not { } t || facts.Raw.HostSizedVectorIn(t) == null))
            diagnostics.Report(NinoDiagnostics.EmptyStruct, type, type.ToDisplayString());

        if (diagnostics != null)
            ReportNames(type, attr.Keyed, members, set.Symbols, facts.Symbols, diagnostics);
        return set;
    }

    /// <summary>
    /// Whether the model of <paramref name="type"/> builds its members: abstract classes only for their subtypes to
    /// delegate to (<see cref="TypeFacts.IsDelegatable"/>), and raw structs never (they are copied whole).
    /// </summary>
    private static bool BuildsMembers(INamedTypeSymbol type, TypeShape shape, TypeFacts facts) =>
        (shape.Kind != FormatterKind.Polymorphic || facts.IsDelegatable(type)) && !shape.RawStruct;

    /// <summary>
    /// Whether building <paramref name="type"/> reports the own problems of a member <paramref name="declaring"/>
    /// declares: when it declares it, or when the declaring type is not built here (a base from another assembly, a
    /// closed generic base such as <c>Base&lt;Action&gt;</c>, an abstract base its subtypes do not delegate to), so
    /// each type writing the member reports it. Otherwise the declaring type's build reports it, once.
    /// </summary>
    private static bool ReportsOwnProblems(INamedTypeSymbol type, INamedTypeSymbol declaring, TypeFacts facts) =>
        SymbolEqualityComparer.Default.Equals(declaring, type) ||
        !(SymbolEqualityComparer.Default.Equals(declaring, declaring.OriginalDefinition) &&
          facts.Visibility.IsGeneratedHere(declaring) && facts.Shape(declaring) is { } shape &&
          BuildsMembers(declaring, shape, facts));

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
    /// creators its members use, and, when <paramref name="namesTypes"/>, the closed generic <c>[NinoType]</c>s, the
    /// measured structs and the nesting formatters (its members' and <paramref name="collection"/>'s) it names.
    /// </summary>
    private static RegistrationModel BuildRegistrations(INamedTypeSymbol type, FormatterKind kind, bool generic,
        MemberSet members, FormatterRef? collection, TypeFacts facts,
        bool namesTypes)
    {
        List<FormatterRegistration>? used = null;
        if (!generic && kind != FormatterKind.Polymorphic)
        {
            foreach (var m in members.Models)
            {
                if (m.TypeId != 0)
                    (used ??= new List<FormatterRegistration>()).Add(
                        new FormatterRegistration(m.TypeName, m.Formatter, m.TypeId));
            }
        }

        // Closed generic [NinoType]s named by the members and base types, and the structs whose layout the runtime
        // measures (LayoutTypes).
        List<TypeRegistration>? genericTypes = null;
        List<string>? layoutTypes = null;
        if (namesTypes)
        {
            genericTypes = new List<TypeRegistration>();
            layoutTypes = new List<string>();
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
            Formatters: used == null ? EquatableArray<FormatterRegistration>.Empty
                : new EquatableArray<FormatterRegistration>(used),
            // Generic types name no closed collection types; their formatters resolve creators at runtime.
            Factories: generic || kind == FormatterKind.Polymorphic || members.Factories == null
                ? EquatableArray<FactoryRegistration>.Empty
                : new EquatableArray<FactoryRegistration>(members.Factories.Distinct()
                    .OrderBy(f => f.TypeName, System.StringComparer.Ordinal)),
            GenericTypes: genericTypes is { Count: > 0 }
                ? new EquatableArray<TypeRegistration>(genericTypes.Distinct()
                    .OrderBy(static g => g.TypeName, System.StringComparer.Ordinal))
                : EquatableArray<TypeRegistration>.Empty,
            LayoutTypes: layoutTypes == null ? EquatableArray<string>.Empty : LayoutTypes.ToArray(layoutTypes),
            Nesting: genericTypes != null && facts.Resolver.HasNestingFormatters
                ? facts.Resolver.NestingFormattersNamedIn(NestingSources(members.Models, genericTypes, collection))
                : EquatableArray<NestingModel>.Empty);
    }

    private static IEnumerable<string> NestingSources(List<MemberModel> members, List<TypeRegistration> genericTypes,
        FormatterRef? collection)
    {
        foreach (var member in members)
            yield return member.Formatter.ToString();
        foreach (var generic in genericTypes)
            yield return generic.Formatter;
        if (collection != null)
            yield return collection.ToString();
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
            baseAttr.Format != attr.Format || SharedInstance(baseType))
            return 0;
        var baseSymbols = facts.Members.Of(baseType, baseAttr);
        if (baseSymbols.Count > symbols.Count)
            return 0;
        for (int i = 0; i < baseSymbols.Count; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(baseSymbols[i], symbols[i]))
                return 0;
        }

        baseFormatter = facts.Resolver.GeneratedFormatter(baseType).ToString();
        return baseSymbols.Count;
    }

    /// <summary>
    /// Whether a closed generic base runs the runtime's shared code: a type argument (of it or of a containing type)
    /// is a reference type, or a generic struct over one, such as <c>Box&lt;(Item, int)&gt;</c>. Its
    /// <c>WriteMembers</c>/<c>ReadMembers</c> are then the runtime's shared code, which looks up what depends on
    /// <c>T</c> in its generic context, so a subtype that is not generic itself writes and reads those members with
    /// their concrete types.
    /// A base over a subtype's own type parameters is shared either way, and stays reused.
    /// </summary>
    private static bool SharedInstance(INamedTypeSymbol type)
    {
        bool shared = false;
        for (var t = type; t != null; t = t.ContainingType)
        {
            foreach (var argument in t.TypeArguments)
            {
                if (ContainsTypeParameter(argument))
                    return false;
                shared |= Shares(argument);
            }
        }

        return shared;

        static bool Shares(ITypeSymbol t) =>
            t.IsReferenceType || t is INamedTypeSymbol { IsGenericType: true } n && n.TypeArguments.Any(Shares);
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
}
