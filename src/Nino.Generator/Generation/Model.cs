using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Nino.Generator.DTOs;

namespace Nino.Generator.Generation;

// Everything in this file is an immutable value: the incremental pipeline compares these to decide what to
// re-emit, so they must never hold symbols, syntax nodes or the compilation.

public enum FormatterKind
{
    /// <summary>Value type: body only.</summary>
    Struct,

    /// <summary>Sealed class: 1-byte null flag, then body.</summary>
    SealedClass,

    /// <summary>Non-sealed instantiable class: type id, then body (subtypes dispatched through the registry).</summary>
    Class,

    /// <summary>Abstract class or interface: always dispatched through the registry.</summary>
    Polymorphic,
}

public enum MemberAccess
{
    /// <summary><c>value.Member</c>.</summary>
    Direct,

    /// <summary><c>[UnsafeAccessor]</c> declared in the formatter (.NET 8+, non-generic declaring type).</summary>
    UnsafeAccessor,

    /// <summary>Accessor class generated inside the (partial) declaring type.</summary>
    Partial,
}

public sealed record MemberModel(
    string Name,
    string TypeName,
    string Formatter,
    // Size expression when the member is written raw as part of an unmanaged block, otherwise empty.
    string RawSize,
    bool IsProperty,
    MemberAccess Access,
    // Type that declares the member (global-qualified), used by accessors.
    string DeclaringType,
    bool DeclaringTypeIsValueType,
    // Whether the member can only be assigned while constructing the instance (readonly field, init-only).
    bool ConstructionOnly,
    // Whether the member is read-only for us even at construction (no way to assign it): read and dropped.
    bool Unassignable);

public sealed record ConstructionModel(
    // Re-create the instance on every read (constructor arguments, init-only or readonly members).
    bool Recreate,
    // "new global::Ns.T" or "global::Ns.T.Factory"; empty when the type cannot be constructed.
    string Creator,
    // Member names passed as constructor arguments, in order.
    EquatableArray<string> Arguments,
    // [NinoRefDeserialization] factory method used when the target is null (in-place mode only).
    string RefFactory);

public sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);

    public static LocationInfo? From(Location? location)
    {
        if (location == null || location.SourceTree == null)
            return null;
        return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }
}

public sealed record DiagnosticInfo(string Id, string Title, string Format, DiagnosticSeverity Severity,
    LocationInfo? Location, EquatableArray<string> Arguments)
{
    public DiagnosticInfo(string id, string title, string format, DiagnosticSeverity severity, LocationInfo? location,
        params string[] arguments) : this(id, title, format, severity, location, new EquatableArray<string>(arguments))
    {
    }

    public Diagnostic ToDiagnostic() =>
        Diagnostic.Create(new DiagnosticDescriptor(Id, Title, Format, "Nino", Severity, true),
            Location?.ToLocation() ?? Microsoft.CodeAnalysis.Location.None, Arguments.Cast<object>().ToArray());
}

public sealed record TypeModel(
    // Fully qualified type, e.g. "global::Ns.Outer.Box<T>".
    string TypeName,
    // Formatter type name without type parameters, e.g. "Ns_Outer_Box_1Formatter".
    string FormatterName,
    // "<T1, T2>" or "".
    string TypeParameters,
    // " where T : struct" ... or "".
    string Constraints,
    // "global::Ns.Outer.Box<,>" for generic types, otherwise "".
    string OpenTypeName,
    FormatterKind Kind,
    // Type id, or 0 for generic types (computed at runtime).
    int TypeId,
    string IdName,
    bool VersionTolerant,
    // Unmanaged struct written as its raw memory.
    bool RawStruct,
    // For [NinoType] collections: the formatter of the elements, written before the members.
    string CollectionFormatter,
    // Whether static WriteMembers/ReadMembers are emitted so derived types can reuse them (classes whose
    // members are all directly accessible).
    bool EmitMembers,
    // Formatter whose WriteMembers/ReadMembers cover the first BaseMemberCount members, or "".
    string BaseFormatter,
    int BaseMemberCount,
    EquatableArray<MemberModel> Members,
    ConstructionModel Construction,
    // NinoType base classes and interfaces this type is registered under (non-generic, instantiable types only).
    EquatableArray<string> PolymorphicBases,
    // (type, formatter) pairs registered for AOT platforms and top-level calls.
    EquatableArray<FormatterRegistration> UsedFormatters,
    // Namespace and containing declarations used to emit the partial accessor class; empty when not needed.
    string AccessorNamespace,
    EquatableArray<string> AccessorDeclarations,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public bool IsGeneric => TypeParameters.Length != 0;
}

public sealed record FormatterRegistration(string TypeName, string Formatter);

public sealed record GeneratorOptions(string AssemblyName, bool HasModuleInitializer, bool HasUnityEngine,
    bool HasUnityEditor);
