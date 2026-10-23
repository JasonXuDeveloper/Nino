using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Nino.Core;

namespace Nino.Fuzz
{
    public enum OutcomeKind
    {
        /// <summary>Read, passed every check.</summary>
        Ok,
        /// <summary>Nino rejected the data the way it documents (NinoDataException "Nino: ...").</summary>
        Rejected,
        /// <summary>Anything else: a finding.</summary>
        Finding,
    }

    public sealed class Outcome
    {
        public OutcomeKind Kind;
        /// <summary>For a finding: what went wrong, without input-specific values (findings are grouped by it).</summary>
        public string Signature;
        public string Details;
        public object Value;
        public double Milliseconds;
        public long Allocated;

        public static readonly Outcome Rejected = new Outcome { Kind = OutcomeKind.Rejected };
    }

    /// <summary>The alarms' limits (Program.cs options).</summary>
    public sealed class Limits
    {
        /// <summary>A read slower than this (twice in a row) is a finding.</summary>
        public double SlowMs = 250;
        /// <summary>A read may allocate up to AllocFactor bytes per input byte, plus AllocSlack.</summary>
        public long AllocFactor = 64;
        public long AllocSlack = 1 << 20;
        /// <summary>Re-serialize what was read and compare a second round trip.</summary>
        public bool RoundTrip = true;
    }

    /// <summary>Runs one input against a target and classifies what happened.</summary>
    public static class Executor
    {
        /// <param name="prime">For a reuse read: the input whose value is read into (null: the target's fresh value).</param>
        public static Outcome Run(Target target, byte[] data, Placement placement, bool reuse, byte[] prime, Limits limits)
        {
            var outcome = RunOnce(target, data, placement, reuse, prime, limits);
            // A slow read is confirmed by a second one, so a GC pause or a page fault does not count.
            if (outcome.Kind != OutcomeKind.Finding && outcome.Milliseconds > limits.SlowMs)
            {
                var again = RunOnce(target, data, placement, reuse, prime, limits);
                if (again.Kind != OutcomeKind.Finding && again.Milliseconds > limits.SlowMs)
                    return new Outcome
                    {
                        Kind = OutcomeKind.Finding, Signature = "SLOW",
                        Details = $"read took {outcome.Milliseconds:F0} ms, then {again.Milliseconds:F0} ms (limit {limits.SlowMs} ms) for {data.Length} bytes",
                    };
                return again;
            }
            return outcome;
        }

        public static Outcome RunOnce(Target target, byte[] data, Placement placement, bool reuse, byte[] prime, Limits limits)
        {
            object existing = null;
            if (reuse)
            {
                try { existing = prime == null ? target.Fresh() : target.Read(prime); }
                catch (Exception e) when (IsRejection(e)) { existing = target.Fresh(); }
                catch (Exception e) { return Finding("PRIME " + Describe(e), e); }
            }

            var span = Guard.Place(data, placement);
            object value;
            long t0, t1, a0, a1;
            try
            {
                a0 = GC.GetAllocatedBytesForCurrentThread();
                t0 = Stopwatch.GetTimestamp();
                value = reuse ? target.ReadInto(span, existing) : target.Read(span);
                t1 = Stopwatch.GetTimestamp();
                a1 = GC.GetAllocatedBytesForCurrentThread();
            }
            catch (Exception e) when (IsRejection(e))
            {
                return Outcome.Rejected;
            }
            catch (Exception e)
            {
                return Finding("EXCEPTION " + Describe(e), e);
            }

            double ms = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
            long allocated = a1 - a0;
            long allowed = limits.AllocFactor * data.Length + limits.AllocSlack;
            if (allocated > allowed)
                return new Outcome
                {
                    Kind = OutcomeKind.Finding, Signature = "ALLOC",
                    Details = $"read allocated {allocated:N0} bytes for {data.Length} input bytes (limit {allowed:N0}: {limits.AllocFactor} per byte + {limits.AllocSlack:N0})",
                };

            try
            {
                Invariants.Check(value);
            }
            catch (InvariantException e)
            {
                return Finding("INVARIANT " + Regex.Replace(e.Message, @"-?\d+", "#"), e);
            }
            catch (Exception e)
            {
                return Finding("CHECK " + Describe(e), e);
            }

            if (limits.RoundTrip)
            {
                // What Nino read, it must write; and what it wrote, it must read back to the same bytes.
                byte[] first, second;
                try { first = target.Write(value); }
                catch (Exception e) { return Finding("WRITE " + Describe(e), e); }
                object back;
                try { back = target.Read(first); }
                catch (Exception e) { return Finding("REREAD " + Describe(e), e); }
                try { second = target.Write(back); }
                catch (Exception e) { return Finding("REWRITE " + Describe(e), e); }
                // Hash collections may enumerate in another order once rebuilt (ConcurrentDictionary's order depends
                // on its growth): bytes that differ only by that are fine.
                if (!first.AsSpan().SequenceEqual(second) && Invariants.Fingerprint(value) != Invariants.Fingerprint(back))
                {
                    int at = 0;
                    while (at < first.Length && at < second.Length && first[at] == second[at]) at++;
                    return new Outcome
                    {
                        Kind = OutcomeKind.Finding, Signature = "ROUNDTRIP value differs",
                        Details = $"the values differ (not only by the order of hash collections); write(read(input)) has {first.Length} bytes, write(read(write(read(input)))) {second.Length}; first difference at {at}\n" +
                                  $"first:  {Hex(first, at)}\nsecond: {Hex(second, at)}",
                    };
                }
            }

            return new Outcome { Kind = OutcomeKind.Ok, Value = value, Milliseconds = ms, Allocated = allocated };
        }

        /// <summary>
        /// What Nino documents for bad data: a NinoDataException with a "Nino: " message. Anything else, an
        /// InvalidOperationException included, is a finding.
        /// </summary>
        public static bool IsRejection(Exception e) =>
            e is NinoDataException && e.Message.StartsWith("Nino: ", StringComparison.Ordinal);

        static Outcome Finding(string signature, Exception e) => new Outcome
        {
            Kind = OutcomeKind.Finding, Signature = signature, Details = e.ToString(),
        };

        /// <summary>The exception type and the first frame that is not a throw helper.</summary>
        public static string Describe(Exception e)
        {
            if (e is System.Reflection.TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e.GetType().Name + " at " + TopFrame(e);
        }

        public static string TopFrame(Exception e)
        {
            var frames = new StackTrace(e, false).GetFrames();
            if (frames == null) return "?";
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m == null) continue;
                var type = m.DeclaringType?.FullName ?? "";
                if (type.StartsWith("System.ThrowHelper", StringComparison.Ordinal) ||
                    type.StartsWith("System.Runtime.CompilerServices", StringComparison.Ordinal) ||
                    type.Contains("NinoThrow")) continue;
                return Regex.Replace(type, @"`\d+(\[.*\])?", "") + "." + m.Name;
            }
            return "?";
        }

        static string Hex(byte[] b, int at)
        {
            int from = Math.Max(0, at - 16);
            int len = Math.Min(b.Length - from, 48);
            return len <= 0 ? "(end)" : $"@{from}: " + Convert.ToHexString(b, from, len);
        }
    }
}
