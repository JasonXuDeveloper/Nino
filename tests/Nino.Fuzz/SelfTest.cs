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

        /// <summary>
        /// A planted stale array: every read into an existing value stores a new array, uninitialized and on the large
        /// object heap, before the data is checked, so each input Nino rejects leaves it holding freed memory (what
        /// <see cref="StaleMemory"/> finds). A target of its own: a poisoned one costs a collection per rejected input,
        /// which slowed the planted crashes past the self test's time.
        /// </summary>
        public static readonly Target Stale = new Target
        {
            Name = "SelfTestStale",
            Covers = "a planted stale array, to test the fuzzer (selftest command)",
            Seeds = Target.Seeds,
            Read = data => NinoDeserializer.Deserialize<Node>(data),
            ReadInto = ReadInto,
            Fresh = () => new Existing(),
            Poisoned = true,
            Write = Target.Write,
        };

        /// <summary>The planted bugs' targets.</summary>
        public static readonly Target[] All = { Target, Stale };

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

        /// <summary>What a reuse read goes into: a node, and the slot the stale array is planted in.</summary>
        sealed class Existing
        {
            public Node Node = Samples.Chain(1);
            public long[] Planted = { 1 };
        }

        static object ReadInto(ReadOnlySpan<byte> data, object existing)
        {
            // The value of another input (a node) when the fuzzer reads into one.
            var into = existing as Existing ?? new Existing { Node = existing as Node };
            // Up to as many as the poisoned memory holds, until one took some of it (the first, as a rule).
            for (int i = 0; i < 64 && (i == 0 || StaleMemory.Find(into.Planted) == null); i++)
                into.Planted = GC.AllocateUninitializedArray<long>(20_000);
            NinoDeserializer.Deserialize(data, ref into.Node);
            // Filled once the read succeeded, as a read that keeps its array would have.
            Array.Clear(into.Planted);
            return into.Node;
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
            o.Seconds = 5;
            o.Targets = new List<string> { Stale.Name };
            Supervisor.Fuzz(o);
            var found = All.Select(t => Path.Combine(dir, t.Name)).Where(Directory.Exists).SelectMany(Directory.GetDirectories)
                .Select(d => File.ReadLines(Path.Combine(d, "info.txt")).First()).ToList();
            var expected = new List<string> { "CRASH stack overflow", "EXCEPTION ArgumentException", "HANG", "STALE" };
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
