using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Nino.Core;

namespace Conformance
{
    /// <summary>What .NET did with one input.</summary>
    internal sealed class Outcome
    {
        /// <summary><c>error</c> (an exception), <c>returned</c> (a value: the data was accepted), <c>crashed</c>, <c>timeout</c>.</summary>
        public string Kind;
        public string Exception;
        public string Reason;

        public JsonObject ToJson()
        {
            var json = new JsonObject { ["outcome"] = Kind };
            if (Exception != null)
                json["exception"] = Exception;
            if (Reason != null)
                json["reason"] = Reason;
            return json;
        }

        public static Outcome Parse(string line)
        {
            var json = JsonNode.Parse(line)!;
            return new Outcome
            {
                Kind = (string)json["outcome"], Exception = (string)json["exception"], Reason = (string)json["reason"],
            };
        }
    }

    /// <summary>
    /// Deserializes untrusted inputs in child processes, so an input that crashes the process (a stack overflow,
    /// which no <c>catch</c> stops) or exhausts memory only ends the child. Each child gets a 512 MB GC heap limit
    /// (<c>DOTNET_GCHeapHardLimit</c>) and a timeout.
    /// </summary>
    internal static class Probe
    {
        private const int TimeoutMs = 60_000;
        private const string HeapLimit = "0x20000000";

        // ------------------------------------------------------------------ in the child

        public static int Run(string typeName, string file)
        {
            var outcome = Invoke(Layouts.ByName[typeName], File.ReadAllBytes(file));
            Console.WriteLine(outcome.ToJson().ToJsonString());
            return 0;
        }

        /// <summary>Every strict prefix of a valid input, in this process; prints a summary.</summary>
        public static int RunPrefixes(string typeName, string file)
        {
            var type = Layouts.ByName[typeName];
            byte[] bytes = File.ReadAllBytes(file);
            var exceptions = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var accepted = new JsonArray();
            for (int length = 0; length < bytes.Length; length++)
            {
                var outcome = Invoke(type, bytes.AsSpan(0, length).ToArray());
                if (outcome.Kind == "error")
                    exceptions[outcome.Exception] = exceptions.GetValueOrDefault(outcome.Exception) + 1;
                else
                    accepted.Add(length);
            }

            var json = new JsonObject { ["outcome"] = accepted.Count == 0 ? "error" : "returned" };
            var counts = new JsonObject();
            foreach (var (name, count) in exceptions)
                counts[name] = count;
            json["exceptions"] = counts;
            if (accepted.Count > 0)
                json["acceptedPrefixLengths"] = accepted;
            Console.WriteLine(json.ToJsonString());
            return 0;
        }

        private static readonly MethodInfo ReadMethod =
            typeof(Probe).GetMethod(nameof(Read), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static object Read<T>(byte[] bytes) => NinoDeserializer.Deserialize<T>(bytes);

        /// <summary>Only for inputs known to be harmless (controls).</summary>
        public static Outcome InProcess(Type type, byte[] bytes) => Invoke(type, bytes);

        private static Outcome Invoke(Type type, byte[] bytes)
        {
            try
            {
                ReadMethod.MakeGenericMethod(type).Invoke(null, new object[] { bytes });
                return new Outcome { Kind = "returned" };
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                return new Outcome { Kind = "error", Exception = e.InnerException.GetType().Name };
            }
        }

        // ------------------------------------------------------------------ in the parent

        public static List<Outcome> InChildren(List<(Type Type, string File)> inputs)
        {
            var results = new Outcome[inputs.Count];
            Parallel.For(0, inputs.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) },
                i => results[i] = Child("--probe", Layouts.Name(inputs[i].Type), inputs[i].File, Outcome.Parse,
                    (kind, reason) => new Outcome { Kind = kind, Reason = reason }));
            return results.ToList();
        }

        public static JsonNode Prefixes(Type type, string file) =>
            Child<JsonNode>("--probe-prefixes", Layouts.Name(type), file, line => JsonNode.Parse(line),
                (kind, reason) => new JsonObject { ["outcome"] = kind, ["reason"] = reason });

        private static T Child<T>(string mode, string typeName, string file, Func<string, T> parse,
            Func<string, string, T> failed)
        {
            string self = Environment.ProcessPath!;
            var start = new ProcessStartInfo(self)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (Path.GetFileNameWithoutExtension(self) == "dotnet")
                start.ArgumentList.Add(typeof(Probe).Assembly.Location);
            start.ArgumentList.Add(mode);
            start.ArgumentList.Add(typeName);
            start.ArgumentList.Add(file);
            start.Environment["DOTNET_GCHeapHardLimit"] = HeapLimit;
            start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            start.Environment["DOTNET_EnableCrashReport"] = "0";

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeoutMs))
            {
                process.Kill(true);
                process.WaitForExit();
                return failed("timeout", $"no result after {TimeoutMs / 1000} s");
            }

            string output = stdout.Result.Trim(), errors = stderr.Result;
            if (process.ExitCode == 0 && output.Length > 0)
                return parse(output.Split('\n')[^1]);
            string reason = errors.Contains("Stack overflow", StringComparison.OrdinalIgnoreCase)
                ? "stack overflow"
                : errors.Contains("OutOfMemory", StringComparison.Ordinal)
                    ? "out of memory"
                    : $"exit code {process.ExitCode}";
            return failed("crashed", reason);
        }
    }
}
