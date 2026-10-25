// Compiled only into the player the release benchmark builds (NINO_UNITY_BENCH, added by
// Test.Editor.Burst.BenchmarkPlayerBuild when the editor runs with -ninoBenchmark): never in the editor, never in the
// player of the ordinary test runs.
#if NINO_BURST && NINO_UNITY_BENCH
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Nino.Burst;
using Nino.Core;
using NUnit.Framework;
using Unity.Burst;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Random = System.Random;

namespace Test.Runtime.Burst
{
    /// <summary>
    /// The release benchmark of the Burst kernels: each case is a call a user makes (NinoSerializer.Serialize to a new
    /// array, NinoDeserializer.Deserialize into an existing array, or to a new string or list), timed without the
    /// kernels (NinoKernels.Register(null), Nino.Core's scalar paths) and with them. Each path is warmed up, then timed
    /// in batches of about <see cref="BatchMs"/> ms, the two paths alternately, and each gives the median of
    /// <see cref="Rounds"/> batches in nanoseconds per call.
    /// <para>The results go to the player's log, one line each, which unity.yml collects and
    /// benchmarks/unity_report.py turns into the release's Unity section:</para>
    /// <code>NINO_UNITY_ENV|key=value|...                         (once: Unity, backend, Development or not, CPU, ...)
    /// NINO_UNITY_BENCH|case|operation|n|scalar ns|Burst ns    (one per case and operation)</code>
    /// Nothing here asserts a speed: the test fails only when Burst or the kernels are missing or when the two paths
    /// write different bytes.
    /// </summary>
    public class BurstBenchmark
    {
        private const int Rounds = 31;
        private const double BatchMs = 10;
        private const double WarmupMs = 100;
        private static readonly int[] Lengths = { 32, 1000, 100000 };

        [Test]
        public void Benchmark()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled, "Burst is off");
            Assert.IsTrue(NinoBurst.Register(), "the kernels did not register");
            Emit("NINO_UNITY_ENV" +
                 Field("unity", Application.unityVersion) +
                 Field("backend", Backend) +
                 Field("development", Debug.isDebugBuild ? "true" : "false") +
                 Field("isa", NinoBurst.Isa == 1 ? "SSSE3" : NinoBurst.Isa == 2 ? "Arm64 Neon" : "none") +
                 Field("cpu", SystemInfo.processorType) +
                 Field("cores", SystemInfo.processorCount.ToString(CultureInfo.InvariantCulture)) +
                 Field("os", SystemInfo.operatingSystem) +
                 Field("family", SystemInfo.operatingSystemFamily.ToString()) +
                 Field("arch", RuntimeInformation.ProcessArchitecture.ToString()) +
                 Field("rounds", Rounds.ToString(CultureInfo.InvariantCulture)) +
                 Field("batch_ms", BatchMs.ToString(CultureInfo.InvariantCulture)));

            var random = new Random(5);
            foreach (int n in Lengths)
                Values($"(int, long)[{n}]", n,
                    Enumerable.Range(0, n).Select(_ => (random.Next(), (long)random.Next() << 13)).ToArray());
            foreach (int n in Lengths)
                Values($"(byte, int)[{n}]", n,
                    Enumerable.Range(0, n).Select(_ => ((byte)random.Next(), random.Next())).ToArray());
            foreach (int n in Lengths)
                Values($"(double, long, double, float, byte)[{n}]", n,
                    Enumerable.Range(0, n)
                        .Select(_ => (random.NextDouble(), (long)random.Next(), random.NextDouble(),
                            (float)random.NextDouble(), (byte)random.Next()))
                        .ToArray());
            foreach (int n in Lengths)
                Values($"PaddedRaw[{n}] (short, long)", n,
                    Enumerable.Range(0, n).Select(i => new PaddedRaw { A = (short)i, B = random.Next() }).ToArray());
            // Latin-1 text (printable ASCII), which Nino writes a byte a char; and CJK text, which it writes as UTF-16
            // after the narrowing stops at the first char.
            foreach (int n in Lengths)
                Text($"ASCII string ({n} chars)", n,
                    new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(32, 127)).ToArray()));
            foreach (int n in Lengths)
                Text($"UTF-16 string ({n} CJK chars)", n,
                    new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(0x4E00, 0x9FA6)).ToArray()));
            var words = Enumerable.Range(0, 1000)
                .Select(_ => new string(Enumerable.Range(0, random.Next(8, 41)).Select(c => (char)random.Next(32, 127))
                    .ToArray()))
                .ToList();
            Text($"List of {words.Count} ASCII strings (8–40 chars)", words.Count, words);

            Assert.IsTrue(NinoBurst.Register());
        }

        private static string Backend =>
