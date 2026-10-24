using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// What the generator reads from the whole compilation (<see cref="Of"/>), in one step of the pipeline, which runs on
/// every change: whether it references Nino.Core (<see cref="Enabled"/>), the options of its registry, the
/// referenced types that make their subtypes <c>[NinoType]</c>s (<c>InheritedCandidates.ReferencedNinoTypes</c>),
/// and whether NativeAOT's attributes can keep the fields of measured structs (<c>LayoutTypes.CanEmit</c>).
/// </summary>
internal sealed record ProjectFacts(bool Enabled, GeneratorOptions Options, EquatableArray<string> ReferencedNinoTypes,
    bool CanKeepLayouts)
{
    public static ProjectFacts Of(Compilation c, CancellationToken ct) => new(
        c.ReferencedAssemblyNames.Any(static a => a.Name == NinoNames.CoreAssembly),
        new GeneratorOptions(
            c.AssemblyName ?? "Assembly",
            TypeNames.CanName(c, "System.Runtime.CompilerServices.ModuleInitializerAttribute"),
            TypeNames.CanName(c, "UnityEngine.RuntimeInitializeOnLoadMethodAttribute"),
            TypeNames.CanName(c, "UnityEditor.InitializeOnLoadMethodAttribute"),
            new EquatableArray<string>(FormatterRules.RegisteringReferences(c)
                .OrderBy(n => n, StringComparer.Ordinal).ToArray())),
        InheritedCandidates.ReferencedNinoTypes(c, ct),
        LayoutTypes.CanEmit(c));
}
