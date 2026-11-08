using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Analysis;

/// <summary>
/// Every diagnostic Nino reports, shared by the analyzer, the model builder and the code fixes. Ids are grouped:
/// the type (001-003), how it is constructed (004-006, 021, 025), its members (007-013, 022-023, 028-031, 033), features (014-017, 024, 026-027, 032), pooling
/// (018-019), formatter registrations (020), the layout baseline (034-037).
/// </summary>
internal static class NinoDiagnostics
{
    private const string Category = "Nino";

    private const string HelpLink = "https://github.com/JasonXuDeveloper/Nino/blob/main/docs/diagnostics.md#";

    private static DiagnosticDescriptor Error(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + id.ToLowerInvariant());

    private static DiagnosticDescriptor Warning(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + id.ToLowerInvariant());

    private static DiagnosticDescriptor Info(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Info, true, helpLinkUri: HelpLink + id.ToLowerInvariant());

    // The type

    public static readonly DiagnosticDescriptor InaccessibleType = Error("NINO001",
        "NinoType must be accessible",
        "NinoType '{0}' cannot be serialized: it must be a non-static class, struct or interface, and it and every type containing it must be public or internal");

    public static readonly DiagnosticDescriptor NotPartial = Error("NINO002", "Type must be partial",
        "'{0}' serializes non-public members that generated code reaches through an accessor inside the type (before .NET 8, and for generic types before .NET 9), or is [NinoPooled], so it (and every type containing it) must be declared partial");

    public static readonly DiagnosticDescriptor SubtypeWithoutNinoType = Warning("NINO003",
        "Subtype of a NinoType without [NinoType]",
        "'{0}' derives from NinoType '{1}', whose [NinoType] does not apply to subtypes (inheritedBySubtypes: false), so writing a '{0}' throws; mark it [NinoType]");

    // Construction

    public static readonly DiagnosticDescriptor NoUsableConstructor = Error("NINO004", "No usable constructor",
        "'{0}' has no accessible parameterless constructor and no constructor whose parameters all match serialized members; mark one with [NinoConstructor]");

    public static readonly DiagnosticDescriptor MissingConstructorMember = Error("NINO005",
        "Constructor parameter matches no member",
        "[NinoConstructor] parameter '{0}' of '{1}' matches no serialized member, so '{1}' cannot be deserialized; name it after a member, or list the members in [NinoConstructor(...)]");

    public static readonly DiagnosticDescriptor InaccessibleConstructor = Error("NINO021",
        "[NinoConstructor] is not accessible",
        "[NinoConstructor] '{0}' of '{1}' cannot be called by generated code; it must be public or internal");

    public static readonly DiagnosticDescriptor FactoryIgnored = Warning("NINO025",
        "[NinoFactory] ignored because of [NinoConstructor]",
        "[NinoFactory] '{0}' of '{1}' is never called: instances are always created through [NinoConstructor] '{2}'; remove one of the two attributes");

    public static readonly DiagnosticDescriptor ReadOnlyMember = Warning("NINO006", "Member cannot be deserialized",
        "Member '{0}' of '{1}' is read-only and is not a constructor parameter; its serialized value is ignored when deserializing");

    // Members

    public static readonly DiagnosticDescriptor UnserializableMember = Warning("NINO007",
        "Member type cannot be serialized",
        "Nino has no formatter for '{1}'{3} in member '{0}' of NinoType '{2}': {4}; mark it [NinoIgnore] or give it a custom formatter");

    public static readonly DiagnosticDescriptor InvalidCustomFormatter = Error("NINO008",
        "Invalid [NinoMemberFormatter]",
        "[NinoMemberFormatter] '{0}' on member '{2}' of NinoType '{3}' cannot format it: a member formatter must be a struct implementing INinoFormatter<{1}>");

    public static readonly DiagnosticDescriptor PrivateMember = Error("NINO009", "Non-public member is not serialized",
        "Member '{0}' of NinoType '{1}' is not public, and the type does not set includeNonPublicMembers: it is not serialized");

    public static readonly DiagnosticDescriptor RedundantMember = Warning("NINO010", "Redundant [NinoMember]",
        "[NinoMember] on '{0}' has no effect: NinoType '{1}' collects it already (autoCollect); remove it");

    public static readonly DiagnosticDescriptor RedundantIgnore = Warning("NINO011", "Redundant [NinoIgnore]",
        "[NinoIgnore] on '{0}' has no effect: NinoType '{1}' collects only [NinoMember] members (autoCollect: false); remove it");

    public static readonly DiagnosticDescriptor NotSerializableMember = Warning("NINO028",
        "[NinoMember] on a member that is never serialized",
        "[NinoMember] on '{0}' of NinoType '{1}' has no effect: {2}; remove it");

    public static readonly DiagnosticDescriptor HostSizedVector = Error("NINO029",
        "Vector<T> cannot be serialized",
        "{0} cannot be serialized: '{1}' {2}, and Vector<T> is 16, 32 or 64 bytes by the CPU; store a fixed number of lanes (an array, a fixed buffer, Vector128<T> or Vector256<T>) and view them as Vector<T> in [NinoIgnore] members");

