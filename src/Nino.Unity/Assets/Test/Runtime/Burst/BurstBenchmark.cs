// Compiled only into the player the release benchmark builds (NINO_UNITY_BENCH, added by
// Test.Editor.Burst.BenchmarkPlayerBuild when the editor runs with -ninoBenchmark): never in the editor, never in the
// player of the ordinary test runs.
#if NINO_BURST && NINO_UNITY_BENCH
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Nino.Burst;
using Nino.Core;
using NUnit.Framework;
using Unity.Burst;
using Unity.PerformanceTesting;
using UnityEngine;
using Random = System.Random;

namespace Test.Runtime.Burst
{
    /// <summary>
    /// The release benchmark of the Burst kernels, measured with the Unity Performance Testing package: each case is a
    /// call a user makes (NinoSerializer.Serialize to a new array, NinoDeserializer.Deserialize into an existing array,
    /// or to a new string, list or class), measured by <c>Measure.Method</c> (its own warm-up and probing, then 9
    /// measurements of about 100 ms, each the mean time per call) once per sample group:
    /// <list type="bullet">
    /// <item>"Without Burst": NinoKernels.Register(null), Nino.Core's own paths (as without com.unity.burst);</item>
    /// <item>"SSSE3 only" (AVX2 CPUs): the kernels limited to their 16-byte paths, to compare the two tiers;</item>
    /// <item>"With Burst": the kernels as a player registers them at startup.</item>
    /// </list>
    /// The package writes each test's result (##performancetestresult2:) and the run's metadata
    /// (##performancetestruninfo2: Unity, platform, CPU, packages) into the test output, which the player's results XML
    /// keeps; benchmarks/unity_report.py turns it into the release's Unity section. The one line of its own here,
    /// NINO_UNITY_ENV, names what the package cannot know: the kernels' instruction set and the process architecture.
    /// Nothing here asserts a speed: a test fails only when Burst or the kernels are missing or when the paths write
    /// different bytes.
    /// </summary>
    public class BurstBenchmark
    {
        private static readonly int[] Lengths = { 32, 1000, 100000 };

        /// <summary>A case: the value, and the calls each test measures.</summary>
        public sealed class Case
        {
            public string Name;
            public Func<byte[]> Serialize;
            public Func<byte[], Action> Deserialize;
            public Func<byte[], byte[]> ReadBack;
            public override string ToString() => Name;
        }

        private static List<Case> _cases;

        public static IEnumerable<TestCaseData> Cases() =>
            (_cases ??= Build()).Select(c => new TestCaseData(c).SetName(c.Name));

        private static List<Case> Build()
        {
            var random = new Random(5);
            var cases = new List<Case>();
            foreach (int n in Lengths)
                cases.Add(Values($"(int, long)[{n}]",
                    Enumerable.Range(0, n).Select(_ => (random.Next(), (long)random.Next() << 13)).ToArray()));
            foreach (int n in Lengths)
                cases.Add(Values($"(byte, int)[{n}]",
                    Enumerable.Range(0, n).Select(_ => ((byte)random.Next(), random.Next())).ToArray()));
            foreach (int n in Lengths)
                cases.Add(Values($"(double, long, double, float, byte)[{n}]",
                    Enumerable.Range(0, n)
                        .Select(_ => (random.NextDouble(), (long)random.Next(), random.NextDouble(),
                            (float)random.NextDouble(), (byte)random.Next()))
                        .ToArray()));
            foreach (int n in Lengths)
                cases.Add(Values($"PaddedRaw[{n}] (short, long)",
                    Enumerable.Range(0, n).Select(i => new PaddedRaw { A = (short)i, B = random.Next() }).ToArray()));
            // Latin-1 text (printable ASCII), which Nino writes a byte a char; and CJK text, which it writes as UTF-16
            // after the narrowing stops at the first char.
            foreach (int n in Lengths)
                cases.Add(One($"ASCII string ({n} chars)",
                    new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(32, 127)).ToArray())));
            foreach (int n in Lengths)
                cases.Add(One($"UTF-16 string ({n} CJK chars)",
                    new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(0x4E00, 0x9FA6)).ToArray())));
            var words = Enumerable.Range(0, 1000)
                .Select(_ => new string(Enumerable.Range(0, random.Next(8, 41)).Select(c => (char)random.Next(32, 127))
                    .ToArray()))
                .ToList();
            cases.Add(One($"List of {words.Count} ASCII strings (8–40 chars)", words));
            // Classes of the .NET benchmark's SimpleClass shape, one and an array of 100 as in its SimpleTest: what the
            // generated formatters cost under IL2CPP. Of their members only Name (36 Latin-1 chars) takes a kernel.
            cases.Add(One("SimpleClass", BenchClass.Create(random)));
            cases.Add(Values("SimpleClass[100]",
                Enumerable.Range(0, 100).Select(_ => BenchClass.Create(random)).ToArray()));
            // [NinoUtf8] text: ASCII, whose UTF-8 the kernels write and read, and CJK, where the kernels stop at the first
            // char and .NET's encoder and decoder do it all.
            foreach (int n in Lengths)
                cases.Add(One($"[NinoUtf8] ASCII string ({n} chars)", new Utf8Text
                {
                    Text = new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(32, 127)).ToArray()),
                }));
            cases.Add(One("[NinoUtf8] UTF-8 string (1000 CJK chars)", new Utf8Text
            {
                Text = new string(Enumerable.Range(0, 1000).Select(_ => (char)random.Next(0x4E00, 0x9FA6)).ToArray()),
            }));
            return cases;
        }

        /// <summary>An array: serialized to a new array, deserialized into an array of its length.</summary>
        private static Case Values<T>(string name, T[] values) => new Case
        {
            Name = name,
            Serialize = () => NinoSerializer.Serialize(values),
            Deserialize = bytes =>
            {
                var into = new T[values.Length];
                return () => NinoDeserializer.Deserialize(bytes, ref into);
            },
            ReadBack = bytes => NinoSerializer.Serialize(NinoDeserializer.Deserialize<T[]>(bytes)),
        };

        /// <summary>A string, a list of them or a class: serialized to a new array, deserialized to a new value.</summary>
        private static Case One<T>(string name, T value) => new Case
        {
            Name = name,
            Serialize = () => NinoSerializer.Serialize(value),
            Deserialize = bytes => () => NinoDeserializer.Deserialize<T>(bytes),
            ReadBack = bytes => NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(bytes)),
        };

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
        public void Deserialize(Case c) => Time(c.Deserialize(Check(c)));

        /// <summary>The bytes of the case: the same on every path, and read back to the same bytes.</summary>
        private static byte[] Check(Case c)
        {
            byte[] expected = null;
            foreach (var (group, isa) in _groups)
            {
                Use(isa);
                byte[] bytes = c.Serialize();
                expected ??= bytes;
                Assert.IsTrue(bytes.SequenceEqual(expected), $"{c.Name}: different bytes ({group})");
                Assert.IsTrue(c.ReadBack(bytes).SequenceEqual(expected), $"{c.Name}: read back differently ({group})");
            }

            return expected;
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
