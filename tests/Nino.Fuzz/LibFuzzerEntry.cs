using System;
using System.IO;
using SharpFuzz;

namespace Nino.Fuzz
{
    /// <summary>A finding inside a libFuzzer run: an exception SharpFuzz reports as a crash.</summary>
    public sealed class FindingException : Exception
    {
        public FindingException(string signature, string details) : base(signature + "\n" + details) { }
    }

    /// <summary>
    /// The SharpFuzz target of one root type, for coverage-guided fuzzing with libFuzzer (libfuzzer/run.sh): the
    /// same checks as the mutational fuzzer, minus the time alarm (libFuzzer's -timeout covers it). Each input is
    /// read fresh, against the trailing guard page for an even length and the leading one for an odd length.
    /// </summary>
    public static class LibFuzzerEntry
    {
        public static int Run(string targetName)
        {
            var target = Targets.Find(targetName);
            if (Environment.GetEnvironmentVariable("__LIBFUZZER_SHM_ID") == null)
            {
                Console.Error.WriteLine("libfuzzer: not started by a libFuzzer driver (libfuzzer/run.sh). To run one input, use: repro <target> <file>");
                return 2;
            }
            var limits = new Limits { SlowMs = double.MaxValue };
            Fuzzer.LibFuzzer.Run(span =>
            {
                var data = span.ToArray();
                var placement = (data.Length & 1) == 0 ? Placement.End : Placement.Start;
                var outcome = Executor.RunOnce(target, data, placement, false, null, limits);
                if (outcome.Kind == OutcomeKind.Finding) throw new FindingException(outcome.Signature, outcome.Details);
            });
            return 0;
        }

        /// <summary>Writes every target's seed inputs to dir/target/seed-N.bin (libFuzzer's starting corpus).</summary>
        public static int WriteSeeds(string dir)
        {
            foreach (var t in Targets.All)
            {
                var d = Path.Combine(dir, t.Name);
                Directory.CreateDirectory(d);
                var seeds = t.Seeds();
                for (int i = 0; i < seeds.Count; i++) File.WriteAllBytes(Path.Combine(d, $"seed-{i}.bin"), seeds[i]);
                Console.WriteLine($"{d}: {seeds.Count} seeds");
            }
            return 0;
        }
    }
}
