#if NINO_BURST
using System;
using System.Linq;
using Nino.Burst;
using Nino.Core;
using NUnit.Framework;
using Unity.Burst;
using Debug = UnityEngine.Debug;

namespace Test.Runtime.Burst
{
    /// <summary>A raw struct with padding (2 + 6 + 8 bytes): arrays of it are written by the masked copy.</summary>
    public struct PaddedRaw
    {
        public short A;
        public long B;
    }

    /// <summary>
    /// The Burst kernels (Nino.Burst) against Nino.Core's scalar paths: the same bytes with the kernels registered and
    /// without, for every kernel kind and lengths around each threshold. Run in play mode and in an IL2CPP player
    /// (Burst AOT), and in edit mode through Test.Editor.Burst.BurstKernelsInEditMode. Their speed is measured by
    /// BurstBenchmark, in the release benchmark's player only.
    /// </summary>
    public unsafe class BurstKernelTests
    {
        private static readonly int[] Lengths = { 0, 1, 2, 15, 16, 17, 31, 32, 33, 47, 63, 64, 65, 100, 1000 };

        [OneTimeSetUp]
        public void Compile()
        {
            Assert.IsTrue(BurstCompiler.IsEnabled, "Burst is off");
            Assert.IsTrue(NinoBurst.Register(), "the kernels did not register");
            // SSSE3, AVX2 or AArch64 Neon; 0 would be the scalar branch (or managed code, not Burst's).
            Assert.AreNotEqual(0, NinoBurst.Isa);
            Debug.Log($"Nino Burst kernels: instruction set {NinoBurst.IsaName}");
        }

        [OneTimeTearDown]
        public void Restore() => NinoBurst.Register();

        /// <summary>Every instruction set the kernels take on this machine: SSSE3 and AVX2 on an AVX2 CPU.</summary>
        private static int[] Tiers
        {
            get
            {
                Assert.IsTrue(NinoBurst.Register());
                return NinoBurst.Isa == NinoBurst.Avx2 ? new[] { NinoBurst.Ssse3, NinoBurst.Avx2 } : new[] { NinoBurst.Isa };
            }
        }

        private static NinoKernels Use(int tier)
        {
            Assert.IsTrue(NinoBurst.Register(tier));
            return NinoKernels.Current;
        }

        [Test]
        public void PermuteMatchesTheScalarReference()
        {
            foreach (int tier in Tiers)
                Permute(Use(tier), NinoBurst.IsaName);
        }

        private static void Permute(NinoKernels kernels, string tier)
        {
            var random = new Random(1);
            for (int trial = 0; trial < 4000; trial++)
            {
                // Half the values of up to 16 bytes, the ones the AVX2 path takes two or more a vector.
                int most = trial % 2 == 0 ? 17 : 65;
                int from = random.Next(1, most), to = random.Next(1, most);
                int count = trial % 4 == 0 ? random.Next(0, 300) : random.Next(0, 70);
                var table = new byte[to];
                for (int j = 0; j < to; j++)
                    table[j] = random.Next(5) == 0 ? (byte)0xFF : (byte)random.Next(from);
                var source = new byte[from * count + 1];
                random.NextBytes(source);
                // Guard bytes after each destination: nothing may be written past the values.
                var burst = new byte[to * count + 64];
                random.NextBytes(burst);
                var scalar = (byte[])burst.Clone();
                fixed (byte* s = source, b = burst, c = scalar, t = table)
                {
                    kernels.Permute(s, from, b, to, t, count);
                    NinoKernels.PermuteScalar(s, from, c, to, t, count);
                }

                if (!burst.SequenceEqual(scalar))
                    Assert.Fail($"{tier}: permute of {count} values, {from} to {to} bytes");
            }
        }

        [Test]
        public void NarrowAndWidenMatchTheScalarReference()
        {
            foreach (int tier in Tiers)
                NarrowAndWiden(Use(tier), NinoBurst.IsaName);
        }

        private static void NarrowAndWiden(NinoKernels kernels, string tier)
        {
            var random = new Random(2);
            for (int n = 16; n < 600; n++)
            for (int bad = -1; bad < n; bad += Math.Max(1, n / 4))
            {
                var chars = new char[n];
                for (int j = 0; j < n; j++)
                    chars[j] = (char)random.Next(256);
                if (bad >= 0)
                    chars[bad] = (char)(0x100 + random.Next(0xD700));
                var burst = new byte[n + 16];
                var scalar = new byte[n + 16];
                bool narrowed, expected;
                fixed (char* c = chars)
                fixed (byte* b = burst, s = scalar)
                {
                    narrowed = kernels.Narrow(c, b, n);
                    expected = NinoKernels.NarrowScalar(c, s, n);
                }

                Assert.AreEqual(expected, narrowed, $"{tier}: narrow {n}, bad at {bad}");
                if (expected)
                    CollectionAssert.AreEqual(scalar, burst, $"{tier}: narrow {n}");
            }

            for (int n = 16; n < 600; n++)
            {
                var bytes = new byte[n];
                random.NextBytes(bytes);
                var burst = new char[n + 8];
                var scalar = new char[n + 8];
                fixed (byte* p = bytes)
                fixed (char* b = burst, s = scalar)
                {
                    kernels.Widen(p, b, n);
                    NinoKernels.WidenScalar(p, s, n);
                }

                CollectionAssert.AreEqual(scalar, burst, $"{tier}: widen {n}");
            }
        }

