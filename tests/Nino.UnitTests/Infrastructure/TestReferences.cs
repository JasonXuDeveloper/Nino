using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Testing;

namespace Nino.UnitTests;

/// <summary>Reference assemblies for analyzer/generator test compilations, matching the test host's runtime.</summary>
internal static class TestReferences
{
    // Created once and shared by every test: a MetadataReference is immutable, and Roslyn reuses what it read for a
    // reference object in every compilation given that same object. New references to the same files are read again:
    // on net10.0 that was about half the time of the analyzer and code-fix tests.
    private static readonly MetadataReference NinoCore = MetadataReference.CreateFromFile(typeof(Core.Writer).Assembly.Location);

#if NET10_0_OR_GREATER
    // No reference pack for net10.0 in this testing library version: compile against the running runtime.
    private static readonly MetadataReference[] Runtime = System.IO.Directory
        .GetFiles(System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll")
        .Where(path =>
        {
            var name = System.IO.Path.GetFileName(path);
            return name == "System.Private.CoreLib.dll" || name == "netstandard.dll" || name == "mscorlib.dll" ||
                   (name.StartsWith("System.") && !name.StartsWith("System.Private.")) && IsManaged(path);
        })
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();
#endif

    public static void Apply(SolutionState state)
    {
        state.AdditionalReferences.Add(NinoCore);
#if NET10_0_OR_GREATER
        state.ReferenceAssemblies = new ReferenceAssemblies("net10.0");
        state.AdditionalReferences.AddRange(Runtime);
#elif NET8_0_OR_GREATER
        state.ReferenceAssemblies = ReferenceAssemblies.Net.Net80;
#else
        state.ReferenceAssemblies = ReferenceAssemblies.NetStandard.NetStandard21;
#endif
    }

#if NET10_0_OR_GREATER
    // The runtime directory also holds native libraries named System.* (System.IO.Compression.Native.dll on Windows).
    private static bool IsManaged(string path)
    {
        try
        {
            System.Reflection.AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (System.BadImageFormatException)
        {
            return false;
        }
    }
#endif
}
