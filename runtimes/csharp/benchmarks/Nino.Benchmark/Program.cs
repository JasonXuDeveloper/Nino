using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;
using Perfolizer.Metrology;

namespace Nino.Benchmark
{
    public class Program
    {
        // One joined summary, and --filter is honored (RunAllJoined ignores it). Times always in ns and sizes in B,
        // as the published report has always shown them: report.yml merges the reports of classes that ran in
        // parallel (runtimes/csharp/benchmarks/reports/merge_reports.py), whose cells then compare as they are.
        // `--verify` checks the cases (Verify.cs) and does not start BenchmarkDotNet; `--write-proto` rewrites the
        // .proto files of Google.Protobuf's messages from the models (ProtoFile); `--write-data [DIR]` writes each case's
        // bytes (WriteData).
        public static int Main(string[] args)
        {
            if (args.Contains("--verify"))
                return Verify.Run();
            if (Array.IndexOf(args, "--write-data") is var at and >= 0)
                return WriteData(at + 1 < args.Length ? args[at + 1] : "bench-data");
#if !NINO_BENCH_V3
            if (args.Contains("--write-proto"))
            {
                foreach (var file in ProtoFile.All)
                    file.Write();
                return 0;
            }
#endif

            // The probes (classes named *Probe, CpuProbe.cs) time kernels on their own jobs: only
            // a filter that names one runs them, never the suite's '*'.
            bool probes = args.Any(arg => arg.Contains("Probe"));
            BenchmarkSwitcher.FromTypes(typeof(Program).Assembly.GetTypes()
                    .Where(type => type.GetMethods().Any(method => method.IsDefined(typeof(BenchmarkAttribute), false)) &&
                                   (probes || !type.Name.EndsWith("Probe")))
                    .ToArray())
                .Run(args, DefaultConfig.Instance.WithOptions(ConfigOptions.JoinSummary)
                    .WithSummaryStyle(SummaryStyle.Default.WithTimeUnit(TimeUnit.Nanosecond)
                        .WithSizeUnit(SizeUnit.B)));
            return 0;
        }

        // The libraries whose bytes --write-data writes: Nino's, which the Go, Rust and TypeScript benchmarks read as
        // their own cases' data, and protobuf-net's, the format their protobuf competitors read (#335).
        private static readonly string[] DataLibraries = ["Nino", "Protobuf"];

        /// <summary>
        /// --write-data [DIR]: the bytes of every case of the classes that declare their cases (bench-cases.json), one
        /// file per case and library, <c>&lt;Case&gt;.&lt;library&gt;.bin</c> (nino, protobuf), from the same values the
        /// benchmarks time. Method names are unique across classes, so the case's name is the file's.
        /// </summary>
        private static int WriteData(string directory)
        {
            foreach (var type in typeof(Program).Assembly.GetTypes().Where(t => t.IsDefined(typeof(BenchCaseAttribute), false)))
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            Directory.CreateDirectory(directory);
            int written = 0;
            foreach (var (method, bytes) in BenchmarkPayloadRegistry.Bytes.OrderBy(p => p.Key, StringComparer.Ordinal))
            foreach (string library in DataLibraries)
            {
                string prefix = library + "Serialize";
                if (!method.StartsWith(prefix, StringComparison.Ordinal)) continue;
                File.WriteAllBytes(Path.Combine(directory, $"{method[prefix.Length..]}.{library.ToLowerInvariant()}.bin"), bytes);
                written++;
            }

#if !NINO_BENCH_V3
            WriteCaseTypes(directory);
#endif
            Console.WriteLine($"{written} files in {Path.GetFullPath(directory)}");
            return 0;
        }

#if !NINO_BENCH_V3
        /// <summary>
        /// cases.json next to the bytes: each case with Nino bytes, the .NET type they hold (runtime-form name: the type
        /// its Nino deserialize method returns), whether that is a [NinoType], its Google.Protobuf message (the
        /// protobuf-net contract the .protobuf.bin bytes follow), whether it is in the quick subset and whether its
        /// bytes are a previous version of the type (Old). The other runtimes' benchmarks read their cases from it.
        /// And roots.txt: nino-gen's --roots for those cases' types that are not [NinoType]s, one name each
        /// (<c>Case&lt;first case&gt;=&lt;type&gt;</c>), whose codecs the runtimes look up by the type's name.
        /// </summary>
        private static void WriteCaseTypes(string directory)
        {
            var lines = new System.Collections.Generic.List<string>();
            var roots = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var type in typeof(Program).Assembly.GetTypes().OrderBy(t => t.Name, StringComparer.Ordinal))
            foreach (var benchCase in type.GetCustomAttributes(typeof(BenchCaseAttribute), false).Cast<BenchCaseAttribute>())
            {
                var read = type.GetMethod("NinoDeserialize" + benchCase.Name);
                if (read == null || !BenchmarkPayloadRegistry.Bytes.Any(p => p.Key == "NinoSerialize" + benchCase.Name))
                    continue;
                var value = read.ReturnType;
                var definition = value.IsGenericType ? value.GetGenericTypeDefinition() : value;
                bool ninoType = definition.IsDefined(typeof(Nino.Core.NinoTypeAttribute), false);
                string name = Nino.Core.NinoTypeId.GetRuntimeName(value);
                if (!ninoType)
                    roots.TryAdd(name, "Case" + benchCase.Name);
                lines.Add(System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["class"] = type.Name,
                    ["case"] = benchCase.Name,
                    ["type"] = name,
                    ["ninoType"] = ninoType,
                    ["proto"] = benchCase.Proto?.Replace("{Case}", benchCase.Name),
                    ["quick"] = benchCase.Quick,
                    ["old"] = benchCase.Old != null,
                }));
            }

            File.WriteAllText(Path.Combine(directory, "cases.json"), "[\n" + string.Join(",\n", lines) + "\n]\n");
            File.WriteAllText(Path.Combine(directory, "roots.txt"), string.Join(";", roots.Select(r => r.Value + "=" + r.Key)) + "\n");
        }
#endif
    }
}
