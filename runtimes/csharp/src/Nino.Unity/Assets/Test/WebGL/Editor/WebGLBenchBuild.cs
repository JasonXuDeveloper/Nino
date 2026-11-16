// Builds the WebGL player of Test.WebGL.WebGLBench (unity.yml's WebGL job, -executeMethod).
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Test.Editor.WebGL
{
    public static class WebGLBenchBuild
    {
        public static void Build()
        {
            const string scene = "Assets/WebGLBench.unity";
            EditorSceneManager.SaveScene(EditorSceneManager.NewScene(NewSceneSetup.EmptyScene), scene);
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
            PlayerSettings.WebGL.decompressionFallback = false;
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            // Unity 6: WebAssembly 2023, which compiles the player (and nino_wasm.c) with SIMD.
#if UNITY_2023_2_OR_NEWER
            PlayerSettings.WebGL.wasm2023 = true;
            const bool wasm2023 = true;
#else
            const bool wasm2023 = false;
#endif
            // -ninoTiming (unity.yml's benchmark run): time every case as well as checking it.
            bool timing = Array.IndexOf(Environment.GetCommandLineArgs(), "-ninoTiming") >= 0;
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.WebGL, Il2CppCompilerConfiguration.Release);
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = "Build/WebGL",
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
                extraScriptingDefines = timing
                    ? new[] { "NINO_WEBGL_BENCH", "NINO_WEBGL_TIMING" }
                    : new[] { "NINO_WEBGL_BENCH" },
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"NINO_WEBGL_BUILD|result={report.summary.result}|wasm2023={wasm2023}|timing={timing}|size={report.summary.totalSize}");
            // game-ci/unity-builder 6 (its CLI) fails the step unless the log has its default build script's summary:
            // a "# Build results #" banner, then "Errors: 0" before a "Size:" line.
            Console.WriteLine(
                "\n###########################\n" +
                "#      Build results      #\n" +
                "###########################\n\n" +
                $"Duration: {report.summary.totalTime}\n" +
                $"Warnings: {report.summary.totalWarnings}\n" +
                $"Errors: {report.summary.totalErrors}\n" +
                $"Size: {report.summary.totalSize} bytes\n");
            if (report.summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
