// Compiled only into the player the release benchmark builds (NINO_UNITY_BENCH, added by
// Test.Editor.Burst.BenchmarkPlayerBuild when the editor runs with -ninoBenchmark): never in the editor, never in the
// player of the ordinary test runs.
#if NINO_BURST && NINO_UNITY_BENCH
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using Nino.Burst;
using Nino.Core;
using NUnit.Framework;
using Unity.Burst;
using Unity.PerformanceTesting;
using UnityEngine;

namespace Test.Runtime.Burst
{
    /// <summary>
    /// The release benchmark of the Burst kernels, measured with the Unity Performance Testing package, on the .NET
    /// suite's cases (runtimes/csharp/benchmarks): bench/generate.sh copies its models into Cases/ and writes the case
    /// list (BenchCases.cs, AddCases) and each case's Nino bytes (StreamingAssets/NinoBench), which the player reads
    /// before it times anything. Each case is a call a user makes (NinoSerializer.Serialize of the value read to a new
    /// array, NinoDeserializer.Deserialize of .NET's bytes to a new value, as the .NET suite times them), measured by
    /// <c>Measure.Method</c> (its own warm-up and probing, then 9 measurements of about 100 ms, each the mean time per
    /// call) once per sample group:
    /// <list type="bullet">
    /// <item>"Without Burst": NinoKernels.Register(null), Nino.Core's own paths (as without com.unity.burst);</item>
    /// <item>"SSSE3 only" (AVX2 CPUs): the kernels limited to their 16-byte paths, to compare the two tiers;</item>
    /// <item>"With Burst": the kernels as a player registers them at startup.</item>
    /// </list>
    /// The package writes each test's result (##performancetestresult2:) and the run's metadata
    /// (##performancetestruninfo2: Unity, platform, CPU, packages) into the test output, which the player's results XML
    /// keeps; runtimes/csharp/benchmarks/reports/unity_report.py turns it into the release's Unity section. The one line of its own here,
    /// NINO_UNITY_ENV, names what the package cannot know: the kernels' instruction set and the process architecture.
    /// Nothing here asserts a speed: a test fails only when Burst or the kernels are missing or when the bytes are
    /// wrong (see Check).
    /// </summary>
    public partial class BurstBenchmark
    {
        /// <summary>
        /// A case: its name (the .NET suite's), and the calls each test measures, set by Prepare (the bytes read in
        /// the case's own tests, so a case that cannot be read fails alone).
        /// </summary>
        public sealed class Case
        {
            public string Name;
            public bool Old;
            public Action Prepare;
            public byte[] Bytes;
            public Func<byte[]> Serialize;
            public Action Deserialize;
            public Func<byte[]> ReadBack;
            public override string ToString() => Name;
        }

        private static List<Case> _cases;

        public static IEnumerable<TestCaseData> Cases() =>
            (_cases ??= Build()).Select(c => new TestCaseData(c).SetName(c.Name));

        private static List<Case> Build()
        {
            var cases = new List<Case>();
            AddCases(cases);
            return cases;
        }

        /// <summary>
        /// A case of type <typeparamref name="T"/>: .NET's Nino bytes, the value they hold, and the calls on it. An
        /// <paramref name="old"/> case's bytes are an earlier version of its type (version tolerance), so they are read
        /// but not what the value writes.
        /// </summary>
        private static Case Load<T>(string name, bool old)
        {
            var c = new Case { Name = name, Old = old };
            c.Prepare = () =>
            {
                if (c.Bytes != null)
                    return;
                byte[] bytes = File.ReadAllBytes(
                    Path.Combine(Application.streamingAssetsPath, "NinoBench", name + ".nino.bin"));
                T value = NinoDeserializer.Deserialize<T>(bytes);
                c.Serialize = () => NinoSerializer.Serialize(value);
                c.Deserialize = () => NinoDeserializer.Deserialize<T>(bytes);
                c.ReadBack = () => NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(bytes));
                c.Bytes = bytes;
            };
            return c;
        }