        /// <summary>The bytes with the kernels and without, and the values read back each way.</summary>
        private static void Same<T>(T value, string what)
        {
            NinoBurst.Unregister();
            byte[] scalar = NinoSerializer.Serialize(value);
            foreach (int tier in Tiers)
            {
                Use(tier);
                byte[] burst = NinoSerializer.Serialize(value);
                CollectionAssert.AreEqual(scalar, burst, $"{NinoBurst.IsaName}: {what}");
                byte[] again = NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(burst));
                CollectionAssert.AreEqual(scalar, again, $"{NinoBurst.IsaName}: {what}");
            }

            NinoBurst.Unregister();
            CollectionAssert.AreEqual(scalar, NinoSerializer.Serialize(NinoDeserializer.Deserialize<T>(scalar)), what);
            Assert.IsTrue(NinoBurst.Register());
        }

        [Test]
        public void SerializedBytesAreTheSameWithAndWithoutTheKernels()
        {
            var random = new Random(3);
            foreach (int n in Lengths)
            {
                Same(Enumerable.Range(0, n).Select(_ => (random.Next(), (long)random.Next() << 13)).ToArray(),
                    $"(int, long) x {n}");
                Same(Enumerable.Range(0, n).Select(_ => ((byte)random.Next(), random.Next())).ToArray(),
                    $"(byte, int) x {n}");
                Same(Enumerable.Range(0, n)
                        .Select(_ => ((short)random.Next(), (byte)random.Next(), (long)random.Next(), random.Next(2) == 1))
                        .ToArray(), $"(short, byte, long, bool) x {n}");
                Same(Enumerable.Range(0, n)
                        .Select(_ => (random.NextDouble(), (long)random.Next(), random.NextDouble(), (float)random.NextDouble(),
                            (byte)random.Next()))
                        .ToArray(), $"(double, long, double, float, byte) x {n}");
                Same(Enumerable.Range(0, n).Select(i => new PaddedRaw { A = (short)i, B = random.Next() }).ToArray(),
                    $"PaddedRaw x {n}");
                string latin1 = new string(Enumerable.Range(0, n).Select(_ => (char)random.Next(1, 256)).ToArray());
                Same(latin1, $"Latin-1 x {n}");
                Same(latin1 + "中", $"UTF-16 x {n}");
            }
        }

        [Test]
        public void GoldenBytes()
        {
            // 40 (byte, int): the length, then each as its byte and its int, little-endian.
            var values = Enumerable.Range(0, 40).Select(i => ((byte)(i * 3), i * 1000003)).ToArray();
            var expected = new byte[4 + 40 * 5];
            BitConverter.GetBytes(40).CopyTo(expected, 0);
            for (int i = 0; i < 40; i++)
            {
                expected[4 + i * 5] = values[i].Item1;
                BitConverter.GetBytes(values[i].Item2).CopyTo(expected, 4 + i * 5 + 1);
            }

            // 40 (int, long): the length, then each as its int and its long (12 bytes, whatever the memory layout).
            var wide = Enumerable.Range(0, 40).Select(i => (i * 7919, (long)i << 40 | (uint)i)).ToArray();
            var expectedWide = new byte[4 + 40 * 12];
            BitConverter.GetBytes(40).CopyTo(expectedWide, 0);
            for (int i = 0; i < 40; i++)
            {
                BitConverter.GetBytes(wide[i].Item1).CopyTo(expectedWide, 4 + i * 12);
                BitConverter.GetBytes(wide[i].Item2).CopyTo(expectedWide, 4 + i * 12 + 4);
            }

            // 40 Latin-1 chars: ~length, then a byte each.
            string text = new string(Enumerable.Range(0, 40).Select(i => (char)(0xA0 + i)).ToArray());
            var latin1 = BitConverter.GetBytes(~40).Concat(text.Select(c => (byte)c)).ToArray();
            foreach (int tier in Tiers)
            {
                Use(tier);
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize(values), NinoBurst.IsaName);
                CollectionAssert.AreEqual(values, NinoDeserializer.Deserialize<(byte, int)[]>(expected), NinoBurst.IsaName);
                CollectionAssert.AreEqual(expectedWide, NinoSerializer.Serialize(wide), NinoBurst.IsaName);
                CollectionAssert.AreEqual(wide, NinoDeserializer.Deserialize<(int, long)[]>(expectedWide), NinoBurst.IsaName);
                CollectionAssert.AreEqual(latin1, NinoSerializer.Serialize(text), NinoBurst.IsaName);
                Assert.AreEqual(text, NinoDeserializer.Deserialize<string>(latin1), NinoBurst.IsaName);
            }
        }

        [Test]
        public void InvalidBoolsAreRejected()
        {
            var values = Enumerable.Range(0, 64).Select(i => (i, i % 2 == 0)).ToArray();
            byte[] bytes = NinoSerializer.Serialize(values);
            bytes[4 + 33 * 5 + 4] = 2;
            foreach (int tier in Tiers)
            {
                Use(tier);
                Assert.Throws<NinoDataException>(() => NinoDeserializer.Deserialize<(int, bool)[]>(bytes), NinoBurst.IsaName);
            }
        }
    }
}
#endif