#if ENABLE_IL2CPP
            "IL2CPP";
#else
            "Mono";
#endif

        private static string Field(string key, string value) => $"|{key}={(value ?? "").Replace('|', '/')}";

        private static void Emit(string line) => Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}", line);

        /// <summary>An array: serialized to a new array, deserialized into an array of its length.</summary>
        private static void Values<T>(string name, int n, T[] values)
        {
            byte[] bytes = Bytes(name, values);
            var into = new T[values.Length];
            Row(name, "Serialize", n, () => NinoSerializer.Serialize(values));
            Row(name, "Deserialize", n, () => NinoDeserializer.Deserialize(bytes, ref into));
        }

        /// <summary>A string or a list of them: serialized to a new array, deserialized to a new value.</summary>
        private static void Text<T>(string name, int n, T value)
        {
            byte[] bytes = Bytes(name, value);
            Row(name, "Serialize", n, () => NinoSerializer.Serialize(value));
            Row(name, "Deserialize", n, () => NinoDeserializer.Deserialize<T>(bytes));
        }

        /// <summary>The bytes of <paramref name="value"/>, the same without the kernels and with them, and read back.</summary>
        private static byte[] Bytes<T>(string name, T value)
        {
            Use(false);
            byte[] scalar = NinoSerializer.Serialize(value);
            Use(true);
            byte[] burst = NinoSerializer.Serialize(value);
            Assert.IsTrue(scalar.SequenceEqual(burst), $"{name}: different bytes with the kernels");
            Assert.IsTrue(NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(burst)).SequenceEqual(burst),
                $"{name}: read back differently with the kernels");
            return burst;
        }

        private static void Row(string name, string operation, int n, Action action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            int scalarCalls = Calibrate(action, false), burstCalls = Calibrate(action, true);
            var scalar = new double[Rounds];
            var burst = new double[Rounds];
            for (int round = 0; round < Rounds; round++)
            {
                scalar[round] = Batch(action, false, scalarCalls);
                burst[round] = Batch(action, true, burstCalls);
            }

            Emit(string.Join("|", "NINO_UNITY_BENCH", name, operation, n.ToString(CultureInfo.InvariantCulture),
                Median(scalar).ToString("F1", CultureInfo.InvariantCulture),
                Median(burst).ToString("F1", CultureInfo.InvariantCulture)));
        }

        private static void Use(bool kernels)
        {
            if (kernels)
                Assert.IsTrue(NinoBurst.Register(), "the kernels did not register");
            else
                NinoBurst.Unregister();
        }

        /// <summary>Warms the path up for <see cref="WarmupMs"/> ms (three calls at least); returns the calls a batch makes.</summary>
        private static int Calibrate(Action action, bool kernels)
        {
            Use(kernels);
            var watch = Stopwatch.StartNew();
            int calls = 0;
            while (calls < 3 || watch.Elapsed.TotalMilliseconds < WarmupMs)
            {
                action();
                calls++;
            }

            return Math.Max(1, (int)(BatchMs / (watch.Elapsed.TotalMilliseconds / calls)));
        }

        /// <summary>Nanoseconds per call over <paramref name="calls"/> calls.</summary>
        private static double Batch(Action action, bool kernels, int calls)
        {
            Use(kernels);
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++)
                action();
            return (Stopwatch.GetTimestamp() - start) * (1e9 / Stopwatch.Frequency) / calls;
        }

        private static double Median(double[] values)
        {
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        }
    }
}
#endif
