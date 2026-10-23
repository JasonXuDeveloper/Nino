using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Analysis;

/// <summary>
/// Every diagnostic Nino reports, shared by the analyzer, the model builder and the code fixes. Ids are grouped:
/// the type (001-003), how it is constructed (004-006, 021, 025), its members (007-013, 022-023), features (014-017, 024, 026-027), pooling
/// (018-019), formatter registrations (020).
/// </summary>
internal static class NinoDiagnostics
{
    private const string Category = "Nino";

    private const string HelpLink = "https://github.com/JasonXuDeveloper/Nino/blob/main/docs/diagnostics.md#";

    private static DiagnosticDescriptor Error(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Error, true, helpLinkUri: HelpLink + id.ToLowerInvariant());

    private static DiagnosticDescriptor Warning(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Warning, true, helpLinkUri: HelpLink + id.ToLowerInvariant());

    // The type

    public static readonly DiagnosticDescriptor InaccessibleType = Error("NINO001",
        "NinoType must be accessible",
        "NinoType '{0}' cannot be serialized: it must be a non-static class, struct or interface, and it and every type containing it must be public or internal");

    public static readonly DiagnosticDescriptor NotPartial = Error("NINO002", "Type must be partial",
        "'{0}' serializes non-public members, so it (and every type containing it) must be declared partial");

    public static readonly DiagnosticDescriptor SubTypeWithoutNinoType = Warning("NINO003",
        "Sub-type of a NinoType should also have the NinoTypeAttribute applied",
        "Sub-type '{0}' of NinoType '{1}' should also have the NinoTypeAttribute applied to ensure serialization does not lead to undefined behavior");

    // Construction

    public static readonly DiagnosticDescriptor NoUsableConstructor = Error("NINO004", "No usable constructor",
        "'{0}' has no accessible parameterless constructor and no constructor whose parameters all match serialized members; mark one with [NinoConstructor]");

    public static readonly DiagnosticDescriptor MissingConstructorMember = Error("NINO005",
        "Constructor parameter matches no member", "Missing constructor member {0} for {1}");

    public static readonly DiagnosticDescriptor InaccessibleConstructor = Error("NINO021",
        "NinoConstructor is not accessible",
        "[NinoConstructor] '{0}' of '{1}' cannot be called by generated code; it must be public or internal");

    public static readonly DiagnosticDescriptor FactoryIgnored = Warning("NINO025",
        "NinoFactory ignored because of NinoConstructor",
        "[NinoFactory] '{0}' of '{1}' is never called: instances are always created through [NinoConstructor] '{2}'; remove one of the two attributes");

    public static readonly DiagnosticDescriptor ReadOnlyMember = Warning("NINO006", "Member cannot be deserialized",
        "Member '{0}' of '{1}' is read-only and is not a constructor parameter; its serialized value is ignored when deserializing");

    // Members

    public static readonly DiagnosticDescriptor UnserializableMember = Warning("NINO007",
        "Member type cannot be serialized",
        "Member '{0}' of type '{1}' in NinoType '{2}' has an unrecognizable type and will be skipped during serialization/deserialization");

    public static readonly DiagnosticDescriptor InvalidCustomFormatter = Error("NINO008",
        "Invalid custom formatter type",
        "Custom formatter '{0}' must be a struct implementing INinoFormatter<{1}>");

    public static readonly DiagnosticDescriptor PrivateMember = Error("NINO009", "Non-public member is not serialized",
        "Member '{0}' of NinoType '{1}' is not public, and the type does not set containNonPublicMembers: it is not serialized");

    public static readonly DiagnosticDescriptor RedundantMember = Warning("NINO010", "Redundant NinoMemberAttribute",
        "Member '{0}' of NinoType '{1}' will be automatically collected, the NinoMemberAttribute is redundant");

    public static readonly DiagnosticDescriptor RedundantIgnore = Warning("NINO011", "Redundant NinoIgnoreAttribute",
        "Member '{0}' of NinoType '{1}' will not be collected at anytime, the NinoIgnoreAttribute is redundant");

    public static readonly DiagnosticDescriptor AmbiguousMember = Error("NINO012", "Ambiguous member",
        "Member '{0}' of NinoType '{1}' is annotated with both NinoMemberAttribute and NinoIgnoreAttribute, it is ambiguous");

    public static readonly DiagnosticDescriptor DuplicateIndex = Error("NINO013", "Duplicate member index",
        "Member '{0}.{1}' has the same index as another member '{2}.{3}'");

    public static readonly DiagnosticDescriptor EmptyStruct = Error("NINO023", "Struct serializes no member",
        "NinoType struct '{0}' serializes no member, so it is written as zero bytes and a collection of it cannot be read back; serialize a member, or give it NinoFormat.VersionTolerant or NinoFormat.Keyed");

    public static readonly DiagnosticDescriptor HiddenMember = Warning("NINO022", "Hidden member cannot be serialized",
        "Member '{0}' of '{1}' is hidden by a member of the same name in '{2}', and generated code can only reach it through [UnsafeAccessor] (.NET 8+, non-generic types); it is skipped");

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
        "Invalid NinoFactoryAttribute usage", "Method '{0}' with [NinoFactory] {1}");

    // Pooling

    public static readonly DiagnosticDescriptor InvalidPooledType = Error("NINO018", "Type cannot be pooled",
        "[NinoPooled] '{0}' must be a sealed class whose members are all assignable, so that Nino can deserialize into a reused instance");

    public static readonly DiagnosticDescriptor PoolHooksNotImplemented = Error("NINO019", "Pool hooks not implemented",
        "[NinoPooled] '{0}' must implement {1}");

    public static readonly DiagnosticDescriptor InvalidFormatterRegistration = Error("NINO020",
        "Invalid formatter registration", "[assembly: NinoFormatter(typeof({0}))] is ignored: {1}");

    /// <summary>Every descriptor above, which the analyzer reports. Last: static fields initialize in order.</summary>
    public static readonly ImmutableArray<DiagnosticDescriptor> All = typeof(NinoDiagnostics)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(static f => f.FieldType == typeof(DiagnosticDescriptor))
        .Select(static f => (DiagnosticDescriptor)f.GetValue(null)!)
        .ToImmutableArray();
}
