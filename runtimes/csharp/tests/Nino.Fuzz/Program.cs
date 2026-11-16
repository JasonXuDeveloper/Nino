using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nino.Fuzz
{
    public static class Program
    {
        const string Usage = @"Nino.Fuzz: fuzzes Nino's deserializer (README.md).

  fuzz [options]                 fuzz every target (or --targets) in worker processes, then run the probes
  repro <target> <file> [--placement end|start|heap] [--reuse] [--prime <file>] [--minimize]
                                 run one input as the fuzzer did; exit 1 when it is still a finding;
                                 --minimize writes the smallest input with the same finding to <file>.min
  probes [name ...] [--strict-known]
                                 run the hand-made probes (stack depth, hash collisions)
  list                           list the targets and probes
  targets                        the target names, one a line
  selftest                       check that the fuzzer finds planted crashes, hangs and exceptions
  seeds <dir>                    write each target's seed inputs to <dir>/<target>/ (libFuzzer corpus)
  libfuzzer <target>             the SharpFuzz target, started by a libFuzzer driver (libfuzzer/run.sh)

fuzz options:
  --seconds <n>         total time over all targets (default 60)
  --seed <n>            the run's seed (default 1); the same seed gives the same inputs
  --targets <a,b,...>   targets to fuzz (default: all)
  --iterations <n>      stop each target after n inputs
  --findings <dir>      where findings go (default tests/Nino.Fuzz/findings)
  --strict-known        known open bugs (probes marked Known) fail the run too (also NINO_FUZZ_STRICT_KNOWN=1)
  --no-probes           skip the probes
  --no-reuse            never read into an existing value
  --no-roundtrip        skip the write/read/write comparison
  --slow-ms <n>         a read slower than this, twice, is a finding (default 250)
  --hang-seconds <n>    a read slower than this ends the worker as a hang (default 20)
  --alloc-factor <n>    a read may allocate n bytes per input byte plus 1 MB (default 64)
  --minimize-seconds <n> time to minimize each finding (default 20)

Exit code: 0 no findings, 1 findings (or failed probes), 2 bad arguments.";

        public static int Main(string[] args)
        {
            try
            {
                string command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "fuzz";
                var rest = args.Length > 0 && !args[0].StartsWith("--") ? args.Skip(1).ToList() : args.ToList();
                switch (command)
                {
                    case "fuzz": return Supervisor.Fuzz(Parse(rest, out _));
                    case "worker":
                    {
                        var o = Parse(rest, out _);
                        return Worker.Run(Targets.Find(o.Targets.Single()), o);
                    }
                    case "repro": return Repro(rest);
                    case "probes":
                    {
                        var o = Parse(rest, out var names);
                        var probes = names.Count == 0 ? Probes.All : names.Select(n => Probes.All.FirstOrDefault(p => p.Name == n) ?? throw new ArgumentException("unknown probe " + n)).ToList();
                        return Probes.RunAll(probes, o.StrictKnown) == 0 ? 0 : 1;
                    }
                    case "probe": return Probes.RunOne(rest.Single());
                    case "list": return List();
                    case "selftest": return SelfTest.Run();
                    case "targets": foreach (var t in Targets.All) Console.WriteLine(t.Name); return 0;
                    case "seeds": return LibFuzzerEntry.WriteSeeds(rest.Single());
                    case "libfuzzer": return LibFuzzerEntry.Run(rest.FirstOrDefault() ?? Environment.GetEnvironmentVariable("NINO_FUZZ_TARGET"));
                    case "help": case "-h": case "--help": Console.WriteLine(Usage); return 0;
                    default: throw new ArgumentException("unknown command " + command);
                }
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                Console.Error.WriteLine();
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }

        static Options Parse(List<string> args, out List<string> positional)
        {
            var o = new Options();
            positional = new List<string>();
            o.StrictKnown = Environment.GetEnvironmentVariable("NINO_FUZZ_STRICT_KNOWN") == "1";
            for (int i = 0; i < args.Count; i++)
            {
                string Next() => i + 1 < args.Count ? args[++i] : throw new ArgumentException(args[i] + " needs a value");
                double Number() => double.Parse(Next(), CultureInfo.InvariantCulture);
                switch (args[i])
                {
                    case "--seconds": o.Seconds = Number(); break;
                    case "--seed": o.Seed = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--targets": case "--target": o.Targets.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                    case "--iterations": o.Iterations = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--start": o.StartIteration = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--findings": o.FindingsDir = Next(); break;
                    case "--strict-known": o.StrictKnown = true; break;
                    case "--no-probes": o.Probes = false; break;
                    case "--no-reuse": o.Reuse = false; break;
                    case "--no-roundtrip": o.Limits.RoundTrip = false; break;
                    case "--slow-ms": o.Limits.SlowMs = Number(); break;
                    case "--hang-seconds": o.HangSeconds = Number(); break;
                    case "--alloc-factor": o.Limits.AllocFactor = (long)Number(); break;
                    case "--minimize-seconds": o.MinimizeSeconds = Number(); break;
                    default:
                        if (args[i].StartsWith("--")) throw new ArgumentException("unknown option " + args[i]);
                        positional.Add(args[i]);
                        break;
                }
            }
            foreach (var t in o.Targets) Targets.Find(t);
            return o;
        }

        static int Repro(List<string> args)
        {
            if (args.Count < 2) throw new ArgumentException("repro needs a target and a file");
            var target = Targets.Find(args[0]);
            target.Configure();
            var data = File.ReadAllBytes(args[1]);
            var placement = Placement.End;
            bool reuse = false, quiet = false, minimize = false;
            byte[] prime = null;
            for (int i = 2; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "--placement": placement = Enum.Parse<Placement>(args[++i], ignoreCase: true); break;
                    case "--reuse": reuse = true; break;
                    case "--prime": prime = File.ReadAllBytes(args[++i]); reuse = true; break;
                    case "--quiet": quiet = true; break;
                    case "--minimize": minimize = true; break;
                    default: throw new ArgumentException("unknown option " + args[i]);
                }
            }
            if (!quiet)
            {
                Console.WriteLine($"{target.Name}: {data.Length} bytes, placement {Guard.Effective(placement, data.Length)}{(reuse ? ", reuse" : "")}");
                Console.WriteLine($"  {Findings.Environment()}");
            }
            var outcome = Executor.Run(target, data, placement, reuse, prime, new Limits());
            if (minimize && outcome.Kind == OutcomeKind.Finding)
            {
                var signature = outcome.Signature;
                var min = Findings.Minimize(data, d => Executor.Run(target, d, placement, reuse, prime, new Limits()) is { Kind: OutcomeKind.Finding } r && r.Signature == signature,
                    TimeSpan.FromSeconds(60), 20000, out var note);
                var path = args[1] + ".min";
                File.WriteAllBytes(path, min);
                Console.WriteLine($"minimized to {min.Length} bytes {note}: {path}\n{(min.Length <= 4096 ? Convert.ToHexString(min) : "")}");
                outcome = Executor.Run(target, min, placement, reuse, prime, new Limits());
            }
            if (!quiet)
            {
                Console.WriteLine(outcome.Kind == OutcomeKind.Finding ? $"FINDING {outcome.Signature}\n{outcome.Details}" :
                    outcome.Kind == OutcomeKind.Rejected ? "rejected by Nino (no finding)" : $"read in {outcome.Milliseconds:F2} ms, {outcome.Allocated:N0} bytes allocated (no finding)");
            }
            return outcome.Kind == OutcomeKind.Finding ? 1 : 0;
        }

        static int List()
        {
            Console.WriteLine("Targets:");
            foreach (var t in Targets.All) Console.WriteLine($"  {t.Name,-8} {t.Covers}");
            Console.WriteLine("Probes:");
            foreach (var p in Probes.All) Console.WriteLine($"  {p.Name,-20} {p.What}{(p.Known != null ? " [known: " + p.Known + "]" : "")}");
            return 0;
        }
    }
}
