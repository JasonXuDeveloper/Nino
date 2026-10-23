using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nino.Core;

namespace Nino.Fuzz
{
    /// <summary>
    /// Checks the fuzzer itself: a target with planted bugs, triggered by the first byte of the input, must produce
    /// each kind of finding (a stack overflow and an access violation that kill the worker, a hang, an exception).
    /// Run by the selftest command, and in CI before fuzzing, so a platform where the supervisor or the guard pages
    /// do not work fails loudly instead of reporting nothing.
    /// </summary>
    public static class SelfTest
    {
        public static readonly Target Target = new Target
        {
            Name = "SelfTest",
            Covers = "planted bugs, to test the fuzzer (selftest command)",
            Seeds = () => new List<byte[]> { NinoSerializer.Serialize(Samples.Chain(1)) },
            Read = Read,
            ReadInto = null,
            Fresh = () => null,
            Write = value => NinoSerializer.Serialize((Node)value),
        };

        static object Read(ReadOnlySpan<byte> data)
        {
            if (data.Length > 0)
            {
                switch (data[0])
                {
                    case 0xF1: return Recurse(data.Length);
                    case 0xF2: return OnePastTheEnd(data);
                    case 0xF3: throw new ArgumentException("planted exception");
                    case 0xF4: Thread.Sleep(Timeout.Infinite); break;
                }
            }
            return NinoDeserializer.Deserialize<Node>(data);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static object Recurse(int n) => n == int.MinValue ? null : Recurse(n + 1) ?? new object();

        /// <summary>Reads the byte after the input: faults against the trailing guard page.</summary>
        static object OnePastTheEnd(ReadOnlySpan<byte> data)
        {
            byte b = Unsafe.Add(ref MemoryMarshal.GetReference(data), data.Length);
            return b == 0x5A ? null : NinoDeserializer.Deserialize<Node>(data);
        }

        public static int Run()
        {
            string dir = Path.Combine(Path.GetTempPath(), "nino-fuzz-selftest-" + Environment.ProcessId);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var o = new Options
            {
                Seconds = 45, Targets = { Target.Name }, FindingsDir = dir, Probes = false, HangSeconds = 1, MinimizeSeconds = 2,
            };
            Supervisor.Fuzz(o);
            var found = Directory.Exists(Path.Combine(dir, Target.Name))
                ? Directory.GetDirectories(Path.Combine(dir, Target.Name)).Select(d => File.ReadLines(Path.Combine(d, "info.txt")).First()).ToList()
                : new List<string>();
            var expected = new List<string> { "CRASH stack overflow", "EXCEPTION ArgumentException", "HANG" };
            if (Guard.Available) expected.Add("CRASH access violation");
            int missing = 0;
            Console.WriteLine("Self test:");
            foreach (var e in expected)
            {
                bool ok = found.Any(f => f.Contains(e));
                if (!ok) missing++;
                Console.WriteLine($"  {(ok ? "found  " : "MISSING")} {e}");
            }
            foreach (var f in found) Console.WriteLine($"  saved: {f}");
            if (missing == 0) Directory.Delete(dir, true);
            else Console.WriteLine($"  findings kept in {dir}");
            return missing == 0 ? 0 : 1;
        }
    }
}