    public static readonly DiagnosticDescriptor Utf8WithoutEffect = Warning("NINO030", "[NinoUtf8] has no effect",
        "[NinoUtf8] on member '{0}' of NinoType '{1}' has no effect: {2}; remove it");

    public static readonly DiagnosticDescriptor Utf8FormatterByHand = Info("NINO031", "Use [NinoUtf8]",
        "Member '{0}' of NinoType '{1}' names Utf8StringFormatter, which is for generated code; mark it [NinoUtf8] instead (the same bytes)");

    public static readonly DiagnosticDescriptor InterfaceMemberAttribute = Warning("NINO033",
        "Attribute on an interface member has no effect",
        "[{0}] on member '{1}' of interface '{2}' has no effect: implementing types write their own member, with its own attributes; put it on the implementing member instead (an explicit implementation writes the interface's member, with these attributes)");

    /// <summary>The <c>{2}</c> of <see cref="HostSizedVector"/>: what <paramref name="type"/> is or holds.</summary>
    public static string HoldsVector(ITypeSymbol type, ITypeSymbol vector) =>
        SymbolEqualityComparer.Default.Equals(type, vector) ? "is a Vector<T>" : $"holds '{vector.ToDisplayString()}'";

    public static readonly DiagnosticDescriptor AmbiguousMember = Error("NINO012", "Member has both [NinoMember] and [NinoIgnore]",
        "Member '{0}' of NinoType '{1}' has both [NinoMember] and [NinoIgnore]; remove the one you do not mean");

    /// <summary>
    /// The property of a <see cref="DuplicateIndex"/> diagnostic holding its rank among the type's duplicates (0 for
    /// the first reported): the code fix gives the n-th the n-th free index, so fixing all of them at once (each fix
    /// sees the original document) assigns distinct indices.
    /// </summary>
    public const string DuplicateRank = "Rank";

    public static readonly DiagnosticDescriptor DuplicateIndex = Error("NINO013", "Duplicate member index",
        "Member '{0}.{1}' has the same [NinoMember] index as '{2}.{3}'; give each member an index of its own");

    public static readonly DiagnosticDescriptor EmptyStruct = Error("NINO023", "Struct serializes no member",
        "NinoType struct '{0}' serializes no member, so it is written as zero bytes and a collection of it cannot be read back; serialize a member, or give it NinoFormat.VersionTolerant or NinoFormat.Keyed");

    public static readonly DiagnosticDescriptor HiddenMember = Warning("NINO022", "Hidden member cannot be serialized",
        "Member '{0}' of '{1}' is hidden by a member of the same name in '{2}', and generated code can only reach it through [UnsafeAccessor] (.NET 8+, generic types .NET 9+); it is skipped");

    // Features

    public static readonly DiagnosticDescriptor KeyCollision = Error("NINO014", "Member key collision",
        "Members '{0}' and '{1}' of keyed NinoType '{2}' hash to the same key; rename one of them");

    public static readonly DiagnosticDescriptor FormerNameNotKeyed = Warning("NINO024",
        "[NinoFormerName] on a member has no effect in a positional type",
        "[NinoFormerName] on member '{0}' has no effect: '{1}' is not keyed, so member names are not written; remove it, or give the type NinoFormat.Keyed");

    public static readonly DiagnosticDescriptor MultipleTypeFormerNames = Error("NINO026",
        "A type takes one [NinoFormerName]",
        "'{0}' has more than one [NinoFormerName]: a type keeps only one earlier name, which its type id is computed from; remove all but one");

    public static readonly DiagnosticDescriptor GenericTypeFormerName = Warning("NINO027",
        "[NinoFormerName] on a generic type used polymorphically",
        "'{0}' is generic, so every instantiation of it takes its type id from [NinoFormerName] '{1}': two of them written with a type id (through a base type, an interface or object) collide; remove the former name, or put it on non-generic types only");

    public static readonly DiagnosticDescriptor TrackReferencesOnStruct = Warning("NINO015",
        "trackReferences on a struct", "'{0}' is a struct; trackReferences only applies to classes and is ignored");

    public static readonly DiagnosticDescriptor TrackReferencesNeedsAssignableMembers = Error("NINO016",
        "Cannot track references",
        "'{0}' is re-created from constructor arguments or read-only members, so a reference to it cannot be read before it exists; trackReferences needs a type whose members are all assignable");

    public static readonly DiagnosticDescriptor InvalidFactory = Error("NINO017",
        "Invalid [NinoFactory]", "[NinoFactory] '{0}' cannot create instances: it {1}");

    /// <summary>The compile-time symbol that made an assembly's positional types version tolerant until 4.0.</summary>
    public const string WeakToleranceSymbol = "WEAK_VERSION_TOLERANCE";

    /// <summary>
    /// The property of a <see cref="WeakToleranceWithoutEffect"/> diagnostic on a type without a <c>[NinoType]</c> of its
    /// own: the attribute to declare on it (<c>NinoType(..., format: NinoFormat.VersionTolerant)</c>).
    /// </summary>
    public const string NinoTypeToAdd = "NinoType";

