using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Nino.Fuzz
{
    /// <summary>The result of a child process.</summary>
    public sealed class ChildResult
    {
        public int ExitCode;
        public bool TimedOut;
        public string StdErr;
        public string StdOut;
        public TimeSpan Elapsed;

        /// <summary>Ended by the runtime or the OS (stack overflow, access violation, abort), not by returning.</summary>
        public bool Crashed => TimedOut || ExitCode != 0 && ExitCode != 1 && ExitCode != 2 && ExitCode != 3;

        public string CrashSignature()
        {
            if (TimedOut) return "CRASH timeout";
            var e = StdErr ?? "";
            if (e.Contains("Stack overflow")) return "CRASH stack overflow";
            if (e.Contains("AccessViolationException") || e.Contains("SIGSEGV") || e.Contains("Segmentation fault") ||
                ExitCode == 139 || ExitCode == -1073741819 /* 0xC0000005 */) return "CRASH access violation";
            if (e.Contains("Fatal error")) return "CRASH fatal error";
            if (e.Contains("OutOfMemory")) return "CRASH out of memory";
            return $"CRASH exit code {ExitCode}";
        }
    }

    /// <summary>
    /// Runs the fuzzer as a parent of worker processes, one target at a time, so an input that kills the process (a
    /// stack overflow, an access violation on a guard page) is recorded and the run goes on: the worker writes each
    /// input to a state file before reading it, and the supervisor saves it as a finding, minimizes it with child
    /// processes, and restarts the worker after it.
    /// </summary>
    public static class Supervisor
    {
        const int MaxRestarts = 25;

        public static int Fuzz(Options o)
        {
            var targets = o.Targets.Count == 0 ? Targets.All.ToList() : o.Targets.Select(Targets.Find).ToList();
            Console.WriteLine($"Nino fuzz: seed {o.Seed}, {o.Seconds:F0} s over {targets.Count} targets, findings in {Path.GetFullPath(o.FindingsDir)}");
            Console.WriteLine($"  {Findings.Environment()}");
            Directory.CreateDirectory(o.FindingsDir);

            int failures = 0;
            double slice = o.Seconds / targets.Count;
            foreach (var target in targets)
            {
                var sw = Stopwatch.StartNew();
                long start = o.StartIteration;
                for (int restart = 0; ; restart++)
                {
                    double left = slice - sw.Elapsed.TotalSeconds;
                    if (restart > 0 && left < 1) break;
                    var args = new List<string>
                    {
                        "worker", "--target", target.Name, "--seconds", Math.Max(left, 1).ToString("F1"), "--seed", o.Seed.ToString(),
                        "--start", start.ToString(), "--findings", o.FindingsDir, "--hang-seconds", o.HangSeconds.ToString(),
                        "--slow-ms", o.Limits.SlowMs.ToString(), "--alloc-factor", o.Limits.AllocFactor.ToString(),
                        "--minimize-seconds", o.MinimizeSeconds.ToString(),
                    };
                    if (o.Iterations != long.MaxValue) args.AddRange(new[] { "--iterations", Math.Max(0, o.Iterations - (start - o.StartIteration)).ToString() });
                    if (!o.Reuse) args.Add("--no-reuse");
                    if (!o.Limits.RoundTrip) args.Add("--no-roundtrip");
                    var r = RunChild(args, TimeSpan.FromSeconds(Math.Max(left, 1) + o.HangSeconds + o.MinimizeSeconds * 4 + 60), echo: true);
                    if (r.ExitCode == 3)
                    {
                        // A read hung; the worker saved it. Go on after it.
                        failures++;
                        var hung = CurrentCase.Load(Worker.StatePath(o.FindingsDir, target.Name), target.Name, o.Seed);
                        if (hung == null || restart + 1 >= MaxRestarts) break;
                        start = hung.Iteration + 1;
                        continue;
                    }
                    if (!r.Crashed)
                    {
                        if (r.ExitCode != 0) failures++;
                        break;
                    }

                    failures++;
                    var c = CurrentCase.Load(Worker.StatePath(o.FindingsDir, target.Name), target.Name, o.Seed);
                    var signature = r.CrashSignature();
                    Console.WriteLine($"[{target.Name}] {signature} (exit code {r.ExitCode})" + (c != null ? $" at iteration {c.Iteration}" : ""));
                    if (c == null) { Console.WriteLine($"[{target.Name}]   no input recorded; stopping this target"); break; }
                    SaveCrash(o, c, signature, r);
                    start = c.Iteration + 1;
                    if (restart + 1 >= MaxRestarts) { Console.WriteLine($"[{target.Name}]   {MaxRestarts} crashes, stopping this target"); break; }
                }
            }

            if (o.Probes) failures += Probes.RunAll(Probes.All, o.StrictKnown);

            Console.WriteLine(failures == 0 ? "Nino fuzz: no findings." : $"Nino fuzz: FAILED ({failures} targets or probes with findings; see {Path.GetFullPath(o.FindingsDir)}).");
            return failures == 0 ? 0 : 1;
        }

        static void SaveCrash(Options o, Case c, string signature, ChildResult r)
        {
            string dir = Path.Combine(o.FindingsDir, ".state");
            string Write(string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
            string prime = c.Prime != null ? Write(c.Target + ".prime.bin", c.Prime) : null;

            bool Same(byte[] candidate)
            {
                var file = Write(c.Target + ".try.bin", candidate);
                var args = new List<string> { "repro", c.Target, file, "--placement", c.Placement.ToString().ToLowerInvariant(), "--quiet" };
                if (c.Reuse) args.Add("--reuse");
                if (prime != null) args.AddRange(new[] { "--prime", prime });
                var rr = RunChild(args, TimeSpan.FromSeconds(o.HangSeconds + 10), echo: false);
                return rr.Crashed && rr.CrashSignature() == signature;
            }

            byte[] min = null;
            string note = "(not minimized: timeouts take too long to minimize)";
            if (Findings.Saved(o.FindingsDir, c, "crash", signature))
            {
                // Seen before: minimizing it again would spend the run's time on restarts of a known crash.
                note = "(not minimized: already saved)";
            }
            else if (!r.TimedOut)
            {
                if (Same(c.Data)) min = Findings.Minimize(c.Data, Same, TimeSpan.FromSeconds(o.MinimizeSeconds * 3), 150, out note);
                else note = "(not minimized: the input alone does not crash a fresh process; see input.bin)";
            }
            var saved = Findings.Save(o.FindingsDir, c, "crash", signature, $"exit code {r.ExitCode}\nstderr:\n{r.StdErr}", min, note);
            Console.WriteLine($"[{c.Target}]   {(saved != null ? "saved in " + saved : "already saved in an earlier run")}");
        }

        /// <summary>Runs this program again with <paramref name="args"/>; kills it after <paramref name="timeout"/>.</summary>
        public static ChildResult RunChild(IList<string> args, TimeSpan timeout, bool echo)
        {
            var psi = new ProcessStartInfo { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            var host = Environment.ProcessPath;
            var entry = Assembly.GetEntryAssembly()!.Location;
            psi.FileName = host;
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(entry);
            foreach (var a in args) psi.ArgumentList.Add(a);
            // A stack overflow prints every frame: no core dumps, keep stderr short.
            psi.Environment["DOTNET_DbgEnableMiniDump"] = "0";

            var err = new BoundedText(8192, 8192);
            var output = new StringBuilder();
            var sw = Stopwatch.StartNew();
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                if (echo) Console.WriteLine(e.Data);
                else lock (output) output.AppendLine(e.Data);
            };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.Add(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            bool done = p.WaitForExit((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
            if (!done)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit();
            }
            else p.WaitForExit(); // drains the output
            return new ChildResult
            {
                ExitCode = done ? p.ExitCode : -1, TimedOut = !done, StdErr = err.ToString(), StdOut = output.ToString(), Elapsed = sw.Elapsed,
            };
        }

        /// <summary>The first and last bytes of a long text (a stack overflow's trace repeats one frame thousands of times).</summary>
        sealed class BoundedText
        {
            readonly int _head, _tail;
            readonly StringBuilder _first = new StringBuilder();
            readonly Queue<string> _last = new Queue<string>();
            int _lastLength;
            long _dropped;

            public BoundedText(int head, int tail) { _head = head; _tail = tail; }

            public void Add(string line)
            {
                lock (this)
                {
                    if (_first.Length < _head) { _first.AppendLine(line); return; }
                    _last.Enqueue(line);
                    _lastLength += line.Length + 1;
                    while (_lastLength > _tail) { _lastLength -= _last.Dequeue().Length + 1; _dropped++; }
                }
            }

            public override string ToString()
            {
                lock (this)
                    return _first + (_dropped > 0 ? $"... ({_dropped} lines left out) ...\n" : "") + string.Join("\n", _last);
            }
        }
    }
}