        [OneTimeSetUp]
        public void Environment()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled, "Burst is off");
            Assert.IsTrue(NinoBurst.Register(), "the kernels did not register");
            _groups = NinoBurst.Isa == NinoBurst.Avx2
                ? new[] { ("Without Burst", -1), ("SSSE3 only", NinoBurst.Ssse3), ("With Burst", NinoBurst.Avx2) }
                : new[] { ("Without Burst", -1), ("With Burst", NinoBurst.Avx2) };
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}",
                $"NINO_UNITY_ENV|isa={NinoBurst.IsaName}|arch={RuntimeInformation.ProcessArchitecture}");
        }

        [OneTimeTearDown]
        public void Restore() => NinoBurst.Register();

        [Test, Performance, TestCaseSource(nameof(Cases))]
        public void Serialize(Case c)
        {
            Check(c);
            Time(() => c.Serialize());
        }

        [Test, Performance, TestCaseSource(nameof(Cases))]
        public void Deserialize(Case c)
        {
            Check(c);
            Time(c.Deserialize);
        }

        /// <summary>
        /// The bytes of the case: on every path the value writes the same bytes, which read back to themselves, and which
        /// are .NET's (but for an old case, whose bytes are another version's). A collection whose order is its
        /// implementation's (ConcurrentDictionary's buckets) may list its items in another order than CoreCLR's, so a
        /// difference of the same length is only logged.
        /// </summary>
        private static void Check(Case c)
        {
            c.Prepare();
            byte[] expected = null;
            string first = null;
            foreach (var (group, isa) in _groups)
            {
                Use(isa);
                byte[] bytes = c.Serialize();
                if (expected == null)
                {
                    expected = bytes;
                    first = group;
                }

                // Which side is .NET's tells which path is wrong (an old case's bytes are another version's).
                string Net(byte[] b) => c.Old ? "" : b.SequenceEqual(c.Bytes) ? ", .NET's: the first" : ", .NET's: neither";
                Same(expected, bytes, $"{c.Name}: the value writes other bytes ({group}) than ({first}){Net(expected)}");
                byte[] back = c.ReadBack();
                Same(expected, back,
                    $"{c.Name}: .NET's bytes read ({group}) write other bytes than the value read ({first}){(back.SequenceEqual(c.Bytes) ? ", .NET's: the second" : Net(expected))}");
            }

            NinoBurst.Register();
            if (c.Old || expected.SequenceEqual(c.Bytes))
                return;
            if (c.Bytes.Length != expected.Length)
                Same(c.Bytes, expected, $"{c.Name}: not .NET's bytes");
            Debug.LogWarning($"{c.Name}: .NET's bytes in another order");
        }

        /// <summary>Fails with where <paramref name="actual"/> first differs, and the bytes around it on both sides.</summary>
        private static void Same(byte[] expected, byte[] actual, string message)
        {
            int n = Math.Min(expected.Length, actual.Length), at = 0;
            while (at < n && expected[at] == actual[at])
                at++;
            if (at == n && expected.Length == actual.Length)
                return;
            int from = Math.Max(0, at - 8);
            string Hex(byte[] b) => BitConverter.ToString(b, Math.Min(from, b.Length), Math.Max(0, Math.Min(40, b.Length - from)));
            Assert.Fail($"{message}: {expected.Length} and {actual.Length} bytes, first difference at {at}; from {from}:\n" +
                        $"  {Hex(expected)}\n  {Hex(actual)}");
        }

        // The sample groups and the kernels each measures (-1: none), set by Environment.
        private static (string, int)[] _groups;

        private static void Use(int isa)
        {
            if (isa < 0)
                NinoBurst.Unregister();
            else
                Assert.IsTrue(NinoBurst.Register(isa), "the kernels did not register");
        }

        /// <summary>
        /// The memory of a <c>SimpleStruct</c> (int + DateTime, Pack = 4) and of a <c>SimpleStruct?</c> on this player,
        /// against the bytes Nino writes for them, logged for #378: only IL2CPP read those values back as other bytes,
        /// and its layouts cannot be seen from CoreCLR or Mono.
        /// </summary>
        [Test, Order(0)] // first: the cases after it read and write these values
        public unsafe void LayoutOfNullableStruct()
        {
            var value = new Nino.Benchmark.SimpleStruct { Id = 0x11223344, CreateTime = new DateTime(0x0123456789ABCDEFL) };
            Nino.Benchmark.SimpleStruct? nullable = value;
            string Bytes(void* p, int n) => BitConverter.ToString(new ReadOnlySpan<byte>(p, n).ToArray());
            void Log(string line) => Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", "NINO_LAYOUT|" + line);
            // The memory first, before Nino touches the values: a write past a value's end corrupts the heap.
            Log($"SizeOf<SimpleStruct>={System.Runtime.CompilerServices.Unsafe.SizeOf<Nino.Benchmark.SimpleStruct>()}" +
                $"|SizeOf<SimpleStruct?>={System.Runtime.CompilerServices.Unsafe.SizeOf<Nino.Benchmark.SimpleStruct?>()}" +
                $"|value={Bytes(&value, System.Runtime.CompilerServices.Unsafe.SizeOf<Nino.Benchmark.SimpleStruct>())}" +
                $"|nullable={Bytes(&nullable, System.Runtime.CompilerServices.Unsafe.SizeOf<Nino.Benchmark.SimpleStruct?>())}" +
                $"|generic:SizeOf<T>={SizeOf<Nino.Benchmark.SimpleStruct>()},SizeOf<T?>={SizeOf<Nino.Benchmark.SimpleStruct?>()}" +
                $",value={Dump(value)},nullable={Dump(nullable)}");
            Log($"plain={BitConverter.ToString(NinoSerializer.Serialize(value))}");
            Log($"single={BitConverter.ToString(NinoSerializer.Serialize(nullable))}");
            var arrayBytes = NinoSerializer.Serialize(new Nino.Benchmark.SimpleStruct?[] { value, null, value });
            Log($"array={BitConverter.ToString(arrayBytes)}");
            var back = NinoDeserializer.Deserialize<Nino.Benchmark.SimpleStruct?[]>(arrayBytes);
            var backValue = back[2].GetValueOrDefault();
            Log($"back[2]={backValue.Id:X8},{backValue.CreateTime.Ticks:X16},{back[1].HasValue}");
            Assert.AreEqual(value.Id, backValue.Id, "Id read back");
            Assert.AreEqual(value.CreateTime, backValue.CreateTime, "CreateTime read back");
        }

        // The same, from shared generic code: IL2CPP may lay a type out otherwise there than in code made for it.
        private static int SizeOf<T>() => System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

        private static string Dump<T>(T value)
        {
            var bytes = new byte[System.Runtime.CompilerServices.Unsafe.SizeOf<T>()];
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(ref bytes[0], value);
            return BitConverter.ToString(bytes);
        }

        private static void Time(Action action)
        {
            foreach (var (group, isa) in _groups)
            {
                Use(isa);
                Measure.Method(action)
                    .SampleGroup(new SampleGroup(group, SampleUnit.Nanosecond))
                    .Run();
            }

            NinoBurst.Register();
        }
    }
}
#endif
