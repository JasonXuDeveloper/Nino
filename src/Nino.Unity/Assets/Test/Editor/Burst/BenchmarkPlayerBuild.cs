// The player of the release benchmark (unity.yml with benchmark on passes -ninoBenchmark to the editor that builds the
// test player). Without that argument the test player is built as the test runner builds it.
#if NINO_BURST
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.TestTools;
using UnityEngine;

[assembly: TestPlayerBuildModifier(typeof(Test.Editor.Burst.BenchmarkPlayerBuild))]

namespace Test.Editor.Burst
{
    /// <summary>
    /// Builds the benchmark's test player as close to a shipped one as the test runner allows: no Development build (the
    /// test runner sets it to report results to the editor, which game-ci's standalone mode does not use: it builds
    /// without the player connection and the player writes its own results), no script debugging or profiler, and
    /// NINO_UNITY_BENCH defined, which compiles Test.Runtime.Burst.BurstBenchmark into it. It logs the build settings
    /// the player cannot read at run time (IL2CPP's C++ configuration and code generation, the Burst version) as one
    /// NINO_UNITY_BUILD line of the editor's log, next to the player's NINO_UNITY_ENV line.
    /// </summary>
    public class BenchmarkPlayerBuild : ITestPlayerBuildModifier
    {
        public BuildPlayerOptions ModifyOptions(BuildPlayerOptions options)
        {
            if (!Environment.GetCommandLineArgs().Contains("-ninoBenchmark"))
                return options;

            options.options &= ~(BuildOptions.Development | BuildOptions.AllowDebugging |
                                 BuildOptions.ConnectWithProfiler | BuildOptions.EnableDeepProfilingSupport);
            options.extraScriptingDefines =
                (options.extraScriptingDefines ?? Array.Empty<string>()).Append("NINO_UNITY_BENCH").ToArray();

            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(options.target));
            var burst = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Unity.Burst.BurstCompiler).Assembly);
            string line = string.Join("|", "NINO_UNITY_BUILD",
                "unity=" + Application.unityVersion,
                "target=" + options.target,
                "backend=" + PlayerSettings.GetScriptingBackend(target),
                "il2cpp_config=" + PlayerSettings.GetIl2CppCompilerConfiguration(target),
                "il2cpp_codegen=" + PlayerSettings.GetIl2CppCodeGeneration(target),
                "stripping=" + PlayerSettings.GetManagedStrippingLevel(target),
                "development=" + ((options.options & BuildOptions.Development) != 0 ? "true" : "false"),
                "burst=" + (burst != null ? burst.version : "unknown"),
                "burst_x64_targets=" + BurstX64Targets(options.target));
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", line);
            return options;
        }

        /// <summary>
        /// The x64 CPU targets Burst AOT compiles for (each a variant, picked by the CPU at run time), as the Burst AOT
        /// Settings of the target list them (ProjectSettings/BurstAotSettings_&lt;target&gt;.json, CpuTargetsX64: bit
        /// 3 SSE2, 4 SSE4, 5 AVX, 6 AVX2); "default" without the file (Burst 1.8.29: SSE2 and AVX2).
        /// </summary>
        private static string BurstX64Targets(BuildTarget target)
        {
            string path = $"ProjectSettings/BurstAotSettings_{target}.json";
            if (!File.Exists(path))
                return "default";
            var match = Regex.Match(File.ReadAllText(path), "\"CpuTargetsX64\"\\s*:\\s*(\\d+)");
            if (!match.Success)
                return "default";
            int bits = int.Parse(match.Groups[1].Value);
            var names = new[] { (3, "SSE2"), (4, "SSE4"), (5, "AVX"), (6, "AVX2") }
                .Where(t => (bits & (1 << t.Item1)) != 0).Select(t => t.Item2);
            return string.Join(" ", names);
        }
    }
}
#endif
