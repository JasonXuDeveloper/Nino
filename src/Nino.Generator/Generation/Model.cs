using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Nino.Generator.DTOs;

namespace Nino.Generator.Generation;

// Everything in this file is an immutable value: the incremental pipeline compares these to decide what to
// re-emit, so they must never hold symbols, syntax nodes or the compilation.

internal enum FormatterKind
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

internal enum MemberAccess
{
    /// <summary><c>value.Member</c>.</summary>
    Direct,

    /// <summary><c>[UnsafeAccessor]</c> declared in the formatter (.NET 8+, non-generic declaring type).</summary>
    UnsafeAccessor,

    /// <summary>Accessor class generated inside the (partial) declaring type.</summary>
    Partial,
}

internal sealed record MemberModel(
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
    bool Unassignable,
    // Runtime type id of the member type when its built-in formatter is registered for top-level use, else 0.
    int TypeId);

internal sealed record ConstructionModel(
    // Re-create the instance on every read (constructor arguments, init-only or readonly members).
    bool Recreate,
    // "new global::Ns.T" or "global::Ns.T.Factory"; empty when the type cannot be constructed.
    string Creator,
    // Member names passed as constructor arguments, in order.
    EquatableArray<string> Arguments,
    // [NinoRefDeserialization] factory method used when the target is null (in-place mode only).
    string RefFactory);

/// <summary>
/// Receives the diagnostics found while a model is built. The analyzer passes one and reports them; the generator
/// passes none, so models carry no locations and an edit that only moves code re-emits nothing.
/// </summary>
internal sealed class DiagnosticSink
{
    private readonly Action<Diagnostic> _report;

    public DiagnosticSink(Action<Diagnostic> report) => _report = report;

    public void Report(DiagnosticDescriptor descriptor, ISymbol at, params object[] args) =>
        _report(Diagnostic.Create(descriptor, at.Locations.FirstOrDefault(), args));
}

internal sealed record TypeModel(
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
    // Hash of the runtime name ignoring [NinoFormerName]: the key a by-type lookup computes without reflection.
    int NameId,
    bool VersionTolerant,
    // Members written as (key, length, value): see NinoTypeAttribute.Keyed.
    bool Keyed,
    // Objects written with a reference header (null, back reference or new): see trackReferences.
    bool TrackReferences,
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
    // (type, formatter) pairs registered for AOT platforms and top-level calls.
    EquatableArray<FormatterRegistration> UsedFormatters,
    // [NinoPooled]: instances are taken from NinoPool<T> before being created, and the partial adds the pool hooks.
    bool Pooled,
    // Namespace and containing declarations of the generated partial (accessor class, pool hooks); empty when
    // neither is needed.
    string PartialNamespace,
    EquatableArray<string> PartialDeclarations)
{
    public bool IsGeneric => TypeParameters.Length != 0;
}

// Id: runtime type id of the closed type (NinoTypeId), the key of lazy registration.
internal sealed record FormatterRegistration(string TypeName, string Formatter, int Id);

internal sealed record GeneratorOptions(string AssemblyName, bool HasModuleInitializer, bool HasUnityEngine,
    bool HasUnityEditor);