    public static readonly DiagnosticDescriptor WeakToleranceWithoutEffect = Error("NINO032",
        "WEAK_VERSION_TOLERANCE does not make types version tolerant",
        "WEAK_VERSION_TOLERANCE does not make NinoType '{0}' version tolerant (it did before 4.0): '{0}' is written positionally and cannot read the data it wrote as version tolerant; to keep its bytes, {1}");

    // Pooling

    public static readonly DiagnosticDescriptor InvalidPooledType = Error("NINO018", "Type cannot be pooled",
        "[NinoPooled] '{0}' must be a sealed class whose members are all assignable, so that Nino can deserialize into a reused instance");

    public static readonly DiagnosticDescriptor PoolHooksNotImplemented = Error("NINO019", "Pool hooks not implemented",
        "[NinoPooled] '{0}' must implement {1}");

    public static readonly DiagnosticDescriptor InvalidFormatterRegistration = Error("NINO020",
        "Invalid formatter registration", "[assembly: NinoFormatter(typeof({0}))] is ignored: {1}");

    // The layout baseline ([assembly: NinoLayouts]): what a change does to data written on either side of it

    /// <summary>
    /// The properties of a layout baseline finding (NINO034-NINO036) for its fixes: the key of the type's section, the
    /// section to write there (none: remove it), the key of a section about the same type to remove (renamed or moved),
    /// the fix besides accepting (<c>LayoutFix</c>) and its argument (a former name). The baseline is the diagnostic's
    /// additional location.
    /// </summary>
    public const string LayoutKey = "LayoutKey", LayoutSection = "LayoutSection", LayoutOldKey = "LayoutOldKey",
        LayoutFixKind = "LayoutFix", LayoutArgument = "LayoutArgument";

    public static readonly DiagnosticDescriptor LayoutBreakingChange = Warning("NINO034",
        "Layout change breaks data written before or after it",
        "'{0}' {1}; {2}");

    public static readonly DiagnosticDescriptor LayoutSilentLoss = Warning("NINO035",
        "Layout change loses data without an error",
        "'{0}' {1}; {2}");

    public static readonly DiagnosticDescriptor LayoutChangeNote = Info("NINO036",
        "Layout change is compatible with a condition",
        "'{0}' {1}; {2}");

    public static readonly DiagnosticDescriptor LayoutBaselineUnreadable = Warning("NINO037",
        "Layout baseline cannot be read",
        "The layout baseline in [assembly: NinoLayouts] cannot be read, so no layout is checked: {0} (line {1}); correct it, or write it again with nino-gen layouts or the refactoring 'Create the layout baseline'");

    // Serialization callbacks ([NinoOnSerializing], [NinoOnSerialized], [NinoOnDeserializing], [NinoOnDeserialized])

    public static readonly DiagnosticDescriptor CallbackSignature = Error("NINO038",
        "Serialization callback has the wrong signature",
        "'{0}' is marked [{1}] but {2}, so Nino never calls it; a callback is a non-generic method that takes no parameters and returns void");

    public static readonly DiagnosticDescriptor CallbackInaccessible = Error("NINO039",
        "Serialization callback is not accessible to generated code",
        "'{0}' is marked [{1}] but is {2}, so the formatter Nino generates for '{3}' cannot call it; {4}");

    public static readonly DiagnosticDescriptor CallbackNotNinoType = Warning("NINO040",
        "Serialization callback is never called",
        "'{0}' is marked [{1}] but {2}, so Nino never calls it; mark the type [NinoType] or remove the attribute");

    public static readonly DiagnosticDescriptor CallbackDuplicate = Error("NINO041",
        "Two methods for one serialization callback",
        "'{0}' marks both '{1}' and '{2}' [{3}], so Nino calls only '{1}'; a type takes one method per callback (a base class may declare its own)");

    public static readonly DiagnosticDescriptor CallbackStatic = Error("NINO042",
        "Serialization callback must be an instance method",
        "'{0}' is marked [{1}] but is static, so Nino never calls it: [{1}] runs on the value; make it an instance method (only [NinoOnDeserializing] may be static)");

    public static readonly DiagnosticDescriptor CallbackBeforeInstance = Error("NINO043",
        "No instance exists before the members are read",
        "Instance method '{0}' is marked [NinoOnDeserializing], but Nino creates '{1}' from the members it reads ({2}), so there is no instance to call it on; make it static, or move the work to [NinoOnDeserialized]");

    public static readonly DiagnosticDescriptor CallbackOnRawStruct = Error("NINO044",
        "Serialization callback on a struct copied as raw memory",
        "'{0}' is marked [{1}] but Nino writes '{2}' as its raw memory (and an array of it as one copy), which runs no code, so it is never called; call it from a callback of the type that holds the struct");

    /// <summary>Every descriptor above, which the analyzer reports. Last: static fields initialize in order.</summary>
    public static readonly ImmutableArray<DiagnosticDescriptor> All = typeof(NinoDiagnostics)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static f => f.FieldType == typeof(DiagnosticDescriptor))
        .Select(static f => (DiagnosticDescriptor)f.GetValue(null)!)
        .ToImmutableArray();
}
