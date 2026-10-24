using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nino.Core;

namespace Nino.Fuzz
{
    /// <summary>
    /// Hand-made inputs for problems mutation does not reach (thousands of nesting levels, keys chosen to collide),
    /// each run in a child process with a timeout, so a stack overflow fails the probe instead of the fuzzer.
    /// </summary>
    public sealed class Probe
    {
        public string Name;
        public string What;
        /// <summary>
        /// A known open bug: its failure is reported, but fails the run only with --strict-known. Set it to null once
        /// the fix is merged, so the probe guards against the bug coming back.
        /// </summary>
        public string Known;
        public Func<string> Run; // returns null when the probe passes, the reason otherwise
        public double TimeoutSeconds = 90;
    }

    public static class Probes
    {
        public static readonly IReadOnlyList<Probe> All = new List<Probe>
        {
            new Probe
            {
                Name = "tree", What = "a class deriving from List of itself, 200,000 levels deep",
                Run = () => ExpectRejectOrValue(() => NinoDeserializer.Deserialize<TreeHolder>(
                    Cat(new byte[] { 1 }, Rep(Ints(1), 200_000), Ints(0), Ints(-1)))),
            },
            new Probe
            {
                Name = "dtree", What = "a class deriving from Dictionary<string, itself>, 200,000 levels deep",
                Run = () => ExpectRejectOrValue(() => NinoDeserializer.Deserialize<TreeHolder>(
                    Cat(new byte[] { 1 }, Ints(-1), Rep(Ints(1, 0), 200_000), Ints(0)))),
            },
            new Probe
            {
                Name = "hash", What = "a Dictionary<long, int> of 50,000 keys that all hash to 0, against 50,000 spread keys",
                Run = () => Collisions(50_000, keys => new HashColl { D = keys.ToDictionary(k => k, _ => 0) }),
            },
            new Probe
            {
                Name = "hash-set", What = "a HashSet<long> of 50,000 keys that all hash to 0, against 50,000 spread keys",
                Run = () => Collisions(50_000, keys => new HashColl { H = keys.ToHashSet() }),
            },
            // Nesting through each kind of member, 100,000 levels on a 1 MB stack (a Windows or Unity thread):
            // Nino's depth limit must reject it before the stack runs out.
            Depth("depth-list", "a class holding a List of itself",
                n => NinoDeserializer.Deserialize<Rec>(Cat(Rep(Cat(new byte[] { 1 }, Ints(0, 1)), n), new byte[] { 1 }, Ints(0, -1)))),
            Depth("depth-struct-array", "a struct holding an array of itself",
                n => NinoDeserializer.Deserialize<SRec>(Cat(Rep(Ints(0, 1), n), Ints(0, -1)))),
            Depth("depth-dictionary", "a class holding a Dictionary of itself",
                n => NinoDeserializer.Deserialize<DictRec>(Cat(Rep(Cat(new byte[] { 1 }, Ints(1, 0)), n), new byte[] { 1 }, Ints(-1)))),
            Depth("depth-lazy", "a class holding a Lazy of itself",
                n => NinoDeserializer.Deserialize<LazyRec>(Cat(Rep(new byte[] { 1, 1 }, n), new byte[] { 0 }))),
            Depth("depth-keyvaluepair", "a class holding a KeyValuePair with itself",
                n => NinoDeserializer.Deserialize<KvRec>(Cat(Rep(Cat(new byte[] { 1 }, Ints(0)), n), new byte[] { 0 }))),
            Depth("depth-tuple", "a class holding a Tuple with itself",
                n => NinoDeserializer.Deserialize<TupleRec>(Cat(Rep(Cat(new byte[] { 1, 1 }, Ints(0)), n), new byte[] { 0 }))),
        };

