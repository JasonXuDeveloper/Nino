using Microsoft.CodeAnalysis.Testing;

namespace Nino.UnitTests;

/// <summary>Reference assemblies for analyzer/generator test compilations, matching the test host's runtime.</summary>
internal static class TestReferences
{
    public static void Apply(SolutionState state)
    {
        state.AdditionalReferences.Add(typeof(Core.Writer).Assembly);
#if NET10_0_OR_GREATER
        // No reference pack for net10.0 in this testing library version: compile against the running runtime.
        state.ReferenceAssemblies = new ReferenceAssemblies("net10.0");
        var runtimeDirectory = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location);
        foreach (var path in System.IO.Directory.GetFiles(runtimeDirectory!, "*.dll"))
        {
            var name = System.IO.Path.GetFileName(path);
            if (name == "System.Private.CoreLib.dll" || name == "netstandard.dll" || name == "mscorlib.dll" ||
                (name.StartsWith("System.") && !name.StartsWith("System.Private.")))
                state.AdditionalReferences.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        }
#elif NET8_0_OR_GREATER
        state.ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
#else
        state.ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard21;
#endif
    }
}
