using Microsoft.CodeAnalysis;

namespace Nino.Generator;

/// <summary>Every diagnostic Nino reports, shared by the analyzer, the generator and the code fixes.</summary>
/// <remarks>NINO008 (nested types with non-public members) is retired: nested types are supported.</remarks>
public static class NinoDiagnostics
{
    private const string Category = "Nino";

    private static DiagnosticDescriptor Error(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Error, true);

    private static DiagnosticDescriptor Warning(string id, string title, string format) =>
        new(id, title, format, Category, DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor MissingConstructorMember = Error("NINO001",
        "Missing constructor member", "Missing constructor member {0} for {1}");

    public static readonly DiagnosticDescriptor NoUsableConstructor = Error("NINO001", "No usable constructor",
        "'{0}' has no accessible parameterless constructor and no constructor whose parameters all match serialized members; mark one with [NinoConstructor]");

    public static readonly DiagnosticDescriptor InaccessibleType = Error("NINO002",
        "NinoType must be accessible",
        "NinoType '{0}' cannot be serialized: it must be a non-static class, struct or interface, and it and every type containing it must be public or internal");

    public static readonly DiagnosticDescriptor SubTypeWithoutNinoType = Warning("NINO003",
        "Sub-type of a NinoType should also have the NinoTypeAttribute applied",
        "Sub-type '{0}' of NinoType '{1}' should also have the NinoTypeAttribute applied to ensure serialization does not lead to undefined behavior");

    public static readonly DiagnosticDescriptor RedundantMember = Warning("NINO004", "Redundant NinoMemberAttribute",
        "Member '{0}' of NinoType '{1}' will be automatically collected, the NinoMemberAttribute is redundant");

    public static readonly DiagnosticDescriptor RedundantIgnore = Warning("NINO005", "Redundant NinoIgnoreAttribute",
        "Member '{0}' of NinoType '{1}' will not be collected at anytime, the NinoIgnoreAttribute is redundant");

    public static readonly DiagnosticDescriptor AmbiguousMember = Error("NINO006", "Ambiguous member",
        "Member '{0}' of NinoType '{1}' is annotated with both NinoMemberAttribute and NinoIgnoreAttribute, it is ambiguous");

    public static readonly DiagnosticDescriptor PrivateMember = Error("NINO007", "Suspicious member",
        "Member '{0}' of NinoType '{1}' is private but this type is not marked as containing non-public members");

    public static readonly DiagnosticDescriptor DuplicateIndex = Error("NINO009", "Duplicate member index",
        "Member '{0}.{1}' has the same index as another member '{2}.{3}'");

    public static readonly DiagnosticDescriptor InvalidCustomFormatter = Error("NINO010",
        "Invalid custom formatter type", "Custom formatter '{0}' must inherit from NinoFormatter<{1}>");

    public static readonly DiagnosticDescriptor UnserializableMember = Warning("NINO011",
        "Member type cannot be serialized",
        "Member '{0}' of type '{1}' in NinoType '{2}' has an unrecognizable type and will be skipped during serialization/deserialization");

    public static readonly DiagnosticDescriptor InvalidRefDeserialization = Error("NINO012",
        "Invalid NinoRefDeserializationAttribute usage", "Method '{0}' with [NinoRefDeserialization] {1}");

    public static readonly DiagnosticDescriptor NotPartial = Error("NINO013", "Type must be partial",
        "'{0}' serializes non-public members, so it (and every type containing it) must be declared partial");

    public static readonly DiagnosticDescriptor ReadOnlyMember = Warning("NINO014", "Member cannot be deserialized",
        "Member '{0}' of '{1}' is read-only and is not a constructor parameter; its serialized value is ignored when deserializing");

    public static readonly DiagnosticDescriptor KeyCollision = Error("NINO015", "Member key collision",
        "Members '{0}' and '{1}' of keyed NinoType '{2}' hash to the same key; rename one of them");

    public static readonly DiagnosticDescriptor TrackReferencesOnStruct = Warning("NINO016",
        "Cannot track references", "'{0}' is a struct; trackReferences only applies to classes and is ignored");

    public static readonly DiagnosticDescriptor TrackReferencesNeedsAssignableMembers = Error("NINO016",
        "Cannot track references",
        "'{0}' is re-created from constructor arguments or read-only members, so a reference to it cannot be read before it exists; trackReferences needs a type whose members are all assignable");
}