        /// <summary>Runs every probe in its own process; returns the number that failed the run.</summary>
        public static int RunAll(IEnumerable<Probe> probes, bool strictKnown)
        {
            int failures = 0;
            Console.WriteLine($"Probes ({(strictKnown ? "strict: known issues fail the run" : "known issues reported only; --strict-known to fail on them")}):");
            foreach (var p in probes)
            {
                var r = Supervisor.RunChild(new[] { "probe", p.Name }, TimeSpan.FromSeconds(p.TimeoutSeconds), echo: false);
                bool pass = !r.Crashed && r.ExitCode == 0;
                string why = r.Crashed ? $"{r.CrashSignature()} after {r.Elapsed.TotalSeconds:F1} s" : r.StdOut.Trim();
                string verdict;
                if (pass) verdict = p.Known != null ? "PASS (known issue looks fixed: remove its Known note in Probes.cs)" : "PASS";
                else if (p.Known != null && !strictKnown) verdict = "KNOWN";
                else { verdict = "FAIL"; failures++; }
                Console.WriteLine($"  {verdict,-5} {p.Name}: {p.What}" + (pass ? (why.Length > 0 ? $" ({why})" : "") : $"\n        {why}") +
                                  (!pass && p.Known != null ? $"\n        known issue: {p.Known}" : ""));
                if (!pass && r.StdErr.Length > 0 && (p.Known == null || strictKnown))
                    Console.WriteLine("        " + string.Join("\n        ", r.StdErr.Split('\n').Take(12)));
            }
            return failures;
        }

        /// <summary>The child side: runs one probe, prints why it failed or a note, exits 0 when it passed.</summary>
        public static int RunOne(string name)
        {
            var p = All.FirstOrDefault(x => x.Name == name);
            if (p == null) { Console.Error.WriteLine($"unknown probe '{name}'; probes: {string.Join(", ", All.Select(x => x.Name))}"); return 2; }
            var failure = p.Run();
            if (failure == null) return 0;
            Console.WriteLine(failure);
            return 1;
        }

        static Probe Depth(string name, string what, Action<int> read) => new Probe
        {
            Name = name, What = what + ", 100,000 levels on a 1 MB stack",
            Run = () =>
            {
                string result = null;
                var t = new Thread(() => result = ExpectRejectOrValue(() => read(100_000)), 1 << 20);
                t.Start();
                t.Join();
                return result;
            },
        };

        static string ExpectRejectOrValue(Action read)
        {
            try { read(); return null; }
            catch (Exception e) when (Executor.IsRejection(e)) { return null; }
            catch (Exception e) { return "unexpected " + e.GetType().Name + ": " + e.Message.Split('\n')[0] + " at " + Executor.TopFrame(e); }
        }

        static string Collisions(int n, Func<IEnumerable<long>, HashColl> make)
        {
            // long.GetHashCode is (int)value ^ (int)(value >> 32): these keys all hash to 0.
            var colliding = NinoSerializer.Serialize(make(Enumerable.Range(1, n).Select(i => ((long)i << 32) | (uint)i)));
            var spread = NinoSerializer.Serialize(make(Enumerable.Range(1, n).Select(i => (long)i)));
            NinoDeserializer.Deserialize<HashColl>(spread); // warm up
            // Reading the colliding keys may be rejected (NinoSettings.MaxHashCollisions): then the time to the
            // rejection counts.
            double Time(byte[] data)
            {
                var sw = Stopwatch.StartNew();
                try { NinoDeserializer.Deserialize<HashColl>(data); }
                catch (Exception e) when (Executor.IsRejection(e)) { }
                return sw.Elapsed.TotalMilliseconds;
            }
            double fast = Time(spread);
            double slow = Time(colliding);
            double limit = Math.Max(1000, 50 * fast);
            if (slow <= limit) return null;
            // For comparison: the same keys added one at a time through the public API.
            var sw = Stopwatch.StartNew();
            make(Enumerable.Range(1, n).Select(i => ((long)i << 32) | (uint)i));
            return $"colliding keys took {slow:F0} ms, spread keys {fast:F1} ms (limit {limit:F0} ms; " +
                   $"the BCL adding the colliding keys one by one: {sw.Elapsed.TotalMilliseconds:F0} ms)";
        }

        static byte[] Ints(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

        static byte[] Rep(byte[] unit, int n)
        {
            var r = new byte[unit.Length * n];
            for (int i = 0; i < n; i++) unit.CopyTo(r, i * unit.Length);
            return r;
        }

        static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    }
}
