namespace Nino.Generator.DTOs;

/// <summary>
/// Value-based representation of compilation metadata.
/// Replaces direct Compilation usage in the incremental pipeline to enable proper caching.
/// </summary>
public record CompilationMetadataDto(
    string AssemblyName,
    bool IsUnityAssembly,
    bool HasNinoCoreUsage,
    EquatableArray<string> ReferencedAssemblyNames
);
