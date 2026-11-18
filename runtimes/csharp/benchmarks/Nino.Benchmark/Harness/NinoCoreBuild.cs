using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Nino.Core;

namespace Nino.Benchmark;

/// <summary>
/// Stops a benchmark process that loaded another Nino.Core build than the one asked for: Nino.Core's newest build (what
/// the project file names when -p:NinoCoreTfm is not set: net10.0 on the benchmarks' .NET 11), or the one
/// -p:NinoCoreTfm names. BenchmarkDotNet's runner project reaches Nino.Core only through this
/// project's references, and with Nino.Immutable built for net8.0 alone it copied Nino.Core's net8.0 build: every
/// runner number up to then timed code without its .NET 10 paths (a 40-byte struct of an int, a long? and a double?,
/// 1,000 of them written in 3.4 µs instead of 1.2, its absent values cleared element by element).
/// </summary>
internal static class NinoCoreBuild
{
#pragma warning disable CA2255 // A module initializer: the runner process calls no code of ours before the cases.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Check()
    {
        var core = typeof(NinoSerializer).Assembly;
        string loaded = core.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "";
        string asked = typeof(NinoCoreBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "NinoCoreTfm")?.Value ?? "";
        string expected = asked.Length == 0
            ? AppContext.TargetFrameworkName ?? ""
            : asked.StartsWith("netstandard", StringComparison.Ordinal)
                ? ".NETStandard,Version=v" + asked.Substring("netstandard".Length)
                : ".NETCoreApp,Version=v" + asked.Substring("net".Length);
        if (loaded != expected)
            throw new InvalidOperationException(
                $"Nino.Core's {loaded} build is loaded from {core.Location}; the benchmarks need its {expected} build.");
    }
}
