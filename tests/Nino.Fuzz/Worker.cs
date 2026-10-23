using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Nino.Fuzz
{
    /// <summary>Everything a worker needs (Program.cs parses it; the supervisor passes it on).</summary>
    public sealed class Options
    {
        public double Seconds = 60;
        public int Seed = 1;
        public long Iterations = long.MaxValue;
        public long StartIteration;
        public List<string> Targets = new List<string>();
        public string FindingsDir = DefaultFindings();

        /// <summary>tests/Nino.Fuzz/findings when run from the project's bin folder (git ignores it), ./findings otherwise.</summary>
        static string DefaultFindings()
        {
            var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            return File.Exists(Path.Combine(project, "Nino.Fuzz.csproj")) ? Path.Combine(project, "findings") : "findings";
        }
        public bool Reuse = true;
        public double HangSeconds = 20;
        public double MinimizeSeconds = 20;
        public bool StrictKnown;
        public bool Probes = true;
        public Limits Limits = new Limits();
    }

    /// <summary>
    /// Fuzzes one target in this process for a time budget: mutates inputs from a pool (the seeds, then inputs that
    /// read successfully), places each against a guard page or in an array, reads it fresh or into an existing value,
    /// and saves every new finding (minimized). Deterministic: the same seed, target and start iteration produce the
    /// same inputs. Exit code: 0 no findings, 1 findings, 3 a read hung.
    /// </summary>
    public static class Worker
    {
        const int PoolSize = 512;

        public static int Run(Target target, Options o)
        {
            var limits = o.Limits;
            List<byte[]> seeds;
            try { seeds = target.Seeds(); }
            catch (Exception e)
            {
                Console.WriteLine($"[{target.Name}] SEED cannot be written: {e}");
                return 1;
            }
            foreach (var s in seeds)
            {
                var check = Executor.RunOnce(target, s, Placement.End, false, null, limits);
                if (check.Kind != OutcomeKind.Ok)
                {
                    Console.WriteLine($"[{target.Name}] SEED does not read back: {check.Signature ?? "rejected"}\n{check.Details}");
                    return 1;
                }
            }

            var pool = new List<byte[]>(seeds);
            var rng = new Random(Mix(o.Seed, target.Name, o.StartIteration));
            var mutator = new Mutator(rng);
            using var current = new CurrentCase(StatePath(o.FindingsDir, target.Name));
            var findings = new Dictionary<string, int>();

            Case running = null;
            long runningSince = 0;
            var watchdog = new Thread(() =>
            {
                long hangTicks = (long)(o.HangSeconds * Stopwatch.Frequency);
                while (true)
                {
                    Thread.Sleep(250);
                    var c = Volatile.Read(ref running);
                    long since = Interlocked.Read(ref runningSince);
                    if (c != null && since != 0 && Stopwatch.GetTimestamp() - since > hangTicks)
                    {
                        var dir = Findings.Save(o.FindingsDir, c, "hang", "HANG",
                            $"one read did not finish in {o.HangSeconds} s (not minimized: the read never returns)", null, "");
                        Console.WriteLine($"[{target.Name}] HANG at iteration {c.Iteration}: {dir ?? "already saved"}");
                        Console.Out.Flush();
                        Environment.Exit(3);
                    }
                }
            }) { IsBackground = true, Name = "watchdog" };
            watchdog.Start();

            var sw = Stopwatch.StartNew();
            long it = o.StartIteration, ok = 0, rejected = 0, found = 0;
            long end = o.Iterations == long.MaxValue ? long.MaxValue : o.StartIteration + o.Iterations;
            for (; it < end; it++)
            {
                if ((it & 63) == 0 && sw.Elapsed.TotalSeconds >= o.Seconds) break;
                var parent = pool[rng.Next(pool.Count)];
                var data = mutator.Mutate(parent, pool);
                var c = new Case
                {
                    Target = target.Name, Seed = o.Seed, Iteration = it, Data = data,
                    Placement = (Placement)(it % 3),
                    Reuse = o.Reuse && target.ReadInto != null && rng.Next(4) == 0,
                };
                if (c.Reuse && rng.Next(2) == 0) c.Prime = pool[rng.Next(pool.Count)];
                current.Record(c);
                Interlocked.Exchange(ref runningSince, Stopwatch.GetTimestamp());
                Volatile.Write(ref running, c);

                var outcome = Executor.Run(target, c.Data, c.Placement, c.Reuse, c.Prime, limits);

                Volatile.Write(ref running, null);
                Interlocked.Exchange(ref runningSince, 0);

                switch (outcome.Kind)
                {
                    case OutcomeKind.Ok:
                        ok++;
                        if (pool.Count < PoolSize) pool.Add(data);
                        else if (rng.Next(8) == 0) pool[seeds.Count + rng.Next(PoolSize - seeds.Count)] = data;
                        break;
                    case OutcomeKind.Rejected:
                        rejected++;
                        break;
                    default:
                        found++;
                        if (findings.TryGetValue(outcome.Signature, out var n)) { findings[outcome.Signature] = n + 1; break; }
                        findings[outcome.Signature] = 1;
                        Report(target, c, outcome, o);
                        break;
                }
            }

            double secs = Math.Max(sw.Elapsed.TotalSeconds, 1e-3);
            long total = it - o.StartIteration;
            Console.WriteLine($"[{target.Name}] {total:N0} inputs ({total / secs:N0}/s): {ok:N0} read, {rejected:N0} rejected, {found:N0} findings" +
                              (findings.Count > 0 ? $" in {findings.Count} signatures" : "") + $"; iterations {o.StartIteration}..{it - 1}");
            foreach (var kv in findings) Console.WriteLine($"[{target.Name}]   {kv.Value,6} x {kv.Key}");
            return findings.Count > 0 ? 1 : 0;
        }

        static void Report(Target target, Case c, Outcome outcome, Options o)
        {
            string signature = outcome.Signature;
            bool Same(byte[] candidate)
            {
                var r = Executor.Run(target, candidate, c.Placement, c.Reuse, c.Prime, o.Limits);
                return r.Kind == OutcomeKind.Finding && r.Signature == signature;
            }
            var min = Findings.Minimize(c.Data, Same, TimeSpan.FromSeconds(o.MinimizeSeconds), 5000, out var note);
            string kind = signature.Split(' ')[0].ToLowerInvariant();
            var dir = Findings.Save(o.FindingsDir, c, kind, signature, outcome.Details, min, note);
            Console.WriteLine($"[{target.Name}] FINDING {signature}\n[{target.Name}]   iteration {c.Iteration}, {c.Data.Length} bytes, minimized to {min.Length}: {dir ?? "already saved in an earlier run"}");
            Console.Out.Flush();
        }

        public static string StatePath(string findingsDir, string target) => Path.Combine(findingsDir, ".state", target + ".cur");

        /// <summary>The RNG seed of a worker: stable across runs and runtimes (string.GetHashCode is not).</summary>
        static int Mix(int seed, string target, long start)
        {
            ulong h = 14695981039346656037 ^ (ulong)(uint)seed;
            foreach (char ch in target) { h ^= ch; h *= 1099511628211; }
            h ^= (ulong)start; h *= 1099511628211;
            return (int)(h ^ (h >> 32));
        }
    }
}
